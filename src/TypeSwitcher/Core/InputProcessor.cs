using System.Windows.Threading;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Core;

/// <summary>
/// Автозамена слова. Window - активное окно на момент решения: если к моменту инъекции
/// оно сменилось, backspace ушли бы в чужой текст - исполнитель замену отменяет.
/// </summary>
public sealed record ReplacementRequest(int Backspaces, string Text, ActionKind Kind, LayoutTarget Layout = LayoutTarget.None, int TrailingVk = 0, string Original = "", string Corrected = "", IntPtr Window = default, IntPtr TargetHkl = default);

/// <summary>
/// Переключение между исправленным и исходным вариантом по хоткею (Pause).
/// Backspaces - сколько стереть, Text - что впечатать, ActivateLayout - на какую
/// раскладку переключиться (None - не трогать), TrailingVk - дожать Enter/Tab,
/// Window - активное окно на момент решения (см. ReplacementRequest).
/// </summary>
public sealed record ToggleRequest(int Backspaces, string Text, LayoutTarget ActivateLayout, int TrailingVk = 0, IntPtr Window = default, IntPtr ActivateHkl = default);
public readonly record struct WordContext(string Word, LayoutTarget Language);

/// <summary>
/// Связывает поток нажатий с буфером слова и анализатором. На границе слова
/// (пробел/знак препинания) запускает анализ и, если нужна замена, подавляет
/// граничную клавишу и планирует инъекцию через Dispatcher.
///
/// Pause циклически переключает последнюю правку: исправленное -> исходное ->
/// исправленное, пока пользователь не начал печатать дальше. Уход на исходный
/// вариант подавляет слово (не трогать), возврат на исправленный - снимает.
///
/// Защита от зацикливания: повторный ручной набор того же исходного слова сразу
/// после правки трактуется как отказ - слово заносится в "не трогать".
/// </summary>
public sealed class InputProcessor
{
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;
    private const int VK_TAB = 0x09;

    /// <summary>VK хоткея отката/смены раскладки (настраивается). По умолчанию Pause.</summary>
    public int HotkeyVk { get; set; } = 0x13;

    /// <summary>VK хоткея вызова окна слова (настраивается). По умолчанию ScrollLock. 0 - выкл.</summary>
    public int SuggestHotkeyVk { get; set; } = 0x91;

    /// <summary>VK хоткея смены регистра последнего слова (0 - выкл) и нужен ли Shift.
    /// По умолчанию Shift+Pause: проверяется раньше хоткея отката, поэтому Pause без
    /// Shift остаётся откатом/сменой раскладки.</summary>
    public int CaseHotkeyVk { get; set; } = 0x13;
    public bool CaseHotkeyShift { get; set; } = true;

    private readonly WordBuffer _buffer = new();
    private readonly Analyzer _analyzer;
    private readonly Dispatcher? _dispatcher;

    // Состояние последней правки для переключения по Pause.
    private sealed class ToggleState
    {
        public required string Original;     // как набрал пользователь
        public required string Corrected;    // что подставила программа
        public required string Separator;    // печатная граница (пробел/знак), "" для Enter/Tab
        public required int SepVk;           // VK границы для Enter/Tab (0 если печатная)
        public required ActionKind Kind;
        public required LayoutTarget Target; // раскладка исправленного варианта (для Layout)
        public IntPtr OriginalHkl;
        public IntPtr CorrectedHkl;
        public string OriginalSeparator = "";
        public bool ShowingCorrected;        // текущий видимый вариант
        public bool Counted;                 // откат по этому исправлению уже учтён

        // Длина "следа" границы в символах (для backspace при переключении).
        public int SepFootprint => Separator.Length + (SepVk != 0 ? 1 : 0);
    }

    private ToggleState? _toggle;
    private bool _toggleAvailable;

    // Для детекта ручного отката: исходное слово последней правки, длина
    // исправленного варианта и сколько backspace нажато после правки. Откатом
    // (одноразовый пропуск) считаем только если правка реально стёрта и слово
    // перенабрано. Постоянной памяти НЕТ - правки продолжаются всегда.
    private string? _recentCorrectedOriginal;
    private int _recentCorrectedLen;
    private int _deletesSinceCorrection;

    // Последнее слово БЕЗ автоправки - доступно принудительное переключение раскладки.
    private string? _forceWord;
    private string? _forceSep;
    private int _forceSepVk;
    private bool _forceAvailable;

    // Снимок "сырого" сегмента последнего завершённого слова (после границы живой
    // сегмент сброшен). Нужен для перебивки по хоткею слов со знаками-буквами
    // (',' -> 'б'), которые буквенный буфер теряет: ',fylbn' -> 'бандит'.
    private readonly List<SegKey> _forceSegment = new();
    private IntPtr _forceSegHkl = IntPtr.Zero;

    // Последнее завершённое слово, КАК ОНО НА ЭКРАНЕ (после правки - исправленное), и
    // разделители после него - для смены регистра хоткеем. Разделители копятся, как у
    // force ("слово, "); сбрасывается в начале нового слова и при уходе каретки.
    private string? _caseWord;
    private string _caseSep = string.Empty;
    private int _caseSepVk;

    // Последнее завершённое слово (исходная форма) - для ручного вызова окна слова.
    private string? _lastWord;
    private LayoutTarget _lastWordLanguage;

    // "Сырой" сегмент клавиш текущего набора (до пробела/Enter) - для перебивки
    // раскладки по хоткею с символами/знаками. Перевод через реальную раскладку.
    private readonly record struct SegKey(uint Vk, uint Scan, bool Shift, bool Caps, bool AltGr);
    private readonly List<SegKey> _segment = new();
    private IntPtr _segHkl = IntPtr.Zero;

    // Пользователь вручную переложил сырой сегмент по хоткею (Pause) до пробела.
    // Тогда авто-детект раскладки на границе НЕ должен переопределять его выбор
    // (иначе после ручного возврата на ghbdtn пробел снова делает привет).
    private bool _segmentManualLayout;

    // Сегмент переполнился (>64 клавиш) - непригоден до следующей границы/сброса.
    private bool _segmentOverflow;

    /// <summary>Раскладки ru/en (HKL) для перебивки сырого сегмента. Задаёт App.</summary>
    public IntPtr RuLayout { get; set; }
    public IntPtr EnLayout { get; set; }
    public IntPtr UkLayout { get; set; }

    private IntPtr LayoutFor(LayoutTarget language) => language switch
    {
        LayoutTarget.Ru => RuLayout,
        LayoutTarget.En => EnLayout,
        LayoutTarget.Uk => UkLayout,
        _ => IntPtr.Zero
    };

    private IEnumerable<IntPtr> OtherLayouts(IntPtr current) => _analyzer.LanguageOrder
        .Select(LayoutFor).Where(hkl => hkl != IntPtr.Zero && !SameLang(hkl, current));

    private IntPtr NextLayout(IntPtr current)
    {
        var order = _analyzer.LanguageOrder.Select(LayoutFor).Where(h => h != IntPtr.Zero).ToArray();
        if (order.Length == 0) return IntPtr.Zero;
        int index = Array.FindIndex(order, h => SameLang(h, current));
        IntPtr next = order[(index + 1) % order.Length];
        return SameLang(next, current) ? IntPtr.Zero : next;
    }

    // Тестовые швы: перевод клавиш, текущая раскладка и модификаторы. Боевые значения -
    // Win32 (KeyTranslator/GetKeyState); SelfTest подменяет их фейковой раскладкой и
    // гоняет сценарии InputProcessor headless (грабли живут здесь, а не в Analyzer).
    public Func<IntPtr> CurrentLayout { get; set; } = KeyTranslator.GetForegroundLayout;
    public Func<uint, uint, IntPtr, string?> TranslateLive { get; set; } = KeyTranslator.Translate;
    public Func<uint, uint, IntPtr, bool, bool, bool, string?> TranslateWith { get; set; } = KeyTranslator.TranslateLayout;
    public Func<bool> AltGrHeld { get; set; } = () => (NativeMethods.GetKeyState(0xA5) & 0x8000) != 0;
    public Func<bool> WindowsKeyHeld { get; set; } = () => (NativeMethods.GetKeyState(0x5B) & 0x8000) != 0 || (NativeMethods.GetKeyState(0x5C) & 0x8000) != 0;
    public Func<(bool Shift, bool Caps)> Modifiers { get; set; } = ()
        => ((NativeMethods.GetKeyState(0x10) & 0x8000) != 0, (NativeMethods.GetKeyState(0x14) & 0x0001) != 0);

    /// <summary>Зажат Ctrl, Alt или Win - нажатие является командой, не текстом.</summary>
    public Func<bool> CommandChordHeld { get; set; } = () =>
        (NativeMethods.GetKeyState(0x11) & 0x8000) != 0     // Ctrl
        || (NativeMethods.GetKeyState(0x12) & 0x8000) != 0  // Alt
        || (NativeMethods.GetKeyState(0x5B) & 0x8000) != 0  // LWin
        || (NativeMethods.GetKeyState(0x5C) & 0x8000) != 0; // RWin

    /// <summary>Активное окно (для сброса состояния при его смене).</summary>
    public Func<IntPtr> ForegroundWindow { get; set; } = NativeMethods.GetForegroundWindow;

    // Исключения (опционально): чёрный список приложений и поля паролей.
    private readonly Interop.ExclusionManager? _exclusions;
    private IntPtr _lastForeground = IntPtr.Zero;
    private bool _foregroundExcluded;

    /// <summary>Пропускать поля паролей (применяется на лету из настроек).</summary>
    public bool SkipPasswordFields { get; set; }

    public bool Enabled { get; set; } = true;

    public event Action<ReplacementRequest>? ReplacementRequested;
    public event Action<ToggleRequest>? ToggleRequested;

    /// <summary>Пользователь откатил правку слова (исправленное -> исходное).</summary>
    public event Action<WordContext>? WordRejected;

    /// <summary>Запрос показать окно слова вручную (второй хоткей).</summary>
    public event Action<WordContext>? SuggestRequested;

    public InputProcessor(Analyzer analyzer, Dispatcher? dispatcher,
        Interop.ExclusionManager? exclusions = null, bool skipPasswordFields = false)
    {
        _analyzer = analyzer;
        _dispatcher = dispatcher;
        _exclusions = exclusions;
        SkipPasswordFields = skipPasswordFields;
    }

    // Эмиссия событий: в бою - через Dispatcher (замена не в потоке хука), в headless
    // SelfTest (без цикла сообщений Dispatcher = null) - синхронно.
    private void Post(Action action)
    {
        // Normal, а не Background: инъекция должна уйти сразу после возврата из хука,
        // иначе клавиши, набранные в промежутке, попадут под наши backspace.
        if (_dispatcher != null) _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        else action();
    }

    /// <summary>
    /// Сбросить накопленное слово и сырой сегмент (каретку переместили мышью или
    /// сменилось активное окно - буфер больше не соответствует позиции каретки).
    /// </summary>
    public void ResetWordState()
    {
        _buffer.Reset();
        ResetSegment();
        ResetForce();
        ResetCase();
        _toggleAvailable = false;
        _recentCorrectedOriginal = null;
    }

    /// <summary>
    /// Исключения изменились (чёрный список в настройках): пересчитать их для активного
    /// окна на следующем нажатии, не дожидаясь смены окна.
    /// </summary>
    public void RefreshExclusions()
    {
        _lastForeground = IntPtr.Zero;
        ResetWordState();
    }

    // Чёрный список или процесс с уровнем целостности выше нашего: UIPI не пропустит
    // туда SendInput, а подавленную границу вернуть будет уже нечем.
    private bool IsExcludedWindow(IntPtr fg)
    {
        if (_exclusions == null) return false;
        if (_exclusions.IsOwnProcessWindow(fg)) return true; // редакторы настроек и захват хоткеев
        if (_exclusions.IsBlacklistedApp(fg)) return true;
        if (_exclusions.IsAboveOwnIntegrity(fg))
        {
            Log.Line("окно процесса с правами выше наших - автозамена отключена для него");
            return true;
        }
        return false;
    }

    public void OnKey(object? sender, KeyboardHook.KeyArgs e)
    {
        if (!e.IsKeyDown) return;
        if (!Enabled) { ResetWordState(); return; }

        // Сами модификаторы (Shift/Ctrl/Alt/Win/CapsLock) состояние не меняют.
        if (IsModifier(e.VkCode)) return;

        // Смена активного окна - ДО хоткеев: буфер, сегмент, force и откат относятся
        // к прежнему окну, и Pause не должен впечатывать их в новое (Alt+Tab, Win+N,
        // переключение без клика мышью - хук мыши этого не видит).
        IntPtr fg = ForegroundWindow();
        if (fg != _lastForeground)
        {
            _lastForeground = fg;
            _foregroundExcluded = IsExcludedWindow(fg);
            ResetWordState();
        }

        // Исключённое окно (чёрный список, процесс с правами выше наших) - ДО хоткеев:
        // там не подавляем ни одной клавиши и ничего не впечатываем.
        if (_foregroundExcluded) { ResetWordState(); return; }
        IntPtr inputLayout = CurrentLayout();
        if (LanguageProfile.FromLayout(inputLayout) == LayoutTarget.None) { ResetWordState(); return; }
        bool characterKey = e.VkCode is >= 0x30 and <= 0x5A or >= 0xBA and <= 0xE2;
        bool altGrText = characterKey && LanguageProfile.FromLayout(inputLayout) == LayoutTarget.Uk && AltGrHeld() && !WindowsKeyHeld();
        if (CommandChordHeld() && !altGrText)
        {
            ResetWordState();
            _exclusions?.InvalidateFocusCache();
            return;
        }

        // Второй хоткей: вызвать окно слова вручную для последнего слова.
        if (!altGrText && SuggestHotkeyVk != 0 && e.VkCode == (uint)SuggestHotkeyVk)
        {
            if (SkipPasswordFields && _exclusions != null && _exclusions.IsPasswordThorough()) { ResetWordState(); return; }
            if (TrySuggest()) e.Suppress = true;
            return;
        }

        // Хоткей регистра (Shift+Pause) - раньше отката: у них одна клавиша по умолчанию.
        if (!altGrText && CaseHotkeyVk != 0 && e.VkCode == (uint)CaseHotkeyVk && Modifiers().Shift == CaseHotkeyShift)
        {
            if (SkipPasswordFields && _exclusions != null && _exclusions.IsPasswordThorough())
            {
                ResetWordState();
                return;
            }
            if (TryChangeCase()) e.Suppress = true;
            return;
        }

        // Хоткей Pause (контекстно): если по слову была автоправка - переключаем её;
        // иначе перебиваем раскладку сырого сегмента (буквы+знаки), затем - запасной
        // вариант по последнему завершённому слову. В поле пароля - ничего: перекладка
        // впечатала бы (и записала в debug-лог) набранный пароль. Проверка полная (UIA),
        // браузерное поле пароля быстрый детект может не увидеть; Pause жмут редко.
        if (!altGrText && e.VkCode == (uint)HotkeyVk)
        {
            if (SkipPasswordFields && _exclusions != null && _exclusions.IsPasswordThorough())
            {
                ResetWordState();
                return;
            }
            if (TryToggle() || TrySegmentRemap() || TryForceLayout()) e.Suppress = true;
            return;
        }

        // Любой другой ввод фиксирует ОТКАТ - его хоткей больше недоступен. А вот
        // force (ручная перекладка завершённого слова) намеренно НЕ гасим здесь:
        // он должен пережить хвост разделителей ("слово, " / "слово  "), иначе Pause
        // после знака+пробела не срабатывает. Гасим force только в начале нового слова.
        _toggleAvailable = false;

        // Быстрый детект поля пароля (нативный ES_PASSWORD или флаг из событий фокуса
        // UIA). В исключённом контексте не трогаем и не логируем ввод.
        if (SkipPasswordFields && _exclusions != null && _exclusions.IsPasswordFast())
        {
            ResetTyping();
            ResetCase();
            if (e.VkCode == VK_TAB || e.VkCode == VK_RETURN) _exclusions.InvalidateFocusCache(); // фокус уходит
            return;
        }

        // Сочетания с Ctrl/Alt/Win - команды, не текст. ToUnicodeEx при зажатом Alt
        // всё равно возвращает символ (табуляцию для Alt+Tab, перевод строки для
        // Alt+Enter, буквы для Alt+F), поэтому без этой отсечки Alt+Tab с невалидным
        // словом в буфере срабатывал как граница: правка улетала в чужое окно, а сам
        // Alt+Tab глушился подавлением клавиши. Ctrl+Tab, Alt+Tab и подобные уводят
        // фокус - кешированный ответ "не пароль" больше не действителен.

        if (e.VkCode == VK_BACK)
        {
            if (_buffer.IsEmpty) ResetCase(); // стирают хвост завершённого слова
            _buffer.Backspace();
            _deletesSinceCorrection++;
            if (_segment.Count > 0) _segment.RemoveAt(_segment.Count - 1);
            if (_segment.Count == 0) { _segHkl = IntPtr.Zero; _segmentManualLayout = false; }
            return;
        }

        IntPtr layout = inputLayout;
        LayoutTarget sourceLanguage = LanguageProfile.FromLayout(layout);
        if (_segHkl != IntPtr.Zero && !SameLang(_segHkl, layout) && !_segmentManualLayout)
        {
            // Смешанный ввод анализируется по буквам; прежний физический прогон уже неоднороден.
            ResetSegment();
            _segmentOverflow = true;
        }
        // Отдельные нажатия НЕ логируются даже в debug-режиме: на этом шаге полная
        // (UIA) проверка поля пароля ещё не выполнена, и символы браузерного пароля
        // оказались бы в typeswitcher_debug.log. Слово пишется в лог только на границе,
        // после проверки.
        string? ch = TranslateLive(e.VkCode, e.ScanCode, layout);

        if (ch == null)
        {
            // Стрелки, Home/End, Delete, F-клавиши: каретка ушла - набор устарел.
            ResetTyping();
            ResetForce();
            ResetCase();
            return;
        }
        if (ch.Length != 1) { ResetWordState(); return; }

        bool internalApostrophe = ch.Length == 1 && LanguageProfile.IsApostrophe(ch[0]) && !_buffer.IsEmpty
            && sourceLanguage is LayoutTarget.Uk or LayoutTarget.En;
        bool combining = ch.Length == 1 && LanguageProfile.IsCombining(ch[0]) && !_buffer.IsEmpty;
        if (combining) { ResetSegment(); _segmentOverflow = true; ResetForce(); ResetCase(); }
        if (ch.Length == 1 && ((char.IsLetterOrDigit(ch[0]) && !LanguageProfile.IsApostrophe(ch[0])) || internalApostrophe || combining))
        {
            // Первый символ нового слова - прежний force (по завершённому слову) устарел.
            if (_buffer.IsEmpty) { ResetForce(); ResetCase(); }
            _buffer.Append(ch);
            RecordSeg(e.VkCode, e.ScanCode);
            return;
        }

        // Граница слова.
        string word = _buffer.Current;
        _buffer.Reset();

        // Снимок сырого сегмента слова ДО сброса/добавления знака-границы - для
        // перебивки раскладки по хоткею уже после границы (слова со знаками-буквами).
        var segSnapshot = new List<SegKey>(_segment);
        IntPtr segSnapshotHkl = _segHkl != IntPtr.Zero ? _segHkl : layout;

        char c0 = ch[0];
        bool isSpace = c0 == ' ';
        bool isEnterTab = c0 == '\r' || c0 == '\n' || c0 == '\t';
        bool isPunct = ch.Length == 1 && !char.IsControl(c0) && !isSpace;
        if (!isSpace && !isEnterTab && !isPunct) return; // прочие управляющие - флаш

        // Граница печатная (пробел/знак) или Enter/Tab. Для Enter/Tab Unicode-инъекция
        // ненадёжна - впечатываем их как нажатие клавиши (VK).
        string sepText;
        int sepVk;
        if (isSpace) { sepText = " "; sepVk = 0; }
        else if (isPunct) { sepText = ch; sepVk = 0; }
        else if (c0 == '\t') { sepText = ""; sepVk = VK_TAB; }
        else { sepText = ""; sepVk = VK_RETURN; }

        // Широкий детект поля пароля (UIA, браузеры/Electron) - ДО любого действия и
        // логирования: ветки сырого прогона (правило/перекладка ',fylbn') и loop-guard
        // ниже тоже эмитят события, поэтому проверка стоит перед ними, а не только
        // перед Analyze. Дёшево: результат кешируется (ExclusionManager).
        if (SkipPasswordFields && _exclusions != null)
        {
            bool isPassword = (word.Length > 0 || _segment.Count > 0) && _exclusions.IsPasswordThorough();

            // Tab/Enter обычно уводят фокус (логин -> пароль, отправка формы): ответ,
            // полученный сейчас, описывает покидаемое поле. Без сброса кеша пароль,
            // набранный быстрее TTL после Tab из поля логина, считался бы обычным словом.
            if (isEnterTab) _exclusions.InvalidateFocusCache();

            if (isPassword)
            {
                Log.Line("boundary: поле пароля (UIA) - пропуск");
                ResetSegment();
                ResetForce();
                ResetCase();
                return;
            }
        }

        if (isSpace || isEnterTab)
        {
            if (TryRawBoundary(e, word, sepText, sepVk, layout)) return;
        }
        else
        {
            RecordSeg(e.VkCode, e.ScanCode); // знак - часть набора
        }

        if (word.Length == 0)
        {
            // Слова нет (граница после знака/пробела), но force по предыдущему слову
            // взведён - "приклеиваем" разделитель к его хвосту. Тогда Pause переложит
            // слово, стерев "слово+знак+пробел" и впечатав переложенное+те же разделители.
            // Это и чинит отказ хоткея после "слово, " / "слово  " / "слово5 ".
            if (_forceAvailable && _forceSep != null && (isSpace || isPunct) && _forceSep.Length < 32)
                _forceSep += sepText;
            if (_caseWord != null)
            {
                // Хвост после Enter/Tab одной строкой уже не повторить - регистр этого
                // слова больше не трогаем.
                if ((isSpace || isPunct) && _caseSepVk == 0 && _caseSep.Length < 32) _caseSep += sepText;
                else ResetCase();
            }
            return;
        }
        _lastWord = word; // запомнить для ручного вызова окна слова
        _lastWordLanguage = sourceLanguage;

        // Детект ручного отката: пользователь СТЁР наше исправление и перенабрал то
        // же исходное слово -> на этот раз не исправляем (одноразово, без памяти).
        // Просто повторный набор того же слова без стирания правки отказом не считаем.
        if (_recentCorrectedOriginal != null)
        {
            string prev = _recentCorrectedOriginal;
            int prevLen = _recentCorrectedLen;
            int deletes = _deletesSinceCorrection;
            _recentCorrectedOriginal = null;
            if (string.Equals(word, prev, StringComparison.OrdinalIgnoreCase) && deletes >= prevLen)
            {
                Log.Line($"loop-guard: '{word}' стёрто и перенабрано -> пропуск (одноразово)");
                // Стирание правки и ручной перенабор - это тоже отказ от автозамены.
                // Учитываем для обучающего окна (иначе счётчик копится лишь по Pause).
                WordRejected?.Invoke(new(word.ToLowerInvariant(), sourceLanguage));
                return;
            }
        }

        Decision decision = _analyzer.Analyze(word, sourceLanguage);

        // Однобуквенное правило на границе-ЗНАКЕ не применяем: одиночная буква - префикс
        // многих слов, а знак может быть знаком-буквой, продолжающей слово. 'вэб' на EN =
        // клавиши d ' , ('э'=апостроф-граница); буфер 'd' матчил правило d->в и оставлял
        // апостроф -> 'в''. На пробеле/Enter такие правила отрабатывают через сырой прогон
        // (в->в цело). Многобуквенные ключи не трогаем - 2+ буквы редко префикс слова.
        if (decision.Kind == ActionKind.Rule && isPunct && word.Length == 1)
        {
            Log.Line($"rule '{word}' на знаке-границе подавлено (однобуквенное)");
            decision = Decision.None;
        }

        // Правило-перекладка (ключ в текущей раскладке, результат - буквы противоположной)
        // на границе-знаке-букве не применяем по той же причине, что и layout-решение ниже:
        // человек печатает в чужой раскладке, и знак - буква, продолжающая слово.
        // 'избранное' на EN = 'bp,hfyyjt', правило 'bp=из' срабатывало на ',' и давало
        // 'из,hfyyjt'. Целое слово чинит сырой прогон на пробеле/Enter; там же правило
        // сработает, если ключ набран целиком ('bp '). Правила в той же письменности
        // ('thx=thanks' перед ',') не трогаем - там знак задуман знаком.
        if (decision.Kind == ActionKind.Rule && isPunct && IsLetterKeyInOppositeLayout(e, layout)
            && IsOppositeScript(decision.Replacement, layout))
        {
            Log.Line($"rule '{word}' на знаке-букве подавлено (целое слово чинит сырой прогон)");
            decision = Decision.None;
        }

        // Layout-решение на границе-знаке-букве не применяем: клавиша знака в
        // противоположной раскладке даёт букву (';'='ж', ','='б', '.'='ю'...), т.е.
        // при наборе в чужой раскладке это почти наверняка продолжение слова, а буфер
        // видит только префикс: 'продолжить' = 'ghjljk;bnm', на ';' префикс 'ghjljk'
        // уходил в FixTypoOnSwitch и давал 'продал;ить'. Целое слово чинит сырой
        // прогон на пробеле/Enter. Задуманная пунктуация при наборе не в той раскладке
        // жмётся по позициям ЦЕЛЕВОЙ раскладки (',' = Shift+/ = '?'), а '?' не
        // знак-буква - для таких границ префикс-детект работает как раньше.
        if (decision.Kind == ActionKind.Layout && isPunct && IsLetterKeyInOppositeLayout(e, layout))
        {
            Log.Line($"layout '{word}' на знаке-букве подавлено (целое слово чинит сырой прогон)");
            decision = Decision.None;
        }

        // Слово перекладываем - значит знак-границу пользователь тоже жал в чужой
        // раскладке, и печатать его надо целевым рендером: 'ghbdtn/' - это 'привет.'
        // (клавиша '/' в русской раскладке даёт точку), а не 'привет/'. Знаки, у
        // которых рендер совпадает в обеих раскладках, этим не затрагиваются.
        if (decision.Kind == ActionKind.Layout && isPunct && decision.Layout != LayoutTarget.None)
        {
            IntPtr tgt = LayoutFor(decision.Layout);
            if (tgt != IntPtr.Zero)
            {
                var (shift, caps) = Modifiers();
                string? tgtSep = TranslateWith(e.VkCode, e.ScanCode, tgt, shift, caps, AltGrHeld());
                if (!string.IsNullOrEmpty(tgtSep) && tgtSep != sepText && !HasLetterOrDigit(tgtSep))
                {
                    Log.Line($"sep '{sepText}' -> '{tgtSep}' (рендер в целевой раскладке)");
                    sepText = tgtSep;
                }
            }
        }

        Log.Line($"boundary word='{word}' decision={decision.Kind} repl='{decision.Replacement}'");
        if (decision.Kind == ActionKind.None)
        {
            // Правки не было - слово доступно для ручного принудительного переключения раскладки.
            ArmForce(word, sepText, sepVk, segSnapshot, segSnapshotHkl);
            ArmCase(word, sepText, sepVk);
            return;
        }

        EmitCorrection(e, word, decision, sepText, sepVk, isPunct ? ch : sepText);
    }

    private bool TryRawBoundary(KeyboardHook.KeyArgs e, string word, string separator, int separatorVk, IntPtr layout)
    {
        var keys = new List<SegKey>(_segment);
        IntPtr sourceHkl = _segHkl != IntPtr.Zero ? _segHkl : layout;
        bool manual = _segmentManualLayout;
        string raw = RenderSeg(keys, sourceHkl);
        ResetSegment(); // в том числе сброс флага переполнения после 64 клавиш
        if (raw.Length == 0) return false;
        LayoutTarget source = LanguageProfile.FromLayout(sourceHkl);

        void KeepRaw()
        {
            _lastWord = raw;
            _lastWordLanguage = source;
            ArmForce(raw, separator, separatorVk, keys, sourceHkl);
            ArmCase(raw, separator, separatorVk);
        }

        if (manual || _analyzer.IsStopped(raw, source)) { KeepRaw(); return true; }
        Decision rule = _analyzer.MatchRule(raw, source);
        if (rule.Kind == ActionKind.Rule)
        {
            _lastWord = raw;
            _lastWordLanguage = source;
            EmitCorrection(e, raw, rule, separator, separatorVk);
            return true;
        }
        if (_analyzer.IsProtected(raw, source)) { KeepRaw(); return true; }
        if (raw == word) return false;

        var candidates = new List<Decision>();
        foreach (IntPtr targetHkl in OtherLayouts(sourceHkl))
        {
            LayoutTarget target = LanguageProfile.FromLayout(targetHkl);
            string rendered = RenderSeg(keys, targetHkl);
            string? replacement = null;
            if (_analyzer.IsLayoutSwap(raw, rendered, target, source)) replacement = rendered;
            else if (TryTailTrimSwap(keys, sourceHkl, targetHkl, raw, target, out string trimmed)) replacement = trimmed;
            else if (TrySplitRemap(keys, sourceHkl, targetHkl, raw, target, out string split)) replacement = split;
            if (replacement != null) candidates.Add(new(ActionKind.Layout, replacement, target, source, "raw-layout"));
        }
        if (candidates.Count > 1) { KeepRaw(); return true; }
        if (candidates.Count == 0) return false;
        _lastWord = raw;
        _lastWordLanguage = source;
        EmitCorrection(e, raw, candidates[0], separator, separatorVk);
        return true;
    }

    private void EmitCorrection(KeyboardHook.KeyArgs e, string original, Decision decision, string sepText, int sepVk, string? originalSeparator = null)
    {
        // Подавляем границу: стираем ровно символы исходника, границу впечатаем сами.
        e.Suppress = true;
        ResetSegment(); // текст изменили - сырой сегмент устарел

        var req = new ReplacementRequest(original.Length, decision.Replacement + sepText, decision.Kind, decision.Layout, sepVk, original, decision.Replacement, _lastForeground, LayoutFor(decision.Layout));

        _toggle = new ToggleState
        {
            Original = original,
            Corrected = decision.Replacement,
            Separator = sepText,
            SepVk = sepVk,
            Kind = decision.Kind,
            Target = decision.Layout,
            OriginalHkl = CurrentLayout(),
            CorrectedHkl = LayoutFor(decision.Layout),
            OriginalSeparator = originalSeparator ?? sepText,
            ShowingCorrected = true
        };
        _toggleAvailable = true;
        _recentCorrectedOriginal = original;
        _recentCorrectedLen = decision.Replacement.Length;
        _deletesSinceCorrection = 0;
        ArmCase(decision.Replacement, sepText, sepVk);

        Post(() => ReplacementRequested?.Invoke(req));
    }

    private bool TryToggle()
    {
        if (!_toggleAvailable || _toggle == null) return false;
        ToggleState t = _toggle;

        ToggleRequest req;
        if (t.ShowingCorrected)
        {
            // Исправленное -> исходное (откат). Считаем откат для обучающего окна.
            // Постоянного подавления здесь НЕТ: слово продолжает исправляться, пока
            // пользователь не сделает выбор в окне (ТЗ, раздел 9). Сессионное
            // подавление - только при ручном повторном наборе (loop-guard).
            req = new ToggleRequest(
                Backspaces: t.Corrected.Length + t.SepFootprint,
                Text: t.Original + t.OriginalSeparator,
                ActivateLayout: t.Kind == ActionKind.Layout ? LanguageProfile.FromLayout(t.OriginalHkl) : LayoutTarget.None,
                TrailingVk: t.SepVk,
                Window: _lastForeground,
                ActivateHkl: t.Kind == ActionKind.Layout ? t.OriginalHkl : IntPtr.Zero);
            if (!t.Counted)
            {
                t.Counted = true;
                WordRejected?.Invoke(new(t.Original.ToLowerInvariant(), LanguageProfile.FromLayout(t.OriginalHkl)));
            }
        }
        else
        {
            // Исходное -> исправленное.
            req = new ToggleRequest(
                Backspaces: t.Original.Length + t.OriginalSeparator.Length + (t.SepVk != 0 ? 1 : 0),
                Text: t.Corrected + t.Separator,
                ActivateLayout: t.Kind == ActionKind.Layout ? t.Target : LayoutTarget.None,
                TrailingVk: t.SepVk,
                Window: _lastForeground,
                ActivateHkl: t.Kind == ActionKind.Layout ? t.CorrectedHkl : IntPtr.Zero);
        }

        t.ShowingCorrected = !t.ShowingCorrected;
        _recentCorrectedOriginal = null;
        ArmCase(t.ShowingCorrected ? t.Corrected : t.Original, t.ShowingCorrected ? t.Separator : t.OriginalSeparator, t.SepVk);
        Log.Line($"toggle -> {(t.ShowingCorrected ? "исправленное" : "исходное")} '{req.Text}'");

        Post(() => ToggleRequested?.Invoke(req));
        return true;
    }

    private void ArmForce(string word, string sep, int sepVk, List<SegKey> segSnapshot, IntPtr segHkl)
    {
        bool letters = LayoutMap.IsAllLatinLetters(word) || LayoutMap.IsAllCyrillic(word);

        // Сырой сегмент пригоден для перебивки, если его рендер в текущей и в
        // противоположной раскладке различается - значит в нём есть что
        // перекладывать (буквы, в т.ч. знаки-буквы вроде запятой ',fylbn').
        // Через реальную раскладку проходят и цифры/знаки (переносятся как есть),
        // поэтому слова со знаками препинания и цифрами ('привет5') тоже взводятся -
        // буквенная таблица LayoutMap их отбрасывала.
        bool segUsable = false;
        if (segSnapshot.Count > 0)
        {
            segUsable = OtherLayouts(segHkl).Any(target => RenderSeg(segSnapshot, segHkl) != RenderSeg(segSnapshot, target));
        }

        if (!letters && !segUsable) return;

        _forceWord = word;
        _forceSep = sep;
        _forceSepVk = sepVk;
        _forceAvailable = true;
        _forceSegment.Clear();
        if (segUsable)
        {
            _forceSegment.AddRange(segSnapshot);
            _forceSegHkl = segHkl;
        }
        else
        {
            _forceSegHkl = IntPtr.Zero;
        }
    }

    private bool TryForceLayout()
    {
        // Сначала - текущее НЕЗАВЕРШЁННОЕ слово в буфере (хоть 1 символ, без пробела).
        if (!_buffer.IsEmpty)
        {
            string w = _buffer.Current;
            if (!TryRemap(w, out string remapped, out LayoutTarget target)) return false;

            _buffer.Reset();
            _buffer.Append(remapped); // буфер следует за текстом для дальнейшего набора
            EmitForce(w.Length, remapped, target, 0);
            return true;
        }

        // Иначе - последнее ЗАВЕРШЁННОЕ слово (с его границей).
        if (_forceAvailable && _forceWord != null && _forceSep != null)
        {
            string sep = _forceSep;
            int footprint = sep.Length + (_forceSepVk != 0 ? 1 : 0);

            // Слово со знаком-буквой (',fylbn') - перебиваем весь сырой сегмент через
            // реальную раскладку; буквенная таблица LayoutMap запятую не знает.
            if (_forceSegment.Count > 0)
            {
                IntPtr cur = _forceSegHkl != IntPtr.Zero ? _forceSegHkl : CurrentLayout();
                string curStr = RenderSeg(_forceSegment, cur);
                IntPtr target = NextLayout(cur);
                if (target == IntPtr.Zero) return false;
                string tgtStr = RenderSeg(_forceSegment, target);
                if (curStr.Length == 0 || tgtStr.Length == 0) return false;

                LayoutTarget lt = LanguageProfile.FromLayout(target);
                _forceSegHkl = target; // перезарядка: повторный Pause перебьёт обратно
                if (_caseWord != null) _caseWord = tgtStr;
                if (curStr == tgtStr) EmitForce(0, "", lt, 0);
                else EmitForce(curStr.Length + footprint, tgtStr + sep, lt, _forceSepVk);
                return true;
            }

            string w = _forceWord;
            if (!TryRemap(w, out string remapped, out LayoutTarget target2)) return false;
            _forceWord = remapped; // перезарядка: повторный Pause перебьёт обратно
            if (_caseWord != null) _caseWord = remapped;
            EmitForce(w.Length + footprint, remapped + sep, target2, _forceSepVk);
            return true;
        }

        return false;
    }

    private void RecordSeg(uint vk, uint scan)
    {
        if (_segmentOverflow) return;
        if (_segment.Count == 0) _segHkl = CurrentLayout();
        var (shift, caps) = Modifiers();
        _segment.Add(new SegKey(vk, scan, shift, caps, AltGrHeld()));
        if (_segment.Count > 64)
        {
            // Молча обрезанное начало давало бы кривую перебивку/детект по половине
            // прогона - сегмент помечается непригодным до следующей границы/сброса.
            _segment.Clear();
            _segHkl = IntPtr.Zero;
            _segmentOverflow = true;
        }
    }

    /// <summary>Сбросить текущий набор (буфер слова + сырой сегмент), force не трогать.</summary>
    private void ResetTyping()
    {
        _buffer.Reset();
        ResetSegment();
    }

    private void ResetSegment()
    {
        _segment.Clear();
        _segHkl = IntPtr.Zero;
        _segmentManualLayout = false;
        _segmentOverflow = false;
    }

    /// <summary>Сбросить взведённую ручную перекладку (force) завершённого слова.</summary>
    private void ResetForce()
    {
        _forceAvailable = false;
        _forceWord = null;
        _forceSep = null;
        _forceSepVk = 0;
        _forceSegment.Clear();
        _forceSegHkl = IntPtr.Zero;
    }

    private void ArmCase(string screenWord, string sep, int sepVk)
    {
        _caseWord = screenWord;
        _caseSep = sep;
        _caseSepVk = sepVk;
    }

    private void ResetCase()
    {
        _caseWord = null;
        _caseSep = string.Empty;
        _caseSepVk = 0;
    }

    /// <summary>
    /// Смена регистра по хоткею: набираемое слово (буфер) или последнее завершённое с его
    /// разделителями. По кругу: строчные -> ПРОПИСНЫЕ -> Первая прописная -> строчные.
    /// Откат и force после этого недоступны - их состояние описывает прежний текст.
    /// </summary>
    private bool TryChangeCase()
    {
        if (!_buffer.IsEmpty)
        {
            string w = _buffer.Current;
            if (w.Any(LanguageProfile.IsCombining)) return false;
            string? next = CaseHelper.NextCase(w);
            if (next == null) return false;
            _buffer.Reset();
            _buffer.Append(next);
            DropAfterCaseChange();
            EmitForce(w.Length, next, LayoutTarget.None, 0);
            return true;
        }

        if (_caseWord == null) return false;
        if (_caseWord.Any(LanguageProfile.IsCombining)) return false;
        string? nw = CaseHelper.NextCase(_caseWord);
        if (nw == null) return false;
        int footprint = _caseSep.Length + (_caseSepVk != 0 ? 1 : 0);
        int backspaces = _caseWord.Length + footprint;
        _caseWord = nw;
        DropAfterCaseChange();
        EmitForce(backspaces, nw + _caseSep, LayoutTarget.None, _caseSepVk);
        return true;
    }

    private void DropAfterCaseChange()
    {
        _toggleAvailable = false;
        _recentCorrectedOriginal = null;
        ResetForce();
        ResetSegment();
    }

    private bool TrySegmentRemap()
    {
        if (_segment.Count == 0) return false;

        IntPtr cur = _segHkl != IntPtr.Zero ? _segHkl : CurrentLayout();
        IntPtr target = NextLayout(cur);
        if (target == IntPtr.Zero) return false;

        string curStr = Render(cur);
        string tgtStr = Render(target);
        if (tgtStr.Length == 0) return false;

        LayoutTarget lt = LanguageProfile.FromLayout(target);
        _segHkl = target;
        _segmentManualLayout = true; // выбор раскладки сделан вручную - граница его не тронет
        _buffer.Reset(); // буфер устарел после перебивки
        EmitForce(curStr == tgtStr ? 0 : curStr.Length, curStr == tgtStr ? "" : tgtStr, lt, 0);
        return true;
    }

    private string Render(IntPtr hkl) => RenderSeg(_segment, hkl);

    private string RenderSeg(List<SegKey> seg, IntPtr hkl)
    {
        var sb = new System.Text.StringBuilder(seg.Count);
        foreach (var k in seg)
        {
            string? s = TranslateWith(k.Vk, k.Scan, hkl, k.Shift, k.Caps, k.AltGr);
            if (string.IsNullOrEmpty(s)) return string.Empty; // непреобразуемую клавишу нельзя потерять
            sb.Append(s);
        }
        return sb.ToString();
    }

    // Клавиша в противоположной раскладке даёт букву (знак-буква: ';'='ж' и т.п.).
    private bool IsLetterKeyInOppositeLayout(KeyboardHook.KeyArgs e, IntPtr current)
    {
        return OtherLayouts(current).Any(target => TranslateWith(e.VkCode, e.ScanCode, target, false, false, false)
            is { Length: 1 } text && char.IsLetter(text[0]));
    }

    // Текст содержит буквы письменности противоположной раскладки (для EN - кириллицу).
    private bool IsOppositeScript(string text, IntPtr current)
    {
        bool oppIsRu = LanguageProfile.FromLayout(current) == LayoutTarget.En;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            bool cyr = (c >= 'Ѐ' && c <= 'ӿ');
            if (cyr == oppIsRu) return true;
        }
        return false;
    }

    /// <summary>
    /// Гипотеза "слово + задуманная пунктуация, набранные в чужой раскладке": хвост
    /// из 1-3 клавиш, дающих НЕ буквы/цифры, отрезается, ядро проверяется как смена
    /// раскладки. Две трактовки хвоста: по целевому рендеру ('ghjljk;bnm?' ->
    /// 'продолжить' + ',' - пунктуацию жали по позициям целевой раскладки) и, если
    /// не вышло, по текущему ('ghbdtn.' -> 'привет' + '.' - точка на клавише 'ю',
    /// в целевой раскладке это буква, но задумана точка). Ложные срабатывания на
    /// нормальном тексте гасит вето IsLayoutSwap: ядро должно быть валидно в
    /// целевой раскладке и НЕвалидно в текущей.
    /// </summary>
    private bool TryTailTrimSwap(List<SegKey> keys, IntPtr curHkl, IntPtr tgtHkl,
        string rawRun, LayoutTarget tgtLang, out string swapped)
    {
        swapped = string.Empty;
        if (keys.Count < 2) return false;

        // Хвост по целевому рендеру: заменяем целиком целевым рендером.
        if (TrimTail(keys, tgtHkl, out var core, out var tail))
        {
            string coreTgt = RenderSeg(core, tgtHkl);
            if (coreTgt.Length > 0 && _analyzer.IsLayoutSwap(RenderSeg(core, curHkl), coreTgt, tgtLang, LanguageProfile.FromLayout(curHkl)))
            {
                swapped = coreTgt + RenderSeg(tail, tgtHkl);
                return swapped != rawRun;
            }
        }

        // Хвост по текущему рендеру: ядро перекладываем, хвост оставляем как набран.
        if (TrimTail(keys, curHkl, out core, out tail))
        {
            string coreTgt = RenderSeg(core, tgtHkl);
            if (coreTgt.Length > 0 && _analyzer.IsLayoutSwap(RenderSeg(core, curHkl), coreTgt, tgtLang, LanguageProfile.FromLayout(curHkl)))
            {
                swapped = coreTgt + RenderSeg(tail, curHkl);
                return swapped != rawRun;
            }
        }

        return false;
    }

    /// <summary>
    /// Гипотеза "несколько слов со знаком между ними, набранные в чужой раскладке":
    /// 'ghbdtn,rfrjq' -> 'привет,какой'. Целиком такой прогон невалиден (в целевой
    /// раскладке запятая - буква 'б', получается 'приветбкакой'), а хвостовой
    /// TryTailTrimSwap не помогает - знак не в хвосте, а в середине.
    ///
    /// Режем прогон по клавишам, которые в ТЕКУЩЕЙ раскладке дают не букву и не
    /// цифру (в примере - запятая), и требуем, чтобы КАЖДЫЙ кусок длиной от
    /// layout.min_word_length прошёл то же вето IsLayoutSwap (валиден в целевой,
    /// невалиден в текущей), и чтобы такой кусок был хотя бы один. Короткие куски
    /// перекладываются заодно - весь прогон набран одной раскладкой.
    ///
    /// Порядок вызова важен: сначала целый прогон, потом хвост, и только затем это.
    /// Иначе 'ghjljk;bnm' ('продолжить', ';' = буква 'ж') разрезалось бы на
    /// 'продол' + 'ить' - обе части невалидны, вето отвергнет, но лишняя работа.
    /// </summary>
    private bool TrySplitRemap(List<SegKey> keys, IntPtr curHkl, IntPtr tgtHkl,
        string rawRun, LayoutTarget tgtLang, out string swapped)
    {
        swapped = string.Empty;
        if (keys.Count < 3) return false;

        // Куски (буквенные группы) и разделители между ними - в порядке набора.
        var chunks = new List<List<SegKey>>();
        var parts = new List<(bool IsChunk, string Text)>();
        var current = new List<SegKey>();
        foreach (var k in keys)
        {
            string? cur = TranslateWith(k.Vk, k.Scan, curHkl, k.Shift, k.Caps, k.AltGr);
            if (string.IsNullOrEmpty(cur) || !HasLetterOrDigit(cur))
            {
                if (current.Count > 0)
                {
                    chunks.Add(current);
                    parts.Add((true, RenderSeg(current, tgtHkl)));
                    current = new List<SegKey>();
                }
                // Разделитель пользователь видел знаком и знаком же задумывал. Целевой
                // рендер берём, только если он тоже знак ('/' в русской раскладке -
                // точка); если там буква (',' = 'б'), оставляем как набрано.
                string? tgt = TranslateWith(k.Vk, k.Scan, tgtHkl, k.Shift, k.Caps, k.AltGr);
                parts.Add((false, !string.IsNullOrEmpty(tgt) && !HasLetterOrDigit(tgt) ? tgt : cur ?? string.Empty));
                continue;
            }
            current.Add(k);
        }
        if (current.Count > 0)
        {
            chunks.Add(current);
            parts.Add((true, RenderSeg(current, tgtHkl)));
        }
        if (chunks.Count < 1 || parts.Count == chunks.Count) return false; // разделителя не было

        bool anyConfirmed = false;
        foreach (var chunk in chunks)
        {
            string chunkTgt = RenderSeg(chunk, tgtHkl);
            if (chunkTgt.Length < _analyzer.LayoutMinWordLength) continue;   // короткий - без вето
            if (!_analyzer.IsLayoutSwap(RenderSeg(chunk, curHkl), chunkTgt, tgtLang, LanguageProfile.FromLayout(curHkl))) return false;
            anyConfirmed = true;
        }
        if (!anyConfirmed) return false;

        var sb = new System.Text.StringBuilder();
        foreach (var (_, text) in parts) sb.Append(text);
        swapped = sb.ToString();
        return swapped.Length > 0 && swapped != rawRun;
    }

    // Отделяет от конца до 3 клавиш, дающих в раскладке hkl только НЕ буквы/цифры
    // (цифры не хвост: слова с цифрами - технические токены, их не перекладываем).
    // false, если таких клавиш нет или не остаётся ядра.
    private bool TrimTail(List<SegKey> keys, IntPtr hkl, out List<SegKey> core, out List<SegKey> tail)
    {
        int n = 0;
        while (n < 3 && n < keys.Count - 1)
        {
            var k = keys[keys.Count - 1 - n];
            string? s = TranslateWith(k.Vk, k.Scan, hkl, k.Shift, k.Caps, k.AltGr);
            if (string.IsNullOrEmpty(s) || HasLetterOrDigit(s)) break;
            n++;
        }
        if (n == 0) { core = tail = null!; return false; }
        core = keys.GetRange(0, keys.Count - n);
        tail = keys.GetRange(keys.Count - n, n);
        return true;
    }

    private static bool HasLetterOrDigit(string s)
    {
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    private static bool SameLang(IntPtr a, IntPtr b)
        => (a.ToInt64() & 0xFFFF) == (b.ToInt64() & 0xFFFF);

    private bool TrySuggest()
    {
        if (string.IsNullOrEmpty(_lastWord)) return false;
        string w = _lastWord;
        Log.Line($"suggest-hotkey: окно слова '{w}'");
        var context = new WordContext(w, _lastWordLanguage);
        Post(() => SuggestRequested?.Invoke(context));
        return true;
    }

    private bool TryRemap(string w, out string remapped, out LayoutTarget target)
    {
        if (w.Any(LanguageProfile.IsCombining)) { remapped = ""; target = LayoutTarget.None; return false; }
        IntPtr current = CurrentLayout();
        target = LanguageProfile.FromLayout(NextLayout(current));
        string? m = LayoutMap.MapBetween(w, LanguageProfile.FromLayout(current), target);
        if (m == null) { remapped = string.Empty; target = LayoutTarget.None; return false; }
        remapped = m;
        return true;
    }

    private void EmitForce(int backspaces, string text, LayoutTarget target, int trailingVk)
    {
        Log.Line($"force-layout: -{backspaces} +'{text}' ({target}) vk={trailingVk}");
        var req = new ToggleRequest(backspaces, text, target, trailingVk, _lastForeground, LayoutFor(target));
        Post(() => ToggleRequested?.Invoke(req));
    }

    private static bool IsModifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => true, // Shift
        0x11 or 0xA2 or 0xA3 => true, // Ctrl
        0x12 or 0xA4 or 0xA5 => true, // Alt
        0x14 => true,                 // CapsLock
        0x5B or 0x5C => true,         // Win
        _ => false
    };
}
