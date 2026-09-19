using MotorLoadBench.Domain;

namespace MotorLoadBench.Application;

// Deterministic simulation/reference model. The physical safety implementation belongs in the PLC.
public sealed class RealtimeModel(BenchConfig config)
{
    private BenchCommand _command = new();
    private uint? _lastHeartbeat;
    private double _sinceHeartbeat, _overTorque, _peakTime, _cooldown;
    private bool _enableEdge, _resetEdge, _disable, _stop;
    private uint _tick;
    public bool ServoOn { get; private set; }
    public double Target { get; private set; }
    public Interlock Fault { get; private set; }
    public BenchState State { get; private set; } = BenchState.DriveReady;
    public void Receive(BenchCommand c)
    {
        if (c.Heartbeat != _lastHeartbeat) { _lastHeartbeat = c.Heartbeat; _sinceHeartbeat = 0; }
        _enableEdge |= c.EnableRequest && !_command.EnableRequest;
        _resetEdge |= c.ResetFault && !_command.ResetFault;
        _command = c;
        if (c.DisableRequest) { _disable = true; _stop = true; }
        if (c.StopRequest) _stop = true;
    }
    public BenchSnapshot Tick(double dt, double rpm, double measuredTorque, double motorTemp = 25,
        double brakeTemp = 25, double dcBus = 310, Interlock external = Interlock.None)
    {
        var l = config.Limits;
        _sinceHeartbeat += dt;
        var active = external;
        if (_sinceHeartbeat * 1000 > l.HeartbeatTimeoutMs) active |= Interlock.Heartbeat;
        if (!double.IsFinite(rpm) || !double.IsFinite(measuredTorque) || !double.IsFinite(motorTemp) || !double.IsFinite(dcBus) ||
            !double.IsFinite(brakeTemp) || !double.IsFinite(_command.TargetTorqueNm) || _command.TargetTorqueNm < 0 ||
            !double.IsFinite(_command.TargetPowerW) || _command.TargetPowerW < 0 ||
            !double.IsFinite(_command.TorqueRampNmPerSec) || _command.TorqueRampNmPerSec <= 0) active |= Interlock.Invalid;
        if (Math.Abs(rpm) > l.MaxSpeedRpm) active |= Interlock.Overspeed;
        if (motorTemp >= l.MotorTripC || brakeTemp >= l.BrakeTripC) active |= Interlock.Overtemp;
        if (l.DcBusTripV is { } max && dcBus >= max) active |= Interlock.Overvoltage;
        if (Math.Abs(rpm) >= l.MinLoadSpeedRpm && Math.Sign(rpm) != config.Drive.ExpectedRotationSign) active |= Interlock.Direction;
        _overTorque = Math.Abs(measuredTorque) > l.MaxTorqueNm + l.OverTorqueMarginNm ? _overTorque + dt : 0;
        if (_overTorque * 1000 >= l.OverTorqueDelayMs) active |= Interlock.OverTorque;
        if (_resetEdge && active == Interlock.None && Math.Abs(measuredTorque) <= l.ZeroTorqueNm && !ServoOn)
        { Fault = Interlock.None; _command = new(TorqueRampNmPerSec: l.StopRampNmPerSec, Heartbeat: _command.Heartbeat); _stop = true; }
        Fault |= active;
        if (_enableEdge && Fault == Interlock.None && Math.Abs(measuredTorque) <= l.ZeroTorqueNm)
        { ServoOn = true; _disable = false; _stop = false; }
        _enableEdge = _resetEdge = false;
        if (Fault != Interlock.None) { _disable = true; _stop = true; }
        double demand = 0;
        if (ServoOn && !_stop) demand = LoadMath.Demand(_command, config, rpm, motorTemp);
        if (Math.Abs(demand) > l.ContinuousTorqueNm)
        {
            if (_cooldown > 0) demand = Math.CopySign(l.ContinuousTorqueNm, demand);
            else _peakTime += dt;
            if (_peakTime >= l.PeakSeconds) { Fault |= Interlock.PeakTimeout; _disable = _stop = true; demand = 0; _cooldown = l.PeakCooldownSeconds; }
        }
        else if (_peakTime > 0) { _cooldown = l.PeakCooldownSeconds; _peakTime = 0; }
        _cooldown = Math.Max(0, _cooldown - dt);
        // Electrical/mechanical severe trips do not ramp through unsafe conditions.
        bool immediate = (Fault & ~Interlock.Heartbeat) != Interlock.None || Math.Abs(rpm) < l.MinLoadSpeedRpm;
        if (immediate) Target = 0;
        else Target = LoadMath.Slew(Target, demand, _stop ? l.StopRampNmPerSec : Math.Min(_command.TorqueRampNmPerSec, l.MaxRampNmPerSec), dt);
        if (_disable && Math.Abs(Target) <= l.ZeroTorqueNm && Math.Abs(measuredTorque) <= l.ZeroTorqueNm) ServoOn = false;
        if (!ServoOn) Target = 0;
        State = Fault != 0 ? BenchState.Fault : !ServoOn ? BenchState.DriveReady :
            _stop && Math.Abs(measuredTorque) > l.ZeroTorqueNm ? BenchState.RampDown :
            Math.Abs(Target) <= l.ZeroTorqueNm ? BenchState.ServoOnIdle : Math.Abs(Target - demand) > .001 ? BenchState.RampUp : BenchState.Running;
        return new() { Connected = true, EtherCatOnline = !external.HasFlag(Interlock.EtherCat), DriveReady = Fault == 0,
            ServoOn = ServoOn, TargetTorqueNm = Target, ActualTorqueNm = measuredTorque, SpeedRpm = rpm, MotorTempC = motorTemp,
            BrakeTempC = brakeTemp, DcBusV = dcBus, Interlocks = Fault, State = State, PlcHeartbeat = ++_tick };
    }
    public void StartLoad() { if (Fault == 0 && ServoOn && !_disable) _stop = false; }
}
