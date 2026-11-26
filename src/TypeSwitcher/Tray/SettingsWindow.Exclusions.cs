using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public sealed partial class SettingsWindow
{
    private UIElement BuildExclusionButtons() => BuildApplicationButtons("exclusions", AddExcludedApplication);

    private UIElement BuildApplicationButtons(string id, Action<string> picked, bool useInputProcess = false)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(FeatureButton("Выбрать окно…", id + ".window", () =>
        {
            var picker = new WindowPicker(useInputProcess) { Owner = this };
            picker.Picked += picked;
            picker.ShowDialog();
        }));
        panel.Children.Add(FeatureButton("Работающие приложения…", id + ".running", () =>
        {
            var picker = new ApplicationPickerWindow(useInputProcess) { Owner = this };
            picker.Picked += picked;
            picker.ShowDialog();
        }));
        panel.Children.Add(FeatureButton("Выбрать .exe…", id + ".file", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = L.T("Выберите приложение"), Filter = L.T("Исполняемые файлы (*.exe)|*.exe"), CheckFileExists = true };
            if (picker.ShowDialog(this) == true) picked(Path.GetFileName(picker.FileName));
        }));
        return panel;
    }

    private void BuildApplicationLayouts(Panel page)
    {
        var card = Card(page, "Язык при входе в приложение");
        _useApplicationLayouts = Toggle(card, "Выбирать язык по приложению", _cfg.Languages.UseApplicationLayouts,
            "Раскладка выбирается один раз при входе. Внутри приложения её можно менять вручную. Выключенные и отсутствующие языки пропускаются.");
        var rows = new StackPanel { Margin = new Thickness(16, 0, 16, 8) };
        card.Children.Add(rows);

        void AddRow(string exe, string language)
        {
            if (_applicationLanguages.TryGetValue(exe, out var existing)) { existing.Focus(); return; }
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var choice = new ComboBox { Width = 125, ItemsSource = LanguageChoices(), DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = language };
            L.Bind(choice, AutomationProperties.NameProperty, "Язык для {0}", exe);
            AutomationProperties.SetAutomationId(choice, "languages.app." + exe);
            var remove = FeatureButton("Удалить", "languages.remove." + exe, () => { _applicationLanguages.Remove(exe); rows.Children.Remove(row); });
            remove.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            DockPanel.SetDock(choice, Dock.Right); row.Children.Add(choice);
            row.Children.Add(new TextBlock { Text = exe, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _applicationLanguages.Add(exe, choice);
            rows.Children.Add(row);
        }

        foreach (var pair in _cfg.Languages.ApplicationLayouts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) AddRow(pair.Key, pair.Value);
        card.Children.Add(BuildApplicationButtons("languages.apps", exe => AddRow(exe, _languageChecks.FirstOrDefault(p => p.Value.IsChecked == true).Key ?? "en"), useInputProcess: true));
    }

    internal void AddExcludedApplication(string name)
    {
        name = Path.GetFileName(name).Trim();
        if (name.Length == 0) return;
        var values = SplitLines(_blacklist.Text);
        if (!values.Contains(name, StringComparer.OrdinalIgnoreCase)) values.Add(name);
        _blacklist.Text = string.Join(Environment.NewLine, values);
        L.Bind(_status, TextBlock.TextProperty, "Приложение добавлено. Нажмите «Сохранить».");
    }
}
