using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;
using Updatum;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// System tray application. Hosts the NotifyIcon, handles the global hotkey,
/// and owns the lifecycle of all components.
/// </summary>
internal sealed class TrayApplication : Form
{
    private const int HOTKEY_ID = 0x1;
    private const int HOTKEY_PIN_ID = 0x2;
    private const int HOTKEY_AUTOPIN_ID = 0x3;
    private const int HOTKEY_RESTORE_ID = 0x4;
    private const int HOTKEY_UNPIN_ID = 0x5;
    private uint _shellRestartMessage;
    private bool _comInitialized;

    private readonly AppSettings _settings;
    private readonly NotifyIcon _trayIcon;
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly SnapWorkspaceTracker _snapTracker;
    private readonly SnapWorkspaceService _snapWorkspaceService;
    private readonly FullScreenManager _manager;
    private readonly WindowMonitor _monitor;
    private readonly MaximizeButtonHook _mouseHook;
    private readonly AutoPinService _autoPin;
    private readonly DesktopTransitionCoordinator _desktopTransitions;
    private readonly System.Windows.Forms.Timer _cleanupTimer;
    private readonly System.Windows.Forms.Timer _emptyDesktopTimer;
    private readonly System.Windows.Forms.Timer _desktopOrderTimer;
    private System.Windows.Forms.Timer? _retryTimer;
    private System.Windows.Forms.Timer? _sortTimer;
    private ToolStripMenuItem _autoPinModeItem = null!;
    private ToolStripMenuItem _autoPinOffItem = null!;
    private ToolStripMenuItem _autoPinOnItem = null!;
    private ToolStripMenuItem _autoPinTrackWindowsItem = null!;
    private ToolStripMenuItem _autoSortItem = null!;
    private ToolStripMenuItem _autoSortOffItem = null!;
    private ToolStripMenuItem _autoSortTimeItem = null!;
    private readonly List<Guid> _desktopMru = new();
    private Guid? _mainDesktopId;
    private Guid? _currentDesktopId;
    private Guid? _confirmedDesktopId;
    private DateTime _desktopEnterTime;
    private bool _sortAfterDesktopSettlement;

    internal static readonly UpdatumManager Updater = new("shanselman", "MaximizeToVirtualDesktop")
    {
        FetchOnlyLatestRelease = true,
        InstallUpdateSingleFileExecutableName = "MaximizeToVirtualDesktop",
    };

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: keep the hidden tray host out of Alt+Tab.
            return parameters;
        }
    }

    public TrayApplication()
    {
        // Make the form invisible — we're a tray-only app
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = new Size(0, 0);

        // Load persisted settings first so all components use them
        _settings = AppSettings.Load();

        // Initialize components
        _vds = new VirtualDesktopService();
        _tracker = new FullScreenTracker();
        _snapTracker = new SnapWorkspaceTracker();
        _autoPin = new AutoPinService(
            _vds, _tracker, _snapTracker, this, () => _mainDesktopId);
        _desktopTransitions = new DesktopTransitionCoordinator(_vds);
        _desktopTransitions.DesktopChanged += OnSharedDesktopChanged;
        _desktopTransitions.DesktopSettled += OnSharedDesktopSettled;
        _autoPin.StableDesktopObservationApplied += OnAutoPinDesktopSettled;
        _manager = new FullScreenManager(_vds, _tracker, _settings, this, _autoPin);
        _snapWorkspaceService = new SnapWorkspaceService(
            _vds, _tracker, _snapTracker, _autoPin, this, () => _mainDesktopId);
        _monitor = new WindowMonitor(
            _manager, _tracker, _snapWorkspaceService, _vds, this, _settings);
        _mouseHook = new MaximizeButtonHook(_manager, this, _settings);

        // System tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = BuildTooltipText(),
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        // Periodic cleanup of stale entries (every 30 seconds)
        _cleanupTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _cleanupTimer.Tick += (_, _) => _manager.CleanupStaleEntries();

        // Periodic cleanup of empty fullscreen desktops (every 60 seconds)
        _emptyDesktopTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _emptyDesktopTimer.Tick += (_, _) => _manager.CleanupEmptyDesktops();

        // MRU ordering does not require animation-frame precision. Keep this
        // as a low-cost fallback behind the desktop-switch event pipeline.
        _desktopOrderTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _desktopOrderTimer.Tick += (_, _) => TrackDesktopUsage();

    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Check Windows version before attempting COM init
        var buildNumber = GetWindowsBuildNumber();
        Trace.WriteLine($"TrayApplication: Windows build {buildNumber}");

        if (buildNumber < 22000)
        {
            // Not Windows 11 at all
            MessageBox.Show(
                "MaximizeToVirtualDesktop requires Windows 11.\n\n" +
                $"Your system is running Windows build {buildNumber}.\n" +
                "Virtual Desktop APIs needed by this app are not available on Windows 10.",
                "MaximizeToVirtualDesktop — Unsupported Windows Version",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
            return;
        }

        // Initialize COM (adapter auto-selects based on build number)
        _comInitialized = _vds.Initialize(buildNumber);
        if (!_comInitialized)
        {
            Trace.WriteLine("TrayApplication: COM initialization failed — entering degraded mode.");
            _trayIcon.Text = "Maximize to Virtual Desktop\n⚠️ COM failed — checking for updates...";
            _trayIcon.BalloonTipTitle = "Maximize to Virtual Desktop";
            _trayIcon.BalloonTipText =
                "Virtual Desktop COM interface failed to initialize.\n" +
                "This usually means Windows updated and broke the internal APIs.\n" +
                "Checking for an updated version now...";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
            _trayIcon.ShowBalloonTip(5000);

            // Immediately check for updates, then retry every 5 minutes
            _ = CheckForUpdatesAsync(userInitiated: false, comFailure: true);
            _retryTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            _retryTimer.Tick += async (_, _) =>
            {
                // Try reinitializing COM in case an in-place Windows update fixed it
                if (_vds.Reinitialize())
                {
                    Trace.WriteLine("TrayApplication: COM reinitialized successfully!");
                    _comInitialized = true;
                    _retryTimer!.Stop();
                    _retryTimer.Dispose();
                    _retryTimer = null;
                    _trayIcon.Text = BuildTooltipText();
                    StartMonitoring();
                    return;
                }
                await CheckForUpdatesAsync(userInitiated: false, comFailure: true);
            };
            _retryTimer.Start();

            // Register for Explorer restart — COM might work after Explorer restarts
            _shellRestartMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            return;
        }

        // Register for Explorer restart notification
        _shellRestartMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // Recover orphaned desktops from a previous crash
        RecoverOrphanedDesktops();

        // Initialize desktop MRU tracking (pure MRU, no fixed main-desktop anchor)
        _currentDesktopId = _vds.GetCurrentDesktopId();
        _desktopEnterTime = DateTime.UtcNow;
        EnsureMainDesktop();

        // If auto-sort is enabled, apply it once on startup.
        _settings.SetAutoSortMode(_settings.ResolveAutoSortMode());
        _settings.Save();
        if (_settings.ResolveAutoSortMode() != DesktopAutoSortMode.Off)
        {
            ScheduleSort();
        }

        // Start monitoring
        StartMonitoring();

        Trace.WriteLine("TrayApplication: Started.");

        // Show first-run balloon tip
        ShowFirstRunBalloon();

        // Check for updates asynchronously
        _ = CheckForUpdatesAsync();
    }

    private void StartMonitoring()
    {
        _desktopTransitions.Start();
        _monitor.Start();
        _snapWorkspaceService.Start();
        _mouseHook.Install();
        _cleanupTimer.Start();
        _emptyDesktopTimer.Start();
        UpdateDesktopUsageMonitoring();

        // Register hotkey if not already registered
        if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_ID,
            _settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT,
            _settings.HotkeyKey))
        {
            Trace.WriteLine("TrayApplication: Failed to register hotkey (may already be registered).");
        }
        else
        {
            Trace.WriteLine($"TrayApplication: Registered hotkey {FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey)}");
        }

        // Restore hotkey — only register if different from maximize (otherwise maximize acts as toggle)
        if (!HotkeysSame(_settings.HotkeyModifiers, _settings.HotkeyKey,
                         _settings.RestoreHotkeyModifiers, _settings.RestoreHotkeyKey))
        {
            if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_RESTORE_ID,
                _settings.RestoreHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                _settings.RestoreHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register restore hotkey.");
            }
            else
            {
                Trace.WriteLine($"TrayApplication: Registered restore hotkey {FormatHotkey(_settings.RestoreHotkeyModifiers, _settings.RestoreHotkeyKey)}");
            }
        }

        if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_PIN_ID,
            _settings.PinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
            _settings.PinHotkeyKey))
        {
            Trace.WriteLine("TrayApplication: Failed to register pin hotkey.");
        }
        else
        {
            Trace.WriteLine($"TrayApplication: Registered pin hotkey {FormatHotkey(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey)}");
        }

        // Unpin hotkey — only register if different from pin (otherwise pin acts as toggle)
        if (!HotkeysSame(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey,
                         _settings.UnpinHotkeyModifiers, _settings.UnpinHotkeyKey))
        {
            if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_UNPIN_ID,
                _settings.UnpinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                _settings.UnpinHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register unpin hotkey.");
            }
            else
            {
                Trace.WriteLine($"TrayApplication: Registered unpin hotkey {FormatHotkey(_settings.UnpinHotkeyModifiers, _settings.UnpinHotkeyKey)}");
            }
        }

        if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_AUTOPIN_ID,
            _settings.AutoPinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
            _settings.AutoPinHotkeyKey))
        {
            Trace.WriteLine("TrayApplication: Failed to register auto-pin hotkey.");
        }
        else
        {
            Trace.WriteLine($"TrayApplication: Registered auto-pin hotkey {FormatHotkey(_settings.AutoPinHotkeyModifiers, _settings.AutoPinHotkeyKey)}");
        }

        // Resolve legacy boolean settings to the equivalent explicit mode.
        var autoPinMode = _settings.ResolveAutoPinMode();
        _settings.SetAutoPinMode(autoPinMode);
        _settings.Save();
        if (autoPinMode != AutoPinMode.Off)
            _autoPin.SetMode(autoPinMode);
        UpdateAutoPinMenuItems();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_ID)
        {
            OnHotkeyPressed();
            return;
        }

        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_RESTORE_ID)
        {
            OnRestoreHotkeyPressed();
            return;
        }

        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_PIN_ID)
        {
            OnPinHotkeyPressed();
            return;
        }

        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_UNPIN_ID)
        {
            OnUnpinHotkeyPressed();
            return;
        }

        if (m.Msg == (int)NativeMethods.WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_AUTOPIN_ID)
        {
            ToggleAutoPin();
            return;
        }

        // Explorer restart: COM objects are now invalid, reinitialize
        if (_shellRestartMessage != 0 && m.Msg == (int)_shellRestartMessage)
        {
            Trace.WriteLine("TrayApplication: Explorer restarted, reinitializing COM...");

            // Windows destroys all virtual desktops on Explorer restart —
            // our tracked COM refs are now stale and must be released.
            _tracker.ClearAll();
            _snapTracker.ClearAll();

            if (_vds.Reinitialize() && !_comInitialized)
            {
                // Recovered from degraded mode!
                Trace.WriteLine("TrayApplication: COM recovered after Explorer restart!");
                _comInitialized = true;
                _retryTimer?.Stop();
                _retryTimer?.Dispose();
                _retryTimer = null;
                _trayIcon.Text = BuildTooltipText();
                StartMonitoring();
            }
            return;
        }

        base.WndProc(ref m);
    }

    private void OnHotkeyPressed()
    {
        var hwnd = GetForegroundWindowForAction("Hotkey");
        if (hwnd == IntPtr.Zero) return;

        // If maximize and restore share the same hotkey, behave as a toggle.
        if (HotkeysSame(_settings.HotkeyModifiers, _settings.HotkeyKey,
                        _settings.RestoreHotkeyModifiers, _settings.RestoreHotkeyKey))
        {
            Trace.WriteLine($"TrayApplication: Hotkey pressed, toggling window {hwnd}");
            _manager.Toggle(hwnd);
        }
        else
        {
            Trace.WriteLine($"TrayApplication: Hotkey pressed, maximizing window {hwnd}");
            _manager.Maximize(hwnd);
        }
    }

    private void OnRestoreHotkeyPressed()
    {
        var hwnd = GetForegroundWindowForAction("Restore hotkey");
        if (hwnd == IntPtr.Zero) return;

        Trace.WriteLine($"TrayApplication: Restore hotkey pressed, restoring window {hwnd}");
        _manager.Restore(hwnd);
    }

    private void OnPinHotkeyPressed()
    {
        var hwnd = GetForegroundWindowForAction("Pin hotkey");
        if (hwnd == IntPtr.Zero) return;

        // If pin and unpin share the same hotkey, behave as a toggle.
        if (HotkeysSame(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey,
                        _settings.UnpinHotkeyModifiers, _settings.UnpinHotkeyKey))
        {
            Trace.WriteLine($"TrayApplication: Pin hotkey pressed, toggling pin for window {hwnd}");
            _manager.PinToggle(hwnd);
        }
        else
        {
            Trace.WriteLine($"TrayApplication: Pin hotkey pressed, pinning window {hwnd}");
            _manager.Pin(hwnd);
        }
    }

    private void OnUnpinHotkeyPressed()
    {
        var hwnd = GetForegroundWindowForAction("Unpin hotkey");
        if (hwnd == IntPtr.Zero) return;

        Trace.WriteLine($"TrayApplication: Unpin hotkey pressed, unpinning window {hwnd}");
        _manager.Unpin(hwnd);
    }

    private IntPtr GetForegroundWindowForAction(string action)
    {
        if (!_comInitialized)
        {
            Trace.WriteLine($"TrayApplication: {action} pressed but COM not initialized.");
            return IntPtr.Zero;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Handle)
        {
            Trace.WriteLine($"TrayApplication: {action} pressed but no valid foreground window.");
            return IntPtr.Zero;
        }
        return hwnd;
    }

    private static bool HotkeysSame(uint mod1, uint key1, uint mod2, uint key2)
        => mod1 == mod2 && key1 == key2;

    private void ToggleAutoPin()
    {
        if (!_comInitialized)
        {
            Trace.WriteLine("TrayApplication: Auto-pin hotkey pressed but COM not initialized.");
            return;
        }

        var next = AutoPinModePolicy.Toggle(
            _autoPin.RequestedMode, _settings.LastEnabledAutoPinMode);
        SelectAutoPinMode(next);
    }

    private void SelectAutoPinMode(AutoPinMode mode)
    {
        if (!_comInitialized)
        {
            Trace.WriteLine($"TrayApplication: Cannot select AutoPin mode {mode}; COM unavailable.");
            return;
        }

        _autoPin.SetMode(mode);
        _settings.SetAutoPinMode(mode);
        _settings.Save();
        UpdateAutoPinMenuItems();
        _trayIcon.Text = BuildTooltipText();

        var (title, message) = mode switch
        {
            AutoPinMode.On => ("📌 Auto-pin On",
                "Active desktop sessions are managed globally and minimized when covered"),
            AutoPinMode.TrackWindows => ("📌 Auto-pin On-TrackWindows",
                "Windows are tracked by their desktop visibility state"),
            _ => ("📌 Auto-pin Off", "Known pin states are being restored"),
        };
        NotificationOverlay.ShowNotification(title, message, IntPtr.Zero);
        Trace.WriteLine($"TrayApplication: Auto-pin mode selected: {mode}.");
    }

    private void UpdateAutoPinMenuItems()
    {
        var mode = _autoPin.RequestedMode;
        _autoPinOffItem.Checked = mode == AutoPinMode.Off;
        _autoPinOnItem.Checked = mode == AutoPinMode.On;
        _autoPinTrackWindowsItem.Checked = mode == AutoPinMode.TrackWindows;
        _autoPinModeItem.Text = $"Auto-pin: {ModeDisplayName(mode)}";
    }

    private static string ModeDisplayName(AutoPinMode mode) => mode switch
    {
        AutoPinMode.On => "On",
        AutoPinMode.TrackWindows => "On-TrackWindows",
        _ => "Off",
    };

    private void SelectAutoSortMode(DesktopAutoSortMode mode)
    {
        _settings.SetAutoSortMode(mode);
        _settings.Save();
        UpdateDesktopUsageMonitoring();
        UpdateAutoSortMenuItem();

        if (mode != DesktopAutoSortMode.Off)
        {
            // Apply the current MRU order immediately.
            ScheduleSort();
        }

        NotificationOverlay.ShowNotification(
            mode switch
            {
                DesktopAutoSortMode.TimeBased => "↔ Auto-sort: stay time",
                _ => "↔ Auto-sort Off",
            },
            mode switch
            {
                DesktopAutoSortMode.TimeBased => "Desktops reorder after the configured stay time",
                _ => "Desktop order left unchanged",
            },
            IntPtr.Zero);
        Trace.WriteLine($"TrayApplication: Auto-sort mode selected: {mode}.");
    }

    private void UpdateAutoSortMenuItem()
    {
        var mode = _settings.ResolveAutoSortMode();
        _autoSortOffItem.Checked = mode == DesktopAutoSortMode.Off;
        _autoSortTimeItem.Checked = mode == DesktopAutoSortMode.TimeBased;
        _autoSortItem.Text = $"Auto-sort: {mode switch
        {
            DesktopAutoSortMode.TimeBased => "By stay time",
            _ => "Off",
        }}";
    }

    private void EnsureMainDesktop(List<Guid>? allDesktopIds = null)
    {
        var ids = allDesktopIds ?? _vds.GetAllDesktopIds();
        if (ids.Count == 0) return;

        if (_mainDesktopId.HasValue && ids.Contains(_mainDesktopId.Value)) return;

        var persistedMain = _settings.MainDesktopId;
        _mainDesktopId = persistedMain.HasValue && ids.Contains(persistedMain.Value)
            ? persistedMain.Value
            : ids[0];
        _settings.MainDesktopId = _mainDesktopId;
        _settings.Save();
        _desktopMru.Remove(_mainDesktopId.Value);
        Trace.WriteLine($"TrayApplication: Desktop 1 is {_mainDesktopId}.");
    }

    private void RecordMru(Guid desktopId)
    {
        if (_mainDesktopId == desktopId) return;
        _desktopMru.Remove(desktopId);
        _desktopMru.Insert(0, desktopId);
    }

    /// <summary>
    /// Tracks desktop switches to maintain a most-recently-used order.
    /// Desktops visited for less than 3 seconds are treated as "passing through"
    /// and are not recorded as recent use.
    /// </summary>
    private void TrackDesktopUsage()
    {
        if (_settings.ResolveAutoSortMode() != DesktopAutoSortMode.TimeBased) return;
        TrackDesktopUsage(_vds.GetCurrentDesktopId());
    }

    private void TrackDesktopUsage(Guid? id)
    {
        if (_settings.ResolveAutoSortMode() != DesktopAutoSortMode.TimeBased) return;
        if (id == null) return;

        var now = DateTime.UtcNow;
        bool switched = id != _currentDesktopId;

        if (switched)
        {
            // Record the previous desktop if we stayed there long enough.
            var oldId = _currentDesktopId;
            if (oldId != null
                && (now - _desktopEnterTime).TotalSeconds >= _settings.MruThresholdSeconds)
            {
                RecordMru(oldId.Value);
            }

            _currentDesktopId = id;
            _desktopEnterTime = now;
            _confirmedDesktopId = null; // new desktop not yet confirmed as "used"
            _sortAfterDesktopSettlement = true;
        }

        // Confirm the current desktop as recently used only after it has been visited
        // for at least the threshold. A quick pass-through does not enter the MRU.
        bool confirmed = false;
        if (_settings.ResolveAutoSortMode() == DesktopAutoSortMode.TimeBased
            && _confirmedDesktopId != id
            && (now - _desktopEnterTime).TotalSeconds >= _settings.MruThresholdSeconds)
        {
            _confirmedDesktopId = id;
            RecordMru(id.Value);
            confirmed = true;
        }

        // A switch may still be in the shell animation. Its one sort is
        // released by the shared settled signal below, not by this timer.
        if (confirmed && !_sortAfterDesktopSettlement) ScheduleSort();
        if (switched) ShowDesktopOrder();
    }

    private void OnSharedDesktopChanged(Guid desktopId)
    {
        _autoPin.ObserveDesktopChange(desktopId);
        _snapWorkspaceService.ObserveDesktopChange(desktopId);
        TrackDesktopUsage(desktopId);
    }

    private void OnSharedDesktopSettled(Guid desktopId)
    {
        _snapWorkspaceService.ObserveDesktopSettledWithoutAutoPin(desktopId);
        // A foreground reconciliation can already have observed the new
        // desktop, so AutoPin may legitimately have no later stable-observation
        // event for this switch. The shared settled signal is the normal
        // Auto-sort release; AutoPin's event is only the deferred path below.
        CompleteDesktopTransitionForAutoSort(desktopId);
    }

    private void OnAutoPinDesktopSettled(Guid desktopId)
    {
        CompleteDesktopTransitionForAutoSort(desktopId);
    }

    private void CompleteDesktopTransitionForAutoSort(Guid desktopId)
    {
        if (_currentDesktopId != desktopId || !_sortAfterDesktopSettlement) return;
        if (_autoPin.IsSuspended || _autoPin.IsTransitioning) return;
        _sortAfterDesktopSettlement = false;
        if (_settings.ResolveAutoSortMode() == DesktopAutoSortMode.TimeBased)
            ScheduleSort();
    }

    private void UpdateDesktopUsageMonitoring()
    {
        if (_settings.ResolveAutoSortMode() == DesktopAutoSortMode.TimeBased)
            _desktopOrderTimer.Start();
        else
            _desktopOrderTimer.Stop();
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }
        action();
    }

    /// <summary>
    /// Runs only after the shared desktop-settlement result. The short delay lets
    /// the final foreground event finish without polling/retrying during a switch.
    /// </summary>
    private void ScheduleSort()
    {
        _sortTimer ??= new System.Windows.Forms.Timer { Interval = 100 };
        _sortTimer.Tick -= OnSortTimerTick;
        _sortTimer.Tick += OnSortTimerTick;
        _sortTimer.Stop();
        _sortTimer.Start();
    }

    private void OnSortTimerTick(object? sender, EventArgs e)
    {
        _sortTimer?.Stop();
        SortDesktops();
    }

    /// <summary>
    /// Reorders desktops: main desktop (desktop 1) stays first, then the rest by
    /// most-recently-used order. Only desktops visited for at least the MRU threshold
    /// are in the MRU list; the current desktop is NOT specially placed — it only moves
    /// to the front once it has been confirmed (visited long enough).
    /// </summary>
    private void SortDesktops()
    {
        if (_settings.ResolveAutoSortMode() == DesktopAutoSortMode.Off) return;
        if (_autoPin.IsSuspended || _autoPin.IsTransitioning)
        {
            Trace.WriteLine("TrayApplication: skipped sort because desktop is not settled.");
            return;
        }

        var allIds = _vds.GetAllDesktopIds();
        if (allIds.Count < 2) return;

        EnsureMainDesktop(allIds);
        if (!_mainDesktopId.HasValue) return;
        var target = DesktopOrderPolicy.CreateTargetOrder(_mainDesktopId.Value, _desktopMru, allIds);

        // No-op if already in the target order.
        bool alreadyOrdered = target.Count == allIds.Count;
        if (alreadyOrdered)
        {
            for (int i = 0; i < target.Count; i++)
            {
                if (target[i] != allIds[i]) { alreadyOrdered = false; break; }
            }
        }

        if (!alreadyOrdered)
        {
            // Guard: remember current desktop so we can switch back if sorting moves it.
            var guardCurrent = _vds.GetCurrentDesktopId();

            for (int i = 0; i < target.Count; i++)
            {
                _vds.MoveDesktopToIndex(target[i], i);
            }

            // Guard: if the current desktop got moved away, switch back to it.
            var afterCurrent = _vds.GetCurrentDesktopId();
            if (guardCurrent != null && afterCurrent != guardCurrent)
            {
                var desktop = _vds.FindDesktop(guardCurrent.Value);
                if (desktop != null)
                {
                    try { _vds.SwitchToDesktop(desktop); }
                    finally { Marshal.ReleaseComObject(desktop); }
                }
            }

            Trace.WriteLine($"TrayApplication: Desktops sorted ({target.Count} desktops).");
        }

        ShowDesktopOrder();
    }

    /// <summary>
    /// Shows a popup describing the current desktop order, highlighting the current desktop.
    /// </summary>
    private void ShowDesktopOrder()
    {
        var allIds = _vds.GetAllDesktopIds();
        if (allIds.Count == 0) return;

        var currentId = _vds.GetCurrentDesktopId();

        var parts = new List<string>(allIds.Count);
        for (int i = 0; i < allIds.Count; i++)
        {
            parts.Add(allIds[i] == currentId ? $"[{i + 1}]" : $"{i + 1}");
        }

        var order = string.Join("  ", parts);
        NotificationOverlay.ShowNotification("桌面顺序", order, IntPtr.Zero);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("No windows tracked") { Enabled = false };
        menu.Opening += (_, _) =>
        {
            var count = _tracker.Count;
            statusItem.Text = count == 0
                ? "No windows tracked"
                : $"{count} window(s) on virtual desktops";
        };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var restoreAllItem = new ToolStripMenuItem("Restore All", null, (_, _) =>
        {
            _manager.RestoreAll();
        });
        menu.Items.Add(restoreAllItem);

        _autoPinModeItem = new ToolStripMenuItem("Auto-pin");
        _autoPinOffItem = new ToolStripMenuItem("Off");
        _autoPinOnItem = new ToolStripMenuItem("On");
        _autoPinTrackWindowsItem = new ToolStripMenuItem("On-TrackWindows");
        _autoPinOffItem.Click += (_, _) => SelectAutoPinMode(AutoPinMode.Off);
        _autoPinOnItem.Click += (_, _) => SelectAutoPinMode(AutoPinMode.On);
        _autoPinTrackWindowsItem.Click += (_, _) =>
            SelectAutoPinMode(AutoPinMode.TrackWindows);
        _autoPinModeItem.DropDownItems.AddRange([
            _autoPinOffItem,
            _autoPinOnItem,
            _autoPinTrackWindowsItem,
        ]);
        menu.Items.Add(_autoPinModeItem);

        _autoSortItem = new ToolStripMenuItem("Auto-sort");
        _autoSortOffItem = new ToolStripMenuItem("Off");
        _autoSortTimeItem = new ToolStripMenuItem("By stay time");
        _autoSortOffItem.Click += (_, _) => SelectAutoSortMode(DesktopAutoSortMode.Off);
        _autoSortTimeItem.Click += (_, _) => SelectAutoSortMode(DesktopAutoSortMode.TimeBased);
        _autoSortItem.DropDownItems.AddRange([
            _autoSortOffItem,
            _autoSortTimeItem,
        ]);
        menu.Items.Add(_autoSortItem);

        menu.Opening += (_, _) => UpdateAutoPinMenuItems();
        menu.Opening += (_, _) => UpdateAutoSortMenuItem();

        menu.Items.Add(new ToolStripSeparator());

        var howToUseItem = new ToolStripMenuItem("How to Use", null, (_, _) =>
        {
            ShowUsageInfo(_settings);
        });
        menu.Items.Add(howToUseItem);

        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) =>
        {
            OpenSettings();
        });
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var updateItem = new ToolStripMenuItem("Check for Updates...", null, async (_, _) =>
        {
            await CheckForUpdatesAsync(userInitiated: true);
        });
        menu.Items.Add(updateItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            Application.Exit();
        });
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        dlg.ApplyToSettings();
        if (!_settings.Save())
        {
            MessageBox.Show(
                "Settings could not be saved. Your changes will apply for this session but won't persist after restart.",
                "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateDesktopUsageMonitoring();
        UpdateAutoSortMenuItem();

        // Re-register hotkeys with the new configuration
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_RESTORE_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_PIN_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_UNPIN_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_AUTOPIN_ID);

        if (_comInitialized)
        {
            var failures = new List<string>();
            if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_ID,
                _settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                _settings.HotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register hotkey after settings change.");
                failures.Add("Maximize hotkey");
            }
            if (!HotkeysSame(_settings.HotkeyModifiers, _settings.HotkeyKey,
                             _settings.RestoreHotkeyModifiers, _settings.RestoreHotkeyKey)
                && !NativeMethods.RegisterHotKey(Handle, HOTKEY_RESTORE_ID,
                    _settings.RestoreHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                    _settings.RestoreHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register restore hotkey after settings change.");
                failures.Add("Restore hotkey");
            }
            if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_PIN_ID,
                _settings.PinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                _settings.PinHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register pin hotkey after settings change.");
                failures.Add("Pin hotkey");
            }
            if (!HotkeysSame(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey,
                             _settings.UnpinHotkeyModifiers, _settings.UnpinHotkeyKey)
                && !NativeMethods.RegisterHotKey(Handle, HOTKEY_UNPIN_ID,
                    _settings.UnpinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                    _settings.UnpinHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register unpin hotkey after settings change.");
                failures.Add("Unpin hotkey");
            }
            if (!NativeMethods.RegisterHotKey(Handle, HOTKEY_AUTOPIN_ID,
                _settings.AutoPinHotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                _settings.AutoPinHotkeyKey))
            {
                Trace.WriteLine("TrayApplication: Failed to register auto-pin hotkey after settings change.");
                failures.Add("Auto-pin hotkey");
            }
            if (failures.Count > 0)
            {
                MessageBox.Show(
                    $"Could not register: {string.Join(", ", failures)}.\n\nThe shortcut may already be in use by another application.",
                    "Hotkey Registration Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        _trayIcon.Text = BuildTooltipText();
        Trace.WriteLine("TrayApplication: Settings saved and hotkeys updated.");
    }

    private async Task CheckForUpdatesAsync(bool userInitiated = false, bool comFailure = false)
    {
        try
        {
            if (!userInitiated && !comFailure) await Task.Delay(5000);

            var updateFound = await Updater.CheckForUpdatesAsync();

            if (!updateFound)
            {
                if (userInitiated)
                    MessageBox.Show("You're running the latest version.", "No Updates",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (comFailure)
                    _trayIcon.Text = "Maximize to Virtual Desktop\n⚠️ COM failed — no update available yet";
                return;
            }

            var release = Updater.LatestRelease!;
            var changelog = Updater.GetChangelog(true) ?? "No release notes available.";

            var message = comFailure
                ? $"A fix may be available! Version {release.TagName} is ready.\n\n{changelog}\n\nDownload and install?"
                : $"Version {release.TagName} is available.\n\n{changelog}\n\nDownload and install?";

            var result = MessageBox.Show(message,
                comFailure ? "Update Available — May Fix COM Issue" : "Update Available",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                var asset = await Updater.DownloadUpdateAsync();
                if (asset != null)
                {
                    await Updater.InstallUpdateAsync(asset);
                }
                else if (userInitiated)
                {
                    MessageBox.Show("Failed to download the update.", "Update Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TrayApplication: Update check failed: {ex.Message}");
            if (userInitiated)
                MessageBox.Show($"Update check failed: {ex.Message}", "Update Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static readonly string FirstRunMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaximizeToVirtualDesktop", ".firstrun");

    private void ShowFirstRunBalloon()
    {
        try
        {
            if (File.Exists(FirstRunMarker)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunMarker)!);
            File.WriteAllText(FirstRunMarker, "");

            var maximizeKey = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);
            var pinKey = FormatHotkey(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey);
            var clickDesc = _settings.InvertShiftClick ? "Click" : "Shift+Click";

            _trayIcon.BalloonTipTitle = "Maximize to Virtual Desktop";
            _trayIcon.BalloonTipText =
                $"Press {maximizeKey} or {clickDesc} the maximize button " +
                "to maximize a window to its own virtual desktop.\n" +
                $"Press {pinKey} to pin a window to all desktops.";
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TrayApplication: First-run balloon failed: {ex.Message}");
        }
    }

    private static void ShowUsageInfo(AppSettings settings)
    {
        using var form = new Form
        {
            Text = "How to Use — Maximize to Virtual Desktop",
            Size = new Size(620, 580),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
        };

        // Add inner padding via a wrapper panel
        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 0),
        };
        contentPanel.Controls.Add(rtb);

        // Build RTF content
        rtb.SelectionFont = new Font("Segoe UI Variable Display", 14f, FontStyle.Bold);
        rtb.AppendText("Maximize to Virtual Desktop\n\n");

        var maximizeKey = FormatHotkey(settings.HotkeyModifiers, settings.HotkeyKey);
        var pinKey = FormatHotkey(settings.PinHotkeyModifiers, settings.PinHotkeyKey);
        var clickDesc = settings.InvertShiftClick
            ? "Click maximize button"
            : "Shift + Click maximize button";
        var clickInstruction = settings.InvertShiftClick
            ? "Click any window's maximize button. Hold Shift for a normal maximize."
            : "Hold Shift and click any window's maximize button.";

        AppendSection(rtb, "Maximize a Window to Its Own Desktop",
            (maximizeKey, "Toggles the focused window to/from its own virtual desktop."),
            (clickDesc, clickInstruction));

        AppendSection(rtb, "Pin a Window to All Desktops",
            (pinKey, "Toggles pin/unpin so the focused window appears on every desktop."));

        AppendSection(rtb, "How It Works",
            ("Maximize", "The window moves to a new virtual desktop and is maximized full-screen."),
            ("Restore", "Close or restore the window to automatically return to your original desktop."),
            ("Restore All", "Use the tray menu to bring all windows back at once."));

        var panel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.System,
        };
        okButton.Location = new Point((panel.Width - okButton.Width) / 2, 9);
        panel.Resize += (_, _) => okButton.Location = new Point((panel.Width - okButton.Width) / 2, 9);
        panel.Controls.Add(okButton);

        form.AcceptButton = okButton;
        form.Controls.Add(contentPanel);
        form.Controls.Add(panel);
        form.ShowDialog();
    }

    private static void AppendSection(RichTextBox rtb, string heading, params (string key, string desc)[] items)
    {
        rtb.SelectionFont = new Font("Segoe UI Variable Display", 10.5f, FontStyle.Bold);
        rtb.AppendText(heading + "\n");

        foreach (var (key, desc) in items)
        {
            rtb.SelectionFont = new Font("Segoe UI Variable Display", 9.5f, FontStyle.Regular);
            rtb.AppendText("  ");
            rtb.SelectionFont = new Font("Consolas", 9f, FontStyle.Bold);
            rtb.AppendText(key);
            rtb.SelectionFont = new Font("Segoe UI Variable Display", 9.5f, FontStyle.Regular);
            rtb.AppendText("  —  " + desc + "\n");
        }

        rtb.AppendText("\n");
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber")?.ToString();
            return int.TryParse(build, out var num) ? num : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string BuildTooltipText()
    {
        var maximize = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);
        var pin = FormatHotkey(_settings.PinHotkeyModifiers, _settings.PinHotkeyKey);
        var click = _settings.InvertShiftClick ? "Click maximize" : "Shift+Click maximize";
        return $"Maximize to Virtual Desktop\n{maximize} | {click} | {pin} to pin";
    }

    private static string FormatHotkey(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(GetKeyName(vk));
        return string.Join("+", parts);
    }

    private static string GetKeyName(uint vk)
    {
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return $"F{vk - 0x70 + 1}";
        return $"0x{vk:X2}";
    }

    private void RecoverOrphanedDesktops()
    {
        var persisted = TrackerPersistence.Load();
        var snapPersisted = SnapWorkspacePersistence.Load();
        if (persisted.Count == 0 && snapPersisted.Count == 0) return;

        Trace.WriteLine(
            $"TrayApplication: Found {persisted.Count + snapPersisted.Count} "
            + "orphaned desktop(s) from previous session.");

        foreach (var entry in persisted)
        {
            var desktop = _vds.FindDesktop(entry.TempDesktopId);
            if (desktop != null)
            {
                Trace.WriteLine($"TrayApplication: Removing orphaned desktop {entry.TempDesktopId} ({entry.ProcessName ?? "unknown"})");
                _vds.RemoveDesktop(desktop);
                Marshal.ReleaseComObject(desktop);
            }
        }

        foreach (var entry in snapPersisted)
        {
            var desktop = _vds.FindDesktop(entry.TempDesktopId);
            if (desktop != null)
            {
                Trace.WriteLine(
                    $"TrayApplication: Removing orphaned Snap desktop {entry.TempDesktopId} "
                    + $"({entry.MonitorId})");
                var fallback = _mainDesktopId ?? _vds.GetAllDesktopIds().FirstOrDefault();
                if (fallback != Guid.Empty) _vds.RemoveDesktop(desktop, fallback);
                else _vds.RemoveDesktop(desktop);
                Marshal.ReleaseComObject(desktop);
            }
        }

        TrackerPersistence.Delete();
        SnapWorkspacePersistence.Delete();
        Trace.WriteLine("TrayApplication: Orphaned desktop recovery complete.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Trace.WriteLine("TrayApplication: Shutting down...");

        _retryTimer?.Stop();
        _retryTimer?.Dispose();

        _cleanupTimer.Stop();
        _cleanupTimer.Dispose();

        _emptyDesktopTimer.Stop();
        _emptyDesktopTimer.Dispose();

        _desktopOrderTimer.Stop();
        _desktopOrderTimer.Dispose();

        _sortTimer?.Stop();
        _sortTimer?.Dispose();

        // Restore all tracked windows before exiting
        _snapWorkspaceService.RemoveAll();
        _manager.RestoreAll();

        // Clean up native resources
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_RESTORE_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_PIN_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_UNPIN_ID);
        NativeMethods.UnregisterHotKey(Handle, HOTKEY_AUTOPIN_ID);
        _mouseHook.Dispose();
        _monitor.Dispose();
        _desktopTransitions.DesktopChanged -= OnSharedDesktopChanged;
        _desktopTransitions.DesktopSettled -= OnSharedDesktopSettled;
        _autoPin.StableDesktopObservationApplied -= OnAutoPinDesktopSettled;
        _desktopTransitions.Dispose();
        _snapWorkspaceService.Dispose();
        _autoPin.Dispose();
        _vds.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        Trace.WriteLine("TrayApplication: Shutdown complete.");
        base.OnFormClosing(e);
    }
}
