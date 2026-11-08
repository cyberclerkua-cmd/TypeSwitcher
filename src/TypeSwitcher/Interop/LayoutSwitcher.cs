using TypeSwitcher.Core;
using static TypeSwitcher.Interop.NativeMethods;

namespace TypeSwitcher.Interop;

/// <summary>Установленные раскладки Windows. HKL сохраняется целиком, включая вариант раскладки.</summary>
public sealed class LayoutSwitcher
{
    private IReadOnlyDictionary<LayoutTarget, IntPtr> _layouts = new Dictionary<LayoutTarget, IntPtr>();
    public IntPtr RuLayout => Get(LayoutTarget.Ru);
    public IntPtr EnLayout => Get(LayoutTarget.En);
    public IntPtr UkLayout => Get(LayoutTarget.Uk);
    public bool BothPresent => RuLayout != IntPtr.Zero && EnLayout != IntPtr.Zero;
    public IReadOnlySet<LayoutTarget> Available => _layouts.Keys.ToHashSet();
    public IntPtr Get(LayoutTarget language) => _layouts.GetValueOrDefault(language);

    public void Detect()
    {
        var found = new Dictionary<LayoutTarget, IntPtr>();
        uint count = GetKeyboardLayoutList(0, null);
        if (count > 0)
        {
            var layouts = new IntPtr[count];
            uint received = GetKeyboardLayoutList(layouts.Length, layouts);
            for (int i = 0; i < received && i < layouts.Length; i++)
            {
                var language = LanguageProfile.FromLayout(layouts[i]);
                if (language != LayoutTarget.None) found.TryAdd(language, layouts[i]);
            }
        }
        _layouts = found;
    }

    public void Activate(IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return;
        IntPtr window = GetForegroundWindow();
        if (window != IntPtr.Zero) PostMessage(window, WM_INPUTLANGCHANGEREQUEST, (IntPtr)INPUTLANGCHANGE_SYSCHARSET, hkl);
    }
}
