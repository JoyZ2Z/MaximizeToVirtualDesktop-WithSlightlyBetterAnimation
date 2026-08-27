using System.Diagnostics;
using System.Text.Json;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Persists user-configurable settings.
/// File lives in %LOCALAPPDATA%\MaximizeToVirtualDesktop\settings.json.
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaximizeToVirtualDesktop", "settings.json");

    /// <summary>Modifier flags for the maximize hotkey (MOD_CONTROL | MOD_ALT | MOD_SHIFT etc.).</summary>
    public uint HotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the maximize hotkey.</summary>
    public uint HotkeyKey { get; set; } = NativeMethods.VK_X;

    /// <summary>Modifier flags for the restore hotkey. Defaults to match the maximize hotkey (toggle behavior).</summary>
    public uint RestoreHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the restore hotkey.</summary>
    public uint RestoreHotkeyKey { get; set; } = NativeMethods.VK_X;

    /// <summary>Modifier flags for the pin hotkey.</summary>
    public uint PinHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the pin hotkey.</summary>
    public uint PinHotkeyKey { get; set; } = NativeMethods.VK_P;

    /// <summary>Modifier flags for the unpin hotkey. Defaults to match the pin hotkey (toggle behavior).</summary>
    public uint UnpinHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the unpin hotkey.</summary>
    public uint UnpinHotkeyKey { get; set; } = NativeMethods.VK_P;

    /// <summary>Modifier flags for the auto-pin hotkey.</summary>
    public uint AutoPinHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the auto-pin hotkey.</summary>
    public uint AutoPinHotkeyKey { get; set; } = NativeMethods.VK_A;

    /// <summary>When true, continuously pin non-fullscreen windows to all desktops.</summary>
    public bool AutoPinEnabled { get; set; } = false;

    /// <summary>Selected AutoPin mode. Null means this is a legacy settings file.</summary>
    public AutoPinMode? AutoPinMode { get; set; }

    /// <summary>Mode restored by the AutoPin hotkey when AutoPin is off.</summary>
    public AutoPinMode LastEnabledAutoPinMode { get; set; } =
        MaximizeToVirtualDesktop.AutoPinMode.TrackWindows;

    public AutoPinMode ResolveAutoPinMode() =>
        AutoPinModePolicy.Resolve(AutoPinMode, AutoPinEnabled);

    public void SetAutoPinMode(AutoPinMode mode)
    {
        AutoPinMode = mode;
        AutoPinEnabled = mode != MaximizeToVirtualDesktop.AutoPinMode.Off;
        if (mode != MaximizeToVirtualDesktop.AutoPinMode.Off)
            LastEnabledAutoPinMode = mode;
    }

    /// <summary>
    /// When true, reorder virtual desktops by recent use after each switch
    /// (main desktop stays first). When false, desktop order is left untouched.
    /// </summary>
    public bool AutoSortEnabled { get; set; } = false;

    /// <summary>Stable identity of Desktop 1, which Auto-Sort always keeps leftmost.</summary>
    public Guid? MainDesktopId { get; set; }

    /// <summary>
    /// How long (in seconds) a desktop must be visited before it counts as "recently used"
    /// for MRU ordering. Visits shorter than this are treated as passing through.
    /// </summary>
    public int MruThresholdSeconds { get; set; } = 5;

    /// <summary>
    /// When true, any click on the maximize button sends the window to a virtual desktop.
    /// Shift+Click performs a normal maximize instead.
    /// </summary>
    public bool InvertShiftClick { get; set; } = false;

    /// <summary>
    /// When true, show on-screen popup notifications when switching windows to/from virtual desktops.
    /// </summary>
    public bool ShowSwitchPopup { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AppSettings: Load failed: {ex.Message}");
            return new AppSettings();
        }
    }

    public bool Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, "settings.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AppSettings: Save failed: {ex.Message}");
            return false;
        }
    }
}
