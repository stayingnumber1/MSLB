namespace MotorLoadBench.Domain;

public readonly record struct LoadedStartDecision(bool AllowTorque, bool ReverseTrip, string State);

/// <summary>Debounced, latched speed trigger for loaded-start when no DUT run signal is available.</summary>
public sealed class LoadedStartGate(double triggerRpm, int triggerConfirmMs,
    double reverseTripRpm, int reverseConfirmMs)
{
    private DateTimeOffset? _forwardSince;
    private DateTimeOffset? _reverseSince;
    private bool _latched;

    public void Reset()
    {
        _forwardSince = null;
        _reverseSince = null;
        _latched = false;
    }

    public LoadedStartDecision Update(double speedRpm, int expectedSign, DateTimeOffset now)
    {
        var signedSpeed = speedRpm * expectedSign;
        if (!_latched)
        {
            if (signedSpeed >= triggerRpm)
            {
                _forwardSince ??= now;
                if (now - _forwardSince.Value >= TimeSpan.FromMilliseconds(triggerConfirmMs))
                    _latched = true;
            }
            else
            {
                _forwardSince = null;
            }
            if (!_latched)
                return new(false, false, _forwardSince.HasValue ? "confirming" : "armed");
        }

        // Once loading is latched, zero crossing and ordinary startup shake do
        // not remove torque. Only a clear, persistent reverse motion trips it.
        if (signedSpeed <= -reverseTripRpm)
        {
            _reverseSince ??= now;
            if (now - _reverseSince.Value >= TimeSpan.FromMilliseconds(reverseConfirmMs))
                return new(false, true, "reverse_trip");
            return new(true, false, "reverse_confirming");
        }
        _reverseSince = null;
        return new(true, false, "loaded");
    }
}
