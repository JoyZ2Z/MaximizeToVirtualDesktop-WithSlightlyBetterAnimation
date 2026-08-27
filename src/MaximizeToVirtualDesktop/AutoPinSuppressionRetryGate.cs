namespace MaximizeToVirtualDesktop;

/// <summary>
/// Prevents a failed minimize-confirm-pin operation from repeatedly issuing
/// ShowWindow/DwmFlush on every unrelated AutoPin observation. A later stable
/// observation may retry; confirmation immediately removes the delay.
/// </summary>
internal sealed class AutoPinSuppressionRetryGate
{
    private readonly TimeSpan _retryDelay;
    private readonly Dictionary<AutoPinWindowIdentity, DateTime> _retryNotBefore = [];

    public AutoPinSuppressionRetryGate(TimeSpan retryDelay) => _retryDelay = retryDelay;

    public AutoPinDecisionPlan Filter(AutoPinDecisionPlan plan, DateTime now)
    {
        var commands = plan.Commands.Where(command =>
        {
            if (command.Preparation != AutoPinPreparation.Minimize) return true;
            var identity = new AutoPinWindowIdentity(command.Hwnd, command.ProcessId);
            return !_retryNotBefore.TryGetValue(identity, out var notBefore)
                || now >= notBefore;
        }).ToArray();
        return commands.Length == plan.Commands.Count
            ? plan
            : plan with { Commands = commands };
    }

    public void Record(AutoPinDecisionPlan appliedPlan,
        IReadOnlyCollection<AutoPinCommand> confirmedCommands, DateTime now)
    {
        var confirmed = confirmedCommands
            .Select(command => (command.Hwnd, command.ProcessId, command.Target))
            .ToHashSet();
        foreach (var command in appliedPlan.Commands)
        {
            if (command.Preparation != AutoPinPreparation.Minimize) continue;
            var identity = new AutoPinWindowIdentity(command.Hwnd, command.ProcessId);
            if (confirmed.Contains((command.Hwnd, command.ProcessId, command.Target)))
                _retryNotBefore.Remove(identity);
            else
                _retryNotBefore[identity] = now.Add(_retryDelay);
        }
    }

    public void Forget(nint hwnd)
    {
        foreach (var identity in _retryNotBefore.Keys
                     .Where(identity => identity.Hwnd == hwnd).ToArray())
            _retryNotBefore.Remove(identity);
    }

    public void Clear() => _retryNotBefore.Clear();
}
