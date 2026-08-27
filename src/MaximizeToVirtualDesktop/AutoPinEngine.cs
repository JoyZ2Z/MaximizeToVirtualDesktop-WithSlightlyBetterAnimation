namespace MaximizeToVirtualDesktop;

/// <summary>
/// Stateless AutoPin policy: displayed views are unpinned; minimized views are
/// pinned. A view hidden below the foreground fullscreen anchor is minimized
/// first, then pinned, so it cannot remain an invisible unpinned overlay.
/// </summary>
internal sealed class AutoPinEngine
{
    public IReadOnlyCollection<nint> ManagedWindows => [];

    public AutoPinDecisionPlan Evaluate(AutoPinObservation observation)
    {
        var commands = new List<AutoPinCommand>();
        foreach (var window in observation.Windows)
        {
            if (!window.IsEligible || !window.IsPinned.HasValue) continue;
            var command = Decide(observation, window);
            if (command is not null) commands.Add(command);
        }
        return new AutoPinDecisionPlan(observation.DesktopId, commands);
    }

    public void Commit(AutoPinDecisionPlan plan,
        IReadOnlyCollection<AutoPinCommand> confirmedCommands) { }
    public void Forget(nint hwnd) { }
    public void Clear() { }

    private static AutoPinCommand? Decide(
        AutoPinObservation observation, AutoPinWindowObservation window)
    {
        // A snapshot may contain globally pinned views owned by another desktop.
        // Never use an observation of the current desktop to mutate them.
        if (!window.IsOnCurrentDesktop)
            return null;

        if (window.IsMinimized)
            return Command(window, AutoPinTarget.Pinned);

        var isCoveredByForegroundAnchor =
            observation.DesktopKind == AutoPinDesktopKind.Fullscreen
            && observation.AnchorWindow.HasValue
            && observation.AnchorZOrder.HasValue
            && observation.ForegroundWindow == observation.AnchorWindow.Value
            && window.IsDisplayed
            && window.ZOrder > observation.AnchorZOrder.Value;
        if (isCoveredByForegroundAnchor)
            return Command(window, AutoPinTarget.Pinned, AutoPinPreparation.Minimize);

        var isCoveredByForegroundSnapWorkspace =
            observation.DesktopKind == AutoPinDesktopKind.Workspace
            && observation.SnapWorkspaceMemberIsForeground
            && window.IsDisplayed
            && window.IsCoveredBySnapMembers;
        if (isCoveredByForegroundSnapWorkspace)
            return Command(window, AutoPinTarget.Pinned, AutoPinPreparation.Minimize);

        return Command(window, AutoPinTarget.Unpinned);
    }

    private static AutoPinCommand? Command(
        AutoPinWindowObservation window,
        AutoPinTarget target,
        AutoPinPreparation preparation = AutoPinPreparation.None)
    {
        var shouldBePinned = target == AutoPinTarget.Pinned;
        if (window.IsPinned == shouldBePinned && preparation == AutoPinPreparation.None)
            return null;

        return new AutoPinCommand(
            window.Hwnd,
            window.ProcessId,
            target,
            new AutoPinLifecycle(
                window.ProcessId,
                shouldBePinned ? AutoPinWindowMode.Pinned : AutoPinWindowMode.NormalReleased,
                FullscreenManaged: false,
                FullscreenAnchorHwnd: null),
            preparation);
    }
}

internal enum AutoPinDesktopKind { Main, Normal, Fullscreen, Workspace }
internal enum AutoPinTarget { Pinned, Unpinned }
internal enum AutoPinPreparation { None, Minimize }
internal enum AutoPinWindowMode { Pinned, MainReleased, NormalReleased, FullscreenReleased, WorkspaceReleased }

internal sealed record AutoPinObservation(
    Guid DesktopId,
    AutoPinDesktopKind DesktopKind,
    nint ForegroundWindow,
    nint? AnchorWindow,
    int? AnchorZOrder,
    IReadOnlyList<AutoPinWindowObservation> Windows,
    bool IsForegroundActivation = false,
    bool SnapWorkspaceMemberIsForeground = false);

internal sealed record AutoPinWindowObservation(
    nint Hwnd,
    int ProcessId,
    bool IsEligible,
    bool IsMinimized,
    bool IsDisplayed,
    int ZOrder,
    bool IsOnCurrentDesktop,
    bool? IsPinned,
    bool IsCoveredBySnapMembers = false);

internal sealed record AutoPinLifecycle(int ProcessId, AutoPinWindowMode Mode,
    bool FullscreenManaged, nint? FullscreenAnchorHwnd,
    Guid? ManagedDesktopId = null);
internal sealed record AutoPinCommand(nint Hwnd, int ProcessId, AutoPinTarget Target,
    AutoPinLifecycle StateOnSuccess,
    AutoPinPreparation Preparation = AutoPinPreparation.None);
internal sealed record AutoPinDecisionPlan(Guid DesktopId, IReadOnlyList<AutoPinCommand> Commands);
