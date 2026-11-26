using TypeSwitcher.Interop;

namespace TypeSwitcher.Core;

public sealed partial class InputProcessor
{
    private const int UndoTailKeys = 24;
    private const int UndoTailLifetimeMs = 10_000;

    // Query only the expected span before the caret. An editor without a text provider
    // can still use the bounded physical input trail; a mismatching readable field cannot.
    public Func<InputTarget, string, Task<bool>>? VerifyUndoText { get; set; }

    private static bool IsUndoCharacter(char c) => char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or '!' or '?' or ':' or ';' or '-';

    private void TrackUndoTail(char c)
    {
        if (_toggle is not { } correction) return;
        string tail = correction.Tail;
        char previous = tail.Length == 0 ? correction.Separator.LastOrDefault() : tail[^1];
        if (correction.InProgress || !correction.ShowingCorrected || correction.SepVk != 0 ||
            Milliseconds() - correction.CompletedAt > UndoTailLifetimeMs || tail.Length >= UndoTailKeys ||
            !IsUndoCharacter(c) || tail.Length == 0 && !CanReplayCorrection(correction) ||
            c == ' ' && previous == ' ' || char.IsPunctuation(c) && char.IsPunctuation(previous))
        {
            _toggle = null;
            return;
        }
        correction.Tail += c;
        correction.TailKeyRevision = Revision;
        correction.TailCaret = null; // this physical key has not reached the editor yet
    }

    private static bool CanReplayCorrection(ToggleState correction) => correction.Separator.All(IsUndoCharacter) &&
        correction.Corrected.Length <= SelectionText.MaximumCharacters && correction.Original.Length <= SelectionText.MaximumCharacters &&
        !correction.Corrected.Any(c => char.IsControl(c) || char.IsSurrogate(c) || LanguageProfile.IsCombining(c) ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private bool TryUndo()
    {
        if (_toggleAvailable) return TryToggle();
        if (_toggle is not { Tail.Length: > 0 } correction) return false;
        var target = ReadTarget();
        if (Milliseconds() - correction.CompletedAt > UndoTailLifetimeMs ||
            correction.TailCaret is { } caret && !caret.Matches(target))
        {
            _toggle = null;
            return false;
        }
        _ = VerifyAndUndoAsync(correction, target, Revision);
        return true;
    }

    private async Task VerifyAndUndoAsync(ToggleState correction, InputTarget target, long revision)
    {
        string expected = correction.Corrected + correction.Separator + correction.Tail;
        bool matches;
        try { matches = VerifyUndoText is not { } verify || await verify(target, expected).ConfigureAwait(false); }
        catch { matches = false; } // inaccessible, busy or timed-out provider
        Post(() =>
        {
            if (_toggle != correction || Revision != revision) return;
            if (!matches || CurrentLayout() != _observedLayout || Milliseconds() - correction.CompletedAt > UndoTailLifetimeMs)
            {
                ResetWordState();
                return;
            }
            _toggle = null;
            _recentCorrectedOriginal = null;
            // Keep the live buffer, its physical keys and HKL: the user can finish the next word.
            string restored = correction.Original + correction.OriginalSeparator + correction.Tail;
            if (ApplyToggle(new(expected.Length, restored, LayoutTarget.None, Window: target.Window, Target: target, Revision: revision)))
            {
                _recentContext.Clear();
                RejectOnce(correction);
            }
        });
    }
}
