namespace MaximizeToVirtualDesktop;

internal enum AutoPinTransitionSource { Internal, External }

/// <summary>
/// Keeps AutoPin writes behind the visual desktop transition. External switches
/// need a quiet period because COM desktop identity can settle before DWM does.
/// </summary>
internal sealed class AutoPinTransitionBarrier
{
    internal const int InternalQuietMs = 100;
    internal const int ExternalQuietMs = 350;

    private readonly object _sync = new();
    private bool _active;
    private AutoPinTransitionSource _source;
    private DateTime _notBefore;

    public bool IsActive
    {
        get { lock (_sync) return _active; }
    }

    public void Begin(DateTime now, AutoPinTransitionSource source)
    {
        lock (_sync)
        {
            _active = true;
            _source = source;
            _notBefore = now.AddMilliseconds(
                source == AutoPinTransitionSource.External
                    ? ExternalQuietMs
                    : InternalQuietMs);
        }
    }

    public void ObserveActivity(DateTime now)
    {
        lock (_sync)
        {
            if (!_active || _source != AutoPinTransitionSource.External) return;
            _notBefore = now.AddMilliseconds(ExternalQuietMs);
        }
    }

    public bool CanObserveDestination(DateTime now, bool destinationReady)
    {
        lock (_sync)
            return _active && destinationReady && now >= _notBefore;
    }

    public void Complete()
    {
        lock (_sync) _active = false;
    }
}
