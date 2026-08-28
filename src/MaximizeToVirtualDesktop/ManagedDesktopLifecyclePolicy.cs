namespace MaximizeToVirtualDesktop;

internal enum ManagedDesktopKind
{
    Unmanaged,
    Fullscreen,
    Snap,
}

internal enum FullscreenExitDecision
{
    None,
    Restore,
    RestoreMinimized,
    PromoteToSnap,
}

/// <summary>
/// Pure lifecycle rules for a desktop whose fullscreen anchor changed state.
/// Timing and Win32 observation stay outside; callers execute only the returned
/// decision after their debounce interval has elapsed.
/// </summary>
internal static class FullscreenExitPolicy
{
    public static TimeSpan ArbitrationDelay => TimeSpan.FromMilliseconds(300);

    public static FullscreenExitDecision DecideAfterDelay(
        bool isStillFullscreen, bool isMinimized, bool isArranged, bool isHidden = false)
    {
        // Tray applications often remain maximized while hiding their top-level
        // window. Treat that state like a minimized exit so the managed desktop
        // is released without forcing the application visible again.
        if (isHidden) return FullscreenExitDecision.RestoreMinimized;
        if (isStillFullscreen) return FullscreenExitDecision.None;
        if (isMinimized) return FullscreenExitDecision.RestoreMinimized;
        return isArranged
            ? FullscreenExitDecision.PromoteToSnap
            : FullscreenExitDecision.Restore;
    }

    public static bool ShouldDeleteDesktop(FullscreenExitDecision decision) =>
        decision is FullscreenExitDecision.Restore
            or FullscreenExitDecision.RestoreMinimized;
}

/// <summary>
/// Defines the exclusive desktop-kind invariant shared by fullscreen and Snap
/// tracking. A desktop with two owners is invalid and must fail closed.
/// </summary>
internal static class ManagedDesktopKindPolicy
{
    public static bool TryResolve(
        bool hasFullscreenOwner,
        bool hasSnapOwner,
        out ManagedDesktopKind kind)
    {
        if (hasFullscreenOwner && hasSnapOwner)
        {
            kind = ManagedDesktopKind.Unmanaged;
            return false;
        }

        kind = hasSnapOwner
            ? ManagedDesktopKind.Snap
            : hasFullscreenOwner
                ? ManagedDesktopKind.Fullscreen
                : ManagedDesktopKind.Unmanaged;
        return true;
    }
}
