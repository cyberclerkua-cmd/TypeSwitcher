namespace TypeSwitcher.Interop;

/// <summary>Identity and native caret position, without reading the field's text.</summary>
public readonly record struct InputTarget(IntPtr Window, IntPtr Control = default, long FocusVersion = 0,
    IntPtr Caret = default, int CaretLeft = 0, int CaretTop = 0)
{
    public bool SameField(InputTarget other) => Window == other.Window && Control == other.Control && FocusVersion == other.FocusVersion;
    public bool Matches(InputTarget other) => SameField(other)
        && (Caret == IntPtr.Zero || Caret == other.Caret && CaretLeft == other.CaretLeft && CaretTop == other.CaretTop);
}
