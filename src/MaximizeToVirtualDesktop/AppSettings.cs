using System.Diagnostics;
using System.Text.Json;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Desktop switch animation mode (placeholder for future options).
/// </summary>
internal enum DesktopSwitchMode
{
    Smooth = 0,
}

internal enum TriggerModifier
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Win = 3,
    Alt = 4,
}

/// <summary>
/// Persists user-configurable settings (portable — stored alongside exe).
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public uint HotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
    public uint HotkeyKey { get; set; } = NativeMethods.VK_X;

    public uint PinHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
    public uint PinHotkeyKey { get; set; } = NativeMethods.VK_P;

    /// <summary>Which modifier key must be held (if any) for the trigger to fire.
    /// None = double-click/maximize directly triggers virtual desktop.
    /// Shift/Ctrl/Win/Alt = hold that key to trigger.</summary>
    public TriggerModifier TriggerKey { get; set; } = TriggerModifier.None;

    public bool ShowSwitchPopup { get; set; } = true;

    public DesktopSwitchMode SwitchMode { get; set; } = DesktopSwitchMode.Smooth;

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
