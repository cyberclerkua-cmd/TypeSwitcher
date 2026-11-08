using System.IO;
using System.Windows.Automation;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Acceptance;

internal static class BrowserChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edge)) throw new FileNotFoundException("Edge is not installed.", edge);
        string title = "TypeSwitcher Acceptance " + Guid.NewGuid().ToString("N");
        string html = session.Fixture("browser.html", $$"""
            <!doctype html><html lang="uk"><meta charset="utf-8"><title>{{title}}</title>
            <style>body{font:20px Segoe UI;padding:32px}textarea,input{display:block;font:24px Segoe UI;width:650px;margin:16px 0}</style>
            <h1>Ізольована перевірка TypeSwitcher</h1>
            <label for="editor">Acceptance browser text</label><textarea id="editor" aria-label="Acceptance browser text" rows="4"></textarea>
            <label for="password">Acceptance browser password</label><input id="password" type="password" aria-label="Acceptance browser password" value="acceptance-only">
            </html>
            """);
        string profile = Path.Combine(session.Root, "edge-profile");
        string arguments = $"--no-first-run --no-default-browser-check --disable-sync --disable-background-networking --disable-background-mode --force-renderer-accessibility --user-data-dir=\"{profile}\" --app=\"{new Uri(html).AbsoluteUri}\"";
        IntPtr previous = Native.GetForegroundWindow();
        IntPtr previousLayout = KeyTranslator.GetForegroundLayout();
        var process = session.StartExecutable(edge, arguments);
        var window = Uia.Window(process.Id, title);
        IntPtr handle = (IntPtr)window.Current.NativeWindowHandle;
        try
        {
            var editor = Uia.ByName(window, "Acceptance browser text", ControlType.Edit);
            var password = Uia.ByName(window, "Acceptance browser password", ControlType.Edit);
            report.Pass("edge-isolated-profile", "local HTML fixture; separate user-data-dir");
            Uia.SetValue(editor, "ghbdsn");
            Focus(editor, handle);
            Native.Key(handle, 0x23); // End, so deletion applies to the seeded text.
            Thread.Sleep(100);
            DesktopChecks.Replace(handle, 6, "привіт ");
            Uia.Until(() => Uia.Value(editor) == "привіт ", "Browser textarea replacement failed.");
            report.Pass("edge-sendinput-replacement");

            using var exclusions = new ExclusionManager();
            exclusions.StartFocusTracking();
            Uia.Until(() => exclusions.FocusTrackingActive || exclusions.FocusTrackingError != null, "UIA registration did not finish.", 10000);
            report.Check("edge-uia-subscription", exclusions.FocusTrackingActive, exclusions.FocusTrackingError ?? "registered");
            exclusions.InvalidateFocusCache();
            report.Check("edge-normal-field", !exclusions.IsPasswordThorough());
            Focus(password, handle);
            exclusions.InvalidateFocusCache();
            report.Check("edge-password-uia", password.Current.IsPassword && exclusions.IsPasswordThorough());
            Uia.Until(exclusions.IsPasswordFast, "Browser focus event was not received.", 5000);
            report.Pass("edge-focus-tracking-password");

            Focus(editor, handle);
            exclusions.InvalidateFocusCache();
            Uia.Until(() => !exclusions.IsPasswordFast(), "Password focus state remained stale.", 5000);
            report.Check("edge-focus-tracking-reset", !exclusions.IsPasswordThorough());

            var layouts = new LayoutSwitcher(); layouts.Detect();
            if (layouts.EnLayout == IntPtr.Zero || layouts.UkLayout == IntPtr.Zero)
                throw new InvalidOperationException("This browser check requires installed EN and UK layouts.");
            var store = new ConfigStore(session.Data, session.Root);
            var cfg = store.Load(); cfg.Languages.Enabled = ["en", "uk"];
            var loaded = DictionaryManager.Load(store, cfg);
            if (loaded.Errors.Any()) throw new InvalidOperationException(string.Join("; ", loaded.Errors));
            Uia.SetValue(editor, ""); Focus(editor, handle);
            var bridge = new InputBridge(handle, editor, loaded.Dictionaries, cfg, layouts);
            bridge.Start(layouts.EnLayout);
            bridge.Type("ghbdsn ");
            report.Check("edge-component-pipeline", Uia.Value(editor) == "привіт ");
            bridge.Press(0x13);
            report.Check("edge-component-undo", Uia.Value(editor) == "ghbdsn ");
            bridge.Press(0x13);
            bridge.Press(0x13, shift: true);
            report.Check("edge-component-case", Uia.Value(editor) == "ПРИВІТ ");
            report.Note("edge-coverage", "Real Edge textarea/password UIA and SendInput; InputProcessor events supplied by a component bridge, not physical typing.");
        }
        finally
        {
            if (Native.GetForegroundWindow() == handle) Native.PostMessage(handle, 0x0050, (IntPtr)1, previousLayout);
            try { ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch (ElementNotAvailableException) { }
            if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
        }
    }

    private static void Focus(AutomationElement element, IntPtr window)
    {
        Native.SetForegroundWindow(window);
        element.SetFocus();
        Uia.Until(() => Native.GetForegroundWindow() == window && element.Current.HasKeyboardFocus, "Browser field focus failed.", 10000);
        Native.RequireForeground(window);
    }
}
