namespace MaximizeToVirtualDesktop;

internal readonly record struct SnapRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public long Area => (long)Width * Height;
    public bool IsEmpty => Width == 0 || Height == 0;

    public SnapRect Intersect(SnapRect other) => new(
        Math.Max(Left, other.Left),
        Math.Max(Top, other.Top),
        Math.Min(Right, other.Right),
        Math.Min(Bottom, other.Bottom));

    public SnapRect Inflate(int amount) => new(
        Left - amount, Top - amount, Right + amount, Bottom + amount);
}

internal sealed record SnapLayoutWindow(nint Hwnd, int ProcessId, SnapRect Frame);

internal sealed record CompletedSnapLayout(
    Guid SourceDesktopId,
    string MonitorId,
    SnapRect WorkArea,
    IReadOnlyList<SnapLayoutWindow> Members)
{
    public string Fingerprint => string.Join('|', Members
        .OrderBy(member => member.Hwnd)
        .Select(member => $"{member.Hwnd:X}:{member.ProcessId}:"
            + $"{member.Frame.Left},{member.Frame.Top},{member.Frame.Right},{member.Frame.Bottom}"));
}

internal static class SnapWorkspacePolicy
{
    /// <summary>
    /// A minimized or hidden member no longer contributes to the user's visible
    /// Snap workspace. This also covers applications that turn a close command
    /// into a tray hide instead of destroying their top-level window.
    /// </summary>
    public static bool ShouldDetachUnavailableMember(
        bool isAlive, bool isVisible, bool isMinimized) =>
        !isAlive || !isVisible || isMinimized;

    /// <summary>
    /// A member moved to another virtual desktop has left this workspace even
    /// when that workspace is no longer the active desktop.
    /// </summary>
    public static bool ShouldDetachAfterRecheck(
        bool isAlive, bool isOnWorkspaceDesktop, bool isVisible, bool isMinimized) =>
        !isOnWorkspaceDesktop
        || ShouldDetachUnavailableMember(isAlive, isVisible, isMinimized);

    /// <summary>
    /// Virtual-desktop removal is unsafe while the shell service cannot report
    /// a current desktop. Callers must defer cleanup until it is available.
    /// </summary>
    public static bool CanRemoveEmptyWorkspace(bool hasCurrentDesktop) =>
        hasCurrentDesktop;

    public static bool ShouldObserveNewLayout(
        bool isDirty, bool hasExistingSnapWorkspace, bool isFullscreenDesktop)
    {
        // A fullscreen MVD desktop may host an independent layout made from
        // ordinary overlay windows. Only an existing Snap workspace owns the
        // desktop strongly enough to suppress discovery of another layout.
        _ = isFullscreenDesktop;
        return isDirty && !hasExistingSnapWorkspace;
    }

    public static CompletedSnapLayout? TryCompleteLayout(
        Guid desktopId,
        string monitorId,
        SnapRect workArea,
        IReadOnlyList<SnapLayoutWindow> candidates,
        int tolerancePixels)
    {
        var members = candidates
            .Where(candidate => !candidate.Frame.Intersect(workArea.Inflate(tolerancePixels)).IsEmpty)
            .OrderBy(candidate => candidate.Hwnd)
            .ToArray();
        if (members.Length < 2) return null;

        var target = workArea;
        var coverage = members.Select(member => member.Frame.Inflate(tolerancePixels));
        return IsFullyCovered(target, coverage)
            ? new CompletedSnapLayout(desktopId, monitorId, workArea, members)
            : null;
    }

    /// <summary>
    /// A fullscreen window can only be promoted after native Snap has produced
    /// the complete layout containing it. Promoting a one-window fragment would
    /// leave already-snapped peers outside the managed-member set.
    /// </summary>
    public static CompletedSnapLayout? FindLayoutContainingWindow(
        IEnumerable<CompletedSnapLayout> layouts, nint hwnd) =>
        layouts.FirstOrDefault(layout => layout.Members.Any(member => member.Hwnd == hwnd));

    public static bool IsFullyCovered(
        SnapRect target, IEnumerable<SnapRect> covers, int tolerancePixels = 0)
    {
        if (target.IsEmpty) return false;
        var clipped = covers
            .Select(cover => tolerancePixels > 0 ? cover.Inflate(tolerancePixels) : cover)
            .Select(cover => cover.Intersect(target))
            .Where(cover => !cover.IsEmpty)
            .ToArray();
        if (clipped.Length == 0) return false;

        var xEdges = clipped.SelectMany(rect => new[] { rect.Left, rect.Right })
            .Append(target.Left).Append(target.Right)
            .Distinct().OrderBy(value => value).ToArray();
        long area = 0;
        for (var i = 0; i < xEdges.Length - 1; i++)
        {
            var left = Math.Max(xEdges[i], target.Left);
            var right = Math.Min(xEdges[i + 1], target.Right);
            if (right <= left) continue;

            var intervals = clipped
                .Where(rect => rect.Left < right && rect.Right > left)
                .Select(rect => (Top: rect.Top, Bottom: rect.Bottom))
                .OrderBy(interval => interval.Top).ToArray();
            if (intervals.Length == 0) continue;

            var coveredHeight = 0;
            var start = intervals[0].Top;
            var end = intervals[0].Bottom;
            foreach (var interval in intervals.Skip(1))
            {
                if (interval.Top > end)
                {
                    coveredHeight += end - start;
                    start = interval.Top;
                    end = interval.Bottom;
                }
                else
                {
                    end = Math.Max(end, interval.Bottom);
                }
            }
            coveredHeight += end - start;
            area += (long)(right - left) * coveredHeight;
        }

        return area >= target.Area;
    }

    /// <summary>
    /// Snap can only cover the workspace work area.  A normal window may hang
    /// past an edge of that area, but pixels outside it are not visible to the
    /// user and therefore must not keep the window released.
    /// </summary>
    public static bool IsCoveredInVisibleWorkspaceArea(
        SnapRect windowFrame,
        SnapRect workspaceWorkArea,
        IEnumerable<SnapRect> covers,
        int tolerancePixels = 0)
    {
        var visibleFrame = windowFrame.Intersect(workspaceWorkArea);
        return visibleFrame.IsEmpty
            || IsFullyCovered(visibleFrame, covers, tolerancePixels);
    }
}

internal sealed class SnapLayoutStabilityGate
{
    private readonly TimeSpan _minimumStableTime;
    private string? _fingerprint;
    private DateTime _firstSeenAt;

    public SnapLayoutStabilityGate(TimeSpan minimumStableTime)
    {
        _minimumStableTime = minimumStableTime;
    }

    public bool Observe(CompletedSnapLayout? layout, DateTime observedAt)
    {
        if (layout is null)
        {
            Reset();
            return false;
        }

        var fingerprint = $"{layout.SourceDesktopId}:{layout.MonitorId}:{layout.Fingerprint}";
        if (_fingerprint != fingerprint)
        {
            _fingerprint = fingerprint;
            _firstSeenAt = observedAt;
            return false;
        }

        return observedAt - _firstSeenAt >= _minimumStableTime;
    }

    public void Reset()
    {
        _fingerprint = null;
        _firstSeenAt = default;
    }
}

internal sealed class SnapWorkspaceLifecycle
{
    private readonly Dictionary<nint, int> _members;

    public SnapWorkspaceLifecycle(IEnumerable<(nint Hwnd, int ProcessId)> members)
    {
        _members = members.ToDictionary(member => member.Hwnd, member => member.ProcessId);
    }

    public IReadOnlyCollection<nint> Members => _members.Keys.ToArray();
    public bool IsEmpty => _members.Count == 0;

    public bool Observe(nint hwnd, int processId, bool isAlive, bool isArranged)
    {
        if (!_members.TryGetValue(hwnd, out var expectedProcessId))
        {
            if (isAlive && isArranged) _members[hwnd] = processId;
            return IsEmpty;
        }
        if (expectedProcessId != processId || !isAlive || !isArranged)
            _members.Remove(hwnd);
        return IsEmpty;
    }
}
