using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Orchestrates the "maximize to virtual desktop" and "restore from virtual desktop" flows.
/// Every mutating step has rollback if the next step fails.
/// </summary>
internal sealed class FullScreenManager
{
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly AppSettings _settings;
    private readonly Control _syncControl;
    private readonly HashSet<IntPtr> _inFlight = new();
    // Track temp desktop COM refs that have already been released to prevent double-release.
    // Multiple tracked windows may share the same TempDesktop COM pointer.
    private readonly HashSet<Guid> _releasedDesktops = new();

    public FullScreenManager(VirtualDesktopService vds, FullScreenTracker tracker, AppSettings settings, Control syncControl)
    {
        _vds = vds;
        _tracker = tracker;
        _settings = settings;
        _syncControl = syncControl;
    }

    /// <summary>
    /// Toggle: if window is tracked, restore it. Otherwise, maximize it to a new desktop.
    /// </summary>
    public void Toggle(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} is not a valid window, ignoring.");
            return;
        }

        if (!_inFlight.Add(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} already in-flight, ignoring.");
            return;
        }

        try
        {
            if (_tracker.IsTracked(hwnd))
            {
                Restore(hwnd);
            }
            else
            {
                MaximizeToDesktop(hwnd);
            }
        }
        finally
        {
            _inFlight.Remove(hwnd);
        }
    }

    /// <summary>
    /// Send a window to a new virtual desktop, maximized.
    /// Only the clicked window is moved; other windows from the same process are not affected.
    /// </summary>
    public void MaximizeToDesktop(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} is not valid, aborting maximize.");
            return;
        }

        if (_tracker.IsTracked(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} already tracked, toggling to restore.");
            Restore(hwnd);
            return;
        }

        // 1. Move only the clicked window
        var allWindows = new List<IntPtr> { hwnd };

        // 2. Record original state for all windows
        var originalDesktopId = _vds.GetDesktopIdForWindow(hwnd);
        if (originalDesktopId == null)
        {
            Trace.WriteLine("FullScreenManager: Could not determine original desktop, aborting.");
            return;
        }

        var originalPlacement = NativeMethods.WINDOWPLACEMENT.Default;
        if (!NativeMethods.GetWindowPlacement(hwnd, ref originalPlacement))
        {
            Trace.WriteLine("FullScreenManager: Could not get window placement, aborting.");
            return;
        }

        // 3. Create new virtual desktop
        var (tempDesktop, tempDesktopId) = _vds.CreateDesktop();
        if (tempDesktop == null || tempDesktopId == null)
        {
            Trace.WriteLine("FullScreenManager: Failed to create desktop, aborting.");
            return;
        }

        // 4. Name the desktop after the window title (or process name as fallback)
        string? processName = null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out int processId);
            using var process = Process.GetProcessById(processId);
            processName = !string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? process.MainWindowTitle
                : process.ProcessName;
            _vds.SetDesktopName(tempDesktop, $"[MVD] {processName}");
        }
        catch
        {
            // Non-critical, continue
        }

        // 5. Move all windows to new desktop
        var movedWindows = new List<IntPtr>();
        foreach (var window in allWindows)
        {
            if (_vds.MoveWindowToDesktop(window, tempDesktop))
            {
                movedWindows.Add(window);
            }
            else if (window == hwnd)
            {
                // Primary window failed — must rollback
                Trace.WriteLine($"FullScreenManager: Failed to move primary window {window}, rolling back.");
                var origDesktop = _vds.FindDesktop(originalDesktopId.Value);
                try
                {
                    if (origDesktop != null)
                    {
                        foreach (var movedWindow in movedWindows)
                        {
                            _vds.MoveWindowToDesktop(movedWindow, origDesktop);
                        }
                    }
                }
                finally
                {
                    if (origDesktop != null) Marshal.ReleaseComObject(origDesktop);
                }
                _vds.RemoveDesktop(tempDesktop);
                Marshal.ReleaseComObject(tempDesktop);
                return;
            }
            else
            {
                // Secondary window failed — skip it, not critical
                Trace.WriteLine($"FullScreenManager: Skipping secondary window {window} (move failed).");
            }
        }

        // 6. Restore → switch → maximize, same flow for all paths.
        //    Keep window topmost during switch to prevent wallpaper/theme flash.
        bool elevated = NativeMethods.IsWindowElevated(hwnd);
        const int HWND_TOPMOST = -1;
        const int HWND_NOTOPMOST = -2;

        bool wasTopmost = (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
        if (!wasTopmost)
        {
            NativeMethods.SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Restore to normal size if maximized (invisible — window is on the new desktop)
        if (!elevated && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, (int)NativeMethods.SW_SHOWNORMAL);
        }

        if (!_vds.SwitchToDesktop(tempDesktop))
        {
            if (!wasTopmost)
                NativeMethods.SetWindowPos(hwnd, (IntPtr)HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            RollbackSwitch(tempDesktop, originalDesktopId.Value, movedWindows, hwnd, originalPlacement, elevated);
            return;
        }

        if (!elevated && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
        }

        // Restore focus — call twice with delay to ensure stickiness
        NativeMethods.SetForegroundWindow(hwnd);
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            if (!_syncControl.IsDisposed && _syncControl.IsHandleCreated)
            {
                try { _syncControl.BeginInvoke(() => { if (NativeMethods.IsWindow(hwnd)) NativeMethods.SetForegroundWindow(hwnd); }); }
                catch (ObjectDisposedException) { }
            }
        });

        if (!wasTopmost)
        {
            NativeMethods.SetWindowPos(hwnd, (IntPtr)HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // 7. Track the window
        _tracker.Track(hwnd, originalDesktopId.Value, tempDesktopId.Value, tempDesktop, processName, originalPlacement);

        if (_settings.ShowSwitchPopup)
        {
            NotificationOverlay.ShowNotification("→ Virtual Desktop", processName ?? "", hwnd);
        }
        Trace.WriteLine($"FullScreenManager: Successfully moved window to desktop {tempDesktopId}");
    }

    /// <summary>
    /// Rollback a failed desktop switch: restore window placement, move windows back, remove temp desktop.
    /// </summary>
    private void RollbackSwitch(IVirtualDesktop tempDesktop, Guid originalDesktopId,
        List<IntPtr> movedWindows, IntPtr hwnd,
        NativeMethods.WINDOWPLACEMENT originalPlacement, bool elevated)
    {
        Trace.WriteLine("FullScreenManager: Failed to switch desktop, rolling back.");
        if (!elevated && NativeMethods.IsWindow(hwnd))
        {
            var rollbackPlacement = originalPlacement;
            NativeMethods.SetWindowPlacement(hwnd, ref rollbackPlacement);
        }
        var origDesktop = _vds.FindDesktop(originalDesktopId);
        try
        {
            if (origDesktop != null)
            {
                foreach (var window in movedWindows)
                {
                    _vds.MoveWindowToDesktop(window, origDesktop);
                }
            }
        }
        finally
        {
            if (origDesktop != null) Marshal.ReleaseComObject(origDesktop);
        }
        _vds.RemoveDesktop(tempDesktop);
        Marshal.ReleaseComObject(tempDesktop);
    }

    /// <summary>
    /// Restore a tracked window: move it back to its original desktop, restore window state,
    /// switch back, and remove the temp desktop.
    /// When <paramref name="keepMinimized"/> is true, the window stays minimized (only desktop cleanup is performed).
    /// </summary>
    public void Restore(IntPtr hwnd, bool keepMinimized = false)
    {
        var entry = _tracker.Get(hwnd);
        if (entry == null)
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} not tracked, ignoring restore.");
            return;
        }

        Trace.WriteLine($"FullScreenManager: Restoring window {hwnd} from temp desktop {entry.TempDesktopId}");

        // Untrack will be performed after successful restore

        var origDesktop = _vds.FindDesktop(entry.OriginalDesktopId);
        try
        {
            // Restore window placement (skip if keeping minimized)
            if (!keepMinimized && NativeMethods.IsWindow(hwnd))
            {
                var placement = entry.OriginalPlacement;
                NativeMethods.SetWindowPlacement(hwnd, ref placement);
                // Ensure the window is shown in its normal (not maximized) state
                NativeMethods.ShowWindow(hwnd, (int)NativeMethods.SW_SHOWNORMAL);
            }

            // Move window back to original desktop
            if (origDesktop != null && NativeMethods.IsWindow(hwnd))
            {
                _vds.MoveWindowToDesktop(hwnd, origDesktop);
            }

            // Switch back to original desktop — keep window topmost to prevent theme flash
            if (origDesktop != null)
            {
                const int HWND_TOPMOST_R = -1;
                const int HWND_NOTOPMOST_R = -2;
                bool wasTopmostR = (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
                if (!wasTopmostR)
                {
                    NativeMethods.SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST_R, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                _vds.SwitchToDesktop(origDesktop);
                if (!wasTopmostR)
                {
                    NativeMethods.SetWindowPos(hwnd, (IntPtr)HWND_NOTOPMOST_R, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
            }
            else
            {
                Trace.WriteLine("FullScreenManager: Original desktop no longer exists, leaving window on current.");
            }
        }
        finally
        {
            if (origDesktop != null) Marshal.ReleaseComObject(origDesktop);
        }

        // Remove temp desktop and release its COM reference
        if (_releasedDesktops.Add(entry.TempDesktopId))
        {
            _vds.RemoveDesktop(entry.TempDesktop);
            Marshal.ReleaseComObject(entry.TempDesktop);
        }
      
        _tracker.Untrack(hwnd);

        // Set focus on the restored window (skip if minimized)
        if (!keepMinimized && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }

        if (_settings.ShowSwitchPopup)
        {
            NotificationOverlay.ShowNotification("← Restored", entry.ProcessName ?? "", hwnd);
        }
        Trace.WriteLine($"FullScreenManager: Restored window to original desktop.");
    }

    /// <summary>
    /// Called when a tracked window is destroyed (closed). Clean up its temp desktop.
    /// </summary>
    public void HandleWindowDestroyed(IntPtr hwnd)
    {
        var entry = _tracker.Untrack(hwnd);
        if (entry == null) return;

        Trace.WriteLine($"FullScreenManager: Tracked window {hwnd} destroyed.");

        Trace.WriteLine($"FullScreenManager: Cleaning up temp desktop {entry.TempDesktopId}");

        // Switch back to original desktop first
        var origDesktop = _vds.FindDesktop(entry.OriginalDesktopId);
        try
        {
            if (origDesktop != null) _vds.SwitchToDesktop(origDesktop);
        }
        finally
        {
            if (origDesktop != null) Marshal.ReleaseComObject(origDesktop);
        }

        // Remove the temp desktop and release its COM reference
        if (_releasedDesktops.Add(entry.TempDesktopId))
        {
            _vds.RemoveDesktop(entry.TempDesktop);
            Marshal.ReleaseComObject(entry.TempDesktop);
        }
    }

    /// <summary>
    /// Clean up all tracked windows — called on app exit.
    /// </summary>
    public void RestoreAll()
    {
        var entries = _tracker.GetAll();
        Trace.WriteLine($"FullScreenManager: Restoring {entries.Count} tracked window(s) on exit.");

        foreach (var entry in entries)
        {
            try
            {
                Restore(entry.Hwnd);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"FullScreenManager: Error restoring {entry.Hwnd}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Remove stale entries for windows that no longer exist.
    /// </summary>
    public void CleanupStaleEntries()
    {
        var stale = _tracker.GetStaleHandles();
        foreach (var hwnd in stale)
        {
            HandleWindowDestroyed(hwnd);
        }
    }

    /// <summary>
    /// Toggle pin/unpin of a window to all virtual desktops.
    /// </summary>
    public void PinToggle(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            Trace.WriteLine($"FullScreenManager: hwnd {hwnd} is not valid, ignoring pin toggle.");
            return;
        }

        string? processName = null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            processName = !string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? process.MainWindowTitle
                : process.ProcessName;
        }
        catch { }

        if (_vds.IsWindowPinned(hwnd))
        {
            if (_vds.UnpinWindow(hwnd))
                NotificationOverlay.ShowNotification("📌 Unpinned", processName ?? "", hwnd);
            else
                NotificationOverlay.ShowNotification("⚠ Unpin Failed", processName ?? "", hwnd);
        }
        else
        {
            if (_vds.PinWindow(hwnd))
                NotificationOverlay.ShowNotification("📌 Pinned to All Desktops", processName ?? "", hwnd);
            else
                NotificationOverlay.ShowNotification("⚠ Pin Failed", processName ?? "", hwnd);
        }
    }

}
