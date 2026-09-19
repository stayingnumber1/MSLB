using System.Runtime.InteropServices;
using MotorLoadBench.Application;
using MotorLoadBench.Domain;
using TwinCAT.Ads;

namespace MotorLoadBench.Infrastructure;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CommandWire
{
    public uint ProtocolVersion, ProfileFingerprint;
    public byte Enable, Disable, Reset, Stop;
    public short Mode;
    public double TorqueNm, RampNmPerSec, PowerW;
    public uint Heartbeat;
}
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StatusWire
{
    public uint RevisionStart, ProtocolVersion, ProfileFingerprint;
    public short State;
    public byte EtherCatOnline, DriveReady, ServoOn, Reserved;
    public double TargetTorqueNm, ActualTorqueNm, SpeedRpm, DcBusV, MotorTempC, BrakeTempC, ServoCurrentA;
    public double ExternalTorqueNm, ExternalSpeedRpm, DutBusV, DutCurrentA;
    public uint ValidMask, ErrorCode, InterlockMask, Heartbeat, RevisionEnd;
}
public sealed class AdsRealtimeBridge(BenchConfig config, uint profileFingerprint) : IRealtimeBridge
{
    private AdsClient? _client;
    private uint _commandHandle, _commitHandle, _statusHandle, _commit;
    private bool _commissioningMatched;
    public bool IsSimulation => false;
    public bool CanWrite => config.Drive.AllowHardwareWrites && _commissioningMatched;
    public Task ConnectAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        Close();
        var client = new AdsClient { Timeout = config.Ads.TimeoutMs };
        try
        {
            client.Connect(config.Ads.AmsNetId, config.Ads.Port);
            if (client.ReadState().AdsState != AdsState.Run) throw new InvalidOperationException("TwinCAT PLC未处于RUN");
            _statusHandle = client.CreateVariableHandle(config.Ads.StatusSymbol);
            var status = (StatusWire)client.ReadAny(_statusHandle, typeof(StatusWire));
            if (status.ProtocolVersion != 1) throw new InvalidDataException("PLC/上位机结构版本不一致");
            _commissioningMatched = status.ProfileFingerprint == profileFingerprint;
            if (config.Drive.AllowHardwareWrites)
            {
                config.ValidateHardware();
                if (!_commissioningMatched) throw new InvalidDataException("PLC配置指纹不匹配，禁止写入");
                _commandHandle = client.CreateVariableHandle(config.Ads.CommandSymbol);
                _commitHandle = client.CreateVariableHandle(config.Ads.CommitSymbol);
                _commit = (uint)client.ReadAny(_commitHandle, typeof(uint));
            }
            _client = client;
        }
        catch { client.Dispose(); throw; }
    }, ct);
    public Task<BenchSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client ?? throw new InvalidOperationException("ADS尚未连接");
        var s = (StatusWire)client.ReadAny(_statusHandle, typeof(StatusWire));
        if (s.ProtocolVersion != 1 || s.RevisionStart != s.RevisionEnd || (s.RevisionStart & 1) != 0)
            throw new InvalidDataException("PLC快照不完整或协议版本错误");
        if (config.Drive.AllowHardwareWrites && s.ProfileFingerprint != profileFingerprint)
            throw new InvalidDataException("PLC配置发生变化");
        double? V(uint bit, double value) => (s.ValidMask & bit) != 0 ? value : null;
        return Task.FromResult(new BenchSnapshot
        {
            Connected = true, State = Enum.IsDefined(typeof(BenchState), (int)s.State) ? (BenchState)s.State : BenchState.Fault,
            EtherCatOnline = s.EtherCatOnline == 1, DriveReady = s.DriveReady == 1, ServoOn = s.ServoOn == 1,
            TargetTorqueNm = s.TargetTorqueNm, ActualTorqueNm = s.ActualTorqueNm, SpeedRpm = s.SpeedRpm,
            DcBusV = V(1, s.DcBusV), MotorTempC = V(2, s.MotorTempC), BrakeTempC = V(4, s.BrakeTempC),
            ServoCurrentA = V(8, s.ServoCurrentA), ExternalTorqueNm = V(16, s.ExternalTorqueNm), ExternalSpeedRpm = V(16, s.ExternalSpeedRpm),
            ExternalHealthy = (s.ValidMask & 16) != 0, DutBusV = V(32, s.DutBusV), DutCurrentA = V(32, s.DutCurrentA),
            ErrorCode = s.ErrorCode, Interlocks = (Interlock)s.InterlockMask, PlcHeartbeat = s.Heartbeat
        });
    }
    public Task WriteCommandAsync(BenchCommand c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanWrite) throw new InvalidOperationException("ADS只读/配置未核实，写入被阻止");
        var client = _client ?? throw new InvalidOperationException("ADS离线");
        var wire = new CommandWire
        {
            ProtocolVersion = 1, ProfileFingerprint = profileFingerprint,
            Enable = c.EnableRequest ? (byte)1 : (byte)0, Disable = c.DisableRequest ? (byte)1 : (byte)0,
            Reset = c.ResetFault ? (byte)1 : (byte)0, Stop = c.StopRequest ? (byte)1 : (byte)0,
            Mode = (short)c.Mode, TorqueNm = c.TargetTorqueNm, RampNmPerSec = c.TorqueRampNmPerSec,
            PowerW = c.TargetPowerW, Heartbeat = c.Heartbeat
        };
        client.WriteAny(_commandHandle, wire);
        client.WriteAny(_commitHandle, ++_commit);
        return Task.CompletedTask;
    }
    private void Close()
    {
        if (_client != null)
        {
            foreach (var h in new[] { _statusHandle, _commandHandle, _commitHandle }.Where(h => h != 0))
                try { _client.DeleteVariableHandle(h); } catch (AdsErrorException) { }
            _client.Dispose(); _client = null;
        }
        _statusHandle = _commandHandle = _commitHandle = 0; _commissioningMatched = false;
    }
    public Task DisconnectAsync() { Close(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
