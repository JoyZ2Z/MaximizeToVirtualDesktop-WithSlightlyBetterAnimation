namespace MaximizeToVirtualDesktop;

/// <summary>
/// Separates transition-produced foreground observations from stable desktop
/// activations. WinEvent is the primary signal; polling only repairs a missed
/// stable foreground change.
/// </summary>
internal sealed class AutoPinForegroundTracker
{
    private const int MaximumProtectionRetries = 25;
    private const int SlowRetryIntervalMs = 1000;
    private readonly object _sync = new();
    private nint _stableForeground;
    private nint _lastReportedForeground;
    private nint _pendingProtection;
    private int _remainingProtectionRetries;
    private long _nextSlowRetryAt;
    private bool _transitioning;

    public void Reset(nint foreground)
    {
        lock (_sync)
        {
            _stableForeground = foreground;
            _lastReportedForeground = foreground;
            _pendingProtection = nint.Zero;
            _remainingProtectionRetries = 0;
            _nextSlowRetryAt = 0;
            _transitioning = false;
        }
    }

    public void RecordEvent(nint foreground)
    {
        lock (_sync) _lastReportedForeground = foreground;
    }

    public nint LastReportedForeground
    {
        get
        {
            lock (_sync) return _lastReportedForeground;
        }
    }

    public void BeginTransition()
    {
        lock (_sync) _transitioning = true;
    }

    public void CompleteTransition(nint foreground)
    {
        lock (_sync)
        {
            _stableForeground = foreground;
            _lastReportedForeground = foreground;
            _pendingProtection = nint.Zero;
            _remainingProtectionRetries = 0;
            _nextSlowRetryAt = 0;
            _transitioning = false;
        }
    }

    public void ConfirmProtection(nint foreground)
    {
        lock (_sync)
        {
            if (_pendingProtection != foreground) return;
            _pendingProtection = nint.Zero;
            _remainingProtectionRetries = 0;
            _nextSlowRetryAt = 0;
        }
    }

    public void RequestProtection(nint foreground)
    {
        if (foreground == nint.Zero) return;
        lock (_sync)
        {
            if (_transitioning) return;
            _stableForeground = foreground;
            _lastReportedForeground = foreground;
            _pendingProtection = foreground;
            _remainingProtectionRetries = MaximumProtectionRetries;
            _nextSlowRetryAt = 0;
        }
    }

    public nint? ObserveEvent(nint foreground) => ObserveStableChange(foreground);

    public nint? ObservePoll(nint foreground) => ObserveStableChange(foreground);

    public nint? ObservePoll(nint foreground, long now) =>
        ObserveStableChange(foreground, now);

    private nint? ObserveStableChange(nint foreground) =>
        ObserveStableChange(foreground, Environment.TickCount64);

    private nint? ObserveStableChange(nint foreground, long now)
    {
        lock (_sync)
        {
            if (_transitioning) return null;
            if (foreground != _stableForeground)
            {
                _stableForeground = foreground;
                _lastReportedForeground = foreground;
                _pendingProtection = foreground;
                _remainingProtectionRetries = MaximumProtectionRetries;
                _nextSlowRetryAt = 0;
                return foreground;
            }

            if (foreground == nint.Zero || foreground != _pendingProtection)
            {
                return null;
            }

            if (_remainingProtectionRetries > 0)
            {
                _remainingProtectionRetries--;
                if (_remainingProtectionRetries == 0)
                    _nextSlowRetryAt = now + SlowRetryIntervalMs;
                return foreground;
            }

            if (now < _nextSlowRetryAt) return null;
            _nextSlowRetryAt = now + SlowRetryIntervalMs;
            return foreground;
        }
    }
}
