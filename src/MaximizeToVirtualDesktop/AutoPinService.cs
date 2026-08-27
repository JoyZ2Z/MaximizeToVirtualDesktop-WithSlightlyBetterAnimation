using System.Diagnostics;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Serializes AutoPin observation and execution on the UI thread. All writes are
/// gated during FullScreenManager operations. Desktop transitions retain a
/// stability observer but do not suppress current-desktop pin-state writes.
/// </summary>
internal sealed class AutoPinService : IDisposable
{
    // WinEvents perform normal reconciliation. These timers are intentionally
    // only a recovery net for a missed notification, not a second event loop.
    private const int ScanIntervalMs = 5000;
    private const int ChangeProbeIntervalMs = 1000;
    private const int MinimumSwitchFreezeMs = 100;
    private const int StabilityProbeIntervalMs = 150;
    private const int MaximumRestoreRetryIntervalMs = 5000;
    private static readonly TimeSpan SuppressionRetryDelay = TimeSpan.FromSeconds(2);

    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly SnapWorkspaceTracker _snapTracker;
    private readonly Func<Guid?> _mainDesktopProvider;
    private readonly AutoPinObservationBuilder _observationBuilder;
    private readonly AutoPinForegroundTracker _foregroundTracker;
    private readonly AutoPinEngine _engine;
    private readonly AutoPinExecutor _executor;
    private readonly AutoPinReconciliationGate _writeGate;
    private readonly AutoPinTransitionBarrier _transitionBarrier;
    private readonly Control _syncControl;
    private readonly System.Windows.Forms.Timer _scanTimer;
    private readonly System.Windows.Forms.Timer _changeTimer;
    private readonly System.Windows.Forms.Timer _stabilityTimer;
    private readonly System.Windows.Forms.Timer _restoreTimer;
    private readonly NativeMethods.WinEventProc _desktopSwitchProc;
    private readonly NativeMethods.WinEventProc _foregroundProc;
    private readonly NativeMethods.WinEventProc _windowChangeProc;
    private readonly NativeMethods.WinEventProc _urgentWindowChangeProc;
    private readonly AutoPinWorkspaceFallbackTracker _workspaceFallbackTracker;
    private readonly AutoPinMinimizeRetryTracker _minimizeRetryTracker;
    private readonly AutoPinSuppressionRetryGate _suppressionRetryGate;
    private IntPtr _desktopSwitchHook;
    private IntPtr _foregroundHook;
    private IntPtr _windowChangeHook;
    private IntPtr _visibilityRelationshipHook;
    private IntPtr _moveSizeEndHook;
    private IntPtr _minimizeHook;
    private IntPtr _windowStateHook;
    private Guid? _lastObservedDesktop;
    private bool _observationDirty;
    private int _dirtyNotificationPosted;
    private int _visibilityReconciliationPosted;
    private int _snapCoverageReconciliationPosted;
    private int _switchFreezeRequested;
    private DateTime _restoreNotBefore;
    private int _restoreRetryIntervalMs = StabilityProbeIntervalMs;
    private int _suspensionCount;
    private bool _restorePending;
    private AutoPinMode _mode;
    private AutoPinMode? _pendingMode;
    private bool _enabled;
    private bool _disposed;

    public AutoPinService(
        VirtualDesktopService vds,
        FullScreenTracker tracker,
        SnapWorkspaceTracker snapTracker,
        Control syncControl,
        Func<Guid?> mainDesktopProvider)
    {
        _vds = vds;
        _tracker = tracker;
        _snapTracker = snapTracker;
        _syncControl = syncControl;
        _mainDesktopProvider = mainDesktopProvider;
        _engine = new AutoPinEngine();
        _foregroundTracker = new AutoPinForegroundTracker();
        _workspaceFallbackTracker = new AutoPinWorkspaceFallbackTracker();
        _minimizeRetryTracker = new AutoPinMinimizeRetryTracker();
        _suppressionRetryGate = new AutoPinSuppressionRetryGate(SuppressionRetryDelay);
        _writeGate = new AutoPinReconciliationGate();
        _transitionBarrier = new AutoPinTransitionBarrier();
        _observationBuilder = new AutoPinObservationBuilder(
            vds, tracker, snapTracker, syncControl);
        _executor = new AutoPinExecutor(
            new AutoPinWindowsCommandPlatform(vds, tracker, snapTracker),
            new AutoPinOwnership());

        _scanTimer = new System.Windows.Forms.Timer { Interval = ScanIntervalMs };
        _scanTimer.Tick += (_, _) => RequestScan();
        _changeTimer = new System.Windows.Forms.Timer { Interval = ChangeProbeIntervalMs };
        _changeTimer.Tick += (_, _) => DetectChanges();
        _stabilityTimer = new System.Windows.Forms.Timer { Interval = MinimumSwitchFreezeMs };
        _stabilityTimer.Tick += (_, _) => ProbeStability();
        _restoreTimer = new System.Windows.Forms.Timer { Interval = StabilityProbeIntervalMs };
        _restoreTimer.Tick += (_, _) => TryCompleteDeferredRestore();

        _desktopSwitchProc = OnDesktopSwitch;
        _foregroundProc = OnForegroundChanged;
        _windowChangeProc = OnWindowChanged;
        _urgentWindowChangeProc = OnUrgentWindowChanged;
    }

    public bool Enabled => _enabled;
    public AutoPinMode Mode => _mode;
    public AutoPinMode RequestedMode => _pendingMode ?? _mode;
    public bool IsSuspended => _suspensionCount > 0;
    public bool IsTransitioning => _enabled && (_writeGate.IsClosed
        || Volatile.Read(ref _switchFreezeRequested) != 0
        || _transitionBarrier.IsActive || _stabilityTimer.Enabled);

    /// <summary>
    /// Raised only after AutoPin has completed its own stable observation and
    /// applied it. Consumers may then perform non-AutoPin work for this desktop.
    /// </summary>
    public event Action<Guid>? StableDesktopObservationApplied;

    /// <summary>
    /// Receives the shared desktop-ID probe. This starts the existing write
    /// barrier sooner than the one-second recovery timer, without changing the
    /// transition's pin/unpin rules.
    /// </summary>
    public void ObserveDesktopChange(Guid desktopId)
    {
        if (_disposed) return;
        void Handle()
        {
            if (desktopId == _lastObservedDesktop) return;
            _lastObservedDesktop = desktopId;
            _observationDirty = false;
            if (!_enabled && _restorePending)
            {
                _writeGate.Close();
                _restoreRetryIntervalMs = StabilityProbeIntervalMs;
                _restoreTimer.Interval = _restoreRetryIntervalMs;
                _restoreNotBefore = DateTime.UtcNow.AddMilliseconds(MinimumSwitchFreezeMs);
                return;
            }
            BeginSwitchFreeze("desktop changed by shared probe");
        }
        if (_syncControl.InvokeRequired) PostToUi(Handle);
        else Handle();
    }

    /// <summary>
    /// A tracked MVD fullscreen window is outside AutoPin's domain until it is
    /// restored and untracked. Drop both lifecycle and baseline state while the
    /// FullScreenManager suspension still owns the transition.
    /// </summary>
    public void ExcludeTrackedFullscreenWindow(nint hwnd)
    {
        _engine.Forget(hwnd);
        _executor.Forget(hwnd);
        _workspaceFallbackTracker.Forget(hwnd);
        _minimizeRetryTracker.Forget(hwnd);
        _suppressionRetryGate.Forget(hwnd);
    }

    public void ExcludeManagedWorkspaceWindow(nint hwnd) =>
        ExcludeTrackedFullscreenWindow(hwnd);

    public void HandleWorkspaceMemberDetached(nint hwnd)
    {
        ExcludeManagedWorkspaceWindow(hwnd);
        if (!_enabled || _suspensionCount > 0) return;
        if (IsSameWindowFamily(NativeMethods.GetForegroundWindow(), hwnd))
            TryProtectForeground(hwnd);
        else
            RequestScan();
    }

    public void HandleWorkspaceRemoved(
        IEnumerable<SnapWorkspaceMember> participants, Guid fallbackDesktopId)
    {
        if (!_enabled) return;
        foreach (var participant in participants)
        {
            var identity = new AutoPinWindowIdentity(
                participant.Hwnd, participant.ProcessId);
            if (!IsSameLiveWindow(identity)) continue;
            _engine.Forget(identity.Hwnd);
            _executor.Forget(identity.Hwnd);
            _workspaceFallbackTracker.Track(identity, fallbackDesktopId);
        }
        _observationDirty = true;
    }

    public IDisposable Suspend(string reason)
    {
        _suspensionCount++;
        _writeGate.Close();
        Trace.WriteLine($"AutoPinService: suspended ({reason}), depth={_suspensionCount}.");
        return new Suspension(this, reason);
    }

    public void SetEnabled(bool enabled) =>
        SetMode(enabled ? AutoPinMode.TrackWindows : AutoPinMode.Off);

    public void SetMode(AutoPinMode mode)
    {
        if (_disposed || RequestedMode == mode) return;

        if (mode == AutoPinMode.Off)
        {
            _pendingMode = null;
            Disable();
            return;
        }

        if (_enabled && mode == _mode)
        {
            _pendingMode = null;
            return;
        }

        if (_enabled)
        {
            if (IsTransitioning)
            {
                _pendingMode = mode;
                Trace.WriteLine($"AutoPinService: queued mode {mode} until transition stabilizes.");
                return;
            }

            _pendingMode = mode;
            if (!TryCompleteActiveModeSwitch())
                BeginSwitchFreeze("active mode handoff needs a stable retry");
            return;
        }

        Enable(mode);
    }

    private void Enable(AutoPinMode mode)
    {
        var requiresReconciliation = _restorePending && _writeGate.IsClosed;
        _restorePending = false;
        _restoreTimer.Stop();
        _enabled = true;
        _mode = mode;
        _pendingMode = null;
        _restoreRetryIntervalMs = StabilityProbeIntervalMs;
        _observationDirty = false;
        _lastObservedDesktop = _vds.GetCurrentDesktopId();
        _foregroundTracker.Reset(NativeMethods.GetForegroundWindow());
        InstallHooks();
        _scanTimer.Start();
        _changeTimer.Start();
        if (requiresReconciliation)
        {
            BeginSwitchFreeze("re-enabled before pending restore stabilized");
            Trace.WriteLine($"AutoPinService: {mode} enabled; waiting for stable observations.");
            return;
        }

        Interlocked.Exchange(ref _switchFreezeRequested, 0);
        _transitionBarrier.Complete();
        _writeGate.Open();
        TryProtectForeground(NativeMethods.GetForegroundWindow());
        RequestScan();
        Trace.WriteLine($"AutoPinService: {mode} enabled.");
    }

    private void SwitchActiveMode(AutoPinMode mode)
    {
        if (!_enabled || mode == AutoPinMode.Off) return;
        _engine.Clear();
        _mode = mode;
        _pendingMode = null;
        _foregroundTracker.Reset(NativeMethods.GetForegroundWindow());
        TryProtectForeground(NativeMethods.GetForegroundWindow());
        RequestScan();
        Trace.WriteLine($"AutoPinService: switched active mode to {mode}.");
    }

    private bool TryCompleteActiveModeSwitch()
    {
        if (!_pendingMode.HasValue) return true;
        var desktop = _vds.GetCurrentDesktopId();
        if (!desktop.HasValue || !MayWrite(desktop.Value)) return false;
        if (!_executor.RestoreUserState(() => MayWrite(desktop.Value))) return false;

        var target = _pendingMode.Value;
        SwitchActiveMode(target);
        return true;
    }

    private void Disable()
    {
        if (!_enabled)
        {
            _mode = AutoPinMode.Off;
            return;
        }

        var mustDeferRestore = _suspensionCount > 0 || IsTransitioning;
        _enabled = false;
        _mode = AutoPinMode.Off;
        _pendingMode = null;
        _scanTimer.Stop();
        _changeTimer.Stop();
        _stabilityTimer.Stop();
        _engine.Clear();
        _workspaceFallbackTracker.Clear();
        _minimizeRetryTracker.Clear();
        _suppressionRetryGate.Clear();
        _observationDirty = false;
        _restorePending = true;
        _restoreRetryIntervalMs = StabilityProbeIntervalMs;
        _restoreTimer.Interval = _restoreRetryIntervalMs;
        _restoreNotBefore = mustDeferRestore
            ? DateTime.UtcNow.AddMilliseconds(MinimumSwitchFreezeMs)
            : DateTime.UtcNow;
        if (mustDeferRestore) _writeGate.Close();
        else _writeGate.Open();
        _changeTimer.Start();
        _restoreTimer.Start();
        TryCompleteDeferredRestore();
        Trace.WriteLine(mustDeferRestore
            ? "AutoPinService: disabled; baseline restore waiting for stable observations."
            : "AutoPinService: disabled; restoring known baseline.");
    }

    private void DetectChanges()
    {
        RetryPendingMinimizedWindows();
        var desktop = _vds.GetCurrentDesktopId();
        if (!desktop.HasValue) return;

        if (desktop != _lastObservedDesktop)
        {
            ObserveDesktopChange(desktop.Value);
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var activation = _foregroundTracker.ObservePoll(foreground);
        if (activation.HasValue && TryProtectForeground(activation.Value))
            _foregroundTracker.ConfirmProtection(activation.Value);

        if (!_observationDirty) return;
        _observationDirty = false;
        RequestScan();
    }

    private void OnDesktopSwitch(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (_disposed) return;
        var source = Volatile.Read(ref _suspensionCount) > 0
            ? AutoPinTransitionSource.Internal
            : AutoPinTransitionSource.External;
        var sourceDesktop = _lastObservedDesktop;
        _transitionBarrier.Begin(DateTime.UtcNow, source);
        _foregroundTracker.BeginTransition();
        _writeGate.Close();
        Interlocked.Exchange(ref _switchFreezeRequested, 1);
        if (!_enabled && _restorePending)
        {
            PostToUi(() =>
            {
                _restoreRetryIntervalMs = StabilityProbeIntervalMs;
                _restoreTimer.Interval = _restoreRetryIntervalMs;
                _restoreNotBefore = DateTime.UtcNow.AddMilliseconds(MinimumSwitchFreezeMs);
                Interlocked.Exchange(ref _switchFreezeRequested, 0);
            });
            return;
        }
        PostToUi(() =>
        {
            BeginSwitchFreeze("desktop switch event", source);
        });
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (!_enabled || _disposed) return;
        _foregroundTracker.RecordEvent(hwnd);

        void HandleForeground()
        {
            _observationDirty = true;
            if (IsTransitioning)
                _transitionBarrier.ObserveActivity(DateTime.UtcNow);
            var isFullscreenAnchorForeground =
                TryGetCurrentFullscreenAnchor(out var currentFullscreenDesktop, out var currentAnchor)
                && IsSameWindowFamily(hwnd, currentAnchor);
            var isSnapWorkspaceMemberForeground =
                TryGetCurrentSnapWorkspace(out var currentSnapDesktop, out var currentWorkspace)
                && currentWorkspace.Members.Any(member =>
                    IsSameWindowFamily(hwnd, member.Hwnd));
            var immediateDesktop = isFullscreenAnchorForeground
                ? currentFullscreenDesktop
                : currentSnapDesktop;
            var appliedImmediateObservation = false;
            if (AutoPinForegroundEventPolicy.RequiresImmediateFullObservation(
                    IsTransitioning,
                    isFullscreenAnchorForeground,
                    isSnapWorkspaceMemberForeground))
            {
                appliedImmediateObservation = ApplyObservation(BuildObservation(immediateDesktop));
            }
            if (isSnapWorkspaceMemberForeground)
                QueueSettledSnapCoverageReconciliation(currentSnapDesktop);
            var activation = _foregroundTracker.ObserveEvent(hwnd);
            if (activation.HasValue && (appliedImmediateObservation
                    || TryProtectForeground(activation.Value)))
                _foregroundTracker.ConfirmProtection(activation.Value);
            if (AutoPinForegroundEventPolicy.RequiresVisibleRelationshipReconciliation(
                    IsTransitioning,
                    isFullscreenAnchorForeground,
                    isSnapWorkspaceMemberForeground))
            {
                QueueVisibleRelationshipReconciliation();
            }
        }

        if (_syncControl.InvokeRequired) PostToUi(HandleForeground);
        else HandleForeground();
    }

    private void OnWindowChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if ((!_enabled && !_restorePending) || _disposed) return;
        if (eventType != AutoPinWindowEventPolicy.MoveSizeEnd
            && (objectId != NativeMethods.OBJID_WINDOW || childId != 0)) return;

        if (eventType is NativeMethods.EVENT_OBJECT_CREATE or NativeMethods.EVENT_OBJECT_DESTROY)
        {
            // Generation boundary: clear synchronously so same-process HWND reuse
            // cannot inherit state before a queued UI callback runs.
            _engine.Forget(hwnd);
            _executor.Forget(hwnd);
            _workspaceFallbackTracker.Forget(hwnd);
            _minimizeRetryTracker.Forget(hwnd);
            _suppressionRetryGate.Forget(hwnd);
        }

        if (_enabled
            && AutoPinWindowEventPolicy.AffectsWindowVisibilityRelationship(eventType))
        {
            QueueVisibleRelationshipReconciliation();
        }

        if (Interlocked.Exchange(ref _dirtyNotificationPosted, 1) != 0) return;
        PostToUi(() =>
        {
            Interlocked.Exchange(ref _dirtyNotificationPosted, 0);
            _observationDirty = true;
        });
    }

    private void OnUrgentWindowChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (!_enabled || _disposed || hwnd == IntPtr.Zero) return;
        if (!AutoPinWindowEventPolicy.RequiresImmediateReconciliation(eventType)) return;
        if (eventType == AutoPinWindowEventPolicy.WindowStateChange
            && (objectId != NativeMethods.OBJID_WINDOW || childId != 0))
        {
            return;
        }

        void Reconcile()
        {
            Trace.WriteLine(
                $"AutoPinService[t={Environment.TickCount64}]: "
                + $"immediate window-state reconciliation event=0x{eventType:X} hwnd={hwnd}.");
            if (eventType == AutoPinWindowEventPolicy.MinimizeEnd)
            {
                _minimizeRetryTracker.Forget(hwnd);
                ReconcileCurrentForeground();
                _observationDirty = true;
                QueueVisibleRelationshipReconciliation();
                return;
            }
            if (eventType == AutoPinWindowEventPolicy.WindowStateChange
                && !IsWindowFamilyMinimized(hwnd, hwnd))
            {
                ReconcileCurrentForeground();
                _observationDirty = true;
                QueueVisibleRelationshipReconciliation();
                return;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId != 0)
                _minimizeRetryTracker.Track(new AutoPinWindowIdentity(hwnd, processId));
            var urgentPinConfirmed = TryPinUrgentMinimizedWindow(eventType, hwnd);
            if (AutoPinWindowEventPolicy.RequiresFullReconciliationAfterMinimize(
                    urgentPinConfirmed))
            {
                QueueVisibleRelationshipReconciliation();
            }
        }

        if (_syncControl.InvokeRequired) PostToUi(Reconcile);
        else Reconcile();
    }

    private bool TryPinUrgentMinimizedWindow(uint eventType, nint hwnd)
    {
        if (!_vds.TryResolveAutoPinView(hwnd, out var viewHwnd, out _))
            return false;
        var isMinimized = IsWindowFamilyMinimized(hwnd, viewHwnd);
        if (!_enabled || _suspensionCount > 0
            || !AutoPinWindowEventPolicy.ShouldPinBeforeDesktopStability(
                eventType, isMinimized)
            || (!WindowEligibility.IsApplicationWindow(
                    hwnd,
                    _syncControl.Handle,
                    candidate => _tracker.IsTracked(candidate)
                        || _snapTracker.IsAttachedMember(candidate),
                    includeMinimized: true)
                && !WindowEligibility.IsApplicationWindow(
                    viewHwnd,
                    _syncControl.Handle,
                    candidate => _tracker.IsTracked(candidate)
                        || _snapTracker.IsAttachedMember(candidate),
                    includeMinimized: true)))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(viewHwnd, out var processId);
        var identity = new AutoPinWindowIdentity(viewHwnd, processId);
        var planDesktopId = _vds.GetDesktopIdForWindow(viewHwnd)
            ?? _lastObservedDesktop
            ?? Guid.Empty;
        var command = new AutoPinCommand(
            viewHwnd,
            processId,
            AutoPinTarget.Pinned,
            new AutoPinLifecycle(processId, AutoPinWindowMode.Pinned,
                FullscreenManaged: false, FullscreenAnchorHwnd: null));
        var plan = new AutoPinDecisionPlan(planDesktopId, [command]);

        // This is the narrow transition-time exception: Win+D has already made
        // the window invisible, so pinning it cannot leak a visible window into
        // the destination. Waiting for destination stability would lose the race
        // against launching the same application immediately after the switch.
        var result = _executor.Apply(plan, () =>
            _enabled
            && _suspensionCount == 0
            && IsSameLiveWindow(identity)
            && IsWindowFamilyMinimized(hwnd, viewHwnd));
        if (result.ConfirmedCommands.Count == 0) return false;

        _engine.Forget(viewHwnd);
        _workspaceFallbackTracker.Forget(viewHwnd);
        _minimizeRetryTracker.Forget(hwnd);
        _minimizeRetryTracker.Forget(viewHwnd);
        Trace.WriteLine(
            $"AutoPinService[t={Environment.TickCount64}]: "
            + $"urgent minimized window pinned before desktop stability "
            + $"eventHwnd={hwnd} viewHwnd={viewHwnd}.");
        return true;
    }

    private void RetryPendingMinimizedWindows()
    {
        if (!_enabled || _suspensionCount > 0) return;
        foreach (var identity in _minimizeRetryTracker.TakeRetryCandidates(IsSameLiveWindow))
            TryPinUrgentMinimizedWindow(AutoPinWindowEventPolicy.MinimizeStart, identity.Hwnd);
    }

    private static bool IsWindowFamilyMinimized(nint eventHwnd, nint viewHwnd)
    {
        if (NativeMethods.IsIconic(eventHwnd) || NativeMethods.IsIconic(viewHwnd))
            return true;
        var eventRoot = NativeMethods.GetAncestor(eventHwnd, NativeMethods.GA_ROOT);
        var eventOwner = NativeMethods.GetAncestor(eventHwnd, NativeMethods.GA_ROOTOWNER);
        var viewRoot = NativeMethods.GetAncestor(viewHwnd, NativeMethods.GA_ROOT);
        var viewOwner = NativeMethods.GetAncestor(viewHwnd, NativeMethods.GA_ROOTOWNER);
        return NativeMethods.IsIconic(eventRoot)
            || NativeMethods.IsIconic(eventOwner)
            || NativeMethods.IsIconic(viewRoot)
            || NativeMethods.IsIconic(viewOwner);
    }

    private void ReconcileCurrentForeground()
    {
        if (!_enabled || _suspensionCount > 0) return;
        var foreground = NativeMethods.GetForegroundWindow();
        _foregroundTracker.RequestProtection(foreground);
        if (TryProtectForeground(foreground))
            _foregroundTracker.ConfirmProtection(foreground);
    }

    /// <summary>
    /// Coalesces a burst of WinEvents into one immediate read-only snapshot of
    /// the current desktop. A command is never generated from the event window
    /// itself, so events raised by other desktops cannot cause cross-desktop
    /// pin/unpin writes.
    /// </summary>
    private void QueueVisibleRelationshipReconciliation()
    {
        if (!_enabled || _suspensionCount > 0 || _disposed) return;
        if (Interlocked.Exchange(ref _visibilityReconciliationPosted, 1) != 0) return;
        PostToUi(() =>
        {
            Interlocked.Exchange(ref _visibilityReconciliationPosted, 0);
            _observationDirty = true;
            if (!_enabled || _suspensionCount > 0) return;
            RequestScan();
        });
    }

    /// <summary>
    /// A foreground notification for a native Snap member can arrive one paint
    /// before Windows commits its new Z-order.  Recheck just that user action
    /// once after the compositor has settled; this is deliberately not a timer
    /// or a general visibility poll.
    /// </summary>
    private async void QueueSettledSnapCoverageReconciliation(Guid desktopId)
    {
        if (Interlocked.Exchange(ref _snapCoverageReconciliationPosted, 1) != 0) return;
        await Task.Delay(75);
        PostToUi(() =>
        {
            Interlocked.Exchange(ref _snapCoverageReconciliationPosted, 0);
            if (!_enabled || _suspensionCount > 0 || IsTransitioning
                || _vds.GetCurrentDesktopId() != desktopId
                || _snapTracker.GetByDesktop(desktopId) is null)
            {
                return;
            }
            RequestScan();
        });
    }

    private bool TryProtectForeground(nint hwnd)
    {
        if (!_enabled || _suspensionCount > 0) return false;
        var desktop = _vds.GetCurrentDesktopId();
        if (!desktop.HasValue) return false;

        _lastObservedDesktop = desktop;

        var observation = _observationBuilder.BuildForeground(
            desktop.Value, hwnd, IsMainDesktop(desktop.Value));
        if (observation is null) return false;
        if (!AutoPinForegroundProtectionPolicy.IsReady(observation)) return false;
        if (_vds.GetCurrentDesktopId() != desktop)
        {
            BeginSwitchFreeze("foreground observation became stale");
            return false;
        }
        if (!ApplyObservation(observation))
        {
            BeginSwitchFreeze("foreground execution became stale");
            return false;
        }
        if (!_vds.TryResolveAutoPinView(
                observation.ForegroundWindow, out _, out var isPinned))
        {
            return false;
        }
        return isPinned == AutoPinForegroundProtectionPolicy.ExpectedPinnedState(
            _mode, observation.DesktopKind);
    }

    private void BeginSwitchFreeze(
        string reason,
        AutoPinTransitionSource source = AutoPinTransitionSource.External)
    {
        if (!_enabled) return;
        _foregroundTracker.BeginTransition();
        _transitionBarrier.Begin(DateTime.UtcNow, source);
        _writeGate.Close();
        _stabilityTimer.Stop();
        _stabilityTimer.Interval = MinimumSwitchFreezeMs;
        _stabilityTimer.Start();
        Interlocked.Exchange(ref _switchFreezeRequested, 0);
        Trace.WriteLine(
            $"AutoPinService[t={Environment.TickCount64}]: freezing {source} writes ({reason}).");
    }

    private void RequestScan()
    {
        if (!_enabled || _suspensionCount > 0) return;
        _observationDirty = false;
        var desktop = _vds.GetCurrentDesktopId();
        if (!desktop.HasValue) return;
        _lastObservedDesktop = desktop;

        var observation = AutoPinForegroundProtectionPolicy.PromoteStableScan(
            BuildObservation(desktop.Value));
        if (!ApplyObservation(observation))
        {
            // The desktop changed while a read-only snapshot was being built.
            // A new desktop-switch notification or the low-frequency safety
            // scan will observe the destination; re-entering the stability
            // loop here caused repeated COM scans without a valid write.
            _observationDirty = true;
        }
    }

    private void ProbeStability()
    {
        _stabilityTimer.Stop();
        if (!_enabled || _suspensionCount > 0) return;

        var observation = BuildObservation();
        if (observation is null)
        {
            _writeGate.InvalidateObservation();
            ScheduleStabilityProbe();
            return;
        }
        var destinationReady = observation.DesktopKind != AutoPinDesktopKind.Fullscreen
            || observation.AnchorZOrder.HasValue;
        if (!_transitionBarrier.CanObserveDestination(DateTime.UtcNow, destinationReady))
        {
            _writeGate.InvalidateObservation();
            ScheduleStabilityProbe();
            return;
        }
        var fingerprint = CreateFingerprint(observation);
        if (_writeGate.TryOpenAfterStableObservation(
                observation.DesktopId,
                fingerprint,
                _vds.GetCurrentDesktopId()))
        {
            if (Volatile.Read(ref _switchFreezeRequested) != 0)
            {
                _writeGate.Close();
                ScheduleStabilityProbe();
                return;
            }
            _transitionBarrier.Complete();
            _lastObservedDesktop = observation.DesktopId;
            _foregroundTracker.CompleteTransition(observation.ForegroundWindow);
            if (_pendingMode.HasValue)
            {
                if (!TryCompleteActiveModeSwitch())
                {
                    BeginSwitchFreeze("active mode handoff restore needs retry");
                }
                return;
            }
            if (ApplyObservation(observation))
            {
                Trace.WriteLine(
                    $"AutoPinService[t={Environment.TickCount64}]: applied stable post-switch observation.");
                StableDesktopObservationApplied?.Invoke(observation.DesktopId);
                return;
            }

            BeginSwitchFreeze("stable observation became stale before execution");
            return;
        }

        ScheduleStabilityProbe();
    }

    private void ScheduleStabilityProbe()
    {
        _stabilityTimer.Interval = StabilityProbeIntervalMs;
        _stabilityTimer.Start();
    }

    private AutoPinObservation? BuildObservation()
    {
        var desktop = _vds.GetCurrentDesktopId();
        return desktop.HasValue
            ? BuildObservation(desktop.Value)
            : null;
    }

    private AutoPinObservation BuildObservation(Guid desktopId)
    {
        var observation = _observationBuilder.Build(
            desktopId, IsMainDesktop(desktopId));
        return observation;
    }

    private bool IsMainDesktop(Guid desktopId) => _mainDesktopProvider() == desktopId;

    private bool ApplyObservation(AutoPinObservation observation)
    {
        if (!MayWrite(observation.DesktopId)) return false;
        var plan = FilterSuppressionRetries(_engine.Evaluate(observation));
        if (!MayWrite(observation.DesktopId)) return false;

        var result = _executor.Apply(plan, () => MayWrite(observation.DesktopId));
        _suppressionRetryGate.Record(plan, result.ConfirmedCommands, DateTime.UtcNow);
        _engine.Commit(plan, result.ConfirmedCommands);
        foreach (var hwnd in _engine.ManagedWindows.Where(hwnd => !NativeMethods.IsWindow(hwnd)).ToArray())
            _engine.Forget(hwnd);
        TryPinMinimizedWorkspaceFallbackWindows(observation.DesktopId);
        return MayWrite(observation.DesktopId);
    }

    private void TryPinMinimizedWorkspaceFallbackWindows(Guid currentDesktopId)
    {
        if (!MayWrite(currentDesktopId)) return;
        foreach (var stale in _workspaceFallbackTracker.GetStale(IsSameLiveWindow))
            _workspaceFallbackTracker.Forget(stale.Hwnd);

        var candidates = _workspaceFallbackTracker.GetPinCandidates(
            currentDesktopId,
            identity => IsSameLiveWindow(identity)
                && NativeMethods.IsIconic(identity.Hwnd));
        if (candidates.Count == 0) return;

        var commands = candidates.Select(identity => new AutoPinCommand(
            identity.Hwnd,
            identity.ProcessId,
            AutoPinTarget.Pinned,
            new AutoPinLifecycle(identity.ProcessId, AutoPinWindowMode.Pinned,
                FullscreenManaged: false, FullscreenAnchorHwnd: null))).ToArray();
        var plan = new AutoPinDecisionPlan(currentDesktopId, commands);
        var result = _executor.Apply(plan, () => MayWrite(currentDesktopId));
        foreach (var command in result.ConfirmedCommands)
        {
            _workspaceFallbackTracker.Forget(command.Hwnd);
            _engine.Forget(command.Hwnd);
            Trace.WriteLine(
                $"AutoPinService: pinned minimized Snap fallback window {command.Hwnd}.");
        }
    }

    private static bool IsSameLiveWindow(AutoPinWindowIdentity identity)
    {
        if (!NativeMethods.IsWindow(identity.Hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(identity.Hwnd, out var processId);
        return processId == identity.ProcessId;
    }

    private bool TryGetCurrentFullscreenAnchor(out Guid desktopId, out nint anchor)
    {
        desktopId = default;
        anchor = nint.Zero;
        var current = _vds.GetCurrentDesktopId();
        if (!current.HasValue) return false;
        if (_snapTracker.GetByDesktop(current.Value) is not null) return false;
        var entry = _tracker.GetByDesktop(current.Value);
        if (entry is null || NativeMethods.IsWindowArranged(entry.Hwnd)) return false;
        desktopId = current.Value;
        anchor = entry.Hwnd;
        return true;
    }

    private bool TryGetCurrentSnapWorkspace(
        out Guid desktopId, out SnapWorkspaceEntry workspace)
    {
        desktopId = default;
        workspace = null!;
        var current = _vds.GetCurrentDesktopId();
        if (!current.HasValue) return false;
        if (_tracker.GetByDesktop(current.Value) is not null) return false;
        var found = _snapTracker.GetByDesktop(current.Value);
        if (found is null) return false;
        desktopId = current.Value;
        workspace = found;
        return true;
    }

    private static bool IsSameWindowFamily(nint left, nint right)
    {
        if (left == nint.Zero || right == nint.Zero) return false;
        if (left == right) return true;
        return NativeMethods.GetAncestor(left, NativeMethods.GA_ROOT) == right
            || NativeMethods.GetAncestor(left, NativeMethods.GA_ROOTOWNER) == right
            || NativeMethods.GetAncestor(right, NativeMethods.GA_ROOT) == left
            || NativeMethods.GetAncestor(right, NativeMethods.GA_ROOTOWNER) == left;
    }

    private bool ApplyPlan(AutoPinDecisionPlan plan)
    {
        var filteredPlan = FilterSuppressionRetries(plan);
        if (!MayWrite(filteredPlan.DesktopId)) return false;
        var result = _executor.Apply(filteredPlan, () => MayWrite(filteredPlan.DesktopId));
        _suppressionRetryGate.Record(
            filteredPlan, result.ConfirmedCommands, DateTime.UtcNow);
        _engine.Commit(filteredPlan, result.ConfirmedCommands);
        return MayWrite(filteredPlan.DesktopId)
            && result.ConfirmedCommands.Count == filteredPlan.Commands.Count;
    }

    private AutoPinDecisionPlan FilterSuppressionRetries(AutoPinDecisionPlan plan) =>
        _suppressionRetryGate.Filter(plan, DateTime.UtcNow);


    private bool MayWrite(Guid observationDesktopId)
    {
        if (!_enabled || _suspensionCount > 0) return false;
        return _vds.GetCurrentDesktopId() == observationDesktopId;
    }

    private static string CreateFingerprint(AutoPinObservation observation) => string.Join('|',
        observation.DesktopId,
        observation.DesktopKind,
        observation.ForegroundWindow,
        observation.AnchorZOrder,
        string.Join(',', observation.Windows.Select(window =>
            $"{window.Hwnd}:{window.ProcessId}:{window.IsMinimized}:{window.IsDisplayed}:"
            + $"{window.ZOrder}:{window.IsOnCurrentDesktop}:{window.IsPinned}")));

    private void InstallHooks()
    {
        if (_desktopSwitchHook != IntPtr.Zero) return;
        _desktopSwitchHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH,
            NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH,
            IntPtr.Zero,
            _desktopSwitchProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _windowChangeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _windowChangeProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _visibilityRelationshipHook = NativeMethods.SetWinEventHook(
            AutoPinWindowEventPolicy.ObjectHide,
            AutoPinWindowEventPolicy.ObjectReorder,
            IntPtr.Zero,
            _windowChangeProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _moveSizeEndHook = NativeMethods.SetWinEventHook(
            AutoPinWindowEventPolicy.MoveSizeEnd,
            AutoPinWindowEventPolicy.MoveSizeEnd,
            IntPtr.Zero,
            _windowChangeProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _minimizeHook = NativeMethods.SetWinEventHook(
            AutoPinWindowEventPolicy.MinimizeStart,
            AutoPinWindowEventPolicy.MinimizeEnd,
            IntPtr.Zero,
            _urgentWindowChangeProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        _windowStateHook = NativeMethods.SetWinEventHook(
            AutoPinWindowEventPolicy.WindowStateChange,
            AutoPinWindowEventPolicy.WindowStateChange,
            IntPtr.Zero,
            _urgentWindowChangeProc,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    private void UninstallHooks()
    {
        if (_desktopSwitchHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_desktopSwitchHook);
            _desktopSwitchHook = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        if (_windowChangeHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_windowChangeHook);
            _windowChangeHook = IntPtr.Zero;
        }
        if (_visibilityRelationshipHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_visibilityRelationshipHook);
            _visibilityRelationshipHook = IntPtr.Zero;
        }
        if (_moveSizeEndHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_moveSizeEndHook);
            _moveSizeEndHook = IntPtr.Zero;
        }
        if (_minimizeHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_minimizeHook);
            _minimizeHook = IntPtr.Zero;
        }
        if (_windowStateHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_windowStateHook);
            _windowStateHook = IntPtr.Zero;
        }
    }

    private void PostToUi(Action action)
    {
        if (_syncControl.IsDisposed || !_syncControl.IsHandleCreated) return;
        try { _syncControl.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void Resume(string reason)
    {
        if (_suspensionCount == 0) return;
        _suspensionCount--;
        Trace.WriteLine($"AutoPinService: resumed ({reason}), depth={_suspensionCount}.");
        if (_suspensionCount == 0 && _enabled)
            BeginSwitchFreeze("MVD/Restore completed", AutoPinTransitionSource.Internal);
        else if (_suspensionCount == 0 && _restorePending)
        {
            _writeGate.Close();
            _restoreRetryIntervalMs = StabilityProbeIntervalMs;
            _restoreTimer.Interval = _restoreRetryIntervalMs;
            _restoreNotBefore = DateTime.UtcNow.AddMilliseconds(MinimumSwitchFreezeMs);
            _restoreTimer.Start();
        }
    }

    private void TryCompleteDeferredRestore()
    {
        if (!_restorePending) return;
        if (_suspensionCount > 0
            || Volatile.Read(ref _switchFreezeRequested) != 0
            || DateTime.UtcNow < _restoreNotBefore)
        {
            return;
        }

        var observation = BuildObservation();
        if (observation is null)
        {
            _writeGate.InvalidateObservation();
            BackOffRestoreRetry();
            return;
        }

        var fingerprint = CreateFingerprint(observation);
        if (!_writeGate.TryOpenAfterStableObservation(
                observation.DesktopId,
                fingerprint,
                _vds.GetCurrentDesktopId()))
        {
            return;
        }

        if (_executor.RestoreUserState(() => MayRestore(observation.DesktopId)))
        {
            FinishDisableRestore();
            return;
        }

        _writeGate.Close();
        BackOffRestoreRetry();
    }

    private void BackOffRestoreRetry()
    {
        _restoreRetryIntervalMs = Math.Min(
            MaximumRestoreRetryIntervalMs, _restoreRetryIntervalMs * 2);
        _restoreTimer.Interval = _restoreRetryIntervalMs;
        _restoreNotBefore = DateTime.UtcNow.AddMilliseconds(_restoreRetryIntervalMs);
    }

    private bool MayRestore(Guid observationDesktopId) =>
        _restorePending
        && _suspensionCount == 0
        && !_writeGate.IsClosed
        && Volatile.Read(ref _switchFreezeRequested) == 0
        && _vds.GetCurrentDesktopId() == observationDesktopId;

    private void FinishDisableRestore()
    {
        _restoreTimer.Stop();
        _changeTimer.Stop();
        _restorePending = false;
        _restoreRetryIntervalMs = StabilityProbeIntervalMs;
        Interlocked.Exchange(ref _switchFreezeRequested, 0);
        UninstallHooks();
        Trace.WriteLine("AutoPinService: restored known user pin state.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetEnabled(false);
        if (_restorePending)
        {
            Trace.WriteLine(
                "AutoPinService: shutdown skipped unsafe or unavailable baseline restore.");
            _restorePending = false;
        }
        _disposed = true;
        _scanTimer.Dispose();
        _changeTimer.Dispose();
        _stabilityTimer.Dispose();
        _restoreTimer.Dispose();
        UninstallHooks();
    }

    private sealed class Suspension : IDisposable
    {
        private AutoPinService? _owner;
        private readonly string _reason;

        public Suspension(AutoPinService owner, string reason) =>
            (_owner, _reason) = (owner, reason);

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Resume(_reason);
        }
    }
}
