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

    /// <summary>
    /// A foreground-only observation is sufficient on ordinary desktops: the
    /// newly active window must be unpinned, while every already visible peer
    /// keeps its state. A full snapshot remains required for the two managed
    /// layouts where an activation can change coverage, and during a desktop
    /// transition where the event may describe a late compositor state.
    /// </summary>
    public static bool RequiresVisibleRelationshipReconciliation(
        bool isTransitioning,
        bool isFullscreenAnchor,
        bool isSnapWorkspaceMemberForeground) =>
        isTransitioning || isFullscreenAnchor || isSnapWorkspaceMemberForeground;
}
