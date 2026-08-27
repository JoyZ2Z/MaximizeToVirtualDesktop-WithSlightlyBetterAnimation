using System.Diagnostics;
using System.Runtime.InteropServices;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

internal sealed record SnapWorkspaceMember(
    nint Hwnd,
    int ProcessId,
    SnapRect ExpectedFrame,
    bool UsesNativeArrangedState);

internal sealed class SnapWorkspaceEntry
{
    private readonly Dictionary<nint, SnapWorkspaceMember> _members;
    private readonly Dictionary<nint, SnapWorkspaceMember> _participants;

    public SnapWorkspaceEntry(
        Guid workspaceId,
        Guid sourceDesktopId,
        Guid tempDesktopId,
        IVirtualDesktop tempDesktop,
        string monitorId,
        SnapRect workArea,
        IEnumerable<SnapWorkspaceMember> members)
    {
        WorkspaceId = workspaceId;
        SourceDesktopId = sourceDesktopId;
        TempDesktopId = tempDesktopId;
        TempDesktop = tempDesktop;
        MonitorId = monitorId;
        WorkArea = workArea;
        _members = members.ToDictionary(member => member.Hwnd);
        _participants = new Dictionary<nint, SnapWorkspaceMember>(_members);
    }

    public Guid WorkspaceId { get; }
    public Guid SourceDesktopId { get; }
    public Guid TempDesktopId { get; }
    public IVirtualDesktop TempDesktop { get; }
    public string MonitorId { get; }
    public SnapRect WorkArea { get; }
    public IReadOnlyList<SnapWorkspaceMember> Members => _members.Values.ToArray();
    public IReadOnlyList<SnapWorkspaceMember> Participants => _participants.Values.ToArray();
    public bool IsEmpty => _members.Count == 0;

    public bool IsMember(nint hwnd) => _members.ContainsKey(hwnd);
    public bool TryGetMember(nint hwnd, out SnapWorkspaceMember member) =>
        _members.TryGetValue(hwnd, out member!);
    public void UpdateMember(SnapWorkspaceMember member)
    {
        _members[member.Hwnd] = member;
        _participants[member.Hwnd] = member;
    }
    public void Attach(SnapWorkspaceMember member)
    {
        _members[member.Hwnd] = member;
        _participants[member.Hwnd] = member;
    }
    public bool Detach(nint hwnd) => _members.Remove(hwnd);
}

internal sealed class SnapWorkspaceTracker
{
    private readonly Dictionary<Guid, SnapWorkspaceEntry> _workspaces = new();
    private readonly object _sync = new();

    public void Track(SnapWorkspaceEntry workspace)
    {
        lock (_sync)
        {
            if (_workspaces.Values.Any(existing =>
                    existing.TempDesktopId == workspace.TempDesktopId))
            {
                throw new InvalidOperationException(
                    $"Desktop {workspace.TempDesktopId} already has a Snap owner.");
            }
            _workspaces.Add(workspace.WorkspaceId, workspace);
        }
        Trace.WriteLine(
            $"SnapWorkspaceTracker: tracking {workspace.WorkspaceId} "
            + $"desktop={workspace.TempDesktopId} members={workspace.Members.Count}.");
        Persist();
    }

    public IReadOnlyList<SnapWorkspaceEntry> GetAll()
    {
        lock (_sync) return _workspaces.Values.ToArray();
    }

    public SnapWorkspaceEntry? GetByDesktop(Guid desktopId)
    {
        lock (_sync) return _workspaces.Values.FirstOrDefault(
            workspace => workspace.TempDesktopId == desktopId);
    }

    public SnapWorkspaceEntry? GetByMember(nint hwnd)
    {
        lock (_sync) return _workspaces.Values.FirstOrDefault(workspace => workspace.IsMember(hwnd));
    }

    public bool IsAttachedMember(nint hwnd) => GetByMember(hwnd) is not null;

    public SnapWorkspaceEntry? Remove(Guid workspaceId)
    {
        SnapWorkspaceEntry? removed;
        lock (_sync)
        {
            removed = _workspaces.Remove(workspaceId, out var workspace) ? workspace : null;
        }
        if (removed is not null) Persist();
        return removed;
    }

    public void ClearAll()
    {
        SnapWorkspaceEntry[] workspaces;
        lock (_sync)
        {
            workspaces = _workspaces.Values.ToArray();
            _workspaces.Clear();
        }
        foreach (var workspace in workspaces)
        {
            try { Marshal.ReleaseComObject(workspace.TempDesktop); } catch { }
        }
        Trace.WriteLine($"SnapWorkspaceTracker: cleared {workspaces.Length} stale workspace(s).");
        SnapWorkspacePersistence.Delete();
    }

    private void Persist()
    {
        SnapWorkspaceEntry[] snapshot;
        lock (_sync) snapshot = _workspaces.Values.ToArray();
        SnapWorkspacePersistence.Save(snapshot);
    }
}
