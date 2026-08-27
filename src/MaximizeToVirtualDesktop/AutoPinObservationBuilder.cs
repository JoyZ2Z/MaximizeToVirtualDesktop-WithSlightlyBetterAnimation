using System.Diagnostics;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Builds read-only AutoPin observations. EnumWindows collects Win32 facts only;
/// virtual-desktop COM queries run afterwards.
/// </summary>
internal sealed class AutoPinObservationBuilder
{
    private const int SnapCoverageTolerancePixels = 4;
    private readonly VirtualDesktopService _vds;
    private readonly FullScreenTracker _tracker;
    private readonly SnapWorkspaceTracker _snapTracker;
    private readonly Control _syncControl;

    public AutoPinObservationBuilder(
        VirtualDesktopService vds,
        FullScreenTracker tracker,
        SnapWorkspaceTracker snapTracker,
        Control syncControl)
    {
        _vds = vds;
        _tracker = tracker;
        _snapTracker = snapTracker;
        _syncControl = syncControl;
    }

    public AutoPinObservation Build(Guid currentDesktopId, bool isMainDesktop)
    {
        var tracked = _tracker.GetAll();
        var currentSnap = _snapTracker.GetByDesktop(currentDesktopId);
        var trackedHandles = GetManagedHandles(tracked);
        var currentFullscreen = tracked.FirstOrDefault(entry => entry.TempDesktopId == currentDesktopId);
        if (!ManagedDesktopKindPolicy.TryResolve(
                currentFullscreen is not null,
                currentSnap is not null,
                out var managedDesktopKind))
        {
            Trace.WriteLine(
                $"AutoPinObservationBuilder: conflicting owners for desktop {currentDesktopId}; "
                + "returning an empty observation.");
            return new AutoPinObservation(
                currentDesktopId,
                isMainDesktop ? AutoPinDesktopKind.Main : AutoPinDesktopKind.Normal,
                NativeMethods.GetForegroundWindow(),
                AnchorWindow: null,
                AnchorZOrder: null,
                Windows: []);
        }
        var rawWindows = EnumerateRawWindows(trackedHandles);
        var anchorWindow = currentFullscreen is null
            ? null
            : rawWindows.FirstOrDefault(window => window.Hwnd == currentFullscreen.Hwnd);
        // A native Snap action first arranges the formerly-fullscreen window,
        // then WindowMonitor transfers it to the Snap tracker. During that
        // short interval it must not suppress peers as if it still covered the
        // entire desktop.
        var fullscreenAnchorIsArranged = currentFullscreen is not null
            && NativeMethods.IsWindowArranged(currentFullscreen.Hwnd);
        int? anchorZOrder = !fullscreenAnchorIsArranged
            && anchorWindow is { IsDisplayed: true, IsMinimized: false }
            ? anchorWindow.ZOrder
            : null;
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (currentFullscreen is not null
            && IsSameWindowFamily(foregroundWindow, currentFullscreen.Hwnd))
        {
            foregroundWindow = currentFullscreen.Hwnd;
        }

        var windows = EnrichEligibleWindows(
            rawWindows, trackedHandles, currentSnap, out var resolvedViewsBySource);
        var foregroundRawIsEligible = rawWindows.FirstOrDefault(
            window => window.Hwnd == foregroundWindow)?.IsEligible == true;
        if (resolvedViewsBySource.TryGetValue(foregroundWindow, out var resolvedForeground))
        {
            foregroundWindow = resolvedForeground;
        }
        else if (!trackedHandles.Contains(foregroundWindow)
            && _vds.TryResolveForegroundAutoPinWindowState(
                foregroundWindow, foregroundRawIsEligible,
                out var foregroundView, out _, out _)
            && windows.Any(window => window.Hwnd == foregroundView))
        {
            foregroundWindow = foregroundView;
        }
        var snapWorkspaceMemberIsForeground = currentSnap is not null
            && rawWindows.Any(window => currentSnap.IsMember(window.Hwnd)
                && IsSameWindowFamily(foregroundWindow, window.Hwnd));

        return new AutoPinObservation(
            currentDesktopId,
            managedDesktopKind switch
            {
                ManagedDesktopKind.Fullscreen => AutoPinDesktopKind.Fullscreen,
                ManagedDesktopKind.Snap => AutoPinDesktopKind.Workspace,
                _ => isMainDesktop
                    ? AutoPinDesktopKind.Main
                    : AutoPinDesktopKind.Normal,
            },
            foregroundWindow,
            currentFullscreen?.Hwnd,
            anchorZOrder,
            windows,
            IsForegroundActivation: false,
            SnapWorkspaceMemberIsForeground: snapWorkspaceMemberIsForeground);
    }

    public AutoPinObservation? BuildForeground(
        Guid currentDesktopId, nint hwnd, bool isMainDesktop)
    {
        var tracked = _tracker.GetAll();
        var currentSnap = _snapTracker.GetByDesktop(currentDesktopId);
        var trackedHandles = GetManagedHandles(tracked);
        var currentFullscreen = tracked.FirstOrDefault(
            entry => entry.TempDesktopId == currentDesktopId);
        if (!ManagedDesktopKindPolicy.TryResolve(
                currentFullscreen is not null,
                currentSnap is not null,
                out var managedDesktopKind))
        {
            Trace.WriteLine(
                $"AutoPinObservationBuilder: conflicting owners for desktop {currentDesktopId}; "
                + "foreground observation skipped.");
            return null;
        }
        var rawIsEligible = WindowEligibility.IsApplicationWindow(
            hwnd, _syncControl.Handle, trackedHandles.Contains, includeMinimized: false);
        nint viewHwnd;
        bool isPinned;
        var resolved = rawIsEligible
            ? _vds.TryResolveForegroundAutoPinWindowState(
                hwnd, allowSameProcessFallback: true,
                out viewHwnd, out _, out isPinned)
            : _vds.TryResolveAutoPinView(hwnd, out viewHwnd, out isPinned);
        if (!resolved)
        {
            return null;
        }
        if (!rawIsEligible
            && !WindowEligibility.IsApplicationWindow(
                viewHwnd, _syncControl.Handle, trackedHandles.Contains,
                includeMinimized: false))
        {
            return null;
        }
        if (trackedHandles.Contains(viewHwnd)) return null;
        NativeMethods.GetWindowThreadProcessId(viewHwnd, out var processId);

        var stateHwnd = rawIsEligible ? hwnd : viewHwnd;
        var isDisplayed = NativeMethods.IsWindowVisible(stateHwnd)
            && !NativeMethods.IsIconic(stateHwnd)
            && !NativeMethods.IsWindowCloaked(stateHwnd);

        if (managedDesktopKind == ManagedDesktopKind.Fullscreen
            && currentFullscreen is not null
            && !NativeMethods.IsWindowArranged(currentFullscreen.Hwnd))
        {
            var rawWindows = EnumerateRawWindows(trackedHandles);
            var foregroundWindow = rawWindows.FirstOrDefault(window =>
                IsSameWindowFamily(window.Hwnd, hwnd)
                || IsSameWindowFamily(window.Hwnd, viewHwnd));
            var anchorWindow = rawWindows.FirstOrDefault(
                window => window.Hwnd == currentFullscreen.Hwnd);
            int? anchorZOrder = anchorWindow is { IsDisplayed: true, IsMinimized: false }
                ? anchorWindow.ZOrder
                : null;
            if (foregroundWindow is null) return null;

            var fullscreenObservation = new AutoPinWindowObservation(
                viewHwnd, processId, IsEligible: true,
                foregroundWindow.IsMinimized, foregroundWindow.IsDisplayed,
                foregroundWindow.ZOrder,
                IsOnCurrentDesktop: _vds.IsWindowOnCurrentDesktop(viewHwnd), isPinned);
            return new AutoPinObservation(
                currentDesktopId, AutoPinDesktopKind.Fullscreen, viewHwnd,
                currentFullscreen.Hwnd, anchorZOrder, [fullscreenObservation],
                IsForegroundActivation: true);
        }

        var window = new AutoPinWindowObservation(
            viewHwnd, processId, IsEligible: true,
            IsMinimized: NativeMethods.IsIconic(stateHwnd), isDisplayed, ZOrder: 0,
            IsOnCurrentDesktop: _vds.IsWindowOnCurrentDesktop(viewHwnd), isPinned);
        return new AutoPinObservation(
            currentDesktopId,
            isMainDesktop ? AutoPinDesktopKind.Main : AutoPinDesktopKind.Normal,
            viewHwnd, AnchorWindow: null, AnchorZOrder: null, [window],
            IsForegroundActivation: true);
    }

    private HashSet<nint> GetManagedHandles(IReadOnlyList<TrackingEntry> fullscreen) =>
        fullscreen.Select(entry => entry.Hwnd)
            .Concat(_snapTracker.GetAll().SelectMany(workspace =>
                workspace.Members.Select(member => member.Hwnd)))
            .ToHashSet();

    private List<RawWindow> EnumerateRawWindows(IReadOnlySet<nint> trackedHandles)
    {
        var rawWindows = new List<RawWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            rawWindows.Add(new RawWindow(
                hwnd, processId, rawWindows.Count,
                WindowEligibility.IsApplicationWindow(hwnd, _syncControl.Handle,
                    trackedHandles.Contains, includeMinimized: true),
                NativeMethods.IsIconic(hwnd),
                NativeMethods.IsWindowVisible(hwnd)
                    && !NativeMethods.IsIconic(hwnd)
                    && !NativeMethods.IsWindowCloaked(hwnd)));
            return true;
        }, IntPtr.Zero);
        return rawWindows;
    }

    private AutoPinWindowObservation[] EnrichEligibleWindows(
        IReadOnlyList<RawWindow> rawWindows,
        IReadOnlySet<nint> trackedHandles,
        SnapWorkspaceEntry? currentSnap,
        out Dictionary<nint, nint> resolvedViewsBySource)
    {
        var snapCovers = currentSnap is null
            ? []
            : rawWindows
                .Where(window => currentSnap.IsMember(window.Hwnd)
                    && window.IsDisplayed && !window.IsMinimized)
                .Select(window => TryGetFrame(window.Hwnd, out var frame)
                    ? new SnapCover(window.ZOrder, frame)
                    : (SnapCover?)null)
                .Where(cover => cover.HasValue)
                .Select(cover => cover!.Value)
                .ToArray();
        var result = new List<AutoPinWindowObservation>();
        resolvedViewsBySource = new Dictionary<nint, nint>();
        var resolvedViews = new HashSet<nint>();
        var resolvedStates = _vds.ResolveAutoPinViews(
            rawWindows.Where(window => window.IsEligible).Select(window => window.Hwnd));
        foreach (var window in rawWindows)
        {
            if (!window.IsEligible) continue;
            if (!resolvedStates.TryGetValue(window.Hwnd, out var state)) continue;
            var viewHwnd = state.ViewHwnd;
            var isPinned = state.IsPinned;
            resolvedViewsBySource[window.Hwnd] = viewHwnd;
            if (trackedHandles.Contains(viewHwnd) || !resolvedViews.Add(viewHwnd)) continue;
            NativeMethods.GetWindowThreadProcessId(viewHwnd, out var processId);
            var isCoveredBySnapMembers = currentSnap is not null
                && window.IsDisplayed
                && TryGetFrame(window.Hwnd, out var windowFrame)
                && SnapWorkspacePolicy.IsCoveredInVisibleWorkspaceArea(
                    windowFrame,
                    currentSnap.WorkArea,
                    snapCovers.Where(cover => cover.ZOrder < window.ZOrder)
                        .Select(cover => cover.Frame),
                    SnapCoverageTolerancePixels);
            var uwpPresentationRole = WindowStateHelper.GetUwpPresentationWindowRole(window.Hwnd);
            result.Add(new AutoPinWindowObservation(
                viewHwnd, processId, IsEligible: true,
                window.IsMinimized, window.IsDisplayed, window.ZOrder,
                _vds.IsWindowOnCurrentDesktop(viewHwnd), isPinned,
                isCoveredBySnapMembers,
                UwpPresentationRole: uwpPresentationRole));
        }
        return MarkUwpHostCorePairs(result);
    }

    /// <summary>
    /// A legacy UWP app exposes an ApplicationFrameWindow host and a separate
    /// CoreWindow with the same AUMID. Their minimized states disagree by
    /// design: the host minimizes while the CoreWindow merely becomes hidden.
    /// Mark only that precise pair as one logical AutoPin application.
    /// </summary>
    private AutoPinWindowObservation[] MarkUwpHostCorePairs(
        IReadOnlyList<AutoPinWindowObservation> windows)
    {
        var pairs = windows
            .Where(window => window.UwpPresentationRole != UwpPresentationWindowRole.None)
            .Select(window =>
            {
                var aumid = _vds.TryGetAppUserModelId(window.Hwnd, out var resolved)
                    ? resolved
                    : null;
                return (Window: window, Aumid: aumid);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Aumid))
            .GroupBy(item => item.Aumid!, StringComparer.Ordinal)
            .Where(group => group.Any(item => item.Window.UwpPresentationRole == UwpPresentationWindowRole.Host)
                && group.Any(item => item.Window.UwpPresentationRole == UwpPresentationWindowRole.Core))
            .ToDictionary(group => group.Key, group => group.Key, StringComparer.Ordinal);

        if (pairs.Count == 0) return windows.ToArray();

        return windows.Select(window =>
        {
            if (window.UwpPresentationRole == UwpPresentationWindowRole.None
                || !_vds.TryGetAppUserModelId(window.Hwnd, out var aumid)
                || aumid is null
                || !pairs.TryGetValue(aumid, out var logicalId))
            {
                return window;
            }
            return window with { LogicalApplicationId = $"uwp:{logicalId}" };
        }).ToArray();
    }

    private static bool TryGetFrame(nint hwnd, out SnapRect frame)
    {
        frame = default;
        if (!NativeMethods.TryGetVisibleFrameBounds(hwnd, out var rect)) return false;
        frame = new SnapRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return !frame.IsEmpty;
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

    private sealed record RawWindow(
        nint Hwnd, int ProcessId, int ZOrder, bool IsEligible,
        bool IsMinimized, bool IsDisplayed);

    private readonly record struct SnapCover(int ZOrder, SnapRect Frame);
}
