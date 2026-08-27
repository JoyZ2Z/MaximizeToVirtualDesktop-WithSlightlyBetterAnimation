namespace MaximizeToVirtualDesktop;

/// <summary>
/// Retains minimize notifications until the application view can be resolved
/// and its pinned state has been confirmed. This closes the short interval in
/// which packaged applications have a live host HWND but no resolvable view.
/// </summary>
internal sealed class AutoPinMinimizeRetryTracker
{
    private const int MaximumAttempts = 25;
    private readonly Dictionary<nint, PendingMinimize> _pending = [];

    public void Track(AutoPinWindowIdentity identity) =>
        _pending[identity.Hwnd] = new PendingMinimize(identity, MaximumAttempts);

    public void Forget(nint hwnd) => _pending.Remove(hwnd);

    public void Clear() => _pending.Clear();

    public IReadOnlyList<AutoPinWindowIdentity> TakeRetryCandidates(
        Func<AutoPinWindowIdentity, bool> isLive)
    {
        var result = new List<AutoPinWindowIdentity>();
        foreach (var pair in _pending.ToArray())
        {
            if (!isLive(pair.Value.Identity) || pair.Value.RemainingAttempts <= 0)
            {
                _pending.Remove(pair.Key);
                continue;
            }

            result.Add(pair.Value.Identity);
            _pending[pair.Key] = pair.Value with
            {
                RemainingAttempts = pair.Value.RemainingAttempts - 1,
            };
        }
        return result;
    }

    private sealed record PendingMinimize(
        AutoPinWindowIdentity Identity, int RemainingAttempts);
}
