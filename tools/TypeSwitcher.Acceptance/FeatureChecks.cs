using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TypeSwitcher.Core;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Acceptance;

internal static class FeatureChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        var process = session.Start("--settings 11 --offscreen");
        var window = Uia.Window(process.Id, "TypeSwitcher");
        var version = Uia.Find(window, Uia.Id("about.version"), "About version");
        report.Check("about-version", version.Current.Name.Contains("2.2.0", StringComparison.Ordinal));
        report.Check("about-github-link", Uia.Find(window, Uia.Id("about.github"), "GitHub hyperlink").Current.ControlType == ControlType.Hyperlink);
        string current = "ru";
        foreach (var (id, name) in new[] { ("en", "English"), ("uk", "Українська"), ("ru", "Русский") })
        {
            Uia.Navigate(window, L.Get("Язык интерфейса", current));
            Uia.Choose(Uia.Find(window, Uia.Id("appearance.language"), "Interface language"), name, process.Id);
            Uia.Navigate(window, L.Get("О программе", id));
            report.Check("about-language-" + id, Uia.Find(window, Uia.Id("about.version"), "About version").Current.Name == L.Get("Версия {0}", id, "2.2.0"));
            current = id;
        }

        Uia.Navigate(window, "Общие");
        report.Check("hotkey-chord-editor", Uia.Value(Uia.Find(window, Uia.Id("hotkey.SelectionLayout"), "Selection shortcut")) == "Ctrl+Alt+L");
        Uia.Toggle(Uia.RowControl(window, "Копить локальный лог правок", ControlType.CheckBox), true);
        Uia.SetValue(Uia.RowControl(window, "Лимит записей истории", ControlType.Edit), "10");
        Uia.Toggle(Uia.RowControl(window, "Показывать раскладку в трее", ControlType.CheckBox), true);
        Uia.Toggle(Uia.RowControl(window, "Показывать язык у каретки", ControlType.CheckBox), true);

        Uia.Navigate(window, "Языки");
        Uia.Toggle(Uia.RowControl(window, "Выбирать язык по приложению", ControlType.CheckBox), true);
        string editor = session.Fixture("test-editor.exe", "Selection fixture; never run.");
        _ = Uia.Invoke(Uia.Find(window, Uia.Id("languages.apps.file"), "Select application for layout"));
        Uia.FileDialog(process.Id, editor, "Выберите приложение");
        Uia.Choose(Uia.Find(window, Uia.Id("languages.app.test-editor.exe"), "Application language"), "Українська", process.Id);

        Uia.Navigate(window, "Опечатки");
        var english = Uia.RowControl(window, "Исправлять английские опечатки", ControlType.CheckBox);
        Uia.Toggle(english, true);
        var reset = Uia.Find(window, Uia.Id("reset.typo.english"), "English setting reset");
        report.Check("individual-reset-enabled", reset.Current.IsEnabled);
        Uia.Invoke(reset).GetAwaiter().GetResult();
        Uia.Until(() => !Uia.Checked(english), "The individual reset did not complete.");
        report.Check("individual-reset-value", !Uia.Checked(english));
        Uia.Toggle(english, true);

        Uia.Navigate(window, "Раскладка");
        Uia.Toggle(Uia.RowControl(window, "Исправлять после паузы", ControlType.CheckBox), true);
        Uia.Toggle(Uia.RowControl(window, "Раннее переключение раскладки", ControlType.CheckBox), true);
        Uia.Choose(Uia.Find(window, Uia.Id("context.mode"), "Context mode"), "Наблюдать", process.Id);
        Uia.Click(window, "Сохранить");
        Uia.Until(() => { using var saved = session.Config(); return saved.RootElement.GetProperty("typo").GetProperty("english_enabled").GetBoolean(); }, "Feature settings were not saved.");
        using (var json = session.Config())
        {
            var root = json.RootElement;
            report.Check("feature-schema", root.GetProperty("schema_version").GetInt32() == 3);
            report.Check("caret-indicator-saved", root.GetProperty("tray").GetProperty("show_caret_indicator").GetBoolean());
            report.Check("application-layout-saved", root.GetProperty("languages").GetProperty("use_application_layouts").GetBoolean()
                && root.GetProperty("languages").GetProperty("application_layouts").GetProperty("test-editor.exe").GetString() == "uk");
            report.Check("english-option-saved", root.GetProperty("typo").GetProperty("english_enabled").GetBoolean());
            report.Check("automation-options-saved", root.GetProperty("layout").GetProperty("correct_on_idle").GetBoolean()
                && root.GetProperty("layout").GetProperty("early_switch").GetBoolean()
                && root.GetProperty("layout").GetProperty("context_mode").GetString() == "observe");
            report.Check("history-and-indicator-saved", root.GetProperty("learning").GetProperty("max_entries").GetInt32() == 10
                && root.GetProperty("tray").GetProperty("show_layout_indicator").GetBoolean());
        }

        Uia.Navigate(window, "Правила раскладки");
        Uia.SetValue(Uia.Find(window, Uia.Id("rules.pattern"), "Rule pattern"), "ghb");
        Uia.Choose(Uia.Find(window, Uia.Id("rules.match"), "Rule match"), "Начинается с", process.Id);
        Uia.Choose(Uia.Find(window, Uia.Id("rules.source"), "Rule source"), "English", process.Id);
        Uia.Invoke(Uia.Find(window, Uia.Id("rules.save"), "Save layout rule")).GetAwaiter().GetResult();
        Uia.Click(window, "Сохранить");
        Uia.Until(() => { using var saved = session.Config(); return saved.RootElement.GetProperty("layout").GetProperty("rules").GetArrayLength() == 1; }, "Layout rule was not saved.");
        using (var json = session.Config())
        {
            var rule = json.RootElement.GetProperty("layout").GetProperty("rules")[0];
            report.Check("layout-rule-saved", rule.GetProperty("pattern").GetString() == "ghb" && rule.GetProperty("match").GetString() == "prefix" && rule.GetProperty("action").GetString() == "keep");
        }

        Uia.Navigate(window, "Проверка");
        Uia.SetValue(Uia.Find(window, Uia.Id("diagnostics.input"), "Diagnostic sample"), "helllo ghbdtn");
        Uia.Choose(Uia.Find(window, Uia.Id("diagnostics.language"), "Sample language"), "English", process.Id);
        Uia.Invoke(Uia.Find(window, Uia.Id("diagnostics.run"), "Run diagnostics")).GetAwaiter().GetResult();
        var output = Uia.Find(window, Uia.Id("diagnostics.result"), "Diagnostic result");
        Uia.Until(() => Uia.Value(output).Contains("helllo → hello", StringComparison.Ordinal), "Diagnostic result not ready.", 30000);
        report.Check("diagnostic-explanation", Uia.Value(output).Contains("Правило запрещает переключение", StringComparison.Ordinal));
        report.Check("diagnostic-sample-not-saved", !File.Exists(Path.Combine(session.Data, "typeswitcher_word_probe.txt")));

        string historyPath = Path.Combine(session.Data, "corrections.jsonl");
        new LearnLog(historyPath, true).LogCorrection("historysample", "result", ActionKind.Typo);
        Uia.Navigate(window, "История");
        Uia.Invoke(Uia.Find(window, Uia.Id("history.refresh"), "Refresh history")).GetAwaiter().GetResult();
        var entry = Uia.Wait(() => Uia.Children(window, Uia.Type(ControlType.ListItem)).FirstOrDefault(e => e.Current.Name.Contains("historysample", StringComparison.Ordinal)), "History record");
        Uia.Select(entry);
        Uia.Invoke(Uia.Find(window, Uia.Id("history.protect"), "Protect history word")).GetAwaiter().GetResult();
        Uia.Click(window, "Сохранить");
        Uia.Until(() => File.ReadAllText(Path.Combine(session.Data, "stopwords.txt")).Contains("historysample", StringComparison.Ordinal), "History word was not saved.");
        report.Pass("history-protect-word");
        Uia.Invoke(Uia.Find(window, Uia.Id("history.clear"), "Clear history")).GetAwaiter().GetResult();
        try { Uia.Until(() => CorrectionHistory.Read(historyPath, 10).Count == 0, "History was not cleared."); }
        catch
        {
            foreach (var visible in Uia.Windows(process.Id))
                report.Note("history-window", visible.Current.Name + ": " + string.Join(" | ", Uia.Children(visible, Uia.Type(ControlType.Text)).Select(e => e.Current.Name)));
            throw;
        }
        report.Check("history-cleared", CorrectionHistory.Read(historyPath, 10).Count == 0);

        Uia.Navigate(window, "Исключения");
        string file = session.Fixture("acceptance-excluded.exe", "Fixture only; never executed.");
        _ = Uia.Invoke(Uia.Find(window, Uia.Id("exclusions.file"), "Choose executable"));
        Uia.FileDialog(process.Id, file, "Выберите приложение");
        Uia.Click(window, "Сохранить");
        Uia.Until(() => { using var saved = session.Config(); return saved.RootElement.GetProperty("exclusions").GetProperty("app_blacklist").EnumerateArray().Any(x => x.GetString() == "acceptance-excluded.exe"); }, "Selected application was not saved.");
        using (var json = session.Config())
            report.Check("executable-picker-saved", json.RootElement.GetProperty("exclusions").GetProperty("app_blacklist").EnumerateArray().Any(x => x.GetString() == "acceptance-excluded.exe"));

        Uia.Close(window, process);
        process = session.Start("--settings 8 --offscreen"); window = Uia.Window(process.Id, "TypeSwitcher");
        report.Check("layout-rule-restored", Uia.Children(window, Uia.Type(ControlType.ListItem)).Any(x => x.Current.Name.Contains("ghb", StringComparison.Ordinal)));
        Uia.Navigate(window, "Языки");
        Uia.Invoke(Uia.Find(window, Uia.Id("languages.remove.test-editor.exe"), "Remove application rule")).GetAwaiter().GetResult();
        Uia.Click(window, "Сохранить");
        Uia.Until(() => { using var saved = session.Config(); return !saved.RootElement.GetProperty("languages").GetProperty("application_layouts").EnumerateObject().Any(); }, "Application rule was not removed.");
        report.Pass("application-layout-removed");
        Uia.Close(window, process);
    }
}
