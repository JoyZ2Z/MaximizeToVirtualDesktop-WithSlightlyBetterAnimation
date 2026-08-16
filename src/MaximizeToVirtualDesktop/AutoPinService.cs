using System.Diagnostics;
using System.Text;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Continuously pins non-fullscreen windows to all virtual desktops while enabled.
/// Windows are unpinned only while the current desktop is a fullscreen desktop
/// (a MaximizeToVirtualDesktop temp desktop with a fullscreen app), so the fullscreen
/// app isn't covered by pinned windows. In every other case all qualifying windows
/// stay pinned to all desktops.
/// </summary>
internal sealed class AutoPinService : IDisposable
{
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly Control _syncControl;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly System.Windows.Forms.Timer _desktopTimer;
    private readonly HashSet<IntPtr> _autoPinned = new();
    private Guid? _lastDesktopId;
    private bool _enabled;

    public AutoPinService(VirtualDesktopService vds, FullScreenTracker tracker, Control syncControl)
    {
        _vds = vds;
        _tracker = tracker;
        _syncControl = syncControl;
        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (_, _) => Scan();
        _desktopTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _desktopTimer.Tick += (_, _) => DetectDesktopChange();
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;

        if (enabled)
        {
            _lastDesktopId = _vds.GetCurrentDesktopId();
            Scan();
            _scanTimer.Start();
            _desktopTimer.Start();
            Trace.WriteLine("AutoPinService: Enabled.");
        }
        else
        {
            _scanTimer.Stop();
            _desktopTimer.Stop();
            UnpinAll();
            Trace.WriteLine("AutoPinService: Disabled.");
        }
    }

    private void DetectDesktopChange()
    {
        var currentId = _vds.GetCurrentDesktopId();
        if (currentId == null || currentId == _lastDesktopId) return;
        _lastDesktopId = currentId;

        if (IsFullScreenDesktop(currentId.Value))
        {
            // On a fullscreen desktop, unpin everything so the fullscreen app is alone.
            UnpinAll();
        }
        else
        {
            // On a normal desktop, pin all qualifying windows.
            Scan();
        }
    }

    private bool IsFullScreenDesktop(Guid desktopId)
    {
        foreach (var entry in _tracker.GetAll())
        {
            if (entry.TempDesktopId == desktopId) return true;
        }
        return false;
    }

    private void Scan()
    {
        var currentId = _vds.GetCurrentDesktopId();
        bool isFullScreen = currentId != null && IsFullScreenDesktop(currentId.Value);

        var currentWindows = new HashSet<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldPin(hwnd))
            {
                currentWindows.Add(hwnd);
                if (!isFullScreen
                    && !_autoPinned.Contains(hwnd)
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

        // On a fullscreen desktop, unpin everything. Otherwise unpin windows that
        // no longer qualify (became maximized, minimized, hidden, or closed).
        var toUnpin = _autoPinned
            .Where(h => isFullScreen || !currentWindows.Contains(h) || !NativeMethods.IsWindow(h))
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
        _desktopTimer.Stop();
        _desktopTimer.Dispose();
        UnpinAll();
    }
}
