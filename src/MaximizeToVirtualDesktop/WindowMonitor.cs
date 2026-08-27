using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Uses SetWinEventHook to monitor tracked windows for state changes (un-maximize, close).
/// All callbacks are marshaled to the UI thread.
/// </summary>
internal sealed class WindowMonitor : IDisposable
{
    private readonly FullScreenManager _manager;
    private readonly SnapWorkspaceService _snapWorkspaceService;
    private readonly FullScreenTracker _tracker;
    private readonly VirtualDesktopService _vds;
    private readonly Control _syncControl;
    private readonly AppSettings _settings;
    private IntPtr _locationChangeHook;
    private IntPtr _destroyHook;
    private IntPtr _hideHook;
    private bool _disposed;

    // Must be stored as fields to prevent GC collection of the delegate
    private readonly NativeMethods.WinEventProc _locationChangeProc;
    private readonly NativeMethods.WinEventProc _destroyProc;
    private readonly NativeMethods.WinEventProc _hideProc;
    private readonly NativeMethods.WinEventProc _moveSizeEndProc;
    private IntPtr _moveSizeEndHook;
    // Track windows that have been maximized but need to wait for resize end
    private readonly HashSet<IntPtr> _pendingMaximize = new();
    private readonly HashSet<IntPtr> _pendingFullscreenExit = new();

    public WindowMonitor(FullScreenManager manager, FullScreenTracker tracker,
        SnapWorkspaceService snapWorkspaceService, VirtualDesktopService vds,
        Control syncControl, AppSettings settings)
    {
        _manager = manager;
        _tracker = tracker;
        _snapWorkspaceService = snapWorkspaceService;
        _vds = vds;
        _syncControl = syncControl;
        _settings = settings;

        _locationChangeProc = OnLocationChange;
        _destroyProc = OnDestroy;
        _hideProc = OnHide;
        _moveSizeEndProc = OnMoveSizeEnd;
    }

    public void Start()
    {
        if (_locationChangeHook != IntPtr.Zero) return;

        // EVENT_OBJECT_LOCATIONCHANGE fires when window state changes (including maximize/restore)
        _locationChangeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationChangeProc,
            0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        // EVENT_SYSTEM_MOVESIZEEND fires after a window finishes moving or resizing (including maximize via shortcuts)
        _moveSizeEndHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _moveSizeEndProc,
            0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        // EVENT_OBJECT_DESTROY fires when a window is closed
        _destroyHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _destroyProc,
            0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        // EVENT_OBJECT_HIDE fires when a window is hidden (closed to tray, or closing)
        _hideHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_HIDE,
            NativeMethods.EVENT_OBJECT_HIDE,
            IntPtr.Zero, _hideProc,
            0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_locationChangeHook == IntPtr.Zero || _destroyHook == IntPtr.Zero)
        {
            Trace.WriteLine("WindowMonitor: Failed to set one or more WinEvent hooks.");
        }
        else
        {
            Trace.WriteLine("WindowMonitor: Started monitoring.");
        }
    }

    private bool ShouldTriggerVirtualDesktop()
    {
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        return _settings.InvertShiftClick ? !shiftHeld : shiftHeld;
    }

    private void OnLocationChange(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Only care about top-level window changes (OBJID_WINDOW)
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        // If the window is already tracked, check if it is being restored (i.e., no longer fullscreen).
        if (_tracker.IsTracked(hwnd))
        {
            if (!WindowStateHelper.IsStillFullscreen(hwnd))
            {
                // A virtual-desktop switch can transiently report a tracked
                // window as restored. This is not UWP-specific: never start
                // fullscreen-exit arbitration unless the window is actually
                // on the active desktop.
                if (!_vds.IsWindowOnCurrentDesktop(hwnd))
                    return;

                // If left mouse button is down, user is dragging — let Windows handle the resize.
                // OnMoveSizeEnd will fire after release and trigger restore.
                bool isDragging = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
                if (isDragging) return;

                Trace.WriteLine(
                    $"WindowMonitor: Tracked window {hwnd} left fullscreen; arbitration queued.");
                MarshalToUiThread(() => QueueFullscreenExitArbitration(hwnd));
                return;
            }
            // Still fullscreen; let MoveSizeEnd handle pending maximize.
            return;
        }

        // Not tracked yet: check for a new maximize event (including via shortcut)
        var newPlacement = NativeMethods.WINDOWPLACEMENT.Default;
        if (!NativeMethods.GetWindowPlacement(hwnd, ref newPlacement)) return;
        if (newPlacement.showCmd != NativeMethods.SW_MAXIMIZE) return;

        bool triggerVirtualDesktop = ShouldTriggerVirtualDesktop();
        if (triggerVirtualDesktop)
        {
            // Defer maximization until after the resize operation completes
            MarshalToUiThread(() =>
            {
                if (_pendingMaximize.Add(hwnd))
                {
                    Trace.WriteLine($"WindowMonitor: Queued maximize for window {hwnd} after resize end.");
                    // Schedule a fallback in case MoveSizeEnd does not fire (e.g., keyboard shortcut)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(50);
                        // Marshal the check/remove back onto the UI thread
                        MarshalToUiThread(() =>
                        {
                            if (_pendingMaximize.Contains(hwnd))
                            {
                                _pendingMaximize.Remove(hwnd);
                                // If already tracked, the hook path already handled it — don't double-trigger
                                if (_tracker.IsTracked(hwnd))
                                {
                                    Trace.WriteLine($"WindowMonitor: Pending window {hwnd} already tracked, skipping fallback.");
                                    return;
                                }
                                var placement = NativeMethods.WINDOWPLACEMENT.Default;
                                bool isMaximized = NativeMethods.GetWindowPlacement(hwnd, ref placement) && placement.showCmd == NativeMethods.SW_MAXIMIZE;
                                if (isMaximized)
                                {
                                    Trace.WriteLine($"WindowMonitor: Fallback processing for pending maximize window {hwnd}.");
                                    _manager.Toggle(hwnd);
                                }
                                else
                                {
                                    Trace.WriteLine($"WindowMonitor: Fallback detected restore for pending window {hwnd}.");
                                    _manager.Restore(hwnd, keepMinimized: NativeMethods.IsIconic(hwnd));
                                }
                            }
                        });
                    });
                }
            });
        }
    }

    private void OnMoveSizeEnd(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Only care about top-level window changes (OBJID_WINDOW)
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;

        // If this window was pending maximize, handle it now
        bool wasPending = false;
        if (!_syncControl.IsDisposed && _syncControl.IsHandleCreated)
        {
            wasPending = (bool)_syncControl.Invoke(new Func<bool>(() => _pendingMaximize.Remove(hwnd)));
        }
        if (wasPending)
        {
            Trace.WriteLine($"WindowMonitor: MoveSizeEnd triggered for pending maximize window {hwnd}.");
            MarshalToUiThread(() => _manager.Toggle(hwnd));
            return;
        }

        // Handle only tracked windows that are being restored (not fullscreen)
        if (!_tracker.IsTracked(hwnd)) return;
        Trace.WriteLine($"WindowMonitor: MoveSizeEnd for tracked window {hwnd}.");
        if (!_vds.IsWindowOnCurrentDesktop(hwnd)) return;
        if (!WindowStateHelper.IsStillFullscreen(hwnd) && !NativeMethods.IsIconic(hwnd))
        {
            Trace.WriteLine(
                $"WindowMonitor: Tracked window {hwnd} left fullscreen via move/size; arbitration queued.");
            MarshalToUiThread(() => QueueFullscreenExitArbitration(hwnd));
        }
    }

    private void QueueFullscreenExitArbitration(IntPtr hwnd)
    {
        if (_disposed || !_tracker.IsTracked(hwnd)
            || !_pendingFullscreenExit.Add(hwnd))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FullscreenExitPolicy.ArbitrationDelay);
            MarshalToUiThread(() => ReconcileFullscreenExit(hwnd));
        });
    }

    private void ReconcileFullscreenExit(IntPtr hwnd)
    {
        _pendingFullscreenExit.Remove(hwnd);
        if (_disposed || !_tracker.IsTracked(hwnd)
            || _manager.IsMutationInFlight(hwnd)
            || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }
        if (!_vds.IsWindowOnCurrentDesktop(hwnd))
        {
            return;
        }

        var decision = FullscreenExitPolicy.DecideAfterDelay(
            WindowStateHelper.IsStillFullscreen(hwnd),
            NativeMethods.IsIconic(hwnd),
            SnapWorkspaceService.IsWindowArranged(hwnd));
        switch (decision)
        {
            case FullscreenExitDecision.None:
                return;
            case FullscreenExitDecision.RestoreMinimized:
                _manager.Restore(hwnd, keepMinimized: true);
                return;
            case FullscreenExitDecision.Restore:
                _manager.Restore(hwnd);
                return;
            case FullscreenExitDecision.PromoteToSnap:
                if (!_snapWorkspaceService.TryPromoteFullscreenWindow(hwnd))
                {
                    Trace.WriteLine(
                        $"WindowMonitor: arranged fullscreen window {hwnd} retained; "
                        + "Snap promotion will retry.");
                    QueueFullscreenExitArbitration(hwnd);
                }
                return;
        }
    }

    private void OnDestroy(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;
        if (!_tracker.IsTracked(hwnd)) return;

        Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} destroyed.");
        MarshalToUiThread(() => _manager.HandleWindowDestroyed(hwnd));
    }

    private void OnHide(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;
        if (!_tracker.IsTracked(hwnd)) return;

        // A desktop switch also fires EVENT_OBJECT_HIDE for windows on other desktops,
        // so a hide alone doesn't mean the window was closed. Verify the handle is
        // actually gone shortly after before cleaning up. (Windows that are alive but
        // invisible — tray apps, exclusive-fullscreen on another desktop — are left
        // alone: IsWindowVisible cannot reliably distinguish them.)
        Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} hidden, verifying if closed.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            MarshalToUiThread(() =>
            {
                if (_tracker.IsTracked(hwnd) && !NativeMethods.IsWindow(hwnd))
                {
                    Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} confirmed closed after hide.");
                    _manager.HandleWindowDestroyed(hwnd);
                }
            });
        });
    }

    private void MarshalToUiThread(Action action)
    {
        if (_syncControl.IsDisposed || !_syncControl.IsHandleCreated) return;

        try
        {
            _syncControl.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // App is shutting down
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_locationChangeHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationChangeHook);
            _locationChangeHook = IntPtr.Zero;
        }
        if (_destroyHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_destroyHook);
            _destroyHook = IntPtr.Zero;
        }
        if (_hideHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hideHook);
            _hideHook = IntPtr.Zero;
        }
        if (_moveSizeEndHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_moveSizeEndHook);
            _moveSizeEndHook = IntPtr.Zero;
        }

        _pendingFullscreenExit.Clear();

        Trace.WriteLine("WindowMonitor: Disposed.");
    }
}
