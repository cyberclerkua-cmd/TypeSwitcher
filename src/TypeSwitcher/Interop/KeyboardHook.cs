using static TypeSwitcher.Interop.NativeMethods;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Interop;

/// <summary>
/// Низкоуровневый глобальный хук клавиатуры (WH_KEYBOARD_LL).
/// Устанавливается из потока с циклом сообщений (UI-поток WPF) и выдаёт
/// события нажатий. Инъецированный ввод (наш собственный) помечен флагом
/// LLKHF_INJECTED и наружу не выдаётся - это защита от самозацикливания.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public sealed class KeyArgs : EventArgs
    {
        public uint VkCode { get; init; }
        public uint ScanCode { get; init; }
        public bool IsKeyDown { get; init; }
        public bool Injected { get; init; }

        /// <summary>Если обработчик выставит true - клавиша не дойдёт до приложения.</summary>
        public bool Suppress { get; set; }
    }

    /// <summary>Сырое событие нажатия/отпускания клавиши (без инъецированного ввода).</summary>
    public event EventHandler<KeyArgs>? KeyAction;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookHandle = IntPtr.Zero;

    public KeyboardHook()
    {
        // Делегат хранится в поле, иначе будет собран GC и хук "умрёт".
        _proc = HookCallback;
    }

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public void Install()
    {
        if (IsInstalled) return;
        // Для WH_KEYBOARD_LL hMod игнорируется, можно передать дескриптор модуля.
        IntPtr hMod = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException(L.T("Не удалось установить хук клавиатуры (SetWindowsHookEx вернул 0)."));
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    /// <summary>
    /// Переустановить хук. Начиная с Windows 7 система МОЛЧА снимает LL-хук, если его
    /// обработчик хоть раз не уложился в LowLevelHooksTimeout (по умолчанию 300 мс) -
    /// Hunspell.Suggest на медленной машине вполне может; после этого программа
    /// просто перестаёт видеть ввод до перезапуска. Периодическая переустановка
    /// (App, таймер) - стандартное лекарство; сама переустановка занимает микросекунды.
    /// </summary>
    public void Reinstall()
    {
        Uninstall();
        Install();
    }

    /// <summary>Самый долгий вызов обработчика (мс) с момента последнего чтения; для диагностики.</summary>
    public long TakeMaxCallbackMs() => Interlocked.Exchange(ref _maxCallbackMs, 0);
    private long _maxCallbackMs;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                bool injected = (data.flags & LLKHF_INJECTED) != 0
                                || (data.flags & LLKHF_LOWER_IL_INJECTED) != 0;

                if (!injected)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        var args = new KeyArgs
                        {
                            VkCode = data.vkCode,
                            ScanCode = data.scanCode,
                            IsKeyDown = isDown,
                            Injected = false
                        };
                        KeyAction?.Invoke(this, args);
                        if (args.Suppress)
                            return (IntPtr)1; // клавиша подавлена - дальше по цепочке не идёт
                    }
                    catch (Exception ex)
                    {
                        // Хук не должен падать из-за ошибки обработчика - проглатываем.
                        TypeSwitcher.Core.Log.Line($"HOOK callback exception: {ex}");
                    }
                    finally
                    {
                        long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        if (ms > _maxCallbackMs) _maxCallbackMs = ms;
                        if (ms >= 200) TypeSwitcher.Core.Log.Line($"HOOK: медленный обработчик {ms} мс (vk=0x{data.vkCode:X2}) - риск снятия хука системой");
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
