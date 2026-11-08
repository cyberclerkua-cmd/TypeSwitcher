using System.IO;
using System.Text.Json;

namespace TypeSwitcher.Config;

/// <summary>Explicit upgrade information written by the data migration tool.</summary>
public static class UpgradeHistory
{
    public const string FileName = "upgrade-history.json";

    public static IReadOnlyList<string> PreviousExecutables(string appDirectory)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectory, "data", FileName)));
            return json.RootElement.GetProperty("previous_executables").EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                .Where(p => Path.IsPathRooted(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return [];
        }
    }

    public static bool PreviousInstanceIsRunning(string appDirectory)
    {
        foreach (string executable in PreviousExecutables(appDirectory))
        {
            string name = Path.GetFileNameWithoutExtension(executable);
            if (name.Equals("TypeSwitcher", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Mutex.TryOpenExisting(@"Local\" + name + "_SingleInstance", out var instance))
                {
                    instance.Dispose();
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }
}
