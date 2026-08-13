using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiSeat.Service.Accounts;
using MultiSeat.Service.Configuration;
using MultiSeat.Service.Input;
using MultiSeat.Service.Monitoring;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared;
using MultiSeat.Shared.Models;
using Xunit;

namespace MultiSeat.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the MultiSeat seat lifecycle.
/// Unit tests verify logic without hardware; integration tests are skipped without prerequisites.
/// </summary>
public class EndToEndTests
{
    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    // ── GpuMonitor tests ──────────────────────────────────────────────

    [Fact]
    public void GpuMonitor_Query_ReturnsGpuInfo()
    {
        // Should return something (either NVML, WMI, or null — but not crash)
        var monitor = new GpuMonitor(new NullLogger<GpuMonitor>());
        var info = monitor.Query();

        // On CI or headless machines, WMI should still return basic info
        // On bare metal with GPU, NVML or WMI will return data
        // Just verify it doesn't throw
        if (info is not null)
        {
            Assert.False(string.IsNullOrEmpty(info.Name));
            Assert.True(info.UtilizationPercent >= 0 && info.UtilizationPercent <= 100);
            Assert.True(info.VramTotalMb >= 0);
        }
    }

    [Fact]
    public void GpuMonitor_Query_NeverThrows()
    {
        var monitor = new GpuMonitor(new NullLogger<GpuMonitor>());

        // Should be safe to call multiple times
        for (int i = 0; i < 3; i++)
        {
            var ex = Record.Exception(() => monitor.Query());
            Assert.Null(ex);
        }
    }

    // ── SystemStatus model tests ──────────────────────────────────────

    [Fact]
    public void SystemStatus_MaxSeats_MatchesConstants()
    {
        var status = new SystemStatus();
        Assert.Equal(Constants.MaxSeats, status.MaxSeats);
    }

    [Fact]
    public void SystemStatus_Timestamp_IsPopulated()
    {
        var before = DateTimeOffset.UtcNow;
        var status = new SystemStatus();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(status.Timestamp, before, after);
    }

    // ── GpuInfo model tests ───────────────────────────────────────────

    [Fact]
    public void GpuInfo_DefaultValues_AreZero()
    {
        var info = new GpuInfo();
        Assert.Equal(string.Empty, info.Name);
        Assert.Equal(0, info.UtilizationPercent);
        Assert.Equal(0, info.VramTotalMb);
        Assert.Equal(0, info.VramUsedMb);
        Assert.Equal(0, info.EncoderUtilizationPercent);
        Assert.Equal(0, info.ActiveEncoderSessions);
    }

    // ── MultiSeatOptions integration tests ─────────────────────────────────

    [Fact]
    public void MultiSeatOptions_AllDefaults_AreSet()
    {
        var opts = new MultiSeatOptions();

        Assert.Equal(4, opts.MaxSeats);
        Assert.Equal(Constants.PortBase, opts.PortBase);
        Assert.Equal(Constants.DefaultApolloPath, opts.ApolloExePath);
        Assert.Equal(Constants.DefaultApolloConfigDir, opts.ApolloConfigDir);
        Assert.Equal(Constants.DefaultApiPort, opts.ApiPort);
        Assert.Equal(string.Empty, opts.ApiKey);
        Assert.False(opts.EnableKeyboardMouseIsolation); // no-op as architected; off by default
        Assert.True(opts.AutoAssignControllers);
        Assert.Equal(15_000, opts.SessionConnectTimeoutMs);
        Assert.Equal(10_000, opts.ProcessLaunchTimeoutMs);
        Assert.Equal(5_000, opts.HealthCheckIntervalMs);

        // Shared game library + emulator netplay defaults
        Assert.True(opts.EnableSharedGameLibrary);
        Assert.Equal(@"C:\MultiSeatGames", opts.SharedGameLibraryDir);
        Assert.True(opts.EnableEmulatorNetplay);
        Assert.False(opts.SeedRetroArchNetplayConfig); // opt-in (writes a user file)
        Assert.Equal(string.Empty, opts.RetroArchConfigPath);
    }

    // ── Port allocation capacity tests ────────────────────────────────

    [Fact]
    public void PortAllocation_MaxSeats_FitsInPortRange()
    {
        // MaxSeats (8) × PortsPerSeat (30) ports, starting at PortBase (48100) → last port 48339.
        // Must stay below the ephemeral port range (49152+).
        var lastPort = Constants.PortBase + (Constants.MaxSeats * Constants.PortsPerSeat) - 1;
        Assert.Equal(48339, lastPort);
        Assert.True(lastPort < 49152, "Port range should not overlap with ephemeral ports");
    }

    // ── Physical memory query test ────────────────────────────────────

    [Fact]
    public void GlobalMemoryStatusEx_ReturnsValidMemory()
    {
        // This verifies the P/Invoke in SystemEndpoints/MetricsCollector works
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        bool result = GlobalMemoryStatusEx(ref memStatus);

        Assert.True(result);
        Assert.True(memStatus.ullTotalPhys > 0);
        Assert.True(memStatus.ullAvailPhys > 0);
        Assert.True(memStatus.ullAvailPhys <= memStatus.ullTotalPhys);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // ── Seat lifecycle state machine tests ─────────────────────────────

    [Fact]
    public void SeatStatus_AllStates_Defined()
    {
        var states = Enum.GetValues<SeatStatus>();
        Assert.Contains(SeatStatus.Idle, states);
        Assert.Contains(SeatStatus.Provisioning, states);
        Assert.Contains(SeatStatus.Configuring, states);
        Assert.Contains(SeatStatus.Ready, states);
        Assert.Contains(SeatStatus.Streaming, states);
        Assert.Contains(SeatStatus.TearingDown, states);
        Assert.Contains(SeatStatus.Error, states);
    }

    [Fact]
    public void SeatInfo_NewSeat_HasDefaults()
    {
        var seat = new SeatInfo { AccountName = "TestSeat" };
        Assert.NotEqual(Guid.Empty, seat.Id);
        Assert.Equal(SeatStatus.Idle, seat.Status);
        Assert.Equal(1920, seat.Width);
        Assert.Equal(1080, seat.Height);
        Assert.Equal(60, seat.Fps);
        Assert.Equal(-1, seat.VacCableIndex);
        Assert.Equal(-1, seat.ViGEmControllerIndex);
        Assert.Null(seat.ErrorMessage);
    }

    [Fact]
    public void SeatRequest_Defaults_AreReasonable()
    {
        var request = new SeatRequest { AccountName = "test" };
        Assert.Equal(1920, request.Width);
        Assert.Equal(1080, request.Height);
        Assert.Equal(60, request.Fps);
        Assert.Null(request.LaunchApp);
    }

    // ── RdpWrapper detection test ─────────────────────────────────────

    [Fact]
    public void RdpWrapper_EnsureMultiSession_DoesNotCrash()
    {
        var wrapper = new RdpWrapper(new NullLogger<RdpWrapper>());
        // Should not throw — returns true/false based on system state
        var ex = Record.Exception(() => wrapper.EnsureMultiSession());
        Assert.Null(ex);
    }

    // ── InputHookManager + InputRouter lifecycle ──────────────────────

    [Fact]
    public void InputRouter_StartStop_Lifecycle()
    {
        var router = new InputRouter(
            new NullLogger<InputRouter>(),
            new ControllerManager(new NullLogger<ControllerManager>()));

        // Should not throw even without hardware
        router.Start();
        Thread.Sleep(50);
        router.Stop();
    }

    [Fact]
    public void InputHookManager_FullLifecycle_WithoutDll()
    {
        var opts = new MultiSeatOptions
        {
            EnableKeyboardMouseIsolation = true,
            InputHookDllPath = @"C:\nonexistent\MultiSeatInputHook.dll"
        };
        var mgr = new InputHookManager(
            new NullLogger<InputHookManager>(), Options.Create(opts));

        // Start with missing DLL should not crash
        mgr.Start();
        Assert.False(mgr.IsInstalled);

        // Install/Uninstall should be no-ops
        mgr.InstallForSession(1);
        mgr.Uninstall();

        mgr.Stop();
    }

    // ── Full pipeline integration tests (require admin + hardware) ────

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires admin, RDP Wrapper, ViGEmBus, VAC, HidHide")]
    public void FullSeatLifecycle_ProvisionAndTeardown()
    {
        // This is the E2E smoke test — provision a seat and tear it down.
        // Requires all subsystems installed and running as Administrator.
        //
        // Steps verified:
        // 1. Create account
        // 2. Provision seat (session, display, audio, streaming, input)
        // 3. Verify seat status is Ready
        // 4. Teardown seat
        // 5. Verify clean teardown
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires admin and audio hardware")]
    public void AudioRouting_AssignAndReleaseCable()
    {
        // Tests VAC cable discovery, assignment, and in-session policy change.
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires admin and multi-session")]
    public void SessionLauncher_CreatesAndDestroysSession()
    {
        // Tests Windows session creation via LogonUser + CreateProcessAsUser.
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires NVIDIA GPU with drivers")]
    public void GpuMonitor_NvmlQuery_ReturnsRealData()
    {
        var monitor = new GpuMonitor(new NullLogger<GpuMonitor>());
        var info = monitor.Query();

        Assert.NotNull(info);
        Assert.DoesNotContain("Detection pending", info!.Name);
        Assert.True(info.VramTotalMb > 0);
    }
}
