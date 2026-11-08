using Microsoft.Win32;

namespace TypeSwitcher.Config;

/// <summary>
/// Автозапуск с Windows через ключ реестра HKCU\...\Run (без прав администратора).
/// </summary>
public static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TypeSwitcher";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? (enabled ? Registry.CurrentUser.CreateSubKey(RunKey) : null);
            if (key == null) return;
            ApplyToKey(key, enabled, Environment.ProcessPath ?? "", UpgradeHistory.PreviousExecutables(AppContext.BaseDirectory));
        }
        catch
        {
            // Автозапуск не критичен - молча игнорируем сбой реестра.
        }
    }

    internal static void ApplyToKey(RegistryKey key, bool enabled, string executable, IEnumerable<string> previousExecutables)
    {
        var previous = previousExecutables.ToArray();
        foreach (string name in key.GetValueNames())
        {
            if (name.Equals(ValueName, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(name) is string command && previous.Any(path => Targets(command, path)))
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        if (enabled && executable.Length > 0) key.SetValue(ValueName, $"\"{executable}\"");
        else if (!enabled && key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static bool Targets(string command, string executable)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 && command[1..end].Equals(executable, StringComparison.OrdinalIgnoreCase);
        }
        return command.Equals(executable, StringComparison.OrdinalIgnoreCase)
            || command.StartsWith(executable + " ", StringComparison.OrdinalIgnoreCase);
    }
}
