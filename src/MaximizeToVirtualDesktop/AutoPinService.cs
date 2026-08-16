using System.Diagnostics;
using System.Text;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Continuously pins non-fullscreen windows to all virtual desktops while enabled.
/// A timer scans top-level windows and pins those that are visible, not minimized,
/// and not maximized. Windows pinned by this service are tracked so they can be
/// unpinned when the feature is turned off.
///
/// The foreground window is temporarily unpinned so it stays on the current desktop
/// (instead of appearing on every desktop). When it loses focus it is re-pinned.
/// </summary>
internal sealed class AutoPinService : IDisposable
{
    private readonly VirtualDesktopService _vds;
    private readonly Control _syncControl;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly System.Windows.Forms.Timer _foregroundTimer;
    private readonly HashSet<IntPtr> _autoPinned = new();
    private readonly HashSet<IntPtr> _temporarilyUnpinned = new();
    private IntPtr _lastForeground;
    private bool _enabled;

    public AutoPinService(VirtualDesktopService vds, Control syncControl)
    {
        _vds = vds;
        _syncControl = syncControl;
        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (_, _) => Scan();
        _foregroundTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _foregroundTimer.Tick += (_, _) => DetectForegroundChange();
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;

        if (enabled)
        {
            Scan();
            _scanTimer.Start();
            _foregroundTimer.Start();
            _lastForeground = NativeMethods.GetForegroundWindow();
            // Unpin the current foreground window so it stays on the current desktop.
            UnpinForeground(_lastForeground);
            Trace.WriteLine("AutoPinService: Enabled.");
        }
        else
        {
            _scanTimer.Stop();
            _foregroundTimer.Stop();
            UnpinAll();
            _temporarilyUnpinned.Clear();
            Trace.WriteLine("AutoPinService: Disabled.");
        }
    }

    private void DetectForegroundChange()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _lastForeground) return;
        var prev = _lastForeground;
        _lastForeground = fg;

        // Re-pin the previous foreground window only on a real focus change
        // (same desktop), not on a desktop switch (different desktop).
        if (prev != IntPtr.Zero && _temporarilyUnpinned.Contains(prev) && SameDesktop(prev, fg))
        {
            _temporarilyUnpinned.Remove(prev);
            if (NativeMethods.IsWindow(prev) && ShouldPin(prev) && !_vds.IsWindowPinned(prev))
            {
                if (_vds.PinWindow(prev))
                {
                    _autoPinned.Add(prev);
                    Trace.WriteLine($"AutoPinService: Re-pinned window {prev}.");
                }
            }
        }

        // Unpin the new foreground window.
        UnpinForeground(fg);
    }

    /// <summary>
    /// True if both windows belong to the same virtual desktop. A desktop switch
    /// changes the foreground window to one on a different desktop, which should
    /// not be treated as a focus loss.
    /// </summary>
    private bool SameDesktop(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        var da = _vds.GetDesktopIdForWindow(a);
        var db = _vds.GetDesktopIdForWindow(b);
        return da != null && db != null && da.Value == db.Value;
    }

    private void UnpinForeground(IntPtr fg)
    {
        if (fg == IntPtr.Zero || fg == _syncControl.Handle) return;
        if (!_autoPinned.Contains(fg)) return;

        if (_vds.UnpinWindow(fg))
        {
            _autoPinned.Remove(fg);
            _temporarilyUnpinned.Add(fg);
            Trace.WriteLine($"AutoPinService: Temporarily unpinned foreground window {fg}.");
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
                if (!_autoPinned.Contains(hwnd)
                    && !_temporarilyUnpinned.Contains(hwnd)
                    && !_vds.IsWindowPinned(hwnd))
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
        _scanTimer.Stop();
        _scanTimer.Dispose();
        _foregroundTimer.Stop();
        _foregroundTimer.Dispose();
        UnpinAll();
        _temporarilyUnpinned.Clear();
    }
}
