using System.Runtime.InteropServices;
using System.Text;
using static TypeSwitcher.Interop.NativeMethods;

namespace TypeSwitcher.Interop;

/// <summary>The foreground frame and the window that actually receives keyboard input can have different threads and HKLs.</summary>
internal readonly record struct FocusedInput(IntPtr Window, IntPtr Receiver, uint ThreadId, uint ProcessId, uint FrameProcessId, GUITHREADINFO Gui)
{
    public IntPtr Layout => ThreadId == 0 ? IntPtr.Zero : GetKeyboardLayout(ThreadId);
    public static FocusedInput Capture() => Read(GetForegroundWindow());

    public static FocusedInput Read(IntPtr window)
    {
        uint frameThread = GetWindowThreadProcessId(window, out uint frameProcess);
        if (frameThread == 0) return default;
        var gui = ThreadInfo(frameThread);
        IntPtr receiver = BelongsTo(window, gui.hwndFocus) ? gui.hwndFocus : window;
        if (receiver == window && ClassName(window) == "ApplicationFrameWindow")
        {
            IntPtr core = FindWindowEx(window, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero) receiver = core;
        }
        uint thread = GetWindowThreadProcessId(receiver, out uint process);
        if (thread == 0) return new(window, window, frameThread, frameProcess, frameProcess, default);
        if (thread != frameThread) gui = ThreadInfo(thread);
        return new(window, receiver, thread, process, frameProcess, gui);
    }

    private static GUITHREADINFO ThreadInfo(uint thread)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(thread, ref info) ? info : default;
    }

    internal static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static bool BelongsTo(IntPtr frame, IntPtr control) => control != IntPtr.Zero && (control == frame || GetAncestor(control, 2 /* GA_ROOT */) == frame);

    // The main application owns the rule (including WebView hosts). For Store apps
    // ApplicationFrameHost is only a shell; use the process owning the CoreWindow.
    public string? ApplicationName() => ExclusionManager.ExecutableName(ProcessId != FrameProcessId && ClassName(Window) == "ApplicationFrameWindow" ? Receiver : Window);

    // The tray may still hold this snapshot after its menu has taken the focus.
    public bool RequestLayout(IntPtr layout) => layout != IntPtr.Zero && ThreadId != 0
        && GetWindowThreadProcessId(Receiver, out uint process) == ThreadId && process == ProcessId && BelongsTo(Window, Receiver)
        && PostMessage(Receiver, WM_INPUTLANGCHANGEREQUEST, (IntPtr)INPUTLANGCHANGE_SYSCHARSET, layout);

    public bool TryGetCaret(out RECT screen)
    {
        screen = default;
        if (!BelongsTo(Window, Gui.hwndCaret) || Gui.rcCaret.Bottom <= Gui.rcCaret.Top) return false;
        var top = new POINT { X = Gui.rcCaret.Left, Y = Gui.rcCaret.Top };
        var bottom = new POINT { X = Gui.rcCaret.Right, Y = Gui.rcCaret.Bottom };
        if (!ClientToScreen(Gui.hwndCaret, ref top) || !ClientToScreen(Gui.hwndCaret, ref bottom)) return false;
        screen = new RECT { Left = top.X, Top = top.Y, Right = bottom.X, Bottom = bottom.Y };
        return true;
    }
}
