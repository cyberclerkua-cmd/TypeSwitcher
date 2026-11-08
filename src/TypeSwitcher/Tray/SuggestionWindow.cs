using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public enum SuggestionChoice { MyWord, StopWord, Rule, LayoutRule, Dismiss }

/// <summary>
/// Ненавязчивое окно обучения: появляется у курсора, не крадёт фокус ввода
/// (ShowActivated=false), предлагает действия по слову, которое пользователь
/// систематически откатывает (ТЗ, раздел 9).
/// </summary>
public sealed class SuggestionWindow : Window
{
    public event Action<SuggestionChoice>? Chosen;
    public string? SelectedReplacement { get; private set; }

    private readonly Style _btnStyle;
    private readonly int _cursorX;
    private readonly int _cursorY;

    // Пассивный хук мыши: клик вне окна закрывает его. Окно не активируется
    // (ShowActivated=false), поэтому событие Deactivated не приходит, а Mouse.Capture
    // съел бы клик, адресованный приложению пользователя. Хук клик не перехватывает.
    private MouseHook? _outsideClickHook;

    public SuggestionWindow(string word, LayoutTarget source = LayoutTarget.None, IEnumerable<LayoutTarget>? languages = null)
    {
        _btnStyle = new Style(typeof(Button), (Style)FindResource(typeof(Button)));
        _btnStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        _btnStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)));
        _btnStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        NativeMethods.GetCursorPos(out var p);
        _cursorX = p.X;
        _cursorY = p.Y;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // не отбирать фокус у активного окна
        SizeToContent = SizeToContent.WidthAndHeight;
        Theme.Attach(this);
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);

        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

        root.Children.Add(new TextBlock
        {
            Text = "TypeSwitcher",
            Foreground = Theme.Muted,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var msg = new TextBlock
        {
            Foreground = Theme.Fg,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 0, 0, 10)
        };
        L.Bind(msg, TextBlock.TextProperty, "Слово «{0}» вы часто возвращаете. Что с ним сделать?", word);
        root.Children.Add(msg);

        var buttons = new WrapPanel { MaxWidth = 360 };

        // Сменить раскладку одним кликом (правило word -> противоположная раскладка),
        // если слово целиком из букв одной раскладки. Как у Punto Switcher.
        if (source == LayoutTarget.None) source = LayoutMap.IsAllLatinLetters(word) ? LayoutTarget.En : LayoutTarget.Ru;
        foreach (var target in (languages ?? new[] { LayoutTarget.En, LayoutTarget.Ru }).Where(t => t != source))
        {
            string? other = LayoutMap.MapBetween(word, source, target);
            if (other != null && other != word)
            {
                var button = MakeButton("{0}: «{1}»", SuggestionChoice.LayoutRule, "Создать правило для исходного языка.", other);
                L.Bind(button, ContentControl.ContentProperty, "{0}: «{1}»", new UiChoice("", LanguageProfile.Find(target)!.Name), other);
                buttons.Children.Add(button);
            }
        }
        var addWord = MakeButton("В словарь: {0}", SuggestionChoice.MyWord, "Считать это слово правильным в выбранном языке.");
        L.Bind(addWord, ContentControl.ContentProperty, "В словарь: {0}", new UiChoice("", LanguageProfile.Find(source)?.Name ?? "Общий"));
        buttons.Children.Add(addWord);

        buttons.Children.Add(MakeButton("Не трогать это слово", SuggestionChoice.StopWord,
            "Никогда не трогать это слово: ни смены раскладки, ни исправления опечатки, ни правил."));
        buttons.Children.Add(MakeButton("Правило…", SuggestionChoice.Rule,
            "Задать произвольную замену (X = Y) в диалоге."));
        buttons.Children.Add(MakeButton("Закрыть", SuggestionChoice.Dismiss,
            "Ничего не делать сейчас."));
        root.Children.Add(buttons);

        Content = root;

        Loaded += OnLoadedPlaceNearCursor;
        Loaded += OnLoadedInstallOutsideClick;
        Closed += OnClosedUninstallHook;
    }

    private void OnLoadedInstallOutsideClick(object sender, RoutedEventArgs e)
    {
        _outsideClickHook = new MouseHook();
        _outsideClickHook.ButtonDown += OnGlobalMouseDown;
        // Без хука окно закрывается только кнопками - не повод ронять программу.
        try { _outsideClickHook.Install(); }
        catch (Exception ex) { TypeSwitcher.Core.Log.Line($"suggestion window: {ex.Message}"); }
    }

    private void OnClosedUninstallHook(object? sender, EventArgs e)
    {
        if (_outsideClickHook == null) return;
        _outsideClickHook.ButtonDown -= OnGlobalMouseDown;
        _outsideClickHook.Dispose();
        _outsideClickHook = null;
    }

    private bool _dismissing;

    private void OnGlobalMouseDown()
    {
        // Клик по физическим координатам курсора. Сравниваем с рамкой окна в тех же
        // физических пикселях (GetWindowRect), чтобы не возиться с DPI. Внутри - клик
        // по кнопке отработает сам; снаружи - закрываем (клик проходит в приложение).
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var p) || !NativeMethods.GetWindowRect(hwnd, out var r)) return;

        bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        if (inside || _dismissing) return;
        _dismissing = true;

        // Как кнопка "Закрыть": отмечаем показ обработанным (сброс счётчика откатов),
        // иначе окно тут же выскочит снова при следующем откате.
        Dispatcher.BeginInvoke(() =>
        {
            try { Chosen?.Invoke(SuggestionChoice.Dismiss); Close(); }
            catch { /* уже закрыто */ }
        });
    }

    private void OnLoadedPlaceNearCursor(object sender, RoutedEventArgs e)
    {
        // Физические пиксели курсора -> единицы WPF с учётом DPI.
        double dpiX = 1, dpiY = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            dpiX = src.CompositionTarget.TransformToDevice.M11;
            dpiY = src.CompositionTarget.TransformToDevice.M22;
        }

        double x = _cursorX / dpiX + 12;
        double y = _cursorY / dpiY + 16;

        var wa = SystemParameters.WorkArea;
        if (x + ActualWidth > wa.Right) x = wa.Right - ActualWidth - 4;
        if (y + ActualHeight > wa.Bottom) y = _cursorY / dpiY - ActualHeight - 8; // над курсором
        if (x < wa.Left) x = wa.Left + 4;
        if (y < wa.Top) y = wa.Top + 4;

        Left = x;
        Top = y;
    }

    private Button MakeButton(string text, SuggestionChoice choice, string tooltip, string? replacement = null)
    {
        var b = new Button
        {
            Style = _btnStyle,
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        if (!text.Contains('{')) L.Bind(b, ContentControl.ContentProperty, text);
        L.Bind(b, ToolTipService.ToolTipProperty, tooltip);
        b.Click += (_, _) =>
        {
            SelectedReplacement = replacement;
            Chosen?.Invoke(choice);
            Close();
        };
        return b;
    }

}
