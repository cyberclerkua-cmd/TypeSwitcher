using System.Text;
using static TypeSwitcher.Interop.NativeMethods;

namespace TypeSwitcher.Interop;

/// <summary>
/// Переводит виртуальную клавишу (vk + scan) в символ с учётом раскладки
/// активного окна и состояния модификаторов (Shift / CapsLock / AltGr).
/// </summary>
public static class KeyTranslator
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;     // Alt
    private const int VK_CAPITAL = 0x14;  // CapsLock

    /// <summary>Раскладка клавиатуры окна, находящегося в фокусе.</summary>
    public static IntPtr GetForegroundLayout()
    {
        IntPtr hwnd = GetForegroundWindow();
        uint threadId = GetWindowThreadProcessId(hwnd, out _);
        return GetKeyboardLayout(threadId);
    }

    /// <summary>
    /// Переводит клавишу в символ в ЗАДАННОЙ раскладке с ЯВНЫМ состоянием
    /// модификаторов (для перебивки сырого сегмента клавиш по хоткею).
    /// </summary>
    public static string? TranslateLayout(uint vkCode, uint scanCode, IntPtr layout, bool shift, bool caps, bool altGr)
    {
        var keyState = new byte[256];
        if (shift) keyState[VK_SHIFT] = 0x80;
        if (caps) keyState[VK_CAPITAL] = 0x01;
        if (altGr) { keyState[VK_CONTROL] = 0x80; keyState[VK_MENU] = 0x80; keyState[0xA5] = 0x80; }

        var sb = new StringBuilder(8);
        int rc = ToUnicodeEx(vkCode, scanCode, keyState, sb, sb.Capacity, 0, layout);
        switch (rc)
        {
            case 0:
                return null;
            case -1:
                ToUnicodeEx(vkCode, scanCode, keyState, sb, sb.Capacity, 0, layout);
                return null;
            default:
                return sb.ToString(0, rc);
        }
    }

    /// <summary>
    /// Возвращает напечатанный символ или null, если клавиша не даёт печатного
    /// символа (модификаторы, функциональные клавиши, мёртвые клавиши).
    /// </summary>
    public static string? Translate(uint vkCode, uint scanCode, IntPtr layout)
    {
        var keyState = new byte[256];

        // Модификаторы берём из актуального состояния клавиатуры.
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) keyState[VK_SHIFT] = 0x80;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) keyState[VK_CONTROL] = 0x80;
        if ((GetKeyState(VK_MENU) & 0x8000) != 0) keyState[VK_MENU] = 0x80;
        if ((GetKeyState(0xA5) & 0x8000) != 0) keyState[0xA5] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0) keyState[VK_CAPITAL] = 0x01;

        var sb = new StringBuilder(8);
        int rc = ToUnicodeEx(vkCode, scanCode, keyState, sb, sb.Capacity, 0, layout);

        switch (rc)
        {
            case 0:
                return null; // нет печатного символа
            case -1:
                // Мёртвая клавиша: повторный вызов, чтобы не испортить состояние.
                ToUnicodeEx(vkCode, scanCode, keyState, sb, sb.Capacity, 0, layout);
                return null;
            default:
                return sb.ToString(0, rc);
        }
    }
}
