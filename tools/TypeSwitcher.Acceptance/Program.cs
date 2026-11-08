using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TypeSwitcher.Config;

namespace TypeSwitcher.Acceptance;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--input-host")) return InputHost.Run();
        int lowIntegrity = Array.IndexOf(args, "--low-integrity-probe");
        if (lowIntegrity >= 0)
        {
            try
            {
                IntPtr window = (IntPtr)long.Parse(args[lowIntegrity + 1]);
                Native.RestrictThisHelperToLowIntegrity();
                bool correct = TypeSwitcher.Interop.ExclusionManager.OwnIntegrityLevel == 0x1000
                    && new TypeSwitcher.Interop.ExclusionManager().IsAboveOwnIntegrity(window);
                return correct ? 0 : 3;
            }
            catch { return 4; }
        }
        string Option(string name, string fallback = "")
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        string app = Path.GetFullPath(Option("--app", "dist/TypeSwitcher/TypeSwitcher.exe"));
        string work = Path.GetFullPath(Option("--work-root", Path.GetTempPath()));
        string report = Path.GetFullPath(Option("--report", "artifacts/acceptance"));
        string phase = Option("--phase", "ui");
        if (phase == "summary") return AcceptanceReport.Summarize(report);
        if (phase == "dpi") return DpiChecks.Run(app, work, report);
        return Task.Run(() => Run(app, work, report, phase)).GetAwaiter().GetResult();
    }

    private static int Run(string app, string work, string reportDirectory, string phase)
    {
        Directory.CreateDirectory(reportDirectory);
        var report = new AcceptanceReport(reportDirectory);
        string sandbox = Path.Combine(work, "TypeSwitcher acceptance " + Guid.NewGuid().ToString("N"));
        using var session = new TestSession(app, sandbox);
        try
        {
            report.Note("OS", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            report.Note("Session", Environment.UserInteractive ? "interactive" : "non-interactive");
            report.Note("Sandbox", sandbox);
            if (phase == "ui") UiChecks.Run(session, report);
            else if (phase == "native") DesktopChecks.Run(session, report);
            else if (phase == "browser") BrowserChecks.Run(session, report);
            else if (phase == "corpus") CorpusChecks.Run(session, report);
            else if (phase == "appearance") AppearanceChecks.Run(session, report);
            else throw new ArgumentException("Unknown phase: " + phase);
        }
        catch (Exception ex)
        {
            report.Fail("run", ex.ToString());
        }
        finally { report.Save(); }
        return report.Failed == 0 ? 0 : 1;
    }
}

internal sealed class AcceptanceReport(string directory)
{
    public string DirectoryPath => directory;
    public static int Summarize(string root)
    {
        int failures = 0, passes = 0;
        foreach (string file in Directory.EnumerateFiles(root, "acceptance.json", SearchOption.AllDirectories))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            int pass = json.RootElement.EnumerateArray().Count(e => e.GetProperty("status").GetString() == "pass");
            int fail = json.RootElement.EnumerateArray().Count(e => e.GetProperty("status").GetString() == "fail");
            Console.WriteLine($"{Path.GetRelativePath(root, file)}: PASS={pass}, FAIL={fail}");
            passes += pass; failures += fail;
        }
        Console.WriteLine($"TOTAL: PASS={passes}, FAIL={failures}");
        return failures == 0 ? 0 : 1;
    }
    private readonly List<object> _events = new();
    private readonly StringBuilder _text = new("# Windows acceptance checks\n\n");
    public int Failed { get; private set; }
    public void Note(string name, string value)
    {
        Console.WriteLine($"INFO {name}: {value}");
        _text.AppendLine($"- {name}: {value}");
        _events.Add(new { status = "info", name, value });
    }
    public void Pass(string name, string detail = "")
    {
        Console.WriteLine($"PASS {name}: {detail}");
        _text.AppendLine($"- PASS **{name}**: {detail}");
        _events.Add(new { status = "pass", name, detail });
    }
    public void Fail(string name, string detail)
    {
        Failed++;
        Console.WriteLine($"FAIL {name}: {detail}");
        _text.AppendLine($"- FAIL **{name}**: {detail}");
        _events.Add(new { status = "fail", name, detail });
    }
    public void Check(string name, bool condition, string detail = "")
    {
        if (!condition) throw new InvalidOperationException(name + ": " + detail);
        Pass(name, detail);
    }
    public void Save()
    {
        File.WriteAllText(Path.Combine(directory, "acceptance.md"), _text.ToString());
        File.WriteAllText(Path.Combine(directory, "acceptance.json"), JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed class TestSession : IDisposable
{
    private readonly List<Process> _owned = new();
    public string Root { get; }
    public string App { get; }
    public string Data => Path.Combine(Root, "data");
    public string Fixtures => Path.Combine(Root, "fixtures");

    public TestSession(string app, string root)
    {
        if (!File.Exists(app)) throw new FileNotFoundException("Build the application first.", app);
        Root = root;
        Directory.CreateDirectory(root);
        Copy(Path.GetDirectoryName(app)!, root);
        App = Path.Combine(root, Path.GetFileName(app));
        Directory.CreateDirectory(Fixtures);
        var store = new ConfigStore(Data, root);
        var cfg = new AppConfig();
        cfg.General.Enabled = false;
        cfg.General.Autostart = false;
        store.Save(cfg);
        store.EnsureUserLists();
    }

    private static void Copy(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source))
            if (!file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string subdirectory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(subdirectory);
            if (name.Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(target, name);
            Directory.CreateDirectory(destination);
            Copy(subdirectory, destination);
        }
    }

    public Process Start(string arguments)
        => StartExecutable(App, arguments);

    public Process StartExecutable(string executable, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Process did not start.");
        _owned.Add(process);
        return process;
    }

    public JsonDocument Config() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "config.json")));
    public string Fixture(string name, string text)
    {
        string path = Path.Combine(Fixtures, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        foreach (var process in _owned)
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        // Keep the isolated data for reproducible failure inspection; no user profile is used.
    }
}
