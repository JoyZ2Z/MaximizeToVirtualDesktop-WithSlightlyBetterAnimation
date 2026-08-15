using System.Diagnostics;
using System.Text;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Continuously pins non-fullscreen windows to all virtual desktops while enabled.
/// A timer scans top-level windows and pins those that are visible, not minimized,
/// and not maximized. Windows pinned by this service are tracked so they can be
/// unpinned when the feature is turned off.
/// </summary>
internal sealed class AutoPinService : IDisposable
{
    private readonly VirtualDesktopService _vds;
    private readonly Control _syncControl;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HashSet<IntPtr> _autoPinned = new();
    private bool _enabled;

    public AutoPinService(VirtualDesktopService vds, Control syncControl)
    {
        _vds = vds;
        _syncControl = syncControl;
        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => Scan();
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;

        if (enabled)
        {
            Scan();
            _timer.Start();
            Trace.WriteLine("AutoPinService: Enabled.");
        }
        else
        {
            _timer.Stop();
            UnpinAll();
            Trace.WriteLine("AutoPinService: Disabled.");
        }
    }

    private void Scan()
    {
        var currentWindows = new HashSet<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldPin(hwnd))
            {
                currentWindows.Add(hwnd);
                if (!_autoPinned.Contains(hwnd) && !_vds.IsWindowPinned(hwnd))
                {
                    if (_vds.PinWindow(hwnd))
                    {
                        _autoPinned.Add(hwnd);
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        // Unpin windows we previously pinned but that no longer qualify
        // (became maximized, minimized, hidden, or closed).
        var toUnpin = _autoPinned
            .Where(h => !currentWindows.Contains(h) || !NativeMethods.IsWindow(h))
            .ToList();

        foreach (var hwnd in toUnpin)
        {
            if (NativeMethods.IsWindow(hwnd)) _vds.UnpinWindow(hwnd);
            _autoPinned.Remove(hwnd);
        }
    }

    private bool ShouldPin(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        if (hwnd == _syncControl.Handle) return false;                 // our own tray form
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;        // hidden
        if (NativeMethods.IsIconic(hwnd)) return false;                // minimized
        if (NativeMethods.GetWindowTextLength(hwnd) == 0) return false; // no title
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false; // tool/dialog

        // Exclude shell windows (taskbar, desktop, etc.)
        var className = GetClassName(hwnd);
        if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            return false;

        // Exclude maximized windows
        var placement = NativeMethods.WINDOWPLACEMENT.Default;
        if (NativeMethods.GetWindowPlacement(hwnd, ref placement)
            && placement.showCmd == NativeMethods.SW_MAXIMIZE)
            return false;

        // Exclude our own process windows
        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == Process.GetCurrentProcess().Id) return false;

        return true;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void UnpinAll()
    {
        foreach (var hwnd in _autoPinned)
        {
            if (NativeMethods.IsWindow(hwnd)) _vds.UnpinWindow(hwnd);
        }
        _autoPinned.Clear();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        UnpinAll();
    }
}
