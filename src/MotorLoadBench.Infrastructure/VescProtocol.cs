using System.Buffers.Binary;
using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public readonly record struct VescTelemetry(double FetTemperatureC, double MotorTemperatureC,
    double MotorCurrentA, double InputCurrentA, double DutyCycle, int ElectricalRpm,
    double InputVoltageV, byte FaultCode);

public static class VescProtocol
{
    // This host only exchanges compact control, identity and telemetry packets.
    // Keeping a bounded response size prevents a stray 0x03/0x04 in a damaged
    // stream from pinning the parser behind a fictitious multi-kilobyte frame.
    public const int MaxAcceptedPayloadLength = 512;
    public const byte CommFwVersion = 0;
    public const byte CommGetValues = 4;
    public const byte CommSetDuty = 5;
    public const byte CommSetCurrent = 6;
    public const byte CommSetCurrentBrake = 7;
    public const byte CommSetRpm = 8;
    public const byte CommSetPosition = 9;
    public const byte CommAppDisableOutput = 63;

    public static byte[] Request(byte command) => Frame([command]);

    // Public speed commands are mechanical RPM; the wire protocol uses electrical RPM.
    public static byte[] SetMechanicalRpm(double rpm, VescConfig config)
    {
        var limit = config.MaxRpm;
        if (!double.IsFinite(rpm) || Math.Abs(rpm) > limit)
            throw new ArgumentOutOfRangeException(nameof(rpm), $"转速范围为 ±{limit:F0} RPM。");
        return SetRpm(rpm, config.MotorPolePairs);
    }

    public static byte[] SetRpm(double mechanicalRpm, double electricalPerMechanical)
    {
        var electrical = checked((int)Math.Round(mechanicalRpm * electricalPerMechanical));
        Span<byte> payload = stackalloc byte[5];
        payload[0] = CommSetRpm;
        BinaryPrimitives.WriteInt32BigEndian(payload[1..], electrical);
        return Frame(payload);
    }

    public static byte[] SetCurrent(double amps)
        => SetScaled(CommSetCurrent, amps, 1000);

    public static byte[] SetBrakeCurrent(double amps)
        => SetScaled(CommSetCurrentBrake, amps, 1000);

    public static byte[] SetDuty(double duty)
        => SetScaled(CommSetDuty, duty, 100000);

    public static byte[] SetPosition(double degrees)
        => SetScaled(CommSetPosition, degrees, 1000000);

    public static byte[] DisableAppOutput(int timeMs = -1)
    {
        Span<byte> payload = stackalloc byte[6];
        payload[0] = CommAppDisableOutput;
        payload[1] = 0; // Do not forward over CAN.
        BinaryPrimitives.WriteInt32BigEndian(payload[2..], timeMs);
        return Frame(payload);
    }

    private static byte[] SetScaled(byte command, double value, double scale)
    {
        Span<byte> payload = stackalloc byte[5];
        payload[0] = command;
        BinaryPrimitives.WriteInt32BigEndian(payload[1..], checked((int)Math.Round(value * scale)));
        return Frame(payload);
    }

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[payload.Length + 5];
        frame[0] = 2;
        frame[1] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(2));
        var crc = Crc16(payload);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(payload.Length + 2), crc);
        frame[^1] = 3;
        return frame;
    }

    public static bool TryTakeFrame(List<byte> buffer, out byte[] payload)
    {
        payload = [];
        while (true)
        {
            while (buffer.Count > 0 && buffer[0] is not (2 or 3 or 4)) buffer.RemoveAt(0);
            if (!TryDescribe(buffer, 0, out var length, out var dataOffset, out var total)) return false;
            if (length is < 1 or > MaxAcceptedPayloadLength) { buffer.RemoveAt(0); continue; }
            if (buffer.Count < total)
            {
                // A damaged stream can expose a payload byte 0x03/0x04 as a
                // plausible long-frame header. Do not wait forever when a
                // complete CRC-valid frame is already present later in the buffer.
                var resync = FindValidatedFrame(buffer, 1);
                if (resync < 0) return false;
                buffer.RemoveRange(0, resync);
                continue;
            }
            if (buffer[total - 1] != 3) { buffer.RemoveAt(0); continue; }
            var candidate = buffer.GetRange(dataOffset, length).ToArray();
            var received = (ushort)((buffer[dataOffset + length] << 8) | buffer[dataOffset + length + 1]);
            if (received != Crc16(candidate)) { buffer.RemoveAt(0); continue; }
            buffer.RemoveRange(0, total);
            payload = candidate;
            return true;
        }
    }

    private static int FindValidatedFrame(List<byte> buffer, int startIndex)
    {
        for (var index = startIndex; index < buffer.Count; index++)
        {
            if (buffer[index] is not (2 or 3 or 4) ||
                !TryDescribe(buffer, index, out var length, out var dataOffset, out var total) ||
                length is < 1 or > MaxAcceptedPayloadLength || buffer.Count - index < total ||
                buffer[index + total - 1] != 3) continue;
            var candidate = buffer.GetRange(index + dataOffset, length).ToArray();
            var crcIndex = index + dataOffset + length;
            var received = (ushort)((buffer[crcIndex] << 8) | buffer[crcIndex + 1]);
            if (received == Crc16(candidate)) return index;
        }
        return -1;
    }

    private static bool TryDescribe(List<byte> buffer, int index, out int length, out int dataOffset, out int total)
    {
        length = dataOffset = total = 0;
        if (index >= buffer.Count) return false;
        switch (buffer[index])
        {
            case 2:
                if (buffer.Count - index < 2) return false;
                length = buffer[index + 1]; dataOffset = 2;
                break;
            case 3:
                if (buffer.Count - index < 3) return false;
                length = (buffer[index + 1] << 8) | buffer[index + 2]; dataOffset = 3;
                break;
            case 4:
                if (buffer.Count - index < 4) return false;
                length = (buffer[index + 1] << 16) | (buffer[index + 2] << 8) | buffer[index + 3]; dataOffset = 4;
                break;
            default:
                return false;
        }
        total = dataOffset + length + 3;
        return true;
    }

    public static bool TryDecodeValues(ReadOnlySpan<byte> payload, out VescTelemetry value)
    {
        value = default;
        if (payload.Length < 29 || payload[0] != CommGetValues) return false;
        var p = 1;
        var fet = ReadI16(payload, ref p) / 10.0;
        var motorTemp = ReadI16(payload, ref p) / 10.0;
        var motorCurrent = ReadI32(payload, ref p) / 100.0;
        var inputCurrent = ReadI32(payload, ref p) / 100.0;
        _ = ReadI32(payload, ref p); // average Id
        _ = ReadI32(payload, ref p); // average Iq
        var duty = ReadI16(payload, ref p) / 1000.0;
        var erpm = ReadI32(payload, ref p);
        var voltage = ReadI16(payload, ref p) / 10.0;
        // Legacy GET_VALUES places fault after 4 energy counters and tachometers.
        var fault = payload.Length > 53 ? payload[53] : (byte)0;
        value = new(fet, motorTemp, motorCurrent, inputCurrent, duty, erpm, voltage, fault);
        return true;
    }

    private static short ReadI16(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt16BigEndian(data[offset..]); offset += 2; return value;
    }

    private static int ReadI32(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(data[offset..]); offset += 4; return value;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
