using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;
using TypeSwitcher.Core;
using System.Globalization;

namespace TypeSwitcher.Tray;

/// <summary>
/// Иконка в трее: встречные стрелки, серая когда программа выключена, кратковременная
/// подсветка при срабатывании. Клик - вкл/выкл, двойной клик - настройки,
/// контекстное меню (правый клик) - вкл/выкл, настройки, выход.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly BitmapSource? _idle;
    private readonly BitmapSource? _active;
    private readonly BitmapSource? _grey;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _clickTimer;
    private readonly MenuItem _toggleItem;
    private bool _enabled;
    private readonly DispatcherTimer _layoutTimer;
    private readonly Dictionary<LayoutTarget, MenuItem> _layoutItems = new();
    private readonly Dictionary<(LayoutTarget, bool), BitmapSource> _layoutIcons = new();
    private LayoutTarget _inputLanguage;
    private bool _showLayout;
    private IntPtr _lastExternalWindow;

    public event Action<bool>? EnabledChanged;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? AboutRequested;
    public event Action<LayoutTarget, IntPtr>? LayoutRequested;

    public TrayApp(string idleIconPath, string activeIconPath, int blinkMs, bool enabled)
    {
        _enabled = enabled;
        _idle = TryLoadImage(idleIconPath);
        _active = TryLoadImage(activeIconPath);
        _grey = _idle != null ? ToGrayscale(_idle) : null;

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(blinkMs) };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkTimer.Stop();
            ApplyStateIcon();
        };

        // Различаем одиночный и двойной клик: одиночный - вкл/выкл (с задержкой на
        // случай, если это начало двойного), двойной - настройки.
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime() + 30) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            EnabledChanged?.Invoke(!_enabled);
        };

        _toggleItem = new MenuItem { IsCheckable = true, IsChecked = enabled }.Localize(HeaderedItemsControl.HeaderProperty, "Включено");
        _toggleItem.Click += (_, _) => EnabledChanged?.Invoke(_toggleItem.IsChecked);

        var settingsItem = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "Настройки…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(UiPreferences.LanguageMenu());
        menu.Items.Add(UiPreferences.ThemeMenu());
        var layouts = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "Раскладка ввода");
        foreach (var profile in LanguageProfile.All)
        {
            var item = new MenuItem { IsCheckable = true, IsEnabled = false }.Localize(HeaderedItemsControl.HeaderProperty, profile.Name);
            item.Click += (_, _) => { if (_lastExternalWindow != IntPtr.Zero) LayoutRequested?.Invoke(profile.Target, _lastExternalWindow); };
            layouts.Items.Add(item); _layoutItems[profile.Target] = item;
        }
        menu.Items.Add(layouts);
        var about = new MenuItem().Localize(HeaderedItemsControl.HeaderProperty, "О программе");
        about.Click += (_, _) => AboutRequested?.Invoke(); menu.Items.Add(about);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ContextMenu = menu,
            Visibility = Visibility.Visible
        };
        _icon.TrayLeftMouseUp += (_, _) => { _clickTimer.Stop(); _clickTimer.Start(); };
        _icon.TrayMouseDoubleClick += (_, _) => { _clickTimer.Stop(); SettingsRequested?.Invoke(); };

        ApplyStateIcon();
        UpdateTooltip();
        L.Changed += LanguageChanged;
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _layoutTimer.Tick += (_, _) => UpdateInputLanguage();
        _layoutTimer.Start();
    }

    public void SetEnabledChecked(bool enabled)
    {
        _enabled = enabled;
        _toggleItem.IsChecked = enabled;
        ApplyStateIcon();
        UpdateTooltip();
    }

    public void Blink()
    {
        if (!_enabled || _active == null) return;
        _icon.IconSource = _active;
        _blinkTimer.Stop();
        _blinkTimer.Start();
    }

    private void ApplyStateIcon()
    {
        if (_showLayout && _inputLanguage != LayoutTarget.None)
        {
            if (!_layoutIcons.TryGetValue((_inputLanguage, _enabled), out var image))
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRoundedRectangle(new SolidColorBrush(_enabled ? Color.FromRgb(24, 110, 190) : Color.FromRgb(100, 100, 100)), null, new Rect(0, 0, 32, 32), 5, 5);
                    var text = new FormattedText(LanguageProfile.Find(_inputLanguage)!.Id.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 18, Brushes.White, 1);
                    drawing.DrawText(text, new Point((32 - text.WidthIncludingTrailingWhitespace) / 2, (32 - text.Height) / 2));
                }
                var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
                image = bitmap; _layoutIcons[(_inputLanguage, _enabled)] = image;
            }
            _icon.IconSource = image;
            return;
        }
        var icon = _enabled ? _idle : _grey;
        if (icon != null) _icon.IconSource = icon;
    }

    private void LanguageChanged(object? sender, EventArgs e) => UpdateTooltip();
    private void UpdateTooltip() => _icon.ToolTipText = L.T(_enabled ? "TypeSwitcher — включено" : "TypeSwitcher — выключено")
        + (_inputLanguage == LayoutTarget.None ? "" : " · " + LanguageProfile.Find(_inputLanguage)!.Id.ToUpperInvariant());

    public void ConfigureInputDisplay(bool showLayout, IEnumerable<LayoutTarget> enabled)
    {
        _showLayout = showLayout;
        var available = enabled.ToHashSet();
        foreach (var item in _layoutItems) item.Value.IsEnabled = available.Contains(item.Key);
        UpdateInputLanguage(); ApplyStateIcon();
    }

    private void UpdateInputLanguage()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid != 0 && pid != Environment.ProcessId && window != NativeMethods.GetShellWindow()) _lastExternalWindow = window;
        var language = LanguageProfile.FromLayout(KeyTranslator.GetForegroundLayout());
        foreach (var item in _layoutItems) item.Value.IsChecked = item.Key == language;
        if (language == _inputLanguage) return;
        _inputLanguage = language;
        if (!_blinkTimer.IsEnabled) ApplyStateIcon();
        UpdateTooltip();
    }

    private static BitmapSource? TryLoadImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Оттенки серого с сохранением альфа-канала (для иконки "выключено").</summary>
    private static BitmapSource ToGrayscale(BitmapSource src)
    {
        var fmt = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        fmt.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
            px[i] = px[i + 1] = px[i + 2] = gray;
        }
        var wb = new WriteableBitmap(w, h, fmt.DpiX, fmt.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        wb.Freeze();
        return wb;
    }

    public void Dispose()
    {
        L.Changed -= LanguageChanged;
        _blinkTimer.Stop();
        _clickTimer.Stop();
        _layoutTimer.Stop();
        _icon.Dispose();
    }
}
