using System.Diagnostics;
using System.Text.Json;

namespace MaximizeToVirtualDesktop;

internal static class SnapWorkspacePersistence
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaximizeToVirtualDesktop", "snap-workspaces.json");
    private static readonly object Sync = new();

    internal sealed record PersistedWorkspace(
        Guid TempDesktopId, string MonitorId, DateTime CreatedAt);

    public static void Save(IEnumerable<SnapWorkspaceEntry> workspaces)
    {
        lock (Sync)
        {
            try
            {
                var entries = workspaces.Select(workspace => new PersistedWorkspace(
                    workspace.TempDesktopId, workspace.MonitorId, DateTime.UtcNow)).ToArray();
                if (entries.Length == 0)
                {
                    Delete();
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"SnapWorkspacePersistence: save failed: {ex.Message}");
            }
        }
    }

    public static IReadOnlyList<PersistedWorkspace> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<PersistedWorkspace[]>(
                File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"SnapWorkspacePersistence: load failed: {ex.Message}");
            return [];
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }
}
