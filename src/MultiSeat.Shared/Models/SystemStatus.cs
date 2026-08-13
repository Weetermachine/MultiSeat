namespace MultiSeat.Shared.Models;

public sealed class SystemStatus
{
    public int ActiveSeats { get; set; }
    public int MaxSeats { get; set; } = Constants.MaxSeats;
    public GpuInfo? Gpu { get; set; }
    public CpuInfo? Cpu { get; set; }
    public NetworkInfo? Network { get; set; }
    public List<TopProcess> TopProcesses { get; set; } = [];
    public List<DiskInfo> Disks { get; set; } = [];
    public HealthScore Health { get; set; } = new();
    public long SystemMemoryMb { get; set; }
    public long AvailableMemoryMb { get; set; }
    public string WindowsBuild { get; set; } = string.Empty;
    /// <summary>
    /// True when TermService is running a multi-session shim (TermWrap, RDP Wrapper, …)
    /// instead of the stock termsrv.dll. Not vendor-specific — see <c>RdpWrapper</c>.
    /// </summary>
    public bool MultiSessionActive { get; set; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class GpuInfo
{
    public string Name { get; set; } = string.Empty;
    public int UtilizationPercent { get; set; }
    public long VramTotalMb { get; set; }
    public long VramUsedMb { get; set; }
    public int EncoderUtilizationPercent { get; set; }
    public int ActiveEncoderSessions { get; set; }
}

public sealed class CpuInfo
{
    public string Name { get; set; } = string.Empty;
    public int UtilizationPercent { get; set; }
    public int LogicalCores { get; set; }
}

public sealed class NetworkInfo
{
    public string PrimaryAdapter { get; set; } = string.Empty;
    public long BytesReceivedPerSec { get; set; }
    public long BytesSentPerSec { get; set; }
}

public sealed class TopProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long MemoryMb { get; set; }
    public int NetworkConnections { get; set; }
}

public sealed class DiskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public long TotalMb { get; set; }
    public long FreeMb { get; set; }
    public int UsedPercent { get; set; }
}

public enum HealthStatus { Healthy, Warning, Critical }

public sealed class HealthScore
{
    public HealthStatus Status { get; set; } = HealthStatus.Healthy;
    public int Score { get; set; } = 100;
    public List<string> Issues { get; set; } = [];
}
