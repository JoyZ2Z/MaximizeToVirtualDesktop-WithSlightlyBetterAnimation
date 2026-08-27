namespace MaximizeToVirtualDesktop;

/// <summary>Recognizes a caption double-click without performing any window action.</summary>
internal sealed class TitleBarDoubleClickTracker
{
    private nint _lastHwnd;
    private int _lastX;
    private int _lastY;
    private uint _lastTimestamp;

    public bool Observe(nint hwnd, int x, int y, uint timestamp,
        uint maximumDelayMs, int maximumDeltaX, int maximumDeltaY)
    {
        var elapsed = unchecked(timestamp - _lastTimestamp);
        var isDoubleClick = _lastHwnd == hwnd
            && elapsed <= maximumDelayMs
            && Math.Abs(x - _lastX) <= maximumDeltaX
            && Math.Abs(y - _lastY) <= maximumDeltaY;

        if (isDoubleClick)
        {
            Reset();
            return true;
        }

        _lastHwnd = hwnd;
        _lastX = x;
        _lastY = y;
        _lastTimestamp = timestamp;
        return false;
    }

    public void Reset()
    {
        _lastHwnd = nint.Zero;
        _lastX = 0;
        _lastY = 0;
        _lastTimestamp = 0;
    }
}
