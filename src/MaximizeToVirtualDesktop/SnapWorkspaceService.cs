using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Detects completed native Snap layouts and owns their multi-window temporary desktops.
/// All callbacks are serialized onto the tray UI thread.
/// </summary>
internal sealed class SnapWorkspaceService : IDisposable
{
    private const int IdleProbeIntervalMs = 500;
    // This probe never enumerates top-level windows.  It only verifies the
    // members of already-created Snap workspaces, so it can be responsive
    // without reintroducing a global polling cost.
    private const int ActiveWorkspaceProbeIntervalMs = 250;
    private const int StableLayoutMs = 300;
    private const int WorkspaceHealthProbeMs = 3000;
    // WinEvent hooks wake the service for real work. This is only a recovery
    // heartbeat for a missed desktop-switch notification.
    private const int IdleDesktopVerificationMs = 3000;
    private const int GeometryTolerancePixels = 4;

    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _fullScreenTracker;
    private readonly SnapWorkspaceTracker _tracker;
    private readonly AutoPinService _autoPin;
    private readonly AppSettings _settings;
    private readonly Control _syncControl;
    private readonly Func<Guid?> _mainDesktopProvider;
    private readonly System.Windows.Forms.Timer _probeTimer;
    private readonly NativeMethods.WinEventProc _windowEventProc;
    private readonly Dictionary<string, SnapLayoutStabilityGate> _layoutGates = new();
    private readonly Dictionary<nint, DateTime> _geometryMismatchSince = new();
    private IntPtr _locationHook;
    private IntPtr _moveSizeStartHook;
    private IntPtr _moveSizeHook;
    private IntPtr _destroyHook;
    private IntPtr _hideHook;
    private IntPtr _minimizeStartHook;
    private bool _dirty = true;
    private int _geometryDirtyNotification;
    // 1 = geometry is still moving; 2 = a move/resize completed.
    // The probe thread turns either into one layout observation.
    private int _layoutObservationNotification;
    // Re-adoption needs a whole top-level-window enumeration.  Geometry health
    // checks do not: they only inspect the members we already own.
    private int _readoptionRequested = 1;
    private bool _inFlight;
    private bool _disposed;
    private Guid? _lastDesktopId;
    private DateTime _desktopStableAfter;
    private DateTime _nextWorkspaceHealthProbe;
    private DateTime _nextIdleDesktopVerification;
    private DateTime _layoutObservationAfter;
    private readonly HashSet<nint> _pendingMemberAvailabilityChecks = [];
    private readonly HashSet<nint> _pendingNativeLayoutCandidates = [];
    // Windows clears its native-arranged bit at the beginning of a drag, not
    // when the user releases the caption. Keep that transitional state local
    // to tracked Snap members until MOVESIZEEND provides the real boundary.
    private readonly HashSet<nint> _membersInMoveSize = [];

    /// <summary>Raised after workspace cleanup switches the user to Desktop 1.</summary>
    public event Action? WorkspaceRemovalSwitchedDesktop;

    public SnapWorkspaceService(
        VirtualDesktopService vds,
        FullScreenTracker fullScreenTracker,
        SnapWorkspaceTracker tracker,
        AutoPinService autoPin,
        AppSettings settings,
        Control syncControl,
        Func<Guid?> mainDesktopProvider)
    {
        _vds = vds;
        _fullScreenTracker = fullScreenTracker;
        _tracker = tracker;
        _autoPin = autoPin;
        _settings = settings;
        _syncControl = syncControl;
        _mainDesktopProvider = mainDesktopProvider;
        _windowEventProc = OnWindowEvent;
        _probeTimer = new System.Windows.Forms.Timer { Interval = IdleProbeIntervalMs };
        _probeTimer.Tick += (_, _) => Probe();
        _autoPin.StableDesktopObservationApplied += OnAutoPinDesktopSettled;
    }

    public void Start()
    {
        if (_disposed || _locationHook != IntPtr.Zero) return;
        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _moveSizeStartHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZESTART,
            NativeMethods.EVENT_SYSTEM_MOVESIZESTART,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _moveSizeHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _destroyHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _hideHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_HIDE,
            NativeMethods.EVENT_OBJECT_HIDE,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _minimizeStartHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            IntPtr.Zero, _windowEventProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _probeTimer.Start();
        Trace.WriteLine("SnapWorkspaceService: started.");
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        if (_disposed) return;
        if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZESTART)
        {
            PostToUi(() => BeginMemberMove(hwnd));
            return;
        }
        if (eventType == NativeMethods.EVENT_SYSTEM_MINIMIZESTART)
        {
            PostToUi(() =>
            {
                PrepareDesktopOneAnimation(hwnd, "minimize");
                QueueMemberAvailabilityCheck(hwnd, "minimize");
            });
            return;
        }
        if (eventType != NativeMethods.EVENT_SYSTEM_MOVESIZEEND
            && (objectId != NativeMethods.OBJID_WINDOW || childId != 0)) return;
        if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZEEND)
        {
            // A completed drag/resize is the meaningful boundary at which a
            // window can join an existing native Snap workspace.
            if (_tracker.IsAttachedMember(hwnd))
                PostToUi(() => FinishMemberMove(hwnd));
            if (IsWindowArranged(hwnd))
                PostToUi(() => QueueNativeLayoutCandidate(hwnd));
            Interlocked.Exchange(ref _readoptionRequested, 1);
            Interlocked.Exchange(ref _geometryDirtyNotification, 1);
            Interlocked.Exchange(ref _layoutObservationNotification, 2);
            return;
        }
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            // A Snap member can be maximized into a new MVD desktop. Its old
            // workspace must then be reconciled even though it is no longer
            // the current desktop.
            if (_tracker.IsAttachedMember(hwnd))
                PostToUi(() => QueueMemberAvailabilityCheck(hwnd, "location change"));
            if (IsWindowArranged(hwnd))
                PostToUi(() => QueueNativeLayoutCandidate(hwnd));
            // Dragging emits LOCATIONCHANGE continuously. Probe consumes this
            // flag at most twice per second on the UI thread.
            if (Volatile.Read(ref _geometryDirtyNotification) == 0)
                Interlocked.Exchange(ref _geometryDirtyNotification, 1);
            if (Volatile.Read(ref _layoutObservationNotification) == 0)
                Interlocked.Exchange(ref _layoutObservationNotification, 1);
            return;
        }
        PostToUi(() =>
        {
            if (eventType == NativeMethods.EVENT_OBJECT_DESTROY)
            {
                var workspace = _tracker.GetByMember(hwnd);
                if (workspace is null) return;
                workspace.Detach(hwnd);
                _geometryMismatchSince.Remove(hwnd);
                if (workspace.IsEmpty) RemoveWorkspace(workspace);
                return;
            }
            if (eventType == NativeMethods.EVENT_OBJECT_HIDE)
                QueueMemberAvailabilityCheck(hwnd, "hide");
        });
    }

    /// <summary>
    /// A desktop switch alone cannot create a Snap layout. Once AutoPin has
    /// settled the same desktop, recheck existing workspace members only; do
    /// not enumerate every top-level window on a normal desktop.
    /// </summary>
    public void ObserveDesktopChange(Guid desktopId)
    {
        if (_disposed) return;
        _lastDesktopId = desktopId;
        _desktopStableAfter = DateTime.MaxValue;
        // Discard work queued by the desktop we just left. A desktop switch by
        // itself must not be interpreted as a new native Snap arrangement.
        _dirty = false;
        Interlocked.Exchange(ref _geometryDirtyNotification, 0);
        Interlocked.Exchange(ref _layoutObservationNotification, 0);
        _layoutObservationAfter = DateTime.MinValue;
        foreach (var gate in _layoutGates.Values) gate.Reset();
    }

    public void ObserveDesktopSettled(Guid desktopId)
    {
        if (_disposed) return;
        OnDesktopSettled(desktopId);
    }

    private void OnAutoPinDesktopSettled(Guid desktopId) => OnDesktopSettled(desktopId);

    private void OnDesktopSettled(Guid desktopId)
    {
        if (_disposed || _lastDesktopId != desktopId) return;
        _desktopStableAfter = DateTime.UtcNow;
        var workspace = _tracker.GetByDesktop(desktopId);
        if (workspace is null) return;
        _dirty = true;
        Interlocked.Exchange(ref _readoptionRequested, 1);
    }

    private void QueueMemberAvailabilityCheck(nint hwnd, string signal)
    {
        if (_disposed || !_pendingMemberAvailabilityChecks.Add(hwnd)) return;
        var workspace = _tracker.GetByMember(hwnd);
        if (workspace is null)
        {
            _pendingMemberAvailabilityChecks.Remove(hwnd);
            return;
        }
        // These calls are already serialized onto the UI thread.  A Snap
        // workspace must disappear as soon as its final member is no longer
        // valid; delaying this leaves an orphan desktop behind.
        ReconcileMemberAvailability(hwnd, workspace.TempDesktopId, signal);
    }

    private void ReconcileMemberAvailability(nint hwnd, Guid sourceDesktopId, string signal)
    {
        _pendingMemberAvailabilityChecks.Remove(hwnd);
        if (_disposed) return;
        var workspace = _tracker.GetByMember(hwnd);
        if (workspace is null || workspace.TempDesktopId != sourceDesktopId) return;

        // The window is still being dragged. Its arrangement and geometry are
        // intentionally transient; defer workspace exit until MOVESIZEEND.
        if (_membersInMoveSize.Contains(hwnd)) return;

        var isAlive = NativeMethods.IsWindow(hwnd);
        var isOnWorkspaceDesktop = isAlive && IsWindowOnDesktop(hwnd, sourceDesktopId);
        var currentDesktopId = _vds.GetCurrentDesktopId();

        // A desktop switch hides the old desktop's windows. If the member is
        // still on that workspace but it is no longer current, this was only a
        // switch notification, not a lifecycle change.
        if (isOnWorkspaceDesktop && currentDesktopId != sourceDesktopId)
        {
            return;
        }

        if (!SnapWorkspacePolicy.ShouldDetachAfterRecheck(
                isAlive,
                isOnWorkspaceDesktop,
                isAlive && NativeMethods.IsWindowVisible(hwnd),
                isAlive && NativeMethods.IsIconic(hwnd)))
        {
            return;
        }

        Detach(workspace, hwnd, $"{signal} left visible Snap layout");
        if (workspace.IsEmpty) RemoveWorkspace(workspace);
    }

    private void BeginMemberMove(nint hwnd)
    {
        if (_settings.RestoreAnimationMode == RestoreAnimationMode.DesktopOne)
        {
            PrepareDesktopOneAnimation(hwnd, "drag");
            return;
        }
        if (_tracker.IsAttachedMember(hwnd)) _membersInMoveSize.Add(hwnd);
    }

    private void PrepareDesktopOneAnimation(nint hwnd, string interaction)
    {
        if (_settings.RestoreAnimationMode != RestoreAnimationMode.DesktopOne) return;
        var workspace = _tracker.GetByMember(hwnd);
        if (workspace is null || workspace.Members.Count != 1
            || !IsWindowOnDesktop(hwnd, workspace.TempDesktopId)) return;

        // This is the explicitly selected legacy-style behavior: return
        // before Windows draws the final drag/minimize animation.
        Detach(workspace, hwnd, $"{interaction} starts on Desktop 1");
        if (workspace.IsEmpty) RemoveWorkspace(workspace);
    }

    private void FinishMemberMove(nint hwnd)
    {
        _membersInMoveSize.Remove(hwnd);
        var workspace = _tracker.GetByMember(hwnd);
        if (workspace is null || _vds.GetCurrentDesktopId() != workspace.TempDesktopId) return;

        // This is the same stable interaction boundary used by the fullscreen
        // lifecycle: decide only after the user has released the window.
        ObserveWorkspaceMembers(workspace, shouldReadopt: true);
    }

    /// <summary>
    /// Normal desktops use the same native-Snap trigger as a fullscreen-to-Snap
    /// promotion: an arranged window event, followed by one short settle delay
    /// and a complete native layout check. This deliberately does not poll or
    /// infer Snap from arbitrary window geometry.
    /// </summary>
    private void QueueNativeLayoutCandidate(nint hwnd)
    {
        if (_disposed || _inFlight || !_pendingNativeLayoutCandidates.Add(hwnd)) return;
        var sourceDesktopId = _vds.GetCurrentDesktopId();
        if (!sourceDesktopId.HasValue || !_vds.IsWindowOnCurrentDesktop(hwnd))
        {
            _pendingNativeLayoutCandidates.Remove(hwnd);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(FullscreenExitPolicy.ArbitrationDelay);
            PostToUi(() => ReconcileNativeLayoutCandidate(hwnd, sourceDesktopId.Value));
        });
    }

    private void ReconcileNativeLayoutCandidate(nint hwnd, Guid sourceDesktopId)
    {
        _pendingNativeLayoutCandidates.Remove(hwnd);
        if (_disposed || _inFlight
            || _vds.GetCurrentDesktopId() != sourceDesktopId
            || _tracker.GetByDesktop(sourceDesktopId) is not null
            || !NativeMethods.IsWindow(hwnd)
            || !IsWindowArranged(hwnd)
            || !IsWindowOnDesktop(hwnd, sourceDesktopId))
        {
            return;
        }

        var layout = SnapWorkspacePolicy.FindLayoutContainingWindow(
            ObserveCompletedLayouts(sourceDesktopId), hwnd);
        if (layout is null) return;

        // A layout containing the existing fullscreen anchor is promoted by
        // WindowMonitor's fullscreen-to-Snap path.  A layout formed only by
        // other windows is independent and must create its own workspace.
        var fullscreen = _fullScreenTracker.GetByDesktop(sourceDesktopId);
        if (fullscreen is not null
            && layout.Members.Any(member => member.Hwnd == fullscreen.Hwnd))
        {
            return;
        }

        Trace.WriteLine(
            $"SnapWorkspaceService: completed native Snap layout on desktop "
            + $"{sourceDesktopId}; creating workspace from candidate {hwnd}.");
        CreateWorkspace(layout);
    }

    private void Probe()
    {
        if (_disposed || _inFlight) return;
        ReconcileMembersMovedAwayFromTrackedWorkspaces();
        if (_inFlight) return;
        var geometryChanged = Interlocked.Exchange(ref _geometryDirtyNotification, 0) != 0;
        var layoutSignal = Interlocked.Exchange(ref _layoutObservationNotification, 0);
        var now = DateTime.UtcNow;
        if (now < _desktopStableAfter) return;
        var requiresIdleVerification = now >= _nextIdleDesktopVerification;
        if (!geometryChanged && layoutSignal == 0 && !_dirty)
        {
            if (!requiresIdleVerification) return;
            var verifiedDesktopId = _vds.GetCurrentDesktopId();
            if (!verifiedDesktopId.HasValue) return;
            _nextIdleDesktopVerification = now.AddMilliseconds(IdleDesktopVerificationMs);
            if (_lastDesktopId != verifiedDesktopId)
            {
                // Recovery only: the shared coordinator normally reports this
                // within 250ms. Do not treat the missed notification as a Snap
                // layout signal.
                ObserveDesktopChange(verifiedDesktopId.Value);
                _desktopStableAfter = now.AddMilliseconds(500);
                return;
            }
            var idleWorkspace = _tracker.GetByDesktop(verifiedDesktopId.Value);
            if (idleWorkspace is null) return;
            ObserveWorkspaceMembers(idleWorkspace, shouldReadopt: false);
            _nextWorkspaceHealthProbe = now.AddMilliseconds(WorkspaceHealthProbeMs);
            return;
        }
        var currentDesktopId = _vds.GetCurrentDesktopId();
        if (!currentDesktopId.HasValue) return;
        _nextIdleDesktopVerification = now.AddMilliseconds(IdleDesktopVerificationMs);
        if (_lastDesktopId != currentDesktopId)
        {
            _lastDesktopId = currentDesktopId;
            _dirty = true;
            _desktopStableAfter = now.AddMilliseconds(500);
            foreach (var gate in _layoutGates.Values) gate.Reset();
            return;
        }

        var currentWorkspace = _tracker.GetByDesktop(currentDesktopId.Value);
        var currentFullscreen = _fullScreenTracker.GetByDesktop(currentDesktopId.Value);
        if (!ManagedDesktopKindPolicy.TryResolve(
                currentFullscreen is not null,
                currentWorkspace is not null,
                out var desktopKind))
        {
            Trace.WriteLine(
                $"SnapWorkspaceService: conflicting owners for desktop {currentDesktopId}; probe skipped.");
            return;
        }
        if (currentWorkspace is not null)
        {
            if (geometryChanged) _dirty = true;
            var workspaceNow = now;
            if (!_dirty && workspaceNow < _nextWorkspaceHealthProbe) return;
            var shouldReadopt = Interlocked.Exchange(ref _readoptionRequested, 0) != 0;
            ObserveWorkspaceMembers(currentWorkspace, shouldReadopt);
            _dirty = false;
            _nextWorkspaceHealthProbe = workspaceNow.AddMilliseconds(
                _geometryMismatchSince.Count == 0
                    ? WorkspaceHealthProbeMs
                    : ActiveWorkspaceProbeIntervalMs);
            return;
        }

        // A layout cannot be complete while a window is still moving.  Native
        // Snap may also be invoked by the keyboard and only produce geometry
        // events, so wait for one stable interval in that case.  A real
        // move/resize end is already a stable boundary and can be observed at
        // the next probe tick.
        if (layoutSignal == 2)
            _layoutObservationAfter = now;
        else if (layoutSignal == 1)
            _layoutObservationAfter = now.AddMilliseconds(StableLayoutMs);
        if (now < _layoutObservationAfter) return;
        if (_layoutObservationAfter != DateTime.MinValue)
        {
            _layoutObservationAfter = DateTime.MinValue;
            _dirty = true;
        }

        if (!SnapWorkspacePolicy.ShouldObserveNewLayout(
                _dirty,
                hasExistingSnapWorkspace: desktopKind == ManagedDesktopKind.Snap,
                isFullscreenDesktop: desktopKind == ManagedDesktopKind.Fullscreen)) return;

        var layouts = ObserveCompletedLayouts(currentDesktopId.Value);
        CompletedSnapLayout? stable = null;
        foreach (var layout in layouts)
        {
            if (!_layoutGates.TryGetValue(layout.MonitorId, out var gate))
            {
                gate = new SnapLayoutStabilityGate(TimeSpan.FromMilliseconds(StableLayoutMs));
                _layoutGates[layout.MonitorId] = gate;
            }
            if (gate.Observe(layout, now))
            {
                stable = layout;
                break;
            }
        }

        var observedMonitors = layouts.Select(layout => layout.MonitorId).ToHashSet();
        foreach (var pair in _layoutGates.Where(pair => !observedMonitors.Contains(pair.Key)))
            pair.Value.Reset();

        if (stable is not null) CreateWorkspace(stable);
        _dirty = layouts.Count > 0;
    }

    /// <summary>
    /// A member moved to a new MVD desktop does not reliably produce a usable
    /// WinEvent on its old desktop. Check only members we already own; this is
    /// deliberately not a global EnumWindows scan.
    /// </summary>
    private void ReconcileMembersMovedAwayFromTrackedWorkspaces()
    {
        var workspaces = _tracker.GetAll();
        RefreshProbeInterval(workspaces.Count != 0);
        foreach (var workspace in workspaces)
        {
            foreach (var member in workspace.Members.ToArray())
            {
                if (!IsSameLiveWindow(member))
                {
                    Detach(workspace, member.Hwnd, "destroyed or HWND reused");
                    continue;
                }

                // The public virtual-desktop membership query is not reliable
                // for a UWP frame on a non-current desktop. Its WinEvents are
                // still handled above; do not make an incorrect remote claim.
                if (WindowStateHelper.IsUwpWindow(member.Hwnd)) continue;
                if (_vds.GetDesktopIdForWindow(member.Hwnd) != workspace.TempDesktopId)
                    Detach(workspace, member.Hwnd, "moved to another desktop");
            }

            if (workspace.IsEmpty) RemoveWorkspace(workspace);
            if (_inFlight) return;
        }
    }

    private void RefreshProbeInterval(bool hasTrackedWorkspace)
    {
        var target = hasTrackedWorkspace
            ? ActiveWorkspaceProbeIntervalMs
            : IdleProbeIntervalMs;
        if (_probeTimer.Interval != target) _probeTimer.Interval = target;
    }

    private List<CompletedSnapLayout> ObserveCompletedLayouts(Guid desktopId)
    {
        var rawCandidates = new List<ObservedSnapWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!WindowEligibility.IsApplicationWindow(hwnd, _syncControl.Handle,
                    candidate => IsManagedWindowForDiscovery(candidate, desktopId),
                    includeMinimized: false)
                || NativeMethods.IsWindowCloaked(hwnd)
                || !IsWindowArranged(hwnd)
                || !NativeMethods.TryGetVisibleFrameBounds(hwnd, out var nativeRect))
            {
                return true;
            }

            var screen = Screen.FromHandle(hwnd);
            var area = screen.WorkingArea;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            rawCandidates.Add(new ObservedSnapWindow(
                hwnd,
                processId,
                screen.DeviceName,
                new SnapRect(area.Left, area.Top, area.Right, area.Bottom),
                new SnapRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom)));
            return true;
        }, IntPtr.Zero);

        var byMonitor = new Dictionary<string, (SnapRect WorkArea, List<SnapLayoutWindow> Windows)>();
        // Virtual-desktop COM calls deliberately happen after EnumWindows returns.
        foreach (var candidate in rawCandidates)
        {
            if (_vds.GetDesktopIdForWindow(candidate.Hwnd) != desktopId) continue;
            if (!byMonitor.TryGetValue(candidate.MonitorId, out var group))
                group = (candidate.WorkArea, []);
            group.Windows.Add(new SnapLayoutWindow(
                candidate.Hwnd, candidate.ProcessId, candidate.Frame));
            byMonitor[candidate.MonitorId] = group;
        }

        return byMonitor.Select(pair => SnapWorkspacePolicy.TryCompleteLayout(
                desktopId, pair.Key, pair.Value.WorkArea, pair.Value.Windows,
                GeometryTolerancePixels))
            .Where(layout => layout is not null).Cast<CompletedSnapLayout>().ToList();
    }

    private bool IsManagedWindow(nint hwnd) =>
        _fullScreenTracker.IsTracked(hwnd) || _tracker.IsAttachedMember(hwnd);

    private bool IsManagedWindowForDiscovery(nint hwnd, Guid sourceDesktopId)
    {
        if (_tracker.IsAttachedMember(hwnd)) return true;
        var fullscreen = _fullScreenTracker.Get(hwnd);
        return fullscreen is not null
            && fullscreen.TempDesktopId != sourceDesktopId;
    }

    public bool TryPromoteFullscreenWindow(nint hwnd)
    {
        if (_disposed || _inFlight) return false;
        var fullscreen = _fullScreenTracker.Get(hwnd);
        if (fullscreen is null
            || _vds.GetCurrentDesktopId() != fullscreen.TempDesktopId
            || !IsWindowArranged(hwnd))
        {
            return false;
        }

        var layout = SnapWorkspacePolicy.FindLayoutContainingWindow(
            ObserveCompletedLayouts(fullscreen.TempDesktopId), hwnd);
        if (layout is null) return false;

        return PromoteFullscreenDesktop(
            fullscreen,
            layout.MonitorId,
            layout.WorkArea,
            layout.Members);
    }

    private bool PromoteFullscreenDesktop(
        TrackingEntry fullscreen,
        string monitorId,
        SnapRect workArea,
        IReadOnlyList<SnapLayoutWindow> members)
    {
        if (_inFlight
            || _vds.GetCurrentDesktopId() != fullscreen.TempDesktopId
            || _tracker.GetByDesktop(fullscreen.TempDesktopId) is not null
            || _fullScreenTracker.GetAll().Count(entry =>
                entry.TempDesktopId == fullscreen.TempDesktopId) != 1)
        {
            return false;
        }

        var memberSnapshot = members
            .Where(member => IsSameLiveWindow(member.Hwnd, member.ProcessId)
                && IsWindowArranged(member.Hwnd)
                && IsWindowOnDesktop(member.Hwnd, fullscreen.TempDesktopId))
            .ToArray();
        if (memberSnapshot.Length != members.Count
            || !memberSnapshot.Any(member => member.Hwnd == fullscreen.Hwnd))
        {
            return false;
        }

        _inFlight = true;
        using var suspension = _autoPin.Suspend("Fullscreen to Snap promotion");
        var baselines = new Dictionary<nint, bool>();
        try
        {
            foreach (var member in memberSnapshot)
            {
                var isUwp = WindowStateHelper.IsUwpWindow(member.Hwnd);
                if (_vds.TryGetWindowPinnedState(member.Hwnd, out var wasPinned))
                {
                    baselines[member.Hwnd] = wasPinned;
                }
                else if (!isUwp)
                {
                    RestorePinBaselines(baselines);
                    return false;
                }

                if ((wasPinned && !_vds.UnpinWindow(member.Hwnd))
                    || (!_vds.TryGetWindowPinnedState(member.Hwnd, out var stillPinned)
                        && !isUwp)
                    || stillPinned)
                {
                    RestorePinBaselines(baselines);
                    return false;
                }
            }

            var trackedMembers = memberSnapshot.Select(member =>
                new SnapWorkspaceMember(
                    member.Hwnd,
                    member.ProcessId,
                    member.Frame,
                    UsesNativeArrangedState: true)).ToArray();
            var workspace = new SnapWorkspaceEntry(
                Guid.NewGuid(),
                fullscreen.OriginalDesktopId,
                fullscreen.TempDesktopId,
                fullscreen.TempDesktop,
                monitorId,
                workArea,
                trackedMembers);

            var transferred = _fullScreenTracker.Untrack(fullscreen.Hwnd);
            if (transferred is null)
            {
                RestorePinBaselines(baselines);
                return false;
            }

            try
            {
                _tracker.Track(workspace);
            }
            catch
            {
                _fullScreenTracker.Track(transferred);
                RestorePinBaselines(baselines);
                throw;
            }

            foreach (var member in trackedMembers)
                _autoPin.ExcludeManagedWorkspaceWindow(member.Hwnd);
            _vds.SetDesktopName(fullscreen.TempDesktop, $"[MVD Snap] {monitorId}");
            _layoutGates.Clear();
            _dirty = true;
            Trace.WriteLine(
                $"SnapWorkspaceService: promoted fullscreen desktop "
                + $"{fullscreen.TempDesktopId} to Snap workspace {workspace.WorkspaceId} "
                + $"members={trackedMembers.Length}.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"SnapWorkspaceService: fullscreen-to-Snap promotion failed: {ex.Message}");
            return false;
        }
        finally
        {
            _inFlight = false;
        }
    }

    private void RestorePinBaselines(IReadOnlyDictionary<nint, bool> baselines)
    {
        foreach (var baseline in baselines)
        {
            if (baseline.Value) _vds.PinWindow(baseline.Key);
            else _vds.UnpinWindow(baseline.Key);
        }
    }

    private bool IsWindowOnDesktop(nint hwnd, Guid desktopId)
    {
        if (WindowStateHelper.IsUwpWindow(hwnd))
        {
            return _vds.GetCurrentDesktopId() == desktopId
                && _vds.IsWindowOnCurrentDesktop(hwnd);
        }
        return _vds.GetDesktopIdForWindow(hwnd) == desktopId;
    }

    private void CreateWorkspace(CompletedSnapLayout layout)
    {
        if (_inFlight || _vds.GetCurrentDesktopId() != layout.SourceDesktopId) return;
        var fullscreen = _fullScreenTracker.GetByDesktop(layout.SourceDesktopId);
        if (fullscreen is not null
            && layout.Members.Any(member => member.Hwnd == fullscreen.Hwnd))
        {
            PromoteFullscreenDesktop(fullscreen, layout.MonitorId,
                layout.WorkArea, layout.Members);
            return;
        }
        _inFlight = true;
        using var suspension = _autoPin.Suspend("Snap workspace create");
        IVirtualDesktop? desktop = null;
        var moved = new List<SnapLayoutWindow>();
        var baselines = new Dictionary<nint, bool>();
        try
        {
            var created = _vds.CreateDesktop();
            desktop = created.desktop;
            if (desktop is null || !created.id.HasValue) return;
            _vds.SetDesktopName(desktop, $"[MVD Snap] {layout.MonitorId}");

            foreach (var member in layout.Members)
            {
                var isUwp = WindowStateHelper.IsUwpWindow(member.Hwnd);
                if (_vds.TryGetWindowPinnedState(member.Hwnd, out var wasPinned))
                {
                    baselines[member.Hwnd] = wasPinned;
                }
                else if (!isUwp)
                {
                    throw new InvalidOperationException(
                        $"Could not read pin baseline for Snap member {member.Hwnd}.");
                }

                if (!isUwp && (!wasPinned && !_vds.PinWindow(member.Hwnd)
                    || !_vds.TryGetWindowPinnedState(member.Hwnd, out var pinned)
                    || !pinned))
                {
                    throw new InvalidOperationException(
                        $"Could not prepare Snap member {member.Hwnd} for an atomic move.");
                }
            }

            if (!_vds.SwitchToDesktop(desktop))
                throw new InvalidOperationException("Could not switch to Snap workspace desktop.");

            // Match the fullscreen transition precisely: keep each ordinary
            // window pinned while switching, then move it into the already
            // visible target desktop, then release the pin. Moving first
            // exposes an empty target and produces a wallpaper flash.
            foreach (var member in layout.Members)
            {
                if (!_vds.MoveWindowToDesktop(member.Hwnd, desktop))
                    throw new InvalidOperationException($"Could not move Snap member {member.Hwnd}.");
                moved.Add(member);
            }

            foreach (var member in layout.Members)
            {
                var isUwp = WindowStateHelper.IsUwpWindow(member.Hwnd);
                var unpinned = _vds.UnpinWindow(member.Hwnd);
                var queried = _vds.TryGetWindowPinnedState(member.Hwnd, out var stillPinned);
                if (!unpinned || (!isUwp && (!queried || stillPinned))
                    || (isUwp && queried && stillPinned))
                {
                    throw new InvalidOperationException(
                        $"Could not confirm Snap member {member.Hwnd} is unpinned.");
                }
            }

            var usesNativeState = layout.Members.All(member => IsWindowArranged(member.Hwnd));
            var trackedMembers = layout.Members.Select(member => new SnapWorkspaceMember(
                member.Hwnd, member.ProcessId, member.Frame, usesNativeState)).ToArray();
            var workspace = new SnapWorkspaceEntry(
                Guid.NewGuid(), layout.SourceDesktopId, created.id.Value, desktop,
                layout.MonitorId, layout.WorkArea, trackedMembers);
            _tracker.Track(workspace);
            RefreshProbeInterval(hasTrackedWorkspace: true);
            foreach (var member in trackedMembers)
                _autoPin.ExcludeManagedWorkspaceWindow(member.Hwnd);
            desktop = null; // ownership transferred to tracker
            _layoutGates.Clear();
            Trace.WriteLine(
                $"SnapWorkspaceService: created workspace {workspace.WorkspaceId} "
                + $"nativeArranged={usesNativeState}.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SnapWorkspaceService: create failed, rolling back: {ex.Message}");
            var source = _vds.FindDesktop(layout.SourceDesktopId);
            try
            {
                if (source is not null)
                {
                    foreach (var member in moved.AsEnumerable().Reverse())
                        _vds.MoveWindowToDesktop(member.Hwnd, source);
                    _vds.SwitchToDesktop(source);
                }
                foreach (var baseline in baselines)
                {
                    if (baseline.Value) _vds.PinWindow(baseline.Key);
                    else _vds.UnpinWindow(baseline.Key);
                }
            }
            finally
            {
                if (source is not null) Marshal.ReleaseComObject(source);
            }
        }
        finally
        {
            if (desktop is not null)
            {
                _vds.RemoveDesktop(desktop, layout.SourceDesktopId);
                Marshal.ReleaseComObject(desktop);
            }
            _inFlight = false;
            _dirty = true;
        }
    }

    private void ObserveWorkspaceMembers(SnapWorkspaceEntry workspace, bool shouldReadopt)
    {
        var now = DateTime.UtcNow;
        foreach (var member in workspace.Members)
        {
            if (!IsSameLiveWindow(member))
            {
                Detach(workspace, member.Hwnd, "destroyed or HWND reused");
                continue;
            }

            if (_membersInMoveSize.Contains(member.Hwnd)) continue;

            if (SnapWorkspacePolicy.ShouldDetachUnavailableMember(
                    isAlive: true,
                    isVisible: NativeMethods.IsWindowVisible(member.Hwnd),
                    isMinimized: NativeMethods.IsIconic(member.Hwnd)))
            {
                Detach(workspace, member.Hwnd, "hidden or minimized");
                continue;
            }

            if (member.UsesNativeArrangedState)
            {
                if (IsWindowArranged(member.Hwnd))
                {
                    _geometryMismatchSince.Remove(member.Hwnd);
                    if (TryGetFrame(member.Hwnd, out var frame) && frame != member.ExpectedFrame)
                        workspace.UpdateMember(member with { ExpectedFrame = frame });
                    continue;
                }
                // Native Snap state is the authoritative exit signal. Do not
                // hold an arbitrary grace period after it clears: a member
                // dragged out, restored, minimized, or closed must restore
                // the workspace immediately.
                Detach(workspace, member.Hwnd, "native arranged state cleared");
                continue;
            }

            if (!TryGetFrame(member.Hwnd, out var currentFrame)) continue;
            if (FramesMatch(member.ExpectedFrame, currentFrame))
            {
                _geometryMismatchSince.Remove(member.Hwnd);
                continue;
            }
            if (!_geometryMismatchSince.TryGetValue(member.Hwnd, out var mismatchSince))
            {
                _geometryMismatchSince[member.Hwnd] = now;
                continue;
            }
            if (now - mismatchSince >= TimeSpan.FromMilliseconds(StableLayoutMs))
                Detach(workspace, member.Hwnd, "stable geometry left committed zone");
        }

        // An empty workspace has no state worth preserving. Restore/remove it
        // immediately, before any expensive re-adoption enumeration.
        if (workspace.IsEmpty)
        {
            RemoveWorkspace(workspace);
            return;
        }

        // Re-adoption enumerates top-level windows only after a completed
        // native arrangement, never for intermediate drag notifications.
        if (shouldReadopt)
            ReadoptArrangedWindows(workspace);
        if (workspace.IsEmpty) RemoveWorkspace(workspace);
    }

    private void ReadoptArrangedWindows(SnapWorkspaceEntry workspace)
    {
        var candidates = new List<ObservedSnapWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!WindowEligibility.IsApplicationWindow(hwnd, _syncControl.Handle,
                    _fullScreenTracker.IsTracked, includeMinimized: false)
                || NativeMethods.IsWindowCloaked(hwnd)
                || !IsWindowArranged(hwnd)
                || !NativeMethods.TryGetVisibleFrameBounds(hwnd, out var nativeRect))
            {
                return true;
            }

            var screen = Screen.FromHandle(hwnd);
            if (screen.DeviceName != workspace.MonitorId) return true;
            var area = screen.WorkingArea;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            candidates.Add(new ObservedSnapWindow(
                hwnd,
                processId,
                screen.DeviceName,
                new SnapRect(area.Left, area.Top, area.Right, area.Bottom),
                new SnapRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom)));
            return true;
        }, IntPtr.Zero);

        foreach (var candidate in candidates)
        {
            if (_vds.GetDesktopIdForWindow(candidate.Hwnd) != workspace.TempDesktopId)
                continue;
            if (workspace.TryGetMember(candidate.Hwnd, out var existing))
            {
                workspace.UpdateMember(existing with
                {
                    ExpectedFrame = candidate.Frame,
                    UsesNativeArrangedState = true,
                });
                _geometryMismatchSince.Remove(candidate.Hwnd);
                continue;
            }

            _autoPin.ExcludeManagedWorkspaceWindow(candidate.Hwnd);
            var unpinCalled = _vds.UnpinWindow(candidate.Hwnd);
            var queried = _vds.TryGetWindowPinnedState(candidate.Hwnd, out var isPinned);
            if ((!unpinCalled && (!queried || isPinned)) || (queried && isPinned))
            {
                Trace.WriteLine(
                    $"SnapWorkspaceService: could not re-adopt {candidate.Hwnd}; unpin not confirmed.");
                continue;
            }

            workspace.Attach(new SnapWorkspaceMember(
                candidate.Hwnd, candidate.ProcessId, candidate.Frame,
                UsesNativeArrangedState: true));
            _geometryMismatchSince.Remove(candidate.Hwnd);
            Trace.WriteLine(
                $"SnapWorkspaceService: re-adopted arranged member {candidate.Hwnd}.");
        }
    }

    private void Detach(SnapWorkspaceEntry workspace, nint hwnd, string reason)
    {
        if (!workspace.Detach(hwnd)) return;
        _geometryMismatchSince.Remove(hwnd);
        _membersInMoveSize.Remove(hwnd);
        Trace.WriteLine($"SnapWorkspaceService: detached {hwnd}: {reason}.");
        if (!workspace.IsEmpty) _autoPin.HandleWorkspaceMemberDetached(hwnd);
    }

    private void RemoveWorkspace(SnapWorkspaceEntry workspace)
    {
        if (_inFlight) return;
        var mainDesktopId = _mainDesktopProvider();
        if (!mainDesktopId.HasValue)
        {
            Trace.WriteLine("SnapWorkspaceService: cannot remove workspace; Desktop 1 is unknown.");
            return;
        }

        _inFlight = true;
        using var suspension = _autoPin.Suspend("Snap workspace remove");
        try
        {
            var current = _vds.GetCurrentDesktopId();
            if (!SnapWorkspacePolicy.CanRemoveEmptyWorkspace(current.HasValue))
            {
                Trace.WriteLine(
                    "SnapWorkspaceService: deferred workspace removal; "
                    + "virtual desktop service has no current desktop.");
                return;
            }
            // Participants retain the last known members even after they have
            // been detached from Snap tracking. Keep only windows that really
            // remain on this temporary desktop; a member already moved to
            // another MVD desktop must never be pulled back to Desktop 1.
            var returningWindows = workspace.Participants
                .Where(member => NativeMethods.IsWindow(member.Hwnd)
                    && IsWindowOnDesktop(member.Hwnd, workspace.TempDesktopId))
                .Select(member => member.Hwnd)
                .ToArray();
            var pinnedForReturn = new List<nint>();
            foreach (var hwnd in returningWindows)
            {
                if (WindowStateHelper.IsUwpWindow(hwnd)) continue;
                if (_vds.PinWindow(hwnd)) pinnedForReturn.Add(hwnd);
            }

            var switchedToMain = false;
            IVirtualDesktop? mainDesktop = null;
            try
            {
                if (current == workspace.TempDesktopId || returningWindows.Length != 0)
                    mainDesktop = _vds.FindDesktop(mainDesktopId.Value);

                if (current == workspace.TempDesktopId)
                {
                    if (mainDesktop is null || !_vds.SwitchToDesktop(mainDesktop)) return;
                    switchedToMain = true;
                }

                // Mirror FullScreenManager's visual transition: pin while
                // changing desktops, move into the visible destination, then
                // release the pin. This prevents an empty Desktop 1 frame.
                if (mainDesktop is not null)
                {
                    foreach (var hwnd in returningWindows)
                        _vds.MoveWindowToDesktop(hwnd, mainDesktop);
                }
                foreach (var hwnd in pinnedForReturn) _vds.UnpinWindow(hwnd);

                if (!_vds.RemoveDesktop(workspace.TempDesktop, mainDesktopId.Value)) return;
                if (_tracker.Remove(workspace.WorkspaceId) is not null)
                    Marshal.ReleaseComObject(workspace.TempDesktop);
                RefreshProbeInterval(_tracker.GetAll().Count != 0);
                _autoPin.HandleWorkspaceRemoved(
                    workspace.Participants, mainDesktopId.Value);
                Trace.WriteLine($"SnapWorkspaceService: removed workspace {workspace.WorkspaceId}.");
                if (switchedToMain) WorkspaceRemovalSwitchedDesktop?.Invoke();
            }
            finally
            {
                if (mainDesktop is not null) Marshal.ReleaseComObject(mainDesktop);
            }
        }
        finally
        {
            _inFlight = false;
            _dirty = true;
        }
    }

    public void RemoveAll()
    {
        foreach (var workspace in _tracker.GetAll()) RemoveWorkspace(workspace);
    }

    internal static bool IsWindowArranged(nint hwnd)
    {
        try { return NativeMethods.IsWindowArranged(hwnd); }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool IsSameLiveWindow(nint hwnd, int expectedProcessId)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == expectedProcessId;
    }

    private static bool IsSameLiveWindow(SnapWorkspaceMember member)
    {
        if (!NativeMethods.IsWindow(member.Hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(member.Hwnd, out var processId);
        return processId == member.ProcessId;
    }

    private static bool TryGetFrame(nint hwnd, out SnapRect frame)
    {
        frame = default;
        if (!NativeMethods.TryGetVisibleFrameBounds(hwnd, out var rect)) return false;
        frame = new SnapRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    private static bool FramesMatch(SnapRect expected, SnapRect actual) =>
        Math.Abs(expected.Left - actual.Left) <= GeometryTolerancePixels
        && Math.Abs(expected.Top - actual.Top) <= GeometryTolerancePixels
        && Math.Abs(expected.Right - actual.Right) <= GeometryTolerancePixels
        && Math.Abs(expected.Bottom - actual.Bottom) <= GeometryTolerancePixels;

    private sealed record ObservedSnapWindow(
        nint Hwnd,
        int ProcessId,
        string MonitorId,
        SnapRect WorkArea,
        SnapRect Frame);

    private void PostToUi(Action action)
    {
        if (_syncControl.IsDisposed || !_syncControl.IsHandleCreated) return;
        try { _syncControl.BeginInvoke(action); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _probeTimer.Stop();
        _probeTimer.Dispose();
        foreach (var hook in new[]
                 { _locationHook, _moveSizeStartHook, _moveSizeHook, _destroyHook, _hideHook, _minimizeStartHook })
            if (hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(hook);
        _autoPin.StableDesktopObservationApplied -= OnAutoPinDesktopSettled;
        _locationHook = _moveSizeStartHook = _moveSizeHook = _destroyHook = _hideHook = _minimizeStartHook = IntPtr.Zero;
        _pendingMemberAvailabilityChecks.Clear();
        _pendingNativeLayoutCandidates.Clear();
        _membersInMoveSize.Clear();
    }
}
