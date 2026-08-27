namespace MaximizeToVirtualDesktop;

/// <summary>
/// Owns the complete minimize transaction so callers cannot accidentally bypass
/// its transition policy or accept a stale HWND.
/// </summary>
internal sealed class AnimationFreeWindowMinimizer
{
    private readonly IWindowMinimizeAdapter _adapter;

    public AnimationFreeWindowMinimizer(IWindowMinimizeAdapter adapter) =>
        _adapter = adapter;

    public bool TryMinimizeAndConfirm(AutoPinWindowIdentity identity)
    {
        if (!_adapter.IsSameLiveWindow(identity)) return false;
        var transitionsDisabled = _adapter.SetTransitionsDisabled(
            identity.Hwnd, disabled: true);
        try
        {
            _adapter.Minimize(identity.Hwnd);
            if (transitionsDisabled) _adapter.FlushComposition();
            return _adapter.IsSameLiveWindow(identity)
                && _adapter.IsMinimized(identity.Hwnd);
        }
        finally
        {
            if (transitionsDisabled)
                _adapter.SetTransitionsDisabled(identity.Hwnd, disabled: false);
        }
    }
}

internal interface IWindowMinimizeAdapter
{
    bool IsSameLiveWindow(AutoPinWindowIdentity identity);
    bool SetTransitionsDisabled(nint hwnd, bool disabled);
    void Minimize(nint hwnd);
    void FlushComposition();
    bool IsMinimized(nint hwnd);
}
