using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;
public interface IRealtimeBridge : IAsyncDisposable
{
    bool IsSimulation { get; }
    bool CanWrite { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct);
    Task WriteCommandAsync(BenchCommand command, CancellationToken ct);
}
public interface ISessionRecorder : IAsyncDisposable
{
    string DirectoryPath { get; }
    void Record(BenchSnapshot snapshot, string pointId);
    void Event(string code, string message);
    Task SaveResultAsync(PointResult result, CancellationToken ct);
    void CheckHealth();
}

public sealed class SafetyTripException(string message) : Exception(message);
public sealed class PointFailedException(string message) : Exception(message);

public sealed class SafetyManager(BenchConfig config)
{
    private uint? _heartbeat;
    private DateTimeOffset _heartbeatAt;
    public void Reset() { _heartbeat = null; _heartbeatAt = default; }
    public string? Evaluate(BenchSnapshot s, DateTimeOffset now)
    {
        var l = config.Limits;
        if (!s.Connected || !s.EtherCatOnline) return "PLC/EtherCAT离线";
        if (!double.IsFinite(s.SpeedRpm) || !double.IsFinite(s.ActualTorqueNm) || !double.IsFinite(s.TargetTorqueNm)) return "反馈含非有限值";
        if (new[] { s.DcBusV, s.MotorTempC, s.BrakeTempC }.Any(v => v.HasValue && !double.IsFinite(v.Value))) return "保护测量无效";
        if (now - s.Timestamp > TimeSpan.FromMilliseconds(l.SnapshotTimeoutMs) || s.Timestamp > now.AddSeconds(1)) return "数据时间戳无效或过期";
        if (_heartbeat != s.PlcHeartbeat) { _heartbeat = s.PlcHeartbeat; _heartbeatAt = now; }
        if (now - _heartbeatAt > TimeSpan.FromMilliseconds(l.HeartbeatTimeoutMs)) return "PLC心跳停止";
        if (s.Interlocks != Interlock.None || s.ErrorCode != 0 || s.State == BenchState.Fault) return $"联锁/驱动故障：{s.Interlocks} / 0x{s.ErrorCode:X}";
        if (Math.Abs(s.SpeedRpm) > l.MaxSpeedRpm) return "超速";
        if (s.MotorTempC >= l.MotorTripC || s.BrakeTempC >= l.BrakeTripC) return "温度超限";
        if (l.DcBusTripV is { } max && s.DcBusV >= max) return "母线过压";
        return null;
    }
}
