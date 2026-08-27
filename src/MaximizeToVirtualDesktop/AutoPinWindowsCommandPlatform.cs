using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>Win32/COM adapter for AutoPinExecutor.</summary>
internal sealed class AutoPinWindowsCommandPlatform : IAutoPinCommandPlatform
{
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly SnapWorkspaceTracker _snapTracker;
    private readonly AnimationFreeWindowMinimizer _minimizer;

    public AutoPinWindowsCommandPlatform(VirtualDesktopService vds, FullScreenTracker tracker,
        SnapWorkspaceTracker snapTracker)
    {
        _vds = vds;
        _tracker = tracker;
        _snapTracker = snapTracker;
        _minimizer = new AnimationFreeWindowMinimizer(new Win32WindowMinimizeAdapter());
    }

    public bool CanMutate(AutoPinWindowIdentity identity) =>
        !_tracker.IsTracked(identity.Hwnd) && !_snapTracker.IsAttachedMember(identity.Hwnd);

    public bool IsSameLiveWindow(AutoPinWindowIdentity identity)
    {
        if (!NativeMethods.IsWindow(identity.Hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(identity.Hwnd, out var processId);
        return processId == identity.ProcessId;
    }

    public bool TryGetPinState(AutoPinWindowIdentity identity, out bool isPinned)
    {
        isPinned = false;
        return IsSameLiveWindow(identity)
            && _vds.TryGetWindowPinnedState(identity.Hwnd, out isPinned);
    }

    public bool TryMinimizeAndConfirm(AutoPinWindowIdentity identity) =>
        _minimizer.TryMinimizeAndConfirm(identity);

    public bool SetPinState(AutoPinWindowIdentity identity, bool shouldBePinned)
    {
        if (!IsSameLiveWindow(identity)) return false;
        var changed = shouldBePinned
            ? _vds.PinWindow(identity.Hwnd)
            : _vds.UnpinWindow(identity.Hwnd);
        return changed
            && TryGetPinState(identity, out var actualState)
            && actualState == shouldBePinned;
    }

    private sealed class Win32WindowMinimizeAdapter : IWindowMinimizeAdapter
    {
        public bool IsSameLiveWindow(AutoPinWindowIdentity identity)
        {
            if (!NativeMethods.IsWindow(identity.Hwnd)) return false;
            NativeMethods.GetWindowThreadProcessId(identity.Hwnd, out var processId);
            return processId == identity.ProcessId;
        }

        public bool SetTransitionsDisabled(nint hwnd, bool disabled) =>
            NativeMethods.SetWindowTransitionsDisabled(hwnd, disabled);

        public void Minimize(nint hwnd) =>
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);

        public void FlushComposition() => NativeMethods.DwmFlush();

        public bool IsMinimized(nint hwnd) => NativeMethods.IsIconic(hwnd);
    }
}
