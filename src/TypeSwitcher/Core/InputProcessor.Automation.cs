using TypeSwitcher.Interop;

namespace TypeSwitcher.Core;

public sealed record CompletedCorrection(string Original, string Corrected, ActionKind Kind, LayoutTarget Language);

public sealed partial class InputProcessor
{
    private readonly RecentContext _recentContext = new();
    private long _lastInputAt, _idleAttempt = -1, _prefixAttempt = -1;
    private InputTarget? _idleAnchor;
    private long _anchorAt;
    private LiveEdit? _liveEdit;
    public ContextAnalysis LastContextAnalysis { get; private set; }
    public event Action<CompletedCorrection>? CorrectionCompleted;

    private sealed class LiveEdit
    {
        public required string Original;
        public required IntPtr OriginalHkl;
        public required ActionKind Kind;
        public required IntPtr TargetHkl;
        public required long AppliedAt;
    }

    public void OnKey(object? sender, KeyboardHook.KeyArgs e)
    {
        if (e.Injected) return;
        try
        {
            if (e.IsKeyDown && !IsModifier(e.VkCode)) { _lastInputAt = Milliseconds(); _idleAnchor = null; }
            ProcessKey(sender, e);
            if (!e.IsKeyDown) TryEarlySwitch();
        }
        finally
        {
            if (e.IsKeyDown && e.VkCode is VK_RETURN or VK_TAB) _recentContext.Clear();
        }
    }

    private void ResetAdvancedState()
    {
        _recentContext.Clear(); _liveEdit = null; _idleAnchor = null;
        LastContextAnalysis = default;
    }

    private Decision AnalyzeCurrent(string word, LayoutTarget source)
    {
        LastContextAnalysis = _analyzer.AnalyzeWithContext(word, source, _recentContext, Milliseconds());
        return LastContextAnalysis.Applied;
    }

    private void RememberWord(string word, LayoutTarget source)
    {
        if (_analyzer.Settings.Layout.ContextMode == "off") return;
        word = word.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '(', ')', '[', ']');
        _recentContext.Add(_analyzer.RecognizeCompletedWord(word, source), Milliseconds());
    }

    private bool MayEditCurrentWord()
    {
        if (!Enabled || _manualWord || _segmentManualLayout || _segmentHasHyphen || _segmentHasNumericDot || _liveEdit != null || _pendingCommand != null
            || _keysDown.Count != 0 || ShortcutModifiers() != 0 || _buffer.IsEmpty || _buffer.Length > 64) return false;
        string word = _buffer.Current;
        if (char.IsAsciiDigit(word[0]) && word.Contains('ю')) return false; // may still become an identifier
        var target = ReadTarget();
        if (_lastField == null || !_lastField.Value.SameField(target)) { ResetWordState(); return false; }
        if (_exclusions != null && (_exclusions.IsExcludedTarget(target) || _exclusions.IsPasswordThorough())) { ResetWordState(); return false; }
        ObserveLayout(CurrentLayout());
        return !_manualWord && !_buffer.IsEmpty;
    }

    private void TryEarlySwitch()
    {
        if (!_analyzer.Settings.Layout.EarlySwitch || _prefixAttempt == Revision || !MayEditCurrentWord()) return;
        _prefixAttempt = Revision;
        var source = LanguageProfile.FromLayout(CurrentLayout());
        Decision decision = _analyzer.AnalyzePrefix(_buffer.Current, source, _recentContext, Milliseconds());
        if (decision.Kind != ActionKind.None) ApplyLiveEdit(decision);
    }

    /// <summary>Called by a hook-thread timer; no network, field-text access or unbounded work.</summary>
    public void Tick()
    {
        ObserveExpectedLayout();
        if (_toggle is { Tail.Length: > 0 } correction && Milliseconds() - correction.CompletedAt > UndoTailLifetimeMs)
            _toggle = null;
        if (!_analyzer.Settings.Layout.CorrectOnIdle || _idleAttempt == Revision || !MayEditCurrentWord()) return;
        long now = Milliseconds();
        var target = ReadTarget();
        if (_idleAnchor == null) { _idleAnchor = target; _anchorAt = now; }
        else if (!_idleAnchor.Value.Matches(target)) { ResetWordState(); return; }
        if (now - _lastInputAt < _analyzer.Settings.Layout.IdleDelayMs || now - _anchorAt < 100) return;
        _idleAttempt = Revision;
        var source = LanguageProfile.FromLayout(CurrentLayout());
        Decision decision = AnalyzeCurrent(_buffer.Current, source);
        if (decision.Kind != ActionKind.None) ApplyLiveEdit(decision);
    }

    private void ApplyLiveEdit(Decision decision)
    {
        string original = _buffer.Current;
        if (original == decision.Replacement || original.Any(LanguageProfile.IsCombining)) return;
        IntPtr source = CurrentLayout();
        IntPtr target = decision.Layout == LayoutTarget.None ? source : LayoutFor(decision.Layout);
        if (target == IntPtr.Zero) return;
        _liveEdit = new() { Original = original, OriginalHkl = source, Kind = decision.Kind, TargetHkl = target, AppliedAt = Milliseconds() };
        _buffer.Reset(); _buffer.Append(decision.Replacement);
        // The physical run no longer describes the displayed text after a spelling edit.
        ResetSegment(); _segmentOverflow = true;
        ArmLiveToggle();
        SendToggle(new(original.Length, decision.Replacement, decision.Layout, Window: _lastForeground,
            ActivateHkl: decision.Layout == LayoutTarget.None ? IntPtr.Zero : target));
    }

    private void ObserveLiveKey(KeyboardHook.KeyArgs e)
    {
        if (_liveEdit == null) return;
        var (shift, caps) = Modifiers();
        string? original = TranslateWith(e.VkCode, e.ScanCode, _liveEdit.OriginalHkl, shift, caps, AltGrHeld());
        if (original == null) { _liveEdit = null; return; }
        if (_liveEdit.Original.Length + original.Length > 128) { _liveEdit = null; _toggleAvailable = false; return; }
        _liveEdit.Original += original;
        ArmLiveToggle();
    }

    private void ArmLiveToggle()
    {
        if (_liveEdit is not { } live) return;
        _toggle = new()
        {
            Original = live.Original, Corrected = _buffer.Current, Separator = "", OriginalSeparator = "", SepVk = 0,
            Kind = live.Kind, Target = LanguageProfile.FromLayout(live.TargetHkl), OriginalHkl = live.OriginalHkl,
            CorrectedHkl = live.TargetHkl, ShowingCorrected = true, InProgress = true
        };
        _toggleAvailable = true;
    }

    private void CompleteLiveEdit(string word, string separator, int separatorVk)
    {
        if (_liveEdit is not { } live) return;
        _liveEdit = null;
        _toggle = new()
        {
            Original = live.Original, Corrected = word, Separator = separator, OriginalSeparator = separator, SepVk = separatorVk,
            Kind = live.Kind, Target = LanguageProfile.FromLayout(live.TargetHkl), OriginalHkl = live.OriginalHkl,
            CorrectedHkl = live.TargetHkl, ShowingCorrected = true, CompletedAt = live.AppliedAt
        };
        _toggleAvailable = true;
        CorrectionCompleted?.Invoke(new(live.Original, word, live.Kind, LanguageProfile.FromLayout(live.TargetHkl)));
    }
}
