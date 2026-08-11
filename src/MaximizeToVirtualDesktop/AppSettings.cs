using System.Diagnostics;
using System.Text.Json;
using MaximizeToVirtualDesktop.Interop;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Desktop switch animation mode (placeholder for future options).
/// </summary>
internal enum DesktopSwitchMode
{
    /// <summary>Immediate — no animations, instant switch + maximize.</summary>
    Immediate = 0,
}

/// <summary>
/// Persists user-configurable settings.
/// File lives in the same directory as the executable (portable).
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Modifier flags for the maximize hotkey (MOD_CONTROL | MOD_ALT | MOD_SHIFT etc.).</summary>
    public uint HotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the maximize hotkey.</summary>
    public uint HotkeyKey { get; set; } = NativeMethods.VK_X;

    /// <summary>Modifier flags for the pin hotkey.</summary>
    public uint PinHotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>Virtual key code for the pin hotkey.</summary>
    public uint PinHotkeyKey { get; set; } = NativeMethods.VK_P;

    /// <summary>
    /// When true, any click on the maximize button sends the window to a virtual desktop.
    /// Shift+Click performs a normal maximize instead.
    /// </summary>
    public bool InvertShiftClick { get; set; } = true;

    /// <summary>
    /// When true, show on-screen popup notifications when switching windows to/from virtual desktops.
    /// </summary>
    public bool ShowSwitchPopup { get; set; } = true;

    /// <summary>
    /// Desktop switch animation: Atomic (24H2, smoothest), Animated (slide), or Instant.
    /// </summary>
    public DesktopSwitchMode SwitchMode { get; set; } = DesktopSwitchMode.Immediate;

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
