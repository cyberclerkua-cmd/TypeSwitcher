using System.IO;
using System.Windows.Automation;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Acceptance;

internal static class SelectionChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        IntPtr previousWindow = Native.GetForegroundWindow();
        var process = session.StartExecutable(Path.Combine(AppContext.BaseDirectory, "TypeSwitcher.Acceptance.exe"), "--input-host");
        var window = Uia.Window(process.Id, "Acceptance Input Host");
        var text = Uia.Find(window, Uia.Id("acceptance.text"), "Selection text field");
        using var exclusions = new ExclusionManager();
        long revision = 0;
        using var service = new SelectionService(exclusions, () => revision);
        try
        {
            void SelectText(string value)
            {
                Uia.SetValue(text, value);
                Uia.Click(window, "Focus WPF text");
                Uia.Until(() => text.Current.HasKeyboardFocus, "Text field did not receive focus.");
                ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.Select();
                Native.RequireForeground((IntPtr)window.Current.NativeWindowHandle);
            }
            SelectionService.Snapshot Capture() => service.CaptureAsync(exclusions.CaptureTarget()).GetAwaiter().GetResult();
            void Replace(SelectionService.Snapshot selected, string replacement)
                => service.ReplaceAsync(selected, replacement, IntPtr.Zero).GetAwaiter().GetResult();

            SelectText("ghbdtn vbh");
            uint clipboard = Native.GetClipboardSequenceNumber();
            var selected = Capture();
            report.Check("selection-uia-read", selected.Text == "ghbdtn vbh");
            Replace(selected, SelectionText.ConvertLayout(selected.Text, LayoutTarget.Ru, LayoutTarget.En));
            Uia.Until(() => Uia.Value(text) == "привет мир", "Selection layout replacement failed.");
            report.Pass("selection-layout-write");
            report.Check("selection-clipboard-untouched", clipboard == Native.GetClipboardSequenceNumber());
            selected = Capture();
            report.Check("selection-result-reselected", selected.Text == "привет мир");
            Replace(selected, SelectionText.ChangeCase(selected.Text));
            Uia.Until(() => Uia.Value(text) == "ПРИВЕТ МИР", "Selection case replacement failed.");
            report.Pass("selection-case-write");

            SelectText("Київ"); selected = Capture();
            Replace(selected, SelectionText.Transliterate(selected.Text, LayoutTarget.Uk));
            Uia.Until(() => Uia.Value(text) == "Kyiv", "Ukrainian transliteration failed.");
            report.Pass("selection-uk-transliteration");

            SelectText("привет vbh!"); selected = Capture();
            Replace(selected, SelectionText.ConvertLayout(selected.Text, LayoutTarget.Ru, LayoutTarget.En));
            Uia.Until(() => Uia.Value(text) == "привет мир!", "Mixed selection conversion failed.");
            report.Pass("selection-mixed-text");

            SelectText("unchanged"); selected = Capture(); revision++;
            bool refused = false;
            try { Replace(selected, "must-not-appear"); } catch (SelectionUnavailableException) { refused = true; }
            report.Check("selection-stale-input-refused", refused && Uia.Value(text) == "unchanged");

            Uia.Click(window, "Focus WPF password");
            Uia.Until(() => Uia.Find(window, Uia.Id("acceptance.password"), "Password field").Current.HasKeyboardFocus, "Password focus failed.");
            refused = false;
            try { _ = Capture(); } catch (SelectionUnavailableException) { refused = true; }
            report.Check("selection-password-refused", refused);

            SelectText("unchanged");
            exclusions.SetBlacklist(["TypeSwitcher.Acceptance.exe"]);
            refused = false;
            try { _ = Capture(); } catch (SelectionUnavailableException) { refused = true; }
            report.Check("selection-excluded-app-refused", refused && Uia.Value(text) == "unchanged");
            Uia.Close(window, process);
        }
        finally { if (previousWindow != IntPtr.Zero) Native.SetForegroundWindow(previousWindow); }
    }
}
