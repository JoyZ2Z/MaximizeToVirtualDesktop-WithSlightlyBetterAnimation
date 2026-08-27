namespace MaximizeToVirtualDesktop;

/// <summary>Chooses foreground events that need a full desktop observation now.</summary>
internal static class AutoPinForegroundEventPolicy
{
    public static bool RequiresImmediateFullObservation(
        bool isTransitioning,
        bool isFullscreenAnchor,
        bool isSnapWorkspaceMemberForeground)
    {
        // AutoPin writes are guarded by the observed current desktop, not by
        // the desktop-animation lifetime. An anchor activation during that
        // animation is exactly when a covered window needs its new state.
        _ = isTransitioning;
        return isFullscreenAnchor || isSnapWorkspaceMemberForeground;
    }
}
