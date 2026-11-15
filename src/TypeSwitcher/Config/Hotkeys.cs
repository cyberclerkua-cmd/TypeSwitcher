using System.Text.Json.Serialization;
using System.Windows.Input;

namespace TypeSwitcher.Config;

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }
public enum InputCommand { ConvertWord, ChangeCase, WordActions, ToggleEnabled, Undo, SelectionLayout, SelectionCase, SelectionTransliterate }

/// <summary>Portable virtual-key binding. Its modifiers must match exactly; AltGr is handled by the input processor.</summary>
public sealed record KeyChord(int Vk = 0, KeyModifiers Modifiers = KeyModifiers.None)
{
    [JsonIgnore] public bool IsAssigned => Vk != 0;
    public bool Matches(uint vk, KeyModifiers modifiers) => IsAssigned && Vk == vk && Modifiers == modifiers;
    public KeyChord Normalize() => new(Math.Clamp(Vk, 0, 255), Modifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows));

    public override string ToString()
    {
        if (!IsAssigned) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Windows)) parts.Add("Win");
        parts.Add(Vk switch { 0x13 => "Pause", 0x91 => "Scroll Lock", 0x20 => "Space", >= 0x30 and <= 0x39 => ((char)Vk).ToString(), _ => KeyInterop.KeyFromVirtualKey(Vk).ToString() });
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = new();
        if (string.IsNullOrWhiteSpace(text)) return true;
        KeyModifiers modifiers = 0;
        int vk = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var modifier = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control, "alt" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift, "win" or "windows" => KeyModifiers.Windows, _ => KeyModifiers.None
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }
            if (vk != 0) return false;
            string name = raw.Replace(" ", "");
            if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) vk = char.ToUpperInvariant(name[0]);
            else if (name.Equals("ScrollLock", StringComparison.OrdinalIgnoreCase)) vk = 0x91;
            else if (Enum.TryParse<Key>(name, true, out var key) && Enum.IsDefined(key)) vk = KeyInterop.VirtualKeyFromKey(key);
            else return false;
        }
        if (vk == 0 || IsModifierKey(vk)) return false;
        chord = new(vk, modifiers);
        return true;
    }

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
}

public sealed class HotkeysConfig
{
    public KeyChord ConvertWord { get; set; } = new(0x13);
    public KeyChord ChangeCase { get; set; } = new(0x13, KeyModifiers.Shift);
    public KeyChord WordActions { get; set; } = new(0x91);
    public KeyChord ToggleEnabled { get; set; } = new(0x50, KeyModifiers.Control | KeyModifiers.Alt);
    public KeyChord Undo { get; set; } = new(0x5A, KeyModifiers.Control | KeyModifiers.Alt);
    public KeyChord SelectionLayout { get; set; } = new(0x4C, KeyModifiers.Control | KeyModifiers.Alt);
    public KeyChord SelectionCase { get; set; } = new(0x55, KeyModifiers.Control | KeyModifiers.Alt);
    public KeyChord SelectionTransliterate { get; set; } = new(0x54, KeyModifiers.Control | KeyModifiers.Alt);
    public int LayoutTapKey { get; set; }
    public int ConvertTapKey { get; set; }

    [JsonIgnore]
    public IEnumerable<(InputCommand Command, KeyChord Chord)> Bindings => Enum.GetValues<InputCommand>().Select(c => (c, Get(c)));

    public KeyChord Get(InputCommand command) => command switch
    {
        InputCommand.ConvertWord => ConvertWord, InputCommand.ChangeCase => ChangeCase,
        InputCommand.WordActions => WordActions, InputCommand.ToggleEnabled => ToggleEnabled,
        InputCommand.Undo => Undo,
        InputCommand.SelectionLayout => SelectionLayout, InputCommand.SelectionCase => SelectionCase,
        _ => SelectionTransliterate
    };
    public void Set(InputCommand command, KeyChord value)
    {
        switch (command)
        {
            case InputCommand.ConvertWord: ConvertWord = value; break;
            case InputCommand.ChangeCase: ChangeCase = value; break;
            case InputCommand.WordActions: WordActions = value; break;
            case InputCommand.ToggleEnabled: ToggleEnabled = value; break;
            case InputCommand.Undo: Undo = value; break;
            case InputCommand.SelectionLayout: SelectionLayout = value; break;
            case InputCommand.SelectionCase: SelectionCase = value; break;
            case InputCommand.SelectionTransliterate: SelectionTransliterate = value; break;
        }
    }

    public bool HasConflicts() => Bindings.Where(b => b.Chord is { IsAssigned: true }).GroupBy(b => b.Chord).Any(g => g.Count() > 1)
        || LayoutTapKey != 0 && LayoutTapKey == ConvertTapKey;

    public void Normalize()
    {
        var defaults = new HotkeysConfig();
        var seen = new HashSet<KeyChord>();
        foreach (var command in Enum.GetValues<InputCommand>())
        {
            var chord = (Get(command) ?? defaults.Get(command)).Normalize();
            if (KeyChord.IsModifierKey(chord.Vk) || chord.IsAssigned && !seen.Add(chord)) chord = new();
            Set(command, chord);
        }
        if (LayoutTapKey is not (0xA0 or 0xA1 or 0xA2 or 0xA3)) LayoutTapKey = 0;
        if (ConvertTapKey is not (0xA0 or 0xA1 or 0xA2 or 0xA3) || ConvertTapKey == LayoutTapKey) ConvertTapKey = 0;
    }

    public static HotkeysConfig FromLegacy(AppConfig cfg) => new()
    {
        ConvertWord = new(cfg.Layout.HotkeyUndoVk), WordActions = new(cfg.Layout.HotkeySuggestVk),
        ChangeCase = new(cfg.Case.HotkeyVk, cfg.Case.HotkeyShift ? KeyModifiers.Shift : KeyModifiers.None)
    };
}
