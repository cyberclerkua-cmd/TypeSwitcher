using TypeSwitcher.Config;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Core;

public sealed partial class InputProcessor
{
    public HotkeysConfig Hotkeys { get; private set; } = new();
    public Func<long> Milliseconds { get; set; } = () => Environment.TickCount64;
    public Func<KeyModifiers> ShortcutModifiers { get; set; } = ReadModifiers;
    public Func<bool> CommandsSuspended { get; set; } = () => false;
    public Func<InputTarget>? TargetReader { get; set; }
    public long Revision { get; private set; }
    public event Action? ToggleEnabledRequested;
    public event Action<InputCommand, InputTarget>? SelectionRequested;

    private readonly HashSet<uint> _keysDown = new();
    private readonly HashSet<uint> _suppressedKeys = new();
    private KeyModifiers _eventModifiers;
    private bool _repeat;
    private PendingCommand? _pendingCommand;
    private ModifierTap? _tap;
    private InputTarget? _lastField;
    private IntPtr _observedLayout, _expectedLayout;
    private long _expectedLayoutUntil;
    private bool _manualWord;
    private sealed record PendingCommand(InputCommand Command, long Deadline, InputTarget Target);
    private sealed record ModifierTap(uint Key, long Started, IntPtr Layout);

    public void Configure(AppConfig snapshot)
    {
        Hotkeys = ConfigStore.Clone(snapshot).Hotkeys;
        ResetWordState();
        _keysDown.Clear(); _suppressedKeys.Clear();
        _observedLayout = IntPtr.Zero; _expectedLayout = IntPtr.Zero;
    }

    private static KeyModifiers ReadModifiers()
    {
        bool Down(int vk) => (NativeMethods.GetKeyState(vk) & 0x8000) != 0;
        return (Down(0x10) ? KeyModifiers.Shift : 0) | (Down(0x11) ? KeyModifiers.Control : 0)
            | (Down(0x12) ? KeyModifiers.Alt : 0) | (Down(0x5B) || Down(0x5C) ? KeyModifiers.Windows : 0);
    }

    private InputTarget ReadTarget() => TargetReader?.Invoke() ?? _exclusions?.CaptureTarget() ?? new(ForegroundWindow());

    private bool PrepareKey(KeyboardHook.KeyArgs e)
    {
        if (e.Injected) return false;
        _repeat = e.IsKeyDown && !_keysDown.Add(e.VkCode);
        if (!e.IsKeyDown) _keysDown.Remove(e.VkCode);
        _eventModifiers = ShortcutModifiers();
        if (!e.IsKeyDown)
        {
            var released = e.VkCode switch
            {
                0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift, 0x11 or 0xA2 or 0xA3 => KeyModifiers.Control,
                0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt, 0x5B or 0x5C => KeyModifiers.Windows, _ => KeyModifiers.None
            };
            _eventModifiers &= ~released;
            e.Suppress = _suppressedKeys.Remove(e.VkCode);
        }
        if (_tap != null && _tap.Key != e.VkCode) _tap = null;
        if (e.IsKeyDown && _pendingCommand != null && !_repeat) _pendingCommand = null;

        var target = ReadTarget();
        if (_lastField is { } previous && !previous.SameField(target)) { ResetWordState(); _observedLayout = IntPtr.Zero; }
        _lastField = target;
        if (e.IsKeyDown && !IsModifier(e.VkCode)) Revision++;

        if (Enabled && e.VkCode is 0xA0 or 0xA1 or 0xA2 or 0xA3
            && (e.VkCode == Hotkeys.LayoutTapKey || e.VkCode == Hotkeys.ConvertTapKey))
        {
            if (e.IsKeyDown && !_repeat && _keysDown.Count == 1) _tap = new(e.VkCode, Milliseconds(), CurrentLayout());
            else if (!e.IsKeyDown && _tap is { } tap)
            {
                _tap = null;
                if (Milliseconds() - tap.Started <= 500 && _keysDown.Count == 0 && _eventModifiers == 0)
                {
                    if (tap.Key == Hotkeys.LayoutTapKey) SwitchByTap(tap.Layout);
                    else ExecuteCommand(InputCommand.ConvertWord);
                }
            }
        }
        if (!e.IsKeyDown)
        {
            if (_pendingCommand is { } pending && _eventModifiers == 0 && _keysDown.Count == 0)
            {
                _pendingCommand = null;
                if (Milliseconds() <= pending.Deadline && pending.Target.Matches(ReadTarget())) ExecuteCommand(pending.Command);
            }
            return false;
        }
        return true;
    }

    private bool HandleGlobalCommand(KeyboardHook.KeyArgs e)
    {
        if (CommandsSuspended()) return false;
        if (AltGrHeld() && !WindowsKeyHeld() && LanguageProfile.FromLayout(CurrentLayout()) == LayoutTarget.Uk
            && e.VkCode is >= 0x30 and <= 0x5A or >= 0xBA and <= 0xE2) return false;
        if (!Hotkeys.ToggleEnabled.Matches(e.VkCode, _eventModifiers)) return false;
        e.Suppress = true; _suppressedKeys.Add(e.VkCode);
        if (!_repeat) { ResetWordState(); ToggleEnabledRequested?.Invoke(); }
        return true;
    }

    private bool HandleTextCommand(KeyboardHook.KeyArgs e)
    {
        var matched = Hotkeys.Bindings.FirstOrDefault(b => b.Command != InputCommand.ToggleEnabled && b.Chord.Matches(e.VkCode, _eventModifiers));
        if (matched.Chord == null) return false;
        if (_exclusions != null && _exclusions.IsPasswordThorough()) { ResetWordState(); return true; }
        if (_repeat) { e.Suppress = true; return true; }
        if ((_eventModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows)) != 0)
        {
            _pendingCommand = new(matched.Command, Milliseconds() + 2500, ReadTarget());
            e.Suppress = true;
        }
        else e.Suppress = ExecuteCommand(matched.Command);
        if (e.Suppress) _suppressedKeys.Add(e.VkCode);
        return true;
    }

    private bool ExecuteCommand(InputCommand command)
    {
        if (!Enabled || _exclusions != null && (_exclusions.IsBlacklistedApp(ForegroundWindow()) || _exclusions.IsAboveOwnIntegrity(ForegroundWindow()) || _exclusions.IsPasswordThorough())) return false;
        switch (command)
        {
            case InputCommand.ConvertWord: return TryToggle() || TrySegmentRemap() || TryForceLayout();
            case InputCommand.Undo: return TryToggle();
            case InputCommand.ChangeCase: return TryChangeCase();
            case InputCommand.WordActions: return TrySuggest();
            case InputCommand.SelectionLayout:
            case InputCommand.SelectionCase:
            case InputCommand.SelectionTransliterate:
                var target = ReadTarget();
                ResetWordState();
                SelectionRequested?.Invoke(command, target);
                return true;
            default: return false;
        }
    }

    private void SwitchByTap(IntPtr previous)
    {
        IntPtr current = CurrentLayout();
        if (!SameLang(current, previous)) { ObserveLayout(current); return; }
        if (_exclusions != null && (_exclusions.IsBlacklistedApp(ForegroundWindow()) || _exclusions.IsAboveOwnIntegrity(ForegroundWindow()) || _exclusions.IsPasswordThorough())) return;
        IntPtr target = NextLayout(current);
        if (target == IntPtr.Zero) return;
        ResetWordState();
        _manualWord = _analyzer.Settings.Layout.RespectManualLayout;
        SendToggle(new(0, "", LanguageProfile.FromLayout(target), Window: ForegroundWindow(), ActivateHkl: target));
    }

    private void ObserveLayout(IntPtr current)
    {
        if (_observedLayout != IntPtr.Zero && !SameLang(_observedLayout, current))
        {
            bool ours = _expectedLayout != IntPtr.Zero && SameLang(current, _expectedLayout) && Milliseconds() <= _expectedLayoutUntil;
            if (!ours)
            {
                ResetWordState();
                _manualWord = _analyzer.Settings.Layout.RespectManualLayout;
            }
            _expectedLayout = IntPtr.Zero;
        }
        _observedLayout = current;
    }

    private void NoteOwnLayout(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        _expectedLayout = target;
        _expectedLayoutUntil = Milliseconds() + 1500;
    }

    public bool CanApply(InputTarget? target, long revision) => Enabled && (revision == 0 || revision == Revision)
        && (target == null || target.Value.Matches(ReadTarget()));

    private void SendReplacement(ReplacementRequest request)
    {
        var captured = request with { Target = ReadTarget(), Revision = Revision };
        Post(() =>
        {
            if (!CanApply(captured.Target, captured.Revision)) { ResetWordState(); return; }
            NoteOwnLayout(captured.TargetHkl);
            ReplacementRequested?.Invoke(captured);
        });
    }

    private void SendToggle(ToggleRequest request)
    {
        var captured = request with { Target = ReadTarget(), Revision = Revision };
        Post(() =>
        {
            if (!CanApply(captured.Target, captured.Revision)) { ResetWordState(); return; }
            NoteOwnLayout(captured.ActivateHkl);
            ToggleRequested?.Invoke(captured);
        });
    }
}
