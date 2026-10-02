using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MotorLoadBench.Infrastructure;

// Optional, non-blocking host-side timing trace. The serial worker only enqueues
// samples; JSON encoding and disk I/O run on a separate task.
internal sealed class VescTimingTrace : IAsyncDisposable
{
    private readonly Channel<Sample> _samples = Channel.CreateBounded<Sample>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Task _writer;
    private long _dropped;

    private readonly record struct Sample(
        DateTimeOffset HostUtc, long MonoTicks, string Phase,
        long RequestSeq, long Value0, long Value1, byte[]? Bytes);

    public string Path { get; }

    private VescTimingTrace(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory,
            $"vesc_timing_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.jsonl");
        _writer = Task.Run(WriteAsync);
        Record("trace_start", 0, Stopwatch.Frequency);
    }

    public static VescTimingTrace? TryCreate()
    {
        var directory = Environment.GetEnvironmentVariable("MSLB_VESC_TIMING_DIR");
        return string.IsNullOrWhiteSpace(directory) ? null : new VescTimingTrace(directory);
    }

    public void Record(string phase, long requestSeq = 0, long value0 = 0, long value1 = 0)
    {
        var sample = new Sample(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
            phase, requestSeq, value0, value1, null);
        if (!_samples.Writer.TryWrite(sample)) Interlocked.Increment(ref _dropped);
    }

    public void RecordRead(long requestSeq, byte[] buffer, int count, int pendingCount)
    {
        var hostUtc = DateTimeOffset.UtcNow;
        var monoTicks = Stopwatch.GetTimestamp();
        var bytes = new byte[count];
        Buffer.BlockCopy(buffer, 0, bytes, 0, count);
        var sample = new Sample(hostUtc, monoTicks, "serial_read_done",
            requestSeq, count, pendingCount, bytes);
        if (!_samples.Writer.TryWrite(sample)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteAsync()
    {
        await using var stream = new StreamWriter(Path, false, new UTF8Encoding(false));
        var sinceFlush = 0;
        await foreach (var sample in _samples.Reader.ReadAllAsync())
        {
            await stream.WriteLineAsync(JsonSerializer.Serialize(new
            {
                hostUtc = sample.HostUtc,
                monoTicks = sample.MonoTicks,
                phase = sample.Phase,
                requestSeq = sample.RequestSeq,
                value0 = sample.Value0,
                value1 = sample.Value1,
                bytesHex = sample.Bytes is null ? null : Convert.ToHexString(sample.Bytes)
            }));
            if (++sinceFlush >= 64)
            {
                await stream.FlushAsync();
                sinceFlush = 0;
            }
        }
        await stream.WriteLineAsync(JsonSerializer.Serialize(new
        {
            hostUtc = DateTimeOffset.UtcNow,
            monoTicks = Stopwatch.GetTimestamp(),
            phase = "trace_end",
            dropped = Interlocked.Read(ref _dropped)
        }));
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _samples.Writer.TryComplete();
        await _writer;
    }
}
