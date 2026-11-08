using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TypeSwitcher.Config;
using TypeSwitcher.Tray;

namespace TypeSwitcher.Acceptance;

internal static class DpiChecks
{
    public static int Run(string executable, string work, string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new AcceptanceReport(directory);
        using var session = new TestSession(executable, Path.Combine(work, "TypeSwitcher layout " + Guid.NewGuid().ToString("N")));
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TypeSwitcher;component/Tray/DarkTheme.xaml", UriKind.Relative) });
            var store = new ConfigStore(session.Data, session.Root);
            var cfg = store.Load();
            var settings = new SettingsWindow(store, cfg, () => { });
            foreach (int percent in new[] { 100, 150, 200 })
                for (int page = 0; page < 7; page++)
                {
                    settings.SelectPage(page);
                    Render((FrameworkElement)settings.Content, directory, $"settings-{page}-{percent}.png", percent, report);
                }
            var library = new DictionaryLibraryWindow(store, cfg, () => { });
            foreach (int percent in new[] { 100, 150, 200 })
                Render((FrameworkElement)library.Content, directory, $"library-{percent}.png", percent, report);
            library.Close(); settings.Close(); app.Shutdown();
            report.Note("dpi-coverage", "24 WPF control-tree renderings at 96/144/192 DPI. The Windows desktop scale and monitor topology were not changed; native WM_DPICHANGED across different-DPI monitors remains separate.");
        }
        catch (Exception ex) { report.Fail("layout", ex.ToString()); }
        finally { report.Save(); }
        return report.Failed == 0 ? 0 : 1;
    }

    private static void Render(FrameworkElement content, string directory, string name, int percent, AcceptanceReport report)
    {
        const int width = 900, height = 660;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        double scale = percent / 100.0;
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
        report.Pass("render-" + name, $"{bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }
}
