namespace MaximizeToVirtualDesktop;

/// <summary>Classifies WinEvents that cannot wait for the periodic AutoPin scan.</summary>
internal static class AutoPinWindowEventPolicy
{
    internal const uint MinimizeStart = 0x0016;
    internal const uint MinimizeEnd = 0x0017;
    internal const uint ObjectCreate = 0x8000;
    internal const uint ObjectShow = 0x8002;
    internal const uint ObjectHide = 0x8003;
    internal const uint ObjectReorder = 0x8004;
    internal const uint WindowStateChange = 0x800A;
    internal const uint MoveSizeEnd = 0x000B;

    public static bool RequiresImmediateOpenReconciliation(uint eventType) =>
        eventType is ObjectCreate or ObjectShow;

    /// <summary>
    /// CREATE/SHOW is delivered for every top-level window, including background
    /// shell and helper windows. Only an event from the current foreground family
    /// can help establish a new user activation; a background event must not queue
    /// a foreground reconciliation or a full-window scan.
    /// </summary>
    public static bool ShouldReconcileForegroundOnOpen(
        uint eventType,
        nint eventWindow,
        nint foregroundWindow,
        Func<nint, nint, bool> isSameWindowFamily) =>
        RequiresImmediateOpenReconciliation(eventType)
        && foregroundWindow != nint.Zero
        && isSameWindowFamily(eventWindow, foregroundWindow);

    public static bool RequiresImmediateReconciliation(uint eventType) => eventType is
        MinimizeStart or MinimizeEnd or WindowStateChange;

    /// <summary>
    /// Changes that can alter which ordinary windows are visible above or below
    /// one another. The service coalesces bursts into one current-desktop-only
    /// snapshot, rather than applying a command per raw WinEvent.
    /// </summary>
    public static bool AffectsWindowVisibilityRelationship(uint eventType) => eventType is
        ObjectCreate or ObjectShow or ObjectHide or ObjectReorder or
        WindowStateChange or MoveSizeEnd or MinimizeStart or MinimizeEnd;

    public static bool ShouldPinBeforeDesktopStability(
        uint eventType, bool isMinimized) =>
        isMinimized && eventType is MinimizeStart or WindowStateChange;
}
