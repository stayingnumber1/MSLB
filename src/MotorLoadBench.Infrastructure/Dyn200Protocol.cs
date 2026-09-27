using MotorLoadBench.Domain;

namespace MotorLoadBench.Infrastructure;

public readonly record struct Dyn200Sample(double TorqueNm, double? SpeedRpm);

public static class Dyn200Protocol
{
    public static bool TryDecode(ReadOnlySpan<byte> frame, TorqueSensorConfig config, out Dyn200Sample sample)
    {
        sample = default;
        return config.Protocol switch
        {
            TorqueSensorProtocol.Hex6 => TryHex6(frame, config, out sample),
            TorqueSensorProtocol.Hex8 => TryHex8(frame, config, out sample),
            TorqueSensorProtocol.Ascii => TryAscii(frame, config, out sample),
            _ => false
        };
    }

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xa001 : crc >> 1);
        }
        return crc;
    }

    private static bool ValidCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3) return false;
        var crc = Crc16(frame[..^2]);
        return frame[^2] == (byte)crc && frame[^1] == (byte)(crc >> 8);
    }

    private static bool TryHex6(ReadOnlySpan<byte> f, TorqueSensorConfig c, out Dyn200Sample s)
    {
        s = default;
        if (f.Length != 6 || !ValidCrc(f)) return false;
        var torqueRaw = (f[0] << 8) | f[1];
        if ((f[2] & 0x80) != 0) torqueRaw = -torqueRaw;
        var speedRaw = ((f[2] & 0x7f) << 8) | f[3];
        s = new(c.DirectionSign * torqueRaw / Pow10(c.TorqueDecimals), speedRaw / Pow10(c.SpeedDecimals));
        return true;
    }

    private static bool TryHex8(ReadOnlySpan<byte> f, TorqueSensorConfig c, out Dyn200Sample s)
    {
        s = default;
        if (f.Length != 8 || !ValidCrc(f)) return false;
        var torqueRaw = (f[0] << 16) | (f[1] << 8) | f[2];
        if ((torqueRaw & 0x800000) != 0) torqueRaw |= unchecked((int)0xff000000);
        var speedRaw = (f[3] << 16) | (f[4] << 8) | f[5];
        s = new(c.DirectionSign * torqueRaw / Pow10(c.TorqueDecimals), speedRaw / Pow10(c.SpeedDecimals));
        return true;
    }

    private static bool TryAscii(ReadOnlySpan<byte> f, TorqueSensorConfig c, out Dyn200Sample s)
    {
        s = default;
        if (f.Length < 2 || f[^1] != 0x0d) return false;
        if (!double.TryParse(System.Text.Encoding.ASCII.GetString(f[..^1]),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var torque)) return false;
        s = new(c.DirectionSign * torque, null);
        return double.IsFinite(torque);
    }

    private static double Pow10(int decimals) => Math.Pow(10, decimals);
}
