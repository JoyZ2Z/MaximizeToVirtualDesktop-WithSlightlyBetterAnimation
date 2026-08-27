namespace MaximizeToVirtualDesktop;

internal enum AutoPinMode
{
    Off = 0,
    On = 1,
    TrackWindows = 2,
}

internal static class AutoPinModePolicy
{
    public static AutoPinMode Resolve(AutoPinMode? persistedMode, bool legacyEnabled) =>
        persistedMode ?? (legacyEnabled ? AutoPinMode.TrackWindows : AutoPinMode.Off);

    public static AutoPinMode Toggle(AutoPinMode currentMode, AutoPinMode lastEnabledMode)
    {
        if (currentMode != AutoPinMode.Off) return AutoPinMode.Off;
        return lastEnabledMode == AutoPinMode.Off
            ? AutoPinMode.TrackWindows
            : lastEnabledMode;
    }
}
