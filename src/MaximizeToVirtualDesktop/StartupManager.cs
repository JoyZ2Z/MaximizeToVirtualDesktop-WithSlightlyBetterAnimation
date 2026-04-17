using System.Diagnostics;
using Microsoft.Win32;

namespace MaximizeToVirtualDesktop;

/// <summary>
/// Manages the app's presence in Windows startup settings (Run registry key).
/// Uses HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
internal static class StartupManager
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MaximizeToVirtualDesktop";

    /// <summary>
    /// Enables the app to run at Windows startup by adding a registry entry.
    /// </summary>
    public static bool EnableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key == null)
                return false;

            string exePath = Path.GetFullPath(Application.ExecutablePath);
            string quotedExePath = $"\"{exePath}\"";
            key.SetValue(AppName, quotedExePath);
            Trace.WriteLine($"StartupManager: Enabled startup for {quotedExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"StartupManager: Failed to enable startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables the app from running at startup by removing the registry entry.
    /// </summary>
    public static bool DisableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key == null)
                return false;

            key.DeleteValue(AppName, throwOnMissingValue: false);
            Trace.WriteLine("StartupManager: Disabled startup");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"StartupManager: Failed to disable startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if the app is currently registered to run at startup.
    /// </summary>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath);
            if (key == null)
                return false;

            var value = key.GetValue(AppName);
            if (value == null)
                return false;

            // Normalize both paths to handle case differences and different separators
            string registryPath = value.ToString()?.Trim() ?? "";
            string currentExePath = Application.ExecutablePath;

            // Remove quotes if present (some registry entries may be quoted)
            registryPath = registryPath.Trim('"');

            // Case-insensitive comparison on Windows (paths are case-insensitive on NTFS)
            // Also normalize path separators
            registryPath = Path.GetFullPath(registryPath);
            currentExePath = Path.GetFullPath(currentExePath);

            Trace.WriteLine($"StartupManager: Comparing registry path '{registryPath}' with current path '{currentExePath}'");
            return registryPath.Equals(currentExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"StartupManager: Failed to check startup status: {ex.Message}");
            return false;
        }
    }
}
