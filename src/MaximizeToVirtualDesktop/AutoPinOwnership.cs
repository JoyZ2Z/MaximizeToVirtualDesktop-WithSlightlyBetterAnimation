namespace MaximizeToVirtualDesktop;

/// <summary>Stores the pre-AutoPin pin state for a specific live window identity.</summary>
internal sealed class AutoPinOwnership
{
    private readonly Dictionary<nint, AutoPinBaseline> _baselines = new();
    private readonly object _sync = new();

    public IReadOnlyCollection<AutoPinBaseline> ManagedWindows
    {
        get { lock (_sync) return _baselines.Values.ToArray(); }
    }

    public void CaptureInitialPinState(nint hwnd, int processId, bool isPinned)
    {
        lock (_sync)
        {
            if (_baselines.TryGetValue(hwnd, out var existing)
                && existing.ProcessId == processId)
            {
                return;
            }

            // Reused HWND: the new process receives its own baseline.
            _baselines[hwnd] = new AutoPinBaseline(hwnd, processId, isPinned);
        }
    }

    public void Forget(nint hwnd)
    {
        lock (_sync) _baselines.Remove(hwnd);
    }

    public void Clear()
    {
        lock (_sync) _baselines.Clear();
    }

}

internal readonly record struct AutoPinWindowIdentity(nint Hwnd, int ProcessId);

internal sealed record AutoPinBaseline(nint Hwnd, int ProcessId, bool WasPinned);
