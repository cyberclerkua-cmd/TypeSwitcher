using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Tray;

/// <summary>UI-thread observation. A layout rule is consumed once per visit, even if the user later changes fields or languages.</summary>
internal sealed class InputLanguageFeedback(ExclusionManager exclusions, LayoutSwitcher layouts) : IDisposable
{
    private readonly Dictionary<string, LayoutTarget> _rules = new(StringComparer.OrdinalIgnoreCase);
    private bool _showCaret, _skipPasswords, _excluded, _checking;
    private int _configuration;
    private uint _frameProcess, _inputProcess;
    private string? _application;
    private LayoutTarget _entryLayout;
    private LayoutTarget _language;
    private InputTarget _field;
    private long _showUntil;
    private CaretLanguageIndicator? _indicator;
    public bool Active => _showCaret || _rules.Count != 0;

    public void Configure(AppConfig cfg)
    {
        _showCaret = cfg.Tray.ShowCaretIndicator;
        _skipPasswords = cfg.Exclusions.SkipPasswordFields;
        _rules.Clear();
        if (cfg.Languages.UseApplicationLayouts)
            foreach (var (app, id) in cfg.Languages.ApplicationLayouts)
                if (cfg.Languages.Enabled.Contains(id) && LanguageProfile.Find(id) is { } profile && layouts.Get(profile.Target) != IntPtr.Zero)
                    _rules[app] = profile.Target;
        Reset();
    }

    public void Reset()
    {
        _configuration++;
        _frameProcess = _inputProcess = 0;
        _application = null; _entryLayout = _language = LayoutTarget.None; _field = default;
        DismissIndicator();
    }

    public void DismissIndicator() { _showUntil = 0; _indicator?.Hide(); }

    public async Task ObserveAsync(FocusedInput input, LayoutTarget language)
    {
        if (input.Window == IntPtr.Zero) { _indicator?.Hide(); return; } // transient gap during activation
        var field = new InputTarget(input.Window, input.Receiver, exclusions.FocusVersion);
        if (_inputProcess != input.ProcessId || _frameProcess != input.FrameProcessId)
        {
            _excluded = exclusions.IsExcludedTarget(field);
            string? app = _excluded || _rules.Count == 0 ? null : input.ApplicationName();
            if (!string.Equals(_application, app, StringComparison.OrdinalIgnoreCase))
            {
                _application = app;
                _entryLayout = app == null ? LayoutTarget.None : _rules.GetValueOrDefault(app);
            }
            _inputProcess = input.ProcessId; _frameProcess = input.FrameProcessId;
        }
        bool changed = !_field.SameField(field) || language != _language;
        _field = field; _language = language;
        long now = Environment.TickCount64;
        if (changed)
        {
            _indicator?.Hide();
            _showUntil = _showCaret ? now + 900 : 0;
        }
        var entryLayout = _entryLayout;
        var profile = LanguageProfile.Find(language);
        bool show = _showCaret && now < _showUntil && profile != null;
        if (!show) _indicator?.Hide();
        if (_excluded || _checking || entryLayout == LayoutTarget.None && !show) return;
        // Only check UIA when there is something to do, never query text. A slow provider
        // may outlive a focus or language change; revalidate before touching either window.
        if (_skipPasswords)
        {
            if (exclusions.IsPasswordFast()) { _indicator?.Hide(); return; }
            _checking = true;
            int configuration = _configuration;
            long revision = exclusions.FeedbackRevision;
            try
            {
                bool protectedField = await Task.Run(() => exclusions.IsProtectedFieldForFeedback(field, revision));
                if (configuration != _configuration) return;
                if (protectedField || revision != exclusions.FeedbackRevision) { _indicator?.Hide(); return; }
            }
            finally { _checking = false; }
        }
        var current = FocusedInput.Capture();
        if (current.Window != input.Window || current.Receiver != input.Receiver || field.FocusVersion != exclusions.FocusVersion
            || LanguageProfile.FromLayout(current.Layout) != language) { _indicator?.Hide(); return; }
        if (entryLayout != LayoutTarget.None && _entryLayout == entryLayout)
        {
            _entryLayout = LayoutTarget.None;
            if (language != entryLayout && current.RequestLayout(layouts.Get(entryLayout))) { _indicator?.Hide(); return; }
        }
        if (!show || Environment.TickCount64 >= _showUntil) return;
        if (!current.TryGetCaret(out var caret)) { _indicator?.Hide(); return; }
        _indicator ??= new CaretLanguageIndicator();
        _indicator.ShowAt(caret, NativeMethods.GetDpiForWindow(current.Gui.hwndCaret) / 96.0, profile!);
    }

    public void Dispose() { Reset(); _indicator?.Close(); _indicator = null; }
}
