using MotorLoadBench.Application;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public sealed class NullSessionRecorder : ISessionRecorder
{
    public string DirectoryPath => "";
    public void Record(BenchSnapshot snapshot, string pointId) { }
    public void Event(string code, string message) { }
    public Task SaveResultAsync(PointResult result, CancellationToken ct) => Task.CompletedTask;
    public void CheckHealth() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
