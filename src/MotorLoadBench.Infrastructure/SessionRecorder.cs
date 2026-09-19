using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class SessionRecorder : ISessionRecorder
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writer;
    private readonly object _eventsGate = new();
    private readonly StreamWriter _events;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private Exception? _error;
    private DateTimeOffset? _lastSample;
    private int _gaps;
    public string DirectoryPath { get; }
    public SessionRecorder(string root, BenchConfig config, bool simulation, string configHash)
    {
        DirectoryPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, "session.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, startedUtc = _start, simulation, configHash, config,
            softwareVersion = typeof(SessionRecorder).Assembly.GetName().Version?.ToString(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SessionRecorder).Assembly.Location))),
            gitCommit = Environment.GetEnvironmentVariable("MOTOR_BENCH_GIT_COMMIT") ?? "unversioned",
            status = "OPEN", recording = "100 Hz host polling target; actual timestamps/gaps recorded; not PLC raw 1 kHz"
        }, BenchConfig.Json));
        _events = new(Path.Combine(DirectoryPath, "events.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _writer = Task.Run(async () =>
        {
            try
            {
                await using var stream = new StreamWriter(Path.Combine(DirectoryPath, "samples.csv"), false, new UTF8Encoding(true));
                await stream.WriteLineAsync("timestamp_utc,elapsed_ms,test_point_id,machine_state,target_torque_nm,actual_torque_nm,speed_rpm,mechanical_power_w,dc_bus_v,servo_current_a,motor_temp_c,brake_resistor_temp_c,dut_bus_v,dut_bus_current_a,dut_input_power_w,external_torque_nm,external_speed_rpm,measured_power_w,efficiency_pct,error_code,interlock_mask,plc_heartbeat");
                int count = 0;
                await foreach (var line in _channel.Reader.ReadAllAsync())
                { await stream.WriteLineAsync(line); if (++count % 50 == 0) await stream.FlushAsync(); }
                await stream.FlushAsync();
            }
            catch (Exception ex) { _error = ex; }
        });
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public void CheckHealth() { if (_error != null) throw new IOException("测试数据写入失败，停止加载：" + _error.Message, _error); }
    public void Record(BenchSnapshot s, string pointId)
    {
        CheckHealth();
        if (_lastSample is { } prev && (s.Timestamp - prev).TotalMilliseconds > 30) _gaps++;
        _lastSample = s.Timestamp;
        static string N(double? x) => x?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        var line = string.Join(',', s.Timestamp.ToString("O"), N((s.Timestamp - _start).TotalMilliseconds),
            '"' + pointId.Replace("\"", "\"\"") + '"', s.State, N(s.TargetTorqueNm), N(s.ActualTorqueNm), N(s.SpeedRpm), N(s.MechanicalPowerW),
            N(s.DcBusV), N(s.ServoCurrentA), N(s.MotorTempC), N(s.BrakeTempC), N(s.DutBusV), N(s.DutCurrentA), N(s.InputPowerW),
            N(s.ExternalTorqueNm), N(s.ExternalSpeedRpm), N(s.MeasuredPowerW), N(s.EfficiencyPct), s.ErrorCode, (uint)s.Interlocks, s.PlcHeartbeat);
        if (!_channel.Writer.TryWrite(line)) { _error = new IOException("记录队列已满，禁止丢样后继续运行"); CheckHealth(); }
    }
    public void Event(string code, string message)
    {
        CheckHealth();
        lock (_eventsGate)
        {
            try { _events.WriteLine(JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, code, message })); }
            catch (Exception ex) { _error = ex; throw new IOException("事件日志写入失败", ex); }
        }
    }
    public async Task SaveResultAsync(PointResult result, CancellationToken ct)
    {
        CheckHealth();
        // User-provided IDs never become filesystem path components.
        var path = Path.Combine(DirectoryPath, $"point_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, BenchConfig.Json), ct);
        Event("point_result", $"{result.PointId}: {result.Result} attempt={result.Attempt}");
    }
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete(); await _writer;
        lock (_eventsGate) _events.Dispose();
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "closed.json"), JsonSerializer.Serialize(new
        { endedUtc = DateTimeOffset.UtcNow, acquisitionGapsOver30Ms = _gaps, recordingError = _error?.Message }, BenchConfig.Json));
    }
}
