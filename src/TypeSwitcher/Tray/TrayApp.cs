using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;

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

    public event Action<bool>? EnabledChanged;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

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
        var icon = _enabled ? _idle : _grey;
        if (icon != null) _icon.IconSource = icon;
    }

    private void LanguageChanged(object? sender, EventArgs e) => UpdateTooltip();
    private void UpdateTooltip() => _icon.ToolTipText = L.T(_enabled ? "TypeSwitcher — включено" : "TypeSwitcher — выключено");

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
        _icon.Dispose();
    }
}
