namespace MaximizeToVirtualDesktop;

/// <summary>
/// Isolates the acceptance rule for GetViewInFocus fallback resolution.
/// A focused view is usable only when it belongs to the same Win32 window family,
/// or when an eligible application HWND and the view share a process. Otherwise
/// it may be the fullscreen anchor that merely received focus later.
/// </summary>
internal static class AutoPinFocusedViewFallbackPolicy
{
    public static bool CanAccept(
        bool isSameWindowFamily,
        bool foregroundIsEligibleApplication,
        bool isSameProcess) =>
        isSameWindowFamily || (foregroundIsEligibleApplication && isSameProcess);
}
