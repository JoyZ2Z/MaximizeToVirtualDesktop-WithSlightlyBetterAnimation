namespace MaximizeToVirtualDesktop;

internal sealed class AutoPinWorkspaceFallbackTracker
{
    private readonly Dictionary<nint, PendingWorkspaceFallback> _pending = new();

    public void Track(AutoPinWindowIdentity identity, Guid fallbackDesktopId) =>
        _pending[identity.Hwnd] = new PendingWorkspaceFallback(identity, fallbackDesktopId);

    public void Forget(nint hwnd) => _pending.Remove(hwnd);
    public void Clear() => _pending.Clear();

    public IReadOnlyList<AutoPinWindowIdentity> GetPinCandidates(
        Guid currentDesktopId,
        Func<AutoPinWindowIdentity, bool> isLiveAndMinimized) =>
        _pending.Values
            .Where(pending => pending.FallbackDesktopId == currentDesktopId)
            .Select(pending => pending.Identity)
            .Where(isLiveAndMinimized)
            .ToArray();

    public IReadOnlyList<AutoPinWindowIdentity> GetStale(
        Func<AutoPinWindowIdentity, bool> isLive) =>
        _pending.Values.Select(pending => pending.Identity)
            .Where(identity => !isLive(identity)).ToArray();

    private sealed record PendingWorkspaceFallback(
        AutoPinWindowIdentity Identity, Guid FallbackDesktopId);
}
