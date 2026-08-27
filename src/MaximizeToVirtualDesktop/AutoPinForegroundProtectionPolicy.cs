namespace MaximizeToVirtualDesktop;

/// <summary>
/// Defines when a foreground observation contains enough final window state to
/// acknowledge an activation, and the pin state that proves reconciliation.
/// </summary>
internal static class AutoPinForegroundProtectionPolicy
{
    public static bool IsReady(AutoPinObservation observation)
    {
        var window = observation.Windows.FirstOrDefault(candidate =>
            candidate.Hwnd == observation.ForegroundWindow);
        if (window is null || !window.IsEligible
            || window.IsMinimized || !window.IsDisplayed)
        {
            return false;
        }

        return observation.DesktopKind != AutoPinDesktopKind.Fullscreen
            || (observation.AnchorZOrder.HasValue
                && window.ZOrder < observation.AnchorZOrder.Value);
    }

    public static bool ExpectedPinnedState(
        AutoPinMode mode, AutoPinDesktopKind desktopKind) => false;

    public static AutoPinObservation PromoteStableScan(AutoPinObservation observation) =>
        IsReady(observation)
            ? observation with { IsForegroundActivation = true }
            : observation;
}
