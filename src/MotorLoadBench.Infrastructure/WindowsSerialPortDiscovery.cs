using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MotorLoadBench.Infrastructure;

public static class WindowsSerialPortDiscovery
{
    public sealed record PortInfo(string PortName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public static IReadOnlyList<string> GetAllPorts()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SetupAPI with DIGCF_PRESENT is authoritative on Windows. The
        // SERIALCOMM registry key and SerialPort.GetPortNames can retain a
        // phantom COM name while a USB CDC device is re-enumerating.
        if (OperatingSystem.IsWindows())
        {
            var present = GetFriendlyNames();
            if (present.Count > 0) return OrderPorts(present.Keys);
        }
        try
        {
            foreach (var port in SerialPort.GetPortNames()) Add(ports, port);
        }
        catch { }

        return OrderPorts(ports);
    }

    public static IReadOnlyList<PortInfo> GetAllPortInfos()
    {
        var names = OperatingSystem.IsWindows() ? GetFriendlyNames() : new Dictionary<string, string>();
        var ports = names.Count > 0 ? OrderPorts(names.Keys) : GetAllPorts();
        return ports.Select(port => new PortInfo(port,
            names.TryGetValue(port, out var friendly) ? friendly : port)).ToArray();
    }

    public static IReadOnlyList<string> OrderPorts(IEnumerable<string> ports) => ports
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(PortNumber)
        .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void Add(ISet<string> ports, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            ports.Add(value.Trim());
    }

    private static int PortNumber(string value) =>
        value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(value.AsSpan(3), out var number) ? number : int.MaxValue;

    private static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var portsClass = new Guid("4D36E978-E325-11CE-BFC1-08002BE10318");
        var set = SetupDiGetClassDevs(ref portsClass, IntPtr.Zero, IntPtr.Zero, 0x02);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                var buffer = new byte[1024];
                if (!SetupDiGetDeviceRegistryProperty(set, ref data, 12, out _, buffer,
                        (uint)buffer.Length, out _)) continue;
                var friendly = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                var match = Regex.Match(friendly, @"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase);
                if (match.Success) result[match.Groups[1].Value.ToUpperInvariant()] = friendly;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SpDevInfoData data,
        uint property, out uint propertyType, byte[] buffer, uint bufferSize, out uint requiredSize);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
