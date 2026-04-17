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
    private readonly FullScreenTracker _tracker;
    private readonly Control _syncControl;
    private readonly AppSettings _settings;
    private IntPtr _locationChangeHook;
    private IntPtr _destroyHook;
    private bool _disposed;

    // Must be stored as fields to prevent GC collection of the delegate
    private readonly NativeMethods.WinEventProc _locationChangeProc;
    private readonly NativeMethods.WinEventProc _destroyProc;
    private readonly NativeMethods.WinEventProc _moveSizeEndProc;
    private IntPtr _moveSizeEndHook;
    // Track windows that have been maximized but need to wait for resize end
    // Access to this set must happen only on the UI thread.
    private readonly HashSet<IntPtr> _pendingMaximize = new();

    public WindowMonitor(FullScreenManager manager, FullScreenTracker tracker, Control syncControl, AppSettings settings)
    {
        _manager = manager;
        _tracker = tracker;
        _syncControl = syncControl;
        _settings = settings;

        _locationChangeProc = OnLocationChange;
        _destroyProc = OnDestroy;
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

        if (_locationChangeHook == IntPtr.Zero || _destroyHook == IntPtr.Zero)
        {
            Trace.WriteLine("WindowMonitor: Failed to set one or more WinEvent hooks.");
        }
        else
        {
            Trace.WriteLine("WindowMonitor: Started monitoring.");
        }
    }

    private void OnLocationChange(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // Only care about top-level window changes (OBJID_WINDOW)
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;
        // Debug: Log entry into OnLocationChange for tracing shortcut handling
        Trace.WriteLine($"WindowMonitor: OnLocationChange triggered for hwnd={hwnd}, eventType={eventType}");
        // If the window is already tracked, check if it is being restored (i.e., no longer maximized)
        if (_tracker.IsTracked(hwnd))
        {
            // Tracked window event: log current placement for debugging
            var placement = NativeMethods.WINDOWPLACEMENT.Default;
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} current showCmd={placement.showCmd}, checking for restore.");
                // Log detailed state
                Trace.WriteLine($"WindowMonitor: Placement details: flags={placement.flags}, ptMinPosition=({placement.ptMinPosition.X},{placement.ptMinPosition.Y}), ptMaxPosition=({placement.ptMaxPosition.X},{placement.ptMaxPosition.Y}), rcNormalPosition=({placement.rcNormalPosition.Left},{placement.rcNormalPosition.Top},{placement.rcNormalPosition.Right},{placement.rcNormalPosition.Bottom})");
                if (placement.showCmd != NativeMethods.SW_MAXIMIZE)
                {
                    Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} restored via location change, invoking restore.");
                    MarshalToUiThread(() => _manager.Restore(hwnd));
                    return;
                }
                else
                {
                    Trace.WriteLine($"WindowMonitor: Tracked window {hwnd} still maximized; awaiting MoveSizeEnd.");
                }
            }
            // Still maximized; let MoveSizeEnd handle pending maximize.
            return;
        }

        // Not tracked yet: check for a new maximize event (including via shortcut)
        var newPlacement = NativeMethods.WINDOWPLACEMENT.Default;
        if (!NativeMethods.GetWindowPlacement(hwnd, ref newPlacement)) return;
        // Log the detected placement for debugging purposes
        Trace.WriteLine($"WindowMonitor: Detected placement.showCmd={newPlacement.showCmd} for hwnd={hwnd}");
        bool shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
        bool triggerVirtualDesktop = _settings.InvertShiftClick ? !shiftHeld : shiftHeld;
        if (newPlacement.showCmd == NativeMethods.SW_MAXIMIZE && triggerVirtualDesktop)
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
                        await Task.Delay(200);
                        // Marshal the check/remove back onto the UI thread
                        MarshalToUiThread(() =>
                        {
                            if (_pendingMaximize.Contains(hwnd))
                            {
                                _pendingMaximize.Remove(hwnd);
                                var placement = NativeMethods.WINDOWPLACEMENT.Default;
                                bool isMaximized = NativeMethods.GetWindowPlacement(hwnd, ref placement) && placement.showCmd == NativeMethods.SW_MAXIMIZE;
                                if (isMaximized)
                                {
                                    Trace.WriteLine($"WindowMonitor: Fallback processing for pending maximize window {hwnd}.");
                                    _manager.MaximizeToDesktop(hwnd);
                                }
                                else
                                {
                                    Trace.WriteLine($"WindowMonitor: Fallback detected restore for pending window {hwnd}.");
                                    _manager.Restore(hwnd);
                                }
                            }
                        });
                    });
                }
            });
        }
    }

    private async void OnMoveSizeEnd(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
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
            MarshalToUiThread(() => _manager.MaximizeToDesktop(hwnd));
            return;
        }

        // Handle only tracked windows that are being restored (not maximized)
        if (!_tracker.IsTracked(hwnd)) return;
        var placement = NativeMethods.WINDOWPLACEMENT.Default;
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement)) return;
        Trace.WriteLine($"WindowMonitor: MoveSizeEnd: placement.showCmd={placement.showCmd}, flags={placement.flags}, ptMinPosition=({placement.ptMinPosition.X},{placement.ptMinPosition.Y}), ptMaxPosition=({placement.ptMaxPosition.X},{placement.ptMaxPosition.Y}), rcNormalPosition=({placement.rcNormalPosition.Left},{placement.rcNormalPosition.Top},{placement.rcNormalPosition.Right},{placement.rcNormalPosition.Bottom})");
        if (placement.showCmd != NativeMethods.SW_MAXIMIZE)
        {
            Trace.WriteLine($"WindowMonitor: MoveSizeEnd detected un-maximized tracked window {hwnd}, restoring after delay.");
            await Task.Delay(100);
            MarshalToUiThread(() => _manager.Restore(hwnd));
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
        if (_moveSizeEndHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_moveSizeEndHook);
            _moveSizeEndHook = IntPtr.Zero;
        }

        Trace.WriteLine("WindowMonitor: Disposed.");
    }
}
