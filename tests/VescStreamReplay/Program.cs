using System.Text.Json;
using MotorLoadBench.Infrastructure;

static List<byte[]> Decode(IEnumerable<byte[]> chunks)
{
    var pending = new List<byte>();
    var result = new List<byte[]>();
    foreach (var chunk in chunks)
    {
        pending.AddRange(chunk);
        while (VescProtocol.TryTakeFrame(pending, out var payload)) result.Add(payload);
    }
    return result;
}

var body = new byte[73]; body[0] = 4;
for (var i = 1; i < body.Length; ++i) body[i] = (byte)(i * 17);
var frame = VescProtocol.Frame(body);
var bad = frame.ToArray(); bad[20] ^= 1;
var tails = Enumerable.Repeat(frame[64..], 7).ToArray();
var cases = new[] {
    new[] { frame[..64], frame[64..] },
    frame.Select(b => new[] { b }).ToArray(),
    new[] { bad, frame[..31], frame[31..] },
    tails.Concat(new[] { frame[..64], frame[64..] }).ToArray(),
    new[] { frame[..64].Concat(frame).ToArray() }
};
foreach (var chunks in cases)
{
    var result = Decode(chunks);
    if (result.Count != 1 || !result[0].SequenceEqual(body)) throw new Exception("Frame boundary/CRC resync regression");
}
if (args.Length != 1) throw new ArgumentException("Supply exported chunks.jsonl (offline only)");
var buffer = new List<byte>();
var frames = new List<object>();
var count = 0;
foreach (var line in File.ReadLines(args[0]))
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    buffer.AddRange(Convert.FromHexString(root.GetProperty("hex").GetString()!));
    while (VescProtocol.TryTakeFrame(buffer, out var payload))
    {
        count++;
        frames.Add(new { chunk=root.GetProperty("index").GetInt32(), us=root.GetProperty("us").GetInt64(),
            command=payload[0], length=payload.Length, telemetry=VescProtocol.TryDecodeValues(payload, out _) });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { syntheticCases=cases.Length, validFrames=count, pending=buffer.Count, frames }));
