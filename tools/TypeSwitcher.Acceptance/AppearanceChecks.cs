using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Acceptance;

internal static class AppearanceChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        var process = session.Start("--settings 1 --offscreen");
        var window = Uia.Window(process.Id, "TypeSwitcher");
        Uia.SetValue(Uia.RowControl(window, "Минимальная длина слова", ControlType.Edit), "9");
        Uia.Navigate(window, "Словари");
        var words = Uia.ByName(window, "Мой словарь", ControlType.Edit);
        Uia.SetValue(words, "unsaved-localization-check\n");
        Uia.Navigate(window, "Язык интерфейса");
        var language = Uia.Find(window, Uia.Id("appearance.language"), "Interface language selector");
        var theme = Uia.Find(window, Uia.Id("appearance.theme-mode"), "Theme selector");

        foreach (var (id, nativeName) in new[] { ("en", "English"), ("uk", "Українська"), ("ru", "Русский") })
        {
            Uia.Choose(language, nativeName, process.Id);
            Uia.Until(() => Uia.Children(window, new AndCondition(Uia.Name(L.Get("Язык интерфейса", id)), Uia.Type(ControlType.RadioButton))).Any(), "Window language did not update.");
            report.Check("window-language-" + id, window.Current.Name == "TypeSwitcher");
            report.Check("saved-ui-language-" + id, Preference(session, "language") == id);
            using (var config = session.Config())
            {
                report.Check("input-languages-unchanged-" + id, !config.RootElement.GetProperty("languages").GetProperty("enabled").EnumerateArray().Any(e => e.GetString() == "uk"));
                report.Check("pending-settings-not-saved-" + id, config.RootElement.GetProperty("typo").GetProperty("min_word_length").GetInt32() == 4);
            }
            foreach (var (mode, key) in new[] { ("light", "Светлая"), ("dark", "Тёмная"), ("system", "Системная") })
            {
                Uia.Choose(theme, L.Get(key, id), process.Id);
                Uia.Until(() => Preference(session, "theme") == mode, "Theme preference not saved.");
                Thread.Sleep(200);
                bool dark = mode == "dark" || mode == "system" && SystemDark();
                string file = Path.Combine(report.DirectoryPath, $"{id}-{mode}.png");
                report.Check("visual-theme-" + id + "-" + mode, Capture((IntPtr)window.Current.NativeWindowHandle, file, dark), file);
            }

            Uia.Navigate(window, L.Get("Опечатки", id));
            report.Check("pending-number-preserved-" + id, Uia.Value(Uia.RowControl(window, L.Get("Минимальная длина слова", id), ControlType.Edit)) == "9");
            Uia.Navigate(window, L.Get("Словари", id));
            report.Check("pending-words-preserved-" + id, Uia.Value(Uia.ByName(window, L.Get("Мой словарь", id), ControlType.Edit)).Contains("unsaved-localization-check"));
            Uia.Navigate(window, L.Get("Язык интерфейса", id));
        }

        Uia.Click(window, "Выбор темы");
        var light = Uia.Wait(() => Uia.Windows(process.Id).SelectMany(w => Uia.Children(w, new AndCondition(Uia.Name("Светлая"), Uia.Type(ControlType.MenuItem)))).FirstOrDefault(), "Theme icon menu");
        _ = Uia.Invoke(light);
        Uia.Until(() => Preference(session, "theme") == "light", "Theme icon did not change theme.");
        report.Pass("theme-icon-menu");
        Uia.Click(window, "Сохранить");
        Uia.Until(() => File.ReadAllText(Path.Combine(session.Data, "my_words.txt")).Contains("unsaved-localization-check"), "Pending words not saved.");
        Uia.Close(window, process);

        process = session.Start("--settings 7 --offscreen");
        window = Uia.Window(process.Id, "TypeSwitcher");
        report.Check("theme-restored-after-restart", Preference(session, "theme") == "light"
            && Capture((IntPtr)window.Current.NativeWindowHandle, Path.Combine(report.DirectoryPath, "restart-light.png"), false));
        Uia.Choose(Uia.Find(window, Uia.Id("appearance.language"), "Language selector"), "English", process.Id);
        Uia.Close(window, process);

        process = session.Start("--dictionary-settings --offscreen");
        window = Uia.Window(process.Id, "TypeSwitcher — dictionaries");
        report.Check("library-localization", Uia.ByName(window, "Import…", ControlType.Button) != null && Uia.ByName(window, "Use selected dictionary", ControlType.CheckBox) != null);
        report.Check("library-light-theme", Capture((IntPtr)window.Current.NativeWindowHandle, Path.Combine(report.DirectoryPath, "library-en-light.png"), false));
        string invalid = session.Fixture("localized-error.txt", "two words\n");
        Uia.Click(window, "Import…");
        Uia.FileDialog(process.Id, invalid, "Choose a dictionary");
        var error = Uia.Wait(() => Uia.Children(window, Uia.Type(ControlType.Text)).FirstOrDefault(e => e.Current.Name.StartsWith("Import failed:", StringComparison.Ordinal)), "Localized import error");
        report.Check("localized-validation-error", error.Current.Name.Contains("one word per line", StringComparison.Ordinal));
        Uia.Close(window, process);
    }

    private static string Preference(TestSession session, string key)
    {
        using var json = session.Config();
        return json.RootElement.GetProperty("general").GetProperty(key).GetString()!;
    }
    private static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static bool Capture(IntPtr window, string path, bool dark)
    {
        if (!Native.GetWindowRect(window, out var rect)) throw new InvalidOperationException("Window rectangle unavailable.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        IntPtr dc = Native.GetWindowDC(window), memory = Native.CreateCompatibleDC(dc);
        IntPtr bitmap = Native.CreateCompatibleBitmap(dc, width, height), previous = Native.SelectObject(memory, bitmap);
        try
        {
            if (!Native.PrintWindow(window, memory, 2)) throw new InvalidOperationException("Window capture failed.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (var output = File.Create(path)) encoder.Save(output);
            var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4]; rgba.CopyPixels(pixels, width * 4, 0);
            byte expected = dark ? (byte)0x20 : (byte)0xF3;
            int matches = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] == expected && pixels[i + 1] == expected && pixels[i + 2] == expected) matches++;
            return matches > width * height / 20;
        }
        finally
        {
            Native.SelectObject(memory, previous); Native.DeleteObject(bitmap); Native.DeleteDC(memory); Native.ReleaseDC(window, dc);
        }
    }
}
