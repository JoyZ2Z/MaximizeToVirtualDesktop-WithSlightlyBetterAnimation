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
/// To avoid pin windows stacking on top of fullscreen windows after a desktop switch,
/// this service also watches for desktop changes: when switching to a non-fullscreen
/// desktop it temporarily unpins that desktop's foreground window, and re-pins it when
/// switching back to a fullscreen desktop.
/// </summary>
internal sealed class AutoPinService : IDisposable
{
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly Control _syncControl;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly System.Windows.Forms.Timer _desktopSwitchTimer;
    private readonly HashSet<IntPtr> _autoPinned = new();
    private readonly HashSet<IntPtr> _temporarilyUnpinned = new();
    private Guid? _lastDesktopId;
    private bool _enabled;

    public AutoPinService(VirtualDesktopService vds, FullScreenTracker tracker, Control syncControl)
    {
        _vds = vds;
        _tracker = tracker;
        _syncControl = syncControl;
        _scanTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _scanTimer.Tick += (_, _) => Scan();
        _desktopSwitchTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _desktopSwitchTimer.Tick += (_, _) => DetectDesktopSwitch();
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
            _desktopSwitchTimer.Start();
            Trace.WriteLine("AutoPinService: Enabled.");
        }
        else
        {
            _scanTimer.Stop();
            _desktopSwitchTimer.Stop();
            UnpinAll();
            _temporarilyUnpinned.Clear();
            Trace.WriteLine("AutoPinService: Disabled.");
        }
    }

    private void DetectDesktopSwitch()
    {
        var currentId = _vds.GetCurrentDesktopId();
        if (currentId == null || currentId == _lastDesktopId) return;
        _lastDesktopId = currentId;
        OnDesktopSwitched(currentId.Value);
    }

    private void OnDesktopSwitched(Guid newDesktopId)
    {
        if (IsFullScreenDesktop(newDesktopId))
        {
            RestoreTemporarilyUnpinned();
        }
        else
        {
            ScheduleUnpinForeground();
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

    private void ScheduleUnpinForeground()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            if (_syncControl.IsDisposed || !_syncControl.IsHandleCreated) return;
            try
            {
                _syncControl.BeginInvoke(() =>
                {
                    var fg = NativeMethods.GetForegroundWindow();
                    if (fg == IntPtr.Zero || fg == _syncControl.Handle) return;
                    if (_autoPinned.Contains(fg))
                    {
                        _vds.UnpinWindow(fg);
                        _autoPinned.Remove(fg);
                        _temporarilyUnpinned.Add(fg);
                        Trace.WriteLine($"AutoPinService: Temporarily unpinned foreground window {fg}.");
                    }
                });
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void RestoreTemporarilyUnpinned()
    {
        foreach (var hwnd in _temporarilyUnpinned)
        {
            if (NativeMethods.IsWindow(hwnd) && !_vds.IsWindowPinned(hwnd))
            {
                if (_vds.PinWindow(hwnd))
                {
                    _autoPinned.Add(hwnd);
                    Trace.WriteLine($"AutoPinService: Re-pinned window {hwnd}.");
                }
            }
        }
        _temporarilyUnpinned.Clear();
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
        _desktopSwitchTimer.Stop();
        _desktopSwitchTimer.Dispose();
        UnpinAll();
        _temporarilyUnpinned.Clear();
    }
}
