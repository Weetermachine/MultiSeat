namespace MultiSeat.Service.Configuration;

public sealed class MultiSeatOptions
{
    public const string SectionName = "MultiSeat";

    // ── Seats ────────────────────────────────────────────────────────
    public int MaxSeats { get; set; } = 4;
    public int PortBase { get; set; } = Shared.Constants.PortBase;

    // ── Apollo / Sunshine ────────────────────────────────────────────
    public string ApolloExePath { get; set; } = Shared.Constants.DefaultApolloPath;
    public string ApolloConfigDir { get; set; } = Shared.Constants.DefaultApolloConfigDir;

    // NVENC quality preset: 1 (P1, lowest latency) → 7 (P7, highest quality).
    // P4 is balanced — good quality without perceptible encode latency.
    // Apollo default is 1; we raise it because the NVENC hardware handles P4 at full framerate.
    public int NvencPreset { get; set; } = 4;

    // ── API ──────────────────────────────────────────────────────────
    public int ApiPort { get; set; } = Shared.Constants.DefaultApiPort;
    public string ApiKey { get; set; } = string.Empty;  // set in appsettings or env
    public bool RequireHttps { get; set; } = true;
    public string[] CorsOrigins { get; set; } = [];

    // NOTE: VacCableCount is gone. Seats no longer use host virtual audio cables — each seat's
    // RDP session owns its own audio endpoint (audiomode:i:0), so seat count is not bounded by
    // installed VB-CABLE / VoiceMeeter devices. Existing appsettings.json files may still carry
    // the key; the configuration binder ignores unknown keys, so leaving it there is harmless.

    // ── HidHide ──────────────────────────────────────────────────────
    public string HidHideCliPath { get; set; } = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

    // ── Input Isolation ──────────────────────────────────────────────
    public string InputHookDllPath { get; set; } = @"MultiSeatInputHook.dll";

    // Keyboard/mouse session isolation via the InputHook DLL.
    // Default OFF — it is a no-op as architected: the low-level WH_KEYBOARD_LL/WH_MOUSE_LL
    // hooks are installed from the service process (Session 0), where GetForegroundWindow()
    // returns NULL, so ShouldPassThrough() always passes the event. There is also no
    // cross-session K/M bleed to prevent (physical input goes to the console session; Moonlight
    // input is SendInput'd inside the seat session). Re-enabling is only meaningful if the hook
    // is re-architected to run inside the seat session. See CLAUDE.md "Known Constraints".
    public bool EnableKeyboardMouseIsolation { get; set; } = false;
    public bool AutoAssignControllers { get; set; } = true;

    // ── Display ──────────────────────────────────────────────────────
    // Enable Windows Advanced Color (HDR) on virtual displays at seat creation.
    // Requires SudoVDA driver v0.5+ with HDR EDID support.
    // When enabled, Apollo will stream in HDR if the Moonlight client also supports it.
    public bool EnableHdr { get; set; } = false;

    // ── Controller emulation ─────────────────────────────────────────
    // When true, MultiSeat creates a ViGEm virtual Xbox 360 controller per seat
    // and routes a host-side physical XInput controller into the session.
    // When false (default), Apollo handles controller forwarding natively
    // from the Moonlight client (e.g. ROG Ally). Enabling this alongside
    // Apollo's built-in controller forwarding causes duplicate controllers.
    public bool EnableViGEmController { get; set; } = false;

    // ── Launch-on-connect apps ───────────────────────────────────────
    // Apps launched into a seat's session when a Moonlight client connects.
    // Empty by default (feature off). Use this INSTEAD of Windows autostart for
    // game launchers (Steam Big Picture, EmulationStation, RetroBat, …): launching
    // them after the client connects guarantees Apollo's virtual controller already
    // exists, so the launcher's startup controller scan detects it. Apps autostarted
    // at login run before any stream and never see the per-stream virtual pad.
    public LaunchOnConnectApp[] LaunchOnConnect { get; set; } = [];

    // Delay after the client-connect event before launching the apps, giving Apollo
    // a moment to create the virtual controller so the apps enumerate it at startup.
    public int LaunchOnConnectDelayMs { get; set; } = 4_000;

    // Kill the launched apps when the Moonlight client disconnects. When false,
    // the apps stay running and are reused on the next connect (no relaunch while
    // still alive). Single-instance launchers like Steam tolerate either setting.
    public bool KillLaunchOnConnectAppsOnDisconnect { get; set; } = false;

    // ── Timeouts ─────────────────────────────────────────────────────
    public int SessionConnectTimeoutMs { get; set; } = 15_000;
    public int ProcessLaunchTimeoutMs { get; set; } = 10_000;
    public int HealthCheckIntervalMs { get; set; } = 5_000;

    // ── Shared game library ──────────────────────────────────────────
    // Create a shared games/ROMs location all seat accounts can read/write, so a Steam game
    // installed by one seat's account is not re-downloaded by another owning account, and ROMs
    // live in one place. Creates {SharedGameLibraryDir}\SteamLibrary and \ROMs at startup and
    // grants BUILTIN\Users Modify. Point each seat's Steam at the SteamLibrary folder manually.
    public bool EnableSharedGameLibrary { get; set; } = true;
    public string SharedGameLibraryDir { get; set; } = @"C:\MultiSeatGames";

    // ── Emulator netplay ─────────────────────────────────────────────
    // Assign each seat a deterministic, collision-free netplay port from its own port block
    // (seat.PortBase + Constants.OffsetRetroArchNetplay) and open it in the firewall. Seats
    // netplay each other over loopback (127.0.0.1:<host-seat-port>).
    public bool EnableEmulatorNetplay { get; set; } = true;

    // Opt-in: seed each seat user's retroarch.cfg with its netplay port + the shared ROM dir.
    // Off by default because it writes into a user-profile / emulator config file.
    public bool SeedRetroArchNetplayConfig { get; set; } = false;

    // Override for the seat's RetroArch config path. Empty → auto-detect
    // C:\Users\{AccountName}\AppData\Roaming\RetroArch\retroarch.cfg.
    public string RetroArchConfigPath { get; set; } = string.Empty;

    // ── Rebuild ───────────────────────────────────────────────────────
    // Absolute path to the repo root. Required for the dashboard Rebuild button.
    // Example: C:\MultiSeat-Development
    public string SourceDir { get; set; } = string.Empty;
}

/// <summary>
/// One app to launch into a seat session when a Moonlight client connects.
/// Configured under MultiSeat:LaunchOnConnect in appsettings.json.
/// </summary>
public sealed class LaunchOnConnectApp
{
    /// <summary>Absolute path to the executable (e.g. Steam.exe).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Optional command-line arguments (e.g. "-bigpicture").</summary>
    public string? Arguments { get; set; }

    /// <summary>Optional working directory; null inherits the launcher default.</summary>
    public string? WorkingDirectory { get; set; }
}
