using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Acceptance;

internal static class DesktopChecks
{
    public static void Run(TestSession session, AcceptanceReport report)
    {
        var layouts = new LayoutSwitcher(); layouts.Detect();
        foreach (var profile in LanguageProfile.All)
        {
            IntPtr hkl = layouts.Get(profile.Target);
            report.Note("layout-" + profile.Id, $"0x{hkl.ToInt64():X}");
            if (hkl == IntPtr.Zero) continue;
            string? key = Translate(0x53, hkl);
            string expected = profile.Target switch { LayoutTarget.En => "s", LayoutTarget.Ru => "ы", _ => "і" };
            report.Check("native-key-S-" + profile.Id, key == expected, key ?? "null");
        }
        if (layouts.UkLayout != IntPtr.Zero)
        {
            report.Check("native-uk-yi", Translate(0xDD, layouts.UkLayout) == "ї");
            report.Check("native-uk-ye", Translate(0xDE, layouts.UkLayout) == "є");
            report.Note("native-uk-apostrophe", Translate(0xC0, layouts.UkLayout) ?? "null");
            report.Note("native-uk-altgr-U", Translate(0x55, layouts.UkLayout, altGr: true) ?? "null");
            report.Note("native-uk-ISO-key", Translate(0xE2, layouts.UkLayout) ?? "null");
        }

        IntPtr previous = Native.GetForegroundWindow();
        IntPtr previousLayout = KeyTranslator.GetForegroundLayout();
        Native.GetCursorPos(out var cursor);
        var process = session.StartExecutable(Environment.ProcessPath!, "--input-host");
        var host = Uia.Window(process.Id, "TypeSwitcher Acceptance Input Host");
        IntPtr handle = (IntPtr)host.Current.NativeWindowHandle;
        try
        {
            using var initial = Status(host);
            report.Check("real-hooks-installed", initial.RootElement.GetProperty("keyboardInstalled").GetBoolean()
                && initial.RootElement.GetProperty("mouseInstalled").GetBoolean() && initial.RootElement.GetProperty("probeInstalled").GetBoolean());
            report.Note("actual-window-dpi", initial.RootElement.GetProperty("dpi").GetUInt32().ToString());
            report.Note("physical-monitor-count", Native.GetSystemMetrics(80).ToString());
            IntPtr native = (IntPtr)initial.RootElement.GetProperty("native").GetInt64();
            IntPtr nativePassword = (IntPtr)initial.RootElement.GetProperty("nativePassword").GetInt64();
            var wpf = Uia.Find(host, Uia.Id("acceptance.text"), "WPF editor");

            Uia.SetValue(AutomationElement.FromHandle(native), "ghbdsn");
            Uia.Until(() => Text(native) == "ghbdsn", "Native EDIT initial value was not set.");
            Focus(host, handle, "Focus native text", native);
            report.Note("native-edit-initial", Text(native));
            report.Check("native-edit-focus", new ExclusionManager().FocusedControl() == native);
            Replace(handle, 6, "привіт ");
            try { Uia.Until(() => Text(native) == "привіт ", "Native EDIT replacement failed."); }
            catch (TimeoutException) { throw new InvalidOperationException("Native EDIT actual: '" + Text(native) + "'"); }
            report.Pass("sendinput-native-edit", "ghbdsn -> привіт");
            Replace(handle, 7, "ghbdsn ");
            Uia.Until(() => Text(native) == "ghbdsn ", "Native EDIT undo failed.");
            report.Pass("sendinput-native-edit-undo");
            long handleCountBefore = process.HandleCount;
            var loopTime = System.Diagnostics.Stopwatch.StartNew();
            for (int iteration = 0; iteration < 100; iteration++)
            {
                string text = iteration % 2 == 0 ? "привіт " : "ghbdsn ";
                Replace(handle, 7, text);
                Uia.Until(() => Text(native) == text, "Repeated native replacement diverged.");
            }
            process.Refresh();
            report.Pass("native-replacement-repeat", $"100 cycles; {loopTime.ElapsedMilliseconds}ms; handle delta={process.HandleCount - handleCountBefore}");

            Uia.SetValue(wpf, "ghbdsn");
            Focus(host, handle, "Focus WPF text");
            report.Note("wpf-initial", Uia.Value(wpf));
            report.Note("wpf-focus", AutomationElement.FocusedElement.Current.AutomationId);
            Replace(handle, 6, "привіт ");
            try { Uia.Until(() => Uia.Value(wpf) == "привіт ", "WPF replacement failed."); }
            catch (TimeoutException) { throw new InvalidOperationException("WPF replacement actual: " + Uia.Value(wpf) + "; focus=" + AutomationElement.FocusedElement.Current.AutomationId); }
            report.Pass("sendinput-wpf-textbox", "real WPF input processing");
            using var afterInput = Status(host);
            report.Check("windows-marks-synthetic-input", afterInput.RootElement.GetProperty("injected").GetInt32() > 0);
            report.Check("production-hook-filters-injected", afterInput.RootElement.GetProperty("productionEvents").GetInt32()
                == afterInput.RootElement.GetProperty("physical").GetInt32(), "no synthetic event surfaced as a physical key");
            report.Note("physical-hook-coverage", "Physical correction remains unverified; this probe confirms installation and injected-event rejection.");

            using var exclusions = new ExclusionManager();
            Focus(host, handle, "Focus native password", nativePassword);
            exclusions.InvalidateFocusCache();
            report.Check("native-password-detection", exclusions.IsPasswordFast() && exclusions.IsPasswordThorough());
            report.Check("native-password-uia", AutomationElement.FromHandle(nativePassword).Current.IsPassword);
            Focus(host, handle, "Focus WPF password");
            exclusions.InvalidateFocusCache();
            report.Check("wpf-password-detection", exclusions.IsPasswordThorough());
            Focus(host, handle, "Focus WPF text");
            exclusions.InvalidateFocusCache();
            report.Check("normal-field-not-password", !exclusions.IsPasswordThorough());
            exclusions.SetBlacklist(["TypeSwitcher.Acceptance.exe"]);
            report.Check("real-process-blacklist", exclusions.IsBlacklistedApp(handle));
            report.Check("same-integrity-window", !exclusions.IsAboveOwnIntegrity(handle));
            var lowHelper = session.StartExecutable(Environment.ProcessPath!, "--low-integrity-probe " + handle.ToInt64());
            report.Check("higher-integrity-gate", lowHelper.WaitForExit(15000) && lowHelper.ExitCode == 0,
                "isolated low-integrity helper recognizes the medium-integrity test window; no UAC elevation");

            Uia.Click(host, "Reinstall hooks");
            using var reinstalled = Status(host);
            report.Check("real-hook-reinstall", reinstalled.RootElement.GetProperty("reinstalls").GetInt32() == 1
                && reinstalled.RootElement.GetProperty("keyboardInstalled").GetBoolean() && reinstalled.RootElement.GetProperty("mouseInstalled").GetBoolean());
            Focus(host, handle, "Focus WPF text");
            var rect = wpf.Current.BoundingRectangle;
            Native.Click(handle, (int)(rect.Left + 20), (int)(rect.Top + 20));
            Uia.Until(() => MouseClicks(host) > 0, "Mouse hook did not see test click.");
            report.Pass("real-mouse-hook", "controlled SendInput click inside owned test window");

            var store = new ConfigStore(session.Data, session.Root);
            var cfg = store.Load(); cfg.Languages.Enabled = ["en", "ru", "uk"];
            var loaded = DictionaryManager.Load(store, cfg);
            report.Check("component-dictionaries-loaded", !loaded.Errors.Any());
            if (layouts.EnLayout != IntPtr.Zero && layouts.UkLayout != IntPtr.Zero)
            {
                cfg.Languages.Enabled = ["en", "uk"];
                var bridge = new InputBridge(handle, wpf, loaded.Dictionaries, cfg, layouts);
                Uia.SetValue(wpf, ""); Focus(host, handle, "Focus WPF text");
                bridge.Start(layouts.EnLayout);
                bridge.Type("ghbdsn ");
                report.Check("native-table-component-pipeline", Uia.Value(wpf) == "привіт ");
                bridge.Press(0x13);
                report.Check("component-native-undo", Uia.Value(wpf) == "ghbdsn " && KeyTranslator.GetForegroundLayout() == layouts.EnLayout);
                bridge.Press(0x13);
                report.Check("component-native-redo", Uia.Value(wpf) == "привіт " && KeyTranslator.GetForegroundLayout() == layouts.UkLayout);
                bridge.Press(0x13, shift: true);
                report.Check("component-native-case", Uia.Value(wpf) == "ПРИВІТ ");
                Uia.SetValue(wpf, ""); Focus(host, handle, "Focus WPF text");
                bridge.Start(layouts.EnLayout);
                bridge.Type("g`znm ");
                report.Check("component-native-apostrophe", Uia.Value(wpf) == "п'ять ");
                cfg.Languages.Enabled = ["en", "ru", "uk"];
                bridge = new InputBridge(handle, wpf, loaded.Dictionaries, cfg, layouts);
                Uia.SetValue(wpf, ""); Focus(host, handle, "Focus WPF text"); bridge.Start(layouts.EnLayout);
                bridge.Type("hello");
                bridge.Press(0x13);
                report.Check("native-three-language-ru", Uia.Value(wpf) == "руддщ" && KeyTranslator.GetForegroundLayout() == layouts.RuLayout);
                bridge.Press(0x13);
                report.Check("native-three-language-uk", Uia.Value(wpf) == "руддщ" && KeyTranslator.GetForegroundLayout() == layouts.UkLayout);
                bridge.Press(0x13);
                report.Check("native-three-language-en", Uia.Value(wpf) == "hello" && KeyTranslator.GetForegroundLayout() == layouts.EnLayout);
                Uia.SetValue(wpf, ""); Focus(host, handle, "Focus WPF text"); bridge.Start(layouts.EnLayout);
                bridge.Type("vfvf ");
                report.Check("native-ambiguous-layout-kept", Uia.Value(wpf) == "vfvf ");
                report.Note("component-pipeline-coverage", "Synthetic events enter InputProcessor directly; real ToUnicodeEx, layout requests, UIA and SendInput execute against an external process. This is not physical-hook E2E.");
            }
        }
        finally
        {
            if (Native.GetForegroundWindow() == handle && previousLayout != IntPtr.Zero)
                Native.PostMessage(handle, 0x0050, (IntPtr)1, previousLayout);
            try { Uia.Close(host, process); } catch (Exception) { }
            Native.SetCursorPos(cursor.X, cursor.Y);
            if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
        }
    }

    internal static string? Translate(uint key, IntPtr layout, bool shift = false, bool caps = false, bool altGr = false)
        => KeyTranslator.TranslateLayout(key, Native.MapVirtualKeyEx(key, 0, layout), layout, shift, caps, altGr);

    internal static void Focus(AutomationElement host, IntPtr handle, string command, IntPtr expectedNative = default)
    {
        var status = Uia.Find(host, Uia.Id("acceptance.status"), "Focus status");
        int before;
        using (var json = JsonDocument.Parse(status.Current.Name)) before = json.RootElement.GetProperty("focusVersion").GetInt32();
        Uia.Click(host, command);
        Uia.Until(() =>
        {
            using var json = JsonDocument.Parse(status.Current.Name);
            return json.RootElement.GetProperty("focusVersion").GetInt32() > before;
        }, "Focus action did not complete.");
        Uia.Until(() => Native.GetForegroundWindow() == handle, "Owned input host could not become foreground.", 5000);
        Uia.Until(() => expectedNative != IntPtr.Zero ? new ExclusionManager().FocusedControl() == expectedNative
            : AutomationElement.FocusedElement?.Current.AutomationId == (command == "Focus WPF password" ? "acceptance.password" : "acceptance.text"),
            "Field did not acquire keyboard focus: " + command, 5000);
        Native.RequireForeground(handle);
    }

    internal static void Replace(IntPtr handle, int count, string text, int vk = 0)
    {
        Native.RequireForeground(handle);
        if (!Replacer.Replace(count, text, vk)) throw new InvalidOperationException("Production Replacer rejected input.");
    }

    private static string Text(IntPtr window)
    {
        // GetWindowText intentionally does not read another process's EDIT contents.
        return Uia.Value(AutomationElement.FromHandle(window));
    }
    private static JsonDocument Status(AutomationElement host)
    {
        var status = Uia.Find(host, Uia.Id("acceptance.status"), "Hook status");
        Uia.Until(() => status.Current.Name.StartsWith('{'), "Initial hook status unavailable.");
        int previous;
        using (var before = JsonDocument.Parse(status.Current.Name)) previous = before.RootElement.GetProperty("statusVersion").GetInt32();
        Uia.Click(host, "Read hook status");
        Uia.Until(() =>
        {
            using var current = JsonDocument.Parse(status.Current.Name);
            return current.RootElement.GetProperty("statusVersion").GetInt32() > previous;
        }, "Hook status action did not complete.");
        return JsonDocument.Parse(status.Current.Name);
    }
    private static int MouseClicks(AutomationElement host)
    {
        using var status = Status(host); return status.RootElement.GetProperty("mouseClicks").GetInt32();
    }
}

/// <summary>Explicit component bridge, not a replacement for the physical keyboard hook.</summary>
internal sealed class InputBridge
{
    private readonly IntPtr _window;
    private readonly AutomationElement _editor;
    private readonly LayoutSwitcher _layouts;
    private readonly InputProcessor _processor;
    private bool _shift;
    private string _expected = "";

    public InputBridge(IntPtr window, AutomationElement editor, Dictionaries dictionary, AppConfig cfg, LayoutSwitcher layouts)
    {
        _window = window; _editor = editor; _layouts = layouts;
        var analyzer = new Analyzer(dictionary, cfg) { LayoutDetectionEnabled = true, AvailableLayouts = layouts.Available };
        _processor = new InputProcessor(analyzer, null, new ExclusionManager(), skipPasswordFields: true)
        {
            RuLayout = layouts.RuLayout, EnLayout = layouts.EnLayout, UkLayout = layouts.UkLayout,
            CurrentLayout = KeyTranslator.GetForegroundLayout,
            TranslateLive = (vk, scan, hkl) => KeyTranslator.TranslateLayout(vk, scan, hkl, _shift, false, false),
            Modifiers = () => (_shift, false), AltGrHeld = () => false, CommandChordHeld = () => false,
            WindowsKeyHeld = () => false
        };
        _processor.ReplacementRequested += r => Apply(r.Backspaces, r.Text, r.TargetHkl, r.TrailingVk);
        _processor.ToggleRequested += r => Apply(r.Backspaces, r.Text, r.ActivateHkl, r.TrailingVk);
    }

    public void Start(IntPtr layout)
    {
        Native.RequireForeground(_window);
        _layouts.Activate(layout);
        Uia.Until(() => KeyTranslator.GetForegroundLayout() == layout, "Layout request was not applied.");
        _processor.ResetWordState(); _expected = Uia.Value(_editor);
    }

    public void Type(string keys)
    {
        foreach (char key in keys)
        {
            uint vk = char.ToUpperInvariant(key);
            bool shift = char.IsUpper(key);
            vk = key switch { ' ' => 0x20, '`' => 0xC0, ';' => 0xBA, '\'' => 0xDE, '[' => 0xDB, ']' => 0xDD, ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, '-' => 0xBD, _ => vk };
            Press(vk, shift);
        }
    }

    public void Press(uint vk, bool shift = false)
    {
        Native.RequireForeground(_window);
        _shift = shift;
        IntPtr layout = KeyTranslator.GetForegroundLayout();
        uint scan = Native.MapVirtualKeyEx(vk, 0, layout);
        var key = new KeyboardHook.KeyArgs { VkCode = vk, ScanCode = scan, IsKeyDown = true };
        _processor.OnKey(null, key);
        if (!key.Suppress)
        {
            string? text = KeyTranslator.TranslateLayout(vk, scan, layout, shift, false, false);
            if (text != null && text.Length > 0)
            {
                DesktopChecks.Replace(_window, 0, text);
                _expected += text;
            }
        }
        Uia.Until(() => Uia.Value(_editor) == _expected, $"Input bridge expected '{_expected}', actual '{Uia.Value(_editor)}'.");
    }

    private void Apply(int backspaces, string text, IntPtr layout, int trailing)
    {
        Native.RequireForeground(_window);
        if (layout != IntPtr.Zero) _layouts.Activate(layout);
        DesktopChecks.Replace(_window, backspaces, text, trailing);
        _expected = _expected[..Math.Max(0, _expected.Length - backspaces)] + text;
        if (layout != IntPtr.Zero) Uia.Until(() => KeyTranslator.GetForegroundLayout() == layout, "Replacement layout did not activate.");
    }
}
