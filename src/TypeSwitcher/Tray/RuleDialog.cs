using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;
using System.Windows.Automation;

namespace TypeSwitcher.Tray;

/// <summary>
/// Мини-окно создания правила замены: слово подставлено в левое поле, справа -
/// на что менять. По «Создать» вызывает callback (откуда -> куда).
/// </summary>
public sealed class RuleDialog : Window
{
    private readonly TextBox _from;
    private readonly TextBox _to;
    private readonly Action<string, string> _onCreate;

    public RuleDialog(string word, Action<string, string> onCreate)
    {
        _onCreate = onCreate;

        L.Bind(this, TitleProperty, "TypeSwitcher — правило");
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Theme.WinBg;
        FontSize = 14;
        Theme.Attach(this);

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Foreground = Theme.Fg,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        }.Localize("Правило замены"));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _from = Box(word, 180);
        _to = Box("", 180);
        L.Bind(_from, AutomationProperties.NameProperty, "Исходное слово");
        L.Bind(_to, AutomationProperties.NameProperty, "Замена");
        row.Children.Add(_from);
        row.Children.Add(new TextBlock
        {
            Text = "→",
            Foreground = Theme.Fg,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        });
        row.Children.Add(_to);
        root.Children.Add(row);

        root.Children.Add(new TextBlock
        {
            Foreground = Theme.Muted,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0)
        }.Localize("Заменять слово слева на слово справа (без учёта регистра)."));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var create = Btn("Создать", accent: true);
        create.Click += (_, _) => Create();
        var cancel = Btn("Отмена", accent: false);
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _to.Focus(); _to.SelectAll(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.UpdateCaption(this);
    }

    private void Create()
    {
        string from = _from.Text.Trim();
        string to = _to.Text.Trim();
        if (from.Length == 0 || to.Length == 0) { Close(); return; }
        _onCreate(from, to);
        Close();
    }

    private static TextBox Box(string text, double width) => new()
    {
        Text = text,
        Width = width,
        VerticalAlignment = VerticalAlignment.Center
    };

    private Button Btn(string text, bool accent)
    {
        var b = new Button
        {
            Margin = new Thickness(6, 0, 0, 0)
        };
        L.Bind(b, ContentControl.ContentProperty, text);
        if (accent) b.Style = (Style)FindResource("AccentButton");
        return b;
    }
}
