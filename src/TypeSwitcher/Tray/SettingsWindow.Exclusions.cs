using System.IO;
using System.Windows;
using System.Windows.Controls;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public sealed partial class SettingsWindow
{
    private UIElement BuildExclusionButtons()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(FeatureButton("Выбрать окно…", "exclusions.window", () =>
        {
            var picker = new WindowPicker { Owner = this };
            picker.Picked += AddExcludedApplication;
            picker.ShowDialog();
        }));
        panel.Children.Add(FeatureButton("Работающие приложения…", "exclusions.running", () =>
        {
            var picker = new ApplicationPickerWindow { Owner = this };
            picker.Picked += AddExcludedApplication;
            picker.ShowDialog();
        }));
        panel.Children.Add(FeatureButton("Выбрать .exe…", "exclusions.file", () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = L.T("Выберите приложение"), Filter = L.T("Исполняемые файлы (*.exe)|*.exe"), CheckFileExists = true };
            if (picker.ShowDialog(this) == true) AddExcludedApplication(Path.GetFileName(picker.FileName));
        }));
        return panel;
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
