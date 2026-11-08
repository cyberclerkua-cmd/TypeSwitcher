using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Tray;
using Xunit;

namespace TypeSwitcher.Tests;

public sealed class UiSmokeTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [Fact]
    public void SettingsAndDictionaryWindowsBuildAndLayoutAllPages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var temp = new TestDirectory();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/TypeSwitcher;component/Tray/DarkTheme.xaml", UriKind.Relative)
                });
                var store = new ConfigStore(temp.Path);
                var cfg = store.Load();
                store.EnsureUserLists();
                var settings = new SettingsWindow(store, cfg, () => { });
                for (int page = 0; page < 8; page++)
                {
                    settings.SelectPage(page);
                    Layout(settings);
                }
                var library = new DictionaryLibraryWindow(store, cfg, () => { });
                Layout(library);
                var suggestion = new SuggestionWindow("привт", LayoutTarget.Uk, [LayoutTarget.En, LayoutTarget.Ru, LayoutTarget.Uk]);
                Layout(suggestion);
                var rule = new RuleDialog("привт", (_, _) => { });
                Layout(rule);
                var languageMenu = UiPreferences.LanguageMenu();
                var themeMenu = UiPreferences.ThemeMenu();
                foreach (string language in new[] { "ru", "en", "uk" })
                {
                    UiPreferences.SetLanguage(language);
                    Drain();
                    Assert.Equal("TypeSwitcher", settings.Title);
                    Assert.Equal(L.Get("TypeSwitcher — подключённые словари", language), library.Title);
                    Assert.Equal(L.Get("TypeSwitcher — правило", language), rule.Title);
                    Assert.Equal(L.Get("Язык интерфейса", language), languageMenu.Header);
                    Assert.Single(languageMenu.Items.Cast<MenuItem>(), item => item.IsChecked);
                    foreach (string mode in new[] { "light", "dark", "system" })
                    {
                        UiPreferences.SetTheme(mode);
                        Drain();
                        bool dark = Theme.ResolveDark(mode, Theme.SystemIsDark());
                        Assert.Equal(Theme.Palette(dark)["WinBg"], Assert.IsType<SolidColorBrush>(settings.Background).Color);
                        Assert.Equal(Theme.Palette(dark)["WinBg"], Assert.IsType<SolidColorBrush>(library.Background).Color);
                        Assert.Equal(Theme.Palette(dark)["WinBg"], Assert.IsType<SolidColorBrush>(suggestion.Background).Color);
                        for (int page = 0; page < 8; page++) { settings.SelectPage(page); Layout(settings); }
                        Layout(library); Layout(suggestion); Layout(rule);
                        var saved = store.Load();
                        Assert.Equal(language, saved.General.Language);
                        Assert.Equal(mode, saved.General.Theme);
                        Assert.Single(themeMenu.Items.Cast<MenuItem>(), item => item.IsChecked);
                    }
                }
                Assert.Empty(L.MissingKeys);
                var oldReader = Theme.SystemThemeReader;
                try
                {
                    bool systemDark = false;
                    Theme.SystemThemeReader = () => systemDark;
                    UiPreferences.SetTheme("system");
                    settings.ShowActivated = false; settings.ShowInTaskbar = false;
                    settings.WindowStartupLocation = WindowStartupLocation.Manual;
                    settings.Left = -3000; settings.Top = -3000; settings.Show();
                    Drain();
                    IntPtr handle = new WindowInteropHelper(settings).Handle;
                    systemDark = true;
                    Assert.NotEqual(IntPtr.Zero, handle);
                    SendMessage(handle, 0x001A, IntPtr.Zero, IntPtr.Zero);
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (!Theme.State.IsDark && timeout.Elapsed < TimeSpan.FromSeconds(2)) { Drain(); Thread.Sleep(10); }
                    Assert.True(Theme.State.IsDark, $"mode={Theme.State.Mode}, configured={cfg.General.Theme}, system={Theme.SystemIsDark()}, hwnd={handle}");
                    Assert.Equal("system", cfg.General.Theme);
                    UiPreferences.SetTheme("light");
                    SendMessage(handle, 0x031A, IntPtr.Zero, IntPtr.Zero);
                    Drain();
                    Assert.False(Theme.State.IsDark);
                }
                finally { Theme.SystemThemeReader = oldReader; }
                UiPreferences.SetLanguage("ru"); UiPreferences.SetTheme("dark");
                rule.Close(); suggestion.Close(); library.Close(); settings.Close();
                Theme.StopWatching();
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Layout(Window window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        content.Measure(new Size(900, 700));
        content.Arrange(new Rect(0, 0, 900, 700));
        content.UpdateLayout();
        Assert.True(content.IsMeasureValid);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
