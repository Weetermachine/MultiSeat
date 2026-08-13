using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using MultiSeat.Service.Sessions;
using MultiSeat.Shared.Models;

namespace MultiSeat.Service.Monitoring;

/// <summary>
/// Aggregates system-wide metrics for the dashboard.
/// Stateful singleton — samples CPU/network deltas between calls.
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private readonly GpuMonitor _gpu;
    private readonly RdpWrapper _rdpWrapper;
    private readonly ILogger<MetricsCollector> _logger;

    private PerformanceCounter? _cpuCounter;
    private string _cpuName = string.Empty;

    private readonly Dictionary<int, (TimeSpan cpu, DateTime time)> _lastProcSample = new();
    private readonly Dictionary<string, (long rx, long tx)> _lastNetSample = new();
    private DateTime _lastNetSampleTime = DateTime.MinValue;

    public MetricsCollector(GpuMonitor gpu, RdpWrapper rdpWrapper, ILogger<MetricsCollector> logger)
    {
        _gpu = gpu;
        _rdpWrapper = rdpWrapper;
        _logger = logger;
        InitCpuCounter();
        InitCpuName();
    }

    private void InitCpuCounter()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // first read always 0
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize CPU performance counter");
        }
    }

    private void InitCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                _cpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                break;
            }
        }
        catch { /* non-critical */ }
    }

    public SystemStatus Collect(SeatManager seatManager)
    {
        GetPhysicalMemory(out var totalMb, out var availMb);
        var gpu = _gpu.Query();

        var cpu = CollectCpu();
        var (network, topProcesses) = CollectNetworkAndProcesses();
        var disks = CollectDisks();

        int memPct = totalMb > 0 ? (int)((totalMb - availMb) * 100 / totalMb) : 0;
        var rdpActive = _rdpWrapper.EnsureMultiSession(); // queried once, reused below
        var health = ComputeHealth(
            cpu?.UtilizationPercent ?? 0,
            memPct,
            gpu?.UtilizationPercent ?? 0,
            seatManager.ActiveSeatCount,
            rdpActive);

        return new SystemStatus
        {
            ActiveSeats = seatManager.ActiveSeatCount,
            Gpu = gpu,
            Cpu = cpu,
            Network = network,
            TopProcesses = topProcesses,
            Disks = disks,
            Health = health,
            WindowsBuild = Environment.OSVersion.VersionString,
            MultiSessionActive = rdpActive,
            SystemMemoryMb = totalMb,
            AvailableMemoryMb = availMb
        };
    }

    private CpuInfo CollectCpu()
    {
        int utilPct = 0;
        try
        {
            if (_cpuCounter != null)
                utilPct = (int)_cpuCounter.NextValue();
        }
        catch { /* non-critical */ }

        return new CpuInfo
        {
            Name = _cpuName,
            UtilizationPercent = utilPct,
            LogicalCores = Environment.ProcessorCount
        };
    }

    private (NetworkInfo network, List<TopProcess> processes) CollectNetworkAndProcesses()
    {
        long rxPerSec = 0, txPerSec = 0;
        string primaryAdapter = string.Empty;

        try
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastNetSampleTime).TotalSeconds;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var primary = interfaces
                .OrderByDescending(n => n.GetIPv4Statistics().BytesReceived)
                .FirstOrDefault();
            primaryAdapter = primary?.Name ?? string.Empty;

            if (elapsed > 0.5 && _lastNetSampleTime != DateTime.MinValue)
            {
                foreach (var iface in interfaces)
                {
                    var stats = iface.GetIPv4Statistics();
                    if (_lastNetSample.TryGetValue(iface.Id, out var last))
                    {
                        rxPerSec += (long)((stats.BytesReceived - last.rx) / elapsed);
                        txPerSec += (long)((stats.BytesSent - last.tx) / elapsed);
                    }
                }
            }

            _lastNetSample.Clear();
            foreach (var iface in interfaces)
            {
                var stats = iface.GetIPv4Statistics();
                _lastNetSample[iface.Id] = (stats.BytesReceived, stats.BytesSent);
            }
            _lastNetSampleTime = now;
        }
        catch { /* non-critical */ }

        var topProcesses = new List<TopProcess>();
        try
        {
            var now = DateTime.UtcNow;
            var procs = Process.GetProcesses();
            var cpuPcts = new Dictionary<int, double>();

            foreach (var proc in procs)
            {
                try
                {
                    var cpuTime = proc.TotalProcessorTime;
                    if (_lastProcSample.TryGetValue(proc.Id, out var last))
                    {
                        var elapsed = (now - last.time).TotalSeconds;
                        if (elapsed > 0)
                            cpuPcts[proc.Id] = (cpuTime - last.cpu).TotalSeconds
                                               / (elapsed * Environment.ProcessorCount) * 100.0;
                    }
                    _lastProcSample[proc.Id] = (cpuTime, now);
                }
                catch { /* process may have exited */ }
            }

            var liveIds = procs.Select(p => p.Id).ToHashSet();
            foreach (var key in _lastProcSample.Keys.Where(k => !liveIds.Contains(k)).ToList())
                _lastProcSample.Remove(key);

            var connCounts = GetTcpConnectionCountByPid();

            topProcesses = procs
                .Where(p => cpuPcts.ContainsKey(p.Id))
                .OrderByDescending(p => cpuPcts[p.Id])
                .Take(5)
                .Select(p =>
                {
                    long memMb = 0;
                    try { memMb = p.WorkingSet64 / (1024 * 1024); } catch { }
                    connCounts.TryGetValue(p.Id, out var conns);
                    return new TopProcess
                    {
                        Pid = p.Id,
                        Name = p.ProcessName,
                        CpuPercent = Math.Round(cpuPcts[p.Id], 1),
                        MemoryMb = memMb,
                        NetworkConnections = conns
                    };
                })
                .ToList();

            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        catch { /* non-critical */ }

        return (
            new NetworkInfo
            {
                PrimaryAdapter = primaryAdapter,
                BytesReceivedPerSec = Math.Max(0, rxPerSec),
                BytesSentPerSec = Math.Max(0, txPerSec)
            },
            topProcesses
        );
    }

    private static Dictionary<int, int> GetTcpConnectionCountByPid()
    {
        var result = new Dictionary<int, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"ROOT\StandardCimv2",
                "SELECT OwningProcess FROM MSFT_NetTCPConnection");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    var pid = Convert.ToInt32(obj["OwningProcess"]);
                    result[pid] = result.TryGetValue(pid, out var c) ? c + 1 : 1;
                }
            }
        }
        catch { /* WMI may not be available */ }
        return result;
    }

    private static List<DiskInfo> CollectDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var totalMb = drive.TotalSize / (1024 * 1024);
                var freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                disks.Add(new DiskInfo
                {
                    Name = drive.Name,
                    Label = drive.VolumeLabel,
                    TotalMb = totalMb,
                    FreeMb = freeMb,
                    UsedPercent = totalMb > 0 ? (int)((totalMb - freeMb) * 100 / totalMb) : 0
                });
            }
        }
        catch { /* non-critical */ }
        return disks;
    }

    private static HealthScore ComputeHealth(
        int cpuPct, int memPct, int gpuPct, int activeSeats, bool rdpActive)
    {
        var issues = new List<string>();
        int score = 100;

        if (cpuPct >= 90) { issues.Add($"CPU critical: {cpuPct}%"); score -= 30; }
        else if (cpuPct >= 75) { issues.Add($"CPU high: {cpuPct}%"); score -= 15; }

        if (memPct >= 90) { issues.Add($"Memory critical: {memPct}%"); score -= 25; }
        else if (memPct >= 80) { issues.Add($"Memory high: {memPct}%"); score -= 10; }

        if (gpuPct >= 95) { issues.Add($"GPU near capacity: {gpuPct}%"); score -= 15; }

        if (!rdpActive) { issues.Add("Multi-session patch inactive — concurrent sessions unavailable"); score -= 30; }

        score = Math.Max(0, score);
        var status = score >= 70 ? HealthStatus.Healthy
                   : score >= 40 ? HealthStatus.Warning
                   : HealthStatus.Critical;

        return new HealthScore { Status = status, Score = score, Issues = issues };
    }

    private static void GetPhysicalMemory(out long totalMb, out long availMb)
    {
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            totalMb = (long)(memStatus.ullTotalPhys / (1024 * 1024));
            availMb = (long)(memStatus.ullAvailPhys / (1024 * 1024));
        }
        else
        {
            totalMb = 0;
            availMb = 0;
        }
    }

    public void Dispose() => _cpuCounter?.Dispose();

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
}
