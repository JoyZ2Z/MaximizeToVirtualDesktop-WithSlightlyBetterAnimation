using System.Diagnostics;
using System.Text;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>Shared baseline filter for normal top-level application windows.</summary>
internal static class WindowEligibility
{
    public static bool IsApplicationWindow(IntPtr hwnd, IntPtr applicationWindow,
        Func<IntPtr, bool> isTrackedMvdWindow, bool includeMinimized)
    {
        if (!NativeMethods.IsWindow(hwnd) || hwnd == applicationWindow) return false;
        if (!NativeMethods.IsWindowVisible(hwnd) && !(includeMinimized && NativeMethods.IsIconic(hwnd))) return false;
        if (!includeMinimized && NativeMethods.IsIconic(hwnd)) return false;
        if (NativeMethods.GetWindowTextLength(hwnd) == 0) return false;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false;
        if (isTrackedMvdWindow(hwnd)) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == Process.GetCurrentProcess().Id) return false;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
            or "XamlExplorerHostIslandWindow");
    }
}
