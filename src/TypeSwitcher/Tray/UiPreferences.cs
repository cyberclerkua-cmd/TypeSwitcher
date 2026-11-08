using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using TypeSwitcher.Config;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

internal static class UiPreferences
{
    private static ConfigStore? _store;
    private static AppConfig? _config;
    public static IReadOnlyList<UiChoice> Languages { get; } =
        [new("ru", "Русский", true), new("en", "English", true), new("uk", "Українська", true)];
    public static IReadOnlyList<UiChoice> Themes { get; } =
        [new("light", "Светлая"), new("dark", "Тёмная"), new("system", "Системная")];

    public static void Initialize(ConfigStore store, AppConfig config)
    {
        _store = store; _config = config;
        L.SetLanguage(config.General.Language);
        Theme.Apply(config.General.Theme);
        Theme.WatchSystem();
    }
    public static void SetLanguage(string language)
    {
        if (_store == null || _config == null) return;
        string previous = _config.General.Language;
        _config.General.Language = L.NormalizeLanguage(language);
        try { _store.Save(_config); }
        catch { _config.General.Language = previous; throw; }
        L.SetLanguage(_config.General.Language);
    }
    public static void SetTheme(string theme)
    {
        if (_store == null || _config == null) return;
        string previous = _config.General.Theme;
        _config.General.Theme = Theme.Normalize(theme);
        try { _store.Save(_config); }
        catch { _config.General.Theme = previous; throw; }
        Theme.Apply(_config.General.Theme);
    }

    public static MenuItem LanguageMenu()
    {
        var menu = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "Язык интерфейса");
        foreach (var language in Languages)
        {
            var item = new MenuItem { Header = language.Label, IsCheckable = true };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(L.LanguageState.Language)) { Source = L.State, Converter = new IsChoice(language.Id), Mode = BindingMode.OneWay });
            item.Click += (_, _) => Save(() => SetLanguage(language.Id));
            menu.Items.Add(item);
        }
        return menu;
    }
    public static MenuItem ThemeMenu()
    {
        var menu = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "Тема");
        menu.Icon = ThemeIcon(16);
        foreach (var theme in Themes)
        {
            var item = new MenuItem { IsCheckable = true }.Localize(HeaderedItemsControl.HeaderProperty, theme.Id switch { "light" => "Светлая", "dark" => "Тёмная", _ => "Системная" });
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(Theme.ThemeState.Mode)) { Source = Theme.State, Converter = new IsChoice(theme.Id), Mode = BindingMode.OneWay });
            item.Click += (_, _) => Save(() => SetTheme(theme.Id));
            menu.Items.Add(item);
        }
        return menu;
    }
    public static Button ThemeButton()
    {
        var button = new Button { MinWidth = 38, Width = 42, Height = 36, Padding = new Thickness(4), Content = ThemeIcon(22) };
        L.Bind(button, ToolTipService.ToolTipProperty, "Выбор темы");
        L.Bind(button, AutomationProperties.NameProperty, "Выбор темы");
        AutomationProperties.SetAutomationId(button, "appearance.theme");
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            var choices = ThemeMenu();
            while (choices.Items.Count > 0) { var item = choices.Items[0]; choices.Items.RemoveAt(0); menu.Items.Add(item); }
            menu.IsOpen = true;
        };
        return button;
    }
    private static Viewbox ThemeIcon(double size)
    {
        var path = new Path
        {
            Width = 24, Height = 24,
            Stroke = Theme.Fg, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetBinding(Path.DataProperty, new Binding(nameof(Theme.ThemeState.Icon)) { Source = Theme.State });
        return new Viewbox { Width = size, Height = size, Child = path, IsHitTestVisible = false };
    }
    public static void Save(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(L.T("Не удалось сохранить: {0}", ex.Message), "TypeSwitcher", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private sealed class IsChoice(string id) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, id);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
