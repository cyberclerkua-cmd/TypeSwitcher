using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Tray;

internal static class Theme
{
    public sealed class ThemeState : INotifyPropertyChanged
    {
        // Shared, frozen outlines on a 24 × 24 canvas; independent of installed fonts.
        private static readonly Geometry Sun = Outline(
            "M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 Z " +
            "M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 " +
            "M4.93,4.93 L6.34,6.34 M17.66,17.66 L19.07,19.07 " +
            "M4.93,19.07 L6.34,17.66 M17.66,6.34 L19.07,4.93");
        private static readonly Geometry Moon = Outline("M20.9,13 A9,9 0 1 1 11,3.1 A7,7 0 0 0 20.9,13 Z");
        private static readonly Geometry Monitor = Outline(
            "M5,4 H19 A2,2 0 0 1 21,6 V15 A2,2 0 0 1 19,17 H5 " +
            "A2,2 0 0 1 3,15 V6 A2,2 0 0 1 5,4 Z M12,17 V21 M8,21 H16");

        public string Mode { get; private set; } = "dark";
        public bool IsDark { get; private set; } = true;
        public Geometry Icon => Mode switch { "light" => Sun, "system" => Monitor, _ => Moon };
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Set(string mode, bool dark)
        {
            Mode = mode; IsDark = dark;
            foreach (string name in new[] { nameof(Mode), nameof(IsDark), nameof(Icon) }) PropertyChanged?.Invoke(this, new(name));
        }
        private static Geometry Outline(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
    }
    public static ThemeState State { get; } = new();
    private static Dictionary<string, SolidColorBrush>? _codeBrushes;
    private static ResourceDictionary? _resources;
    private static bool _watching;
    public static Brush WinBg => Brush("WinBg");
    public static Brush NavBg => Brush("NavBg");
    public static Brush PanelBg => Brush("CardBg");
    public static Brush CtrlBg => Brush("CtrlBg");
    public static Brush Fg => Brush("Fg");
    public static Brush Muted => Brush("Muted");
    public static Brush Border => Brush("Border");
    public static Brush Divider => Brush("Divider");
    public static Brush Accent => Brush("Accent");
    public static Brush AccentHover => Brush("AccentHover");

    private static SolidColorBrush Brush(string key)
    {
        _codeBrushes ??= Palette(State.IsDark).ToDictionary(p => p.Key, p => new SolidColorBrush(p.Value));
        return _codeBrushes[key];
    }
    public static string Normalize(string? mode) => mode?.ToLowerInvariant() is "light" or "system" ? mode.ToLowerInvariant() : "dark";
    public static bool ResolveDark(string mode, bool systemDark) => Normalize(mode) switch { "dark" => true, "light" => false, _ => systemDark };
    internal static Func<bool> SystemThemeReader { get; set; } = ReadWindowsTheme;
    public static bool SystemIsDark() => SystemThemeReader();
    private static bool ReadWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    internal static IReadOnlyDictionary<string, Color> Palette(bool dark)
    {
        string[] keys = ["WinBg", "NavBg", "CardBg", "CardHover", "CtrlBg", "CtrlHover", "Border", "Divider", "Fg", "Muted", "Accent", "AccentHover", "AccentPressed", "OnAccent", "ScrollThumb", "ScrollThumbHover"];
        string[] values = dark
            ? ["#202020", "#1B1B1B", "#2B2B2B", "#323232", "#353535", "#3D3D3D", "#3F3F3F", "#363636", "#F0F0F0", "#A6A6A6", "#4CC2FF", "#6ED0FF", "#3AA6E0", "#0B1A24", "#5A5A5A", "#7A7A7A"]
            : ["#F3F3F3", "#EAEAEA", "#FFFFFF", "#F8F8F8", "#FFFFFF", "#F5F5F5", "#B8B8B8", "#DEDEDE", "#1B1B1B", "#5F5F5F", "#0067C0", "#005A9E", "#004578", "#FFFFFF", "#858585", "#606060"];
        return keys.Select((key, index) => new KeyValuePair<string, Color>(key, (Color)ColorConverter.ConvertFromString(values[index]))).ToDictionary();
    }

    public static void Apply(string mode)
    {
        var app = Application.Current;
        app?.Dispatcher.VerifyAccess();
        mode = Normalize(mode);
        bool dark = ResolveDark(mode, SystemIsDark());
        var palette = Palette(dark);
        _codeBrushes ??= palette.ToDictionary(p => p.Key, p => new SolidColorBrush(p.Value));
        foreach (var entry in palette) _codeBrushes[entry.Key].Color = entry.Value;
        var resources = new ResourceDictionary();
        foreach (var entry in palette)
        {
            var brush = new SolidColorBrush(entry.Value); brush.Freeze(); resources[entry.Key] = brush;
        }
        if (app != null)
        {
            if (_resources != null) app.Resources.MergedDictionaries.Remove(_resources);
            app.Resources.MergedDictionaries.Add(resources);
        }
        _resources = resources;
        State.Set(mode, dark);
        if (app != null) foreach (Window window in app.Windows) UpdateCaption(window);
    }

    public static void Attach(Window window)
    {
        window.Background = WinBg; window.Foreground = Fg;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        window.SourceInitialized += (_, _) =>
        {
            UpdateCaption(window);
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message is 0x001A or 0x031A && State.Mode == "system") window.Dispatcher.BeginInvoke(RefreshSystemPreference);
                return IntPtr.Zero;
            };
            source?.AddHook(hook);
        };
        window.Closed += (_, _) => { if (source != null && hook != null) source.RemoveHook(hook); };
    }

    public static void UpdateCaption(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = State.IsDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
    }

    public static void WatchSystem()
    {
        if (_watching) return;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        _watching = true;
    }
    private static void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(RefreshSystemPreference);
    }
    private static void RefreshSystemPreference()
    {
        if (State.Mode == "system" && State.IsDark != SystemIsDark()) Apply("system");
    }
    public static void StopWatching()
    {
        if (!_watching) return;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged; _watching = false;
    }
}
