namespace MaximizeToVirtualDesktop;

/// <summary>
/// Small, side-effect-free state machine for one shared current-desktop probe.
/// A changed ID is reported immediately; the same ID must be observed twice
/// afterwards before consumers may treat the transition as settled.
/// </summary>
internal sealed class DesktopTransitionDetector
{
    private Guid? _current;
    private Guid? _pending;
    private int _pendingObservations;

    public DesktopTransitionSignal Observe(Guid? desktopId)
    {
        if (!desktopId.HasValue) return DesktopTransitionSignal.None;
        if (!_current.HasValue)
        {
            _current = desktopId;
            return DesktopTransitionSignal.None;
        }
        if (_current != desktopId)
        {
            _current = desktopId;
            _pending = desktopId;
            _pendingObservations = 0;
            return new DesktopTransitionSignal(desktopId, Changed: true, Settled: false);
        }
        if (_pending != desktopId) return DesktopTransitionSignal.None;
        _pendingObservations++;
        if (_pendingObservations < 2) return DesktopTransitionSignal.None;
        _pending = null;
        return new DesktopTransitionSignal(desktopId, Changed: false, Settled: true);
    }
}

internal readonly record struct DesktopTransitionSignal(
    Guid? DesktopId, bool Changed, bool Settled)
{
    public static DesktopTransitionSignal None => new(null, false, false);
}
