namespace MotorLoadBench.Infrastructure;

public static class VescEmergencyStop
{
    public const int FrameCount = 3;
    public const int FrameIntervalMs = 10;

    public static async Task SendAsync(Func<byte[], CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        Exception? firstFailure = null;
        var successfulFrames = 0;
        var frame = VescProtocol.SetCurrent(0);
        for (var i = 0; i < FrameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await write(frame, ct);
                successfulFrames++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }

            if (i + 1 < FrameCount) await Task.Delay(FrameIntervalMs, ct);
        }

        if (successfulFrames != FrameCount)
            throw new IOException($"VESC 零电流紧急停机帧仅成功发送 {successfulFrames}/{FrameCount} 次。", firstFailure);
    }
}
