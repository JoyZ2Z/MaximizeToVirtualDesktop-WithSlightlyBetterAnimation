using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Shared window-state predicates used by FullScreenManager and WindowMonitor.
/// Probe-verified: DrawboardPDF-style UWP host windows report showCmd ==
/// SW_MAXIMIZE when fullscreen, so a plain showCmd check is correct for them
/// too. Rect-based checks are unreliable for windows on non-current desktops
/// (GetWindowRect reports stale/odd values there), so they are not used.
/// </summary>
internal static class WindowStateHelper
{
    public static bool IsUwpWindow(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString() == "ApplicationFrameWindow";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True if the window is still fullscreen (showCmd == SW_MAXIMIZE).
    /// False when restored or minimized — both count as "no longer fullscreen".
    /// </summary>
    public static bool IsStillFullscreen(IntPtr hwnd)
    {
        var placement = NativeMethods.WINDOWPLACEMENT.Default;
        return NativeMethods.GetWindowPlacement(hwnd, ref placement)
            && placement.showCmd == NativeMethods.SW_MAXIMIZE;
    }
}
