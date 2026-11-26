using System.Windows.Automation;
using System.Windows.Threading;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using TypeSwitcher.Tray;

namespace TypeSwitcher.Acceptance;

internal static class FeedbackChecks
{
    public static void Run(AutomationElement host, IntPtr frame, IntPtr password, IntPtr edit, LayoutSwitcher layouts, AcceptanceReport report)
    {
        // WPF UI and async UIA callbacks run on a pumping STA; the input host is a separate process.
        var dispatcher = new Thread(() => Dispatcher.Run()) { IsBackground = true };
        dispatcher.SetApartmentState(ApartmentState.STA);
        dispatcher.Start();
        var ui = Uia.Wait(() => Dispatcher.FromThread(dispatcher), "Feedback STA");
        using var exclusions = new ExclusionManager();
        var feedback = ui.Invoke(() => new InputLanguageFeedback(exclusions, layouts));
        var cfg = new AppConfig();
        string exe = ExclusionManager.ExecutableName(edit)!;
        cfg.Languages.Enabled = ["uk", "en"]; cfg.Languages.UseApplicationLayouts = true;
        cfg.Languages.ApplicationLayouts[exe.ToUpperInvariant()] = "uk"; cfg.Tray.ShowCaretIndicator = true;
        void Observe()
        {
            Native.RequireForeground(frame);
            var input = FocusedInput.Capture();
            ui.Invoke(() => feedback.ObserveAsync(input, LanguageProfile.FromLayout(input.Layout))).GetAwaiter().GetResult();
        }
        IntPtr VisibleBadge()
        {
            IntPtr hwnd = Native.FindWindow(null, "TypeSwitcher · EN/RU/UK");
            return Native.IsWindowVisible(hwnd) ? hwnd : IntPtr.Zero;
        }
        void WaitForLayout(IntPtr layout) => Uia.Until(() => { Observe(); return KeyTranslator.GetForegroundLayout() == layout; }, "Expected feedback layout: " + layout);
        void SetLayout(IntPtr layout) { layouts.Activate(layout); Uia.Until(() => KeyTranslator.GetForegroundLayout() == layout, "Fixture layout was not set."); }
        try
        {
            SetLayout(layouts.EnLayout);
            ui.Invoke(() => feedback.Configure(cfg));
            WaitForLayout(layouts.UkLayout);
            Uia.Until(() => { Observe(); return VisibleBadge() != IntPtr.Zero; }, "Caret badge was not shown.");
            IntPtr badge = VisibleBadge();
            var input = FocusedInput.Capture();
            report.Check("application-layout-on-entry", input.Receiver == edit && input.Layout == layouts.UkLayout);
            report.Check("caret-badge-language", AutomationElement.FromHandle(badge).FindFirst(TreeScope.Descendants, Uia.Name("UK")) != null);
            report.Check("caret-badge-position", input.TryGetCaret(out var caret) && Native.GetWindowRect(badge, out var rect)
                && Math.Abs(rect.Left - caret.Right) < 80 && Math.Abs(rect.Top - caret.Bottom) < 80);
            report.Check("caret-badge-no-focus", Native.GetForegroundWindow() == frame && exclusions.FocusedControl() == edit
                && (NativeMethods.GetWindowLongPtr(badge, -20).ToInt64() & 0x08000020) == 0x08000020);
            Uia.Until(() => { Observe(); return VisibleBadge() == IntPtr.Zero; }, "Caret badge did not expire.");
            report.Pass("caret-badge-temporary");

            SetLayout(layouts.EnLayout);
            Uia.Until(() => { Observe(); return VisibleBadge() != IntPtr.Zero; }, "Manual language change did not show a badge.");
            report.Check("manual-layout-survives", KeyTranslator.GetForegroundLayout() == layouts.EnLayout);
            report.Check("manual-layout-badge-language", AutomationElement.FromHandle(VisibleBadge()).FindFirst(TreeScope.Descendants, Uia.Name("EN")) != null);
            DesktopChecks.Focus(host, frame, "Focus WPF text");
            Observe();
            DesktopChecks.Focus(host, frame, "Focus threaded text", edit);
            Observe();
            report.Check("layout-not-forced-on-second-field", KeyTranslator.GetForegroundLayout() == layouts.EnLayout);

            DesktopChecks.Focus(host, frame, "Focus native password", password);
            SetLayout(layouts.EnLayout);
            ui.Invoke(() => feedback.Configure(cfg));
            Observe();
            report.Check("feedback-password-exclusion", KeyTranslator.GetForegroundLayout() == layouts.EnLayout && VisibleBadge() == IntPtr.Zero);
            DesktopChecks.Focus(host, frame, "Focus threaded text", edit);
            exclusions.SetBlacklist([exe]);
            ui.Invoke(() => feedback.Configure(cfg));
            Observe();
            report.Check("feedback-process-exclusion", KeyTranslator.GetForegroundLayout() == layouts.EnLayout && VisibleBadge() == IntPtr.Zero);
            exclusions.SetBlacklist([]);
            cfg.Languages.Enabled = ["en"]; cfg.Tray.ShowCaretIndicator = false;
            ui.Invoke(() => feedback.Configure(cfg));
            report.Check("disabled-app-language-skipped", !feedback.Active);
        }
        finally
        {
            ui.Invoke(feedback.Dispose);
            ui.InvokeShutdown();
            dispatcher.Join(2000);
        }
    }
}
