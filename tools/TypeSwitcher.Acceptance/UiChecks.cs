using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TypeSwitcher.Config;
using TypeSwitcher.Core;

namespace TypeSwitcher.Acceptance;

internal static class UiChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        var process = session.Start("--settings 3 --offscreen");
        var window = Uia.Window(process.Id, "TypeSwitcher");
        report.Pass("real-wpf-window", $"PID {process.Id}");
        var ukrainian = Uia.RowControl(window, "Українська", ControlType.CheckBox);
        Uia.Toggle(ukrainian, true);
        Uia.SetValue(Uia.RowControl(window, "Порядок раскладок", ControlType.Edit), "en, uk, ru");
        Uia.Click(window, "Сохранить");
        Uia.Until(() => Languages(session).Contains("uk"), "Language setting not saved.");
        report.Pass("language-selection-save", "uk enabled through UI Automation");

        Uia.Navigate(window, "Словари");
        var scope = Uia.Find(window, Uia.Type(ControlType.ComboBox), "List scope");
        Uia.Choose(scope, "Українська", process.Id);
        var edits = Uia.Children(window, Uia.Type(ControlType.Edit)).ToArray();
        report.Check("dictionary-editors", edits.Length == 2, "two visible editors");
        Uia.SetValue(edits[0], "зумбоприймач\n");
        Uia.SetValue(edits[1], "нечіпай\n");
        Uia.Click(window, "Сохранить");
        Uia.Until(() => File.ReadAllText(Path.Combine(session.Data, "my_words.uk.txt")).Contains("зумбоприймач"), "Personal words not saved.");
        Uia.Choose(scope, "Русский", process.Id);
        report.Check("ui-language-isolation", !Uia.Value(edits[0]).Contains("зумбоприймач"));

        Uia.Navigate(window, "Правила");
        Uia.Choose(Uia.Find(window, Uia.Type(ControlType.ComboBox), "Rule scope"), "Українська", process.Id);
        Uia.SetValue(Uia.Find(window, Uia.Type(ControlType.Edit), "Rule editor"), "скор = довге\n");
        Uia.Click(window, "Сохранить");
        Uia.Until(() => File.ReadAllText(Path.Combine(session.Data, "rules.uk.txt")).Contains("скор = довге"), "Rule not saved.");
        report.Pass("personal-lists-save", "UK words, stops and rules written by the real window");
        Uia.Close(window, process);

        process = session.Start("--settings 3 --offscreen");
        window = Uia.Window(process.Id, "TypeSwitcher");
        report.Check("restart-language", Uia.Checked(Uia.RowControl(window, "Українська", ControlType.CheckBox)));
        report.Check("restart-order", Uia.Value(Uia.RowControl(window, "Порядок раскладок", ControlType.Edit)) == "en, uk, ru");
        Uia.Close(window, process);

        process = session.Start("--dictionary-settings --offscreen");
        window = Uia.Window(process.Id, "подключённые словари");
        var combos = Uia.Children(window, Uia.Type(ControlType.ComboBox)).ToArray();
        report.Check("library-combos", combos.Length == 2);
        Uia.Choose(combos[0], "Українська", process.Id);
        Uia.Choose(combos[1], "Список слов", process.Id);
        string words = session.Fixture("acceptance-words.txt", "зумбопак\nзумбослівник\n");
        Uia.Click(window, "Импорт…");
        Uia.FileDialog(process.Id, words);
        Uia.Until(() => Entries(session).Length == 1, "Words not imported.");
        var first = Entries(session)[0];
        report.Check("real-file-dialog-import", first.GetProperty("language").GetString() == "uk" && first.GetProperty("word_count").GetInt32() == 2);
        string oldId = first.GetProperty("id").GetString()!;
        var enabled = Uia.ByName(window, "Использовать выбранный словарь", ControlType.CheckBox);
        Uia.Toggle(enabled, false);
        Uia.Until(() => !Entries(session)[0].GetProperty("enabled").GetBoolean(), "Disable not persisted.");
        Uia.Toggle(enabled, true);
        Uia.Until(() => Entries(session)[0].GetProperty("enabled").GetBoolean(), "Enable not persisted.");
        report.Pass("import-enable-disable");

        string updated = session.Fixture("acceptance-words-updated.txt", "зумбопак\nоновленеслівце\n");
        Uia.Click(window, "Обновить…");
        Uia.FileDialog(process.Id, updated);
        Uia.Until(() => Entries(session)[0].GetProperty("id").GetString() != oldId, "Update not committed.");
        report.Check("import-update", !Directory.Exists(Path.Combine(session.Data, "imported", oldId)), "new revision; old owned directory removed");
        string currentId = Entries(session)[0].GetProperty("id").GetString()!;
        string invalid = session.Fixture("acceptance-invalid.txt", "two words on a line\n");
        Uia.Click(window, "Импорт…");
        Uia.FileDialog(process.Id, invalid);
        Uia.Wait(() => Uia.Children(window, Uia.Type(ControlType.Text)).FirstOrDefault(e => e.Current.Name.StartsWith("Импорт не выполнен", StringComparison.Ordinal)), "Import error not shown.");
        report.Check("invalid-import-keeps-state", Entries(session).Length == 1 && Entries(session)[0].GetProperty("id").GetString() == currentId);
        report.Check("staging-cleanup", !Directory.EnumerateDirectories(Path.Combine(session.Data, "imported"), ".stage-*").Any());

        Uia.Choose(combos[1], "Словарь Hunspell", process.Id);
        string dic = session.Fixture("acceptance-hunspell.dic", "1\nзумбопак/Q\n");
        session.Fixture("acceptance-hunspell.aff", "SET UTF-8\nSFX Q Y 1\nSFX Q 0 и .\n");
        Uia.Click(window, "Импорт…");
        Uia.FileDialog(process.Id, dic);
        Uia.Until(() => Entries(session).Length == 2, "Hunspell not imported.");
        var store = new ConfigStore(session.Data, session.Root);
        var loaded = DictionaryManager.Load(store, store.Load());
        report.Check("imported-hunspell-morphology", !loaded.Errors.Any() && loaded.Dictionaries.IsValid(LayoutTarget.Uk, "зумбопаки"));
        Uia.Click(window, "Удалить");
        Uia.Until(() => Entries(session).Length == 1, "Imported dictionary not removed.");
        report.Pass("ui-delete-imported-dictionary");
        Uia.Close(window, process);
        process = session.Start("--dictionary-settings --offscreen");
        window = Uia.Window(process.Id, "подключённые словари");
        report.Check("library-restart", Uia.Children(window, Uia.Type(ControlType.ListItem)).Any(e => e.Current.Name.Contains("acceptance-words-updated", StringComparison.Ordinal)));
        Uia.Close(window, process);
    }

    private static string[] Languages(TestSession session)
    {
        using var json = session.Config();
        return json.RootElement.GetProperty("languages").GetProperty("enabled").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
    private static JsonElement[] Entries(TestSession session)
    {
        using var json = session.Config();
        return json.RootElement.GetProperty("dictionaries").GetProperty("installed").EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}
