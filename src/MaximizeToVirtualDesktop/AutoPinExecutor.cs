using System.Diagnostics;

namespace MaximizeToVirtualDesktop;

/// <summary>Applies pin-state commands and preserves the user's known baseline.</summary>
internal sealed class AutoPinExecutor
{
    private readonly IAutoPinCommandPlatform _platform;
    private readonly AutoPinOwnership _ownership;

    public AutoPinExecutor(IAutoPinCommandPlatform platform, AutoPinOwnership ownership)
    {
        _platform = platform;
        _ownership = ownership;
    }

    public AutoPinExecutionResult Apply(AutoPinDecisionPlan plan, Func<bool> mayContinue)
    {
        var confirmed = new List<AutoPinCommand>();
        foreach (var command in plan.Commands)
        {
            if (!mayContinue()) break;
            var identity = new AutoPinWindowIdentity(command.Hwnd, command.ProcessId);
            if (!_platform.CanMutate(identity))
            {
                _ownership.Forget(command.Hwnd);
                continue;
            }
            if (!_platform.IsSameLiveWindow(identity))
            {
                _ownership.Forget(command.Hwnd);
                continue;
            }

            // An unavailable application view is never permission to mutate.
            if (!_platform.TryGetPinState(identity, out var isPinned)) continue;
            _ownership.CaptureInitialPinState(command.Hwnd, command.ProcessId, isPinned);

            var shouldBePinned = command.Target == AutoPinTarget.Pinned;
            if (command.Preparation == AutoPinPreparation.Minimize)
            {
                if (!mayContinue()) break;
                if (!_platform.TryMinimizeAndConfirm(identity))
                {
                    Trace.WriteLine(
                        $"AutoPinExecutor[t={Environment.TickCount64}]: "
                        + $"minimize not confirmed for {command.Hwnd}; pin skipped.");
                    continue;
                }
            }

            if (isPinned == shouldBePinned)
            {
                if (!mayContinue()) break;
                confirmed.Add(command);
                continue;
            }

            if (!mayContinue()) break;
            var changed = _platform.SetPinState(identity, shouldBePinned);
            Trace.WriteLine(
                $"AutoPinExecutor[t={Environment.TickCount64}]: "
                + $"{command.Target} {command.Hwnd}: {changed}.");
            if (changed && mayContinue()) confirmed.Add(command);
        }

        return new AutoPinExecutionResult(confirmed);
    }

    public void Forget(nint hwnd) => _ownership.Forget(hwnd);

    public bool RestoreUserState(Func<bool> mayContinue)
    {
        foreach (var baseline in _ownership.ManagedWindows)
        {
            if (!mayContinue()) return false;
            var identity = new AutoPinWindowIdentity(baseline.Hwnd, baseline.ProcessId);
            if (!_platform.CanMutate(identity))
            {
                _ownership.Forget(baseline.Hwnd);
                continue;
            }
            if (!_platform.IsSameLiveWindow(identity))
            {
                _ownership.Forget(baseline.Hwnd);
                continue;
            }
            if (!_platform.TryGetPinState(identity, out var isPinned)) continue;
            if (isPinned == baseline.WasPinned)
            {
                if (!mayContinue()) return false;
                _ownership.Forget(baseline.Hwnd);
                continue;
            }

            if (!mayContinue()) return false;
            var changed = _platform.SetPinState(identity, baseline.WasPinned);
            Trace.WriteLine(
                $"AutoPinExecutor[t={Environment.TickCount64}]: restored "
                + $"{identity.Hwnd} pinned={baseline.WasPinned}: {changed}.");
            if (changed && mayContinue()) _ownership.Forget(baseline.Hwnd);
        }

        return _ownership.ManagedWindows.Count == 0;
    }

}

internal interface IAutoPinCommandPlatform
{
    bool CanMutate(AutoPinWindowIdentity identity);
    bool IsSameLiveWindow(AutoPinWindowIdentity identity);
    bool TryGetPinState(AutoPinWindowIdentity identity, out bool isPinned);
    bool TryMinimizeAndConfirm(AutoPinWindowIdentity identity);
    bool SetPinState(AutoPinWindowIdentity identity, bool shouldBePinned);
}

internal sealed record AutoPinExecutionResult(
    IReadOnlyList<AutoPinCommand> ConfirmedCommands);
