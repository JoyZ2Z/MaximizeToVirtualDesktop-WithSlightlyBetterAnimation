using System.Diagnostics;
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
    public static UwpPresentationWindowRole GetUwpPresentationWindowRole(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString() switch
            {
                "ApplicationFrameWindow" => UwpPresentationWindowRole.Host,
                "Windows.UI.Core.CoreWindow" => UwpPresentationWindowRole.Core,
                _ => UwpPresentationWindowRole.None,
            };
        }
        catch
        {
            return UwpPresentationWindowRole.None;
        }
    }

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
    /// Produces low-frequency diagnostics for packaged/UWP host resolution.
    /// It is intentionally invoked only when an HWND maps to a different
    /// IApplicationView HWND, never from the hot observation path.
    /// </summary>
    public static string DescribeWindowForDiagnostics(IntPtr hwnd)
    {
        var className = "<unknown>";
        var processName = "<unknown>";
        var processId = 0;
        try
        {
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            className = sb.ToString();
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId != 0)
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
        }
        catch
        {
            // Diagnostics must never alter the view-resolution path.
        }
        return $"sourceHwnd={hwnd}; class={className}; pid={processId}; process={processName}";
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

/// <summary>
/// The two top-level windows commonly exposed by legacy UWP applications.
/// They share an AUMID but must be treated as one logical application by
/// AutoPin when both are present.
/// </summary>
internal enum UwpPresentationWindowRole { None, Host, Core }
