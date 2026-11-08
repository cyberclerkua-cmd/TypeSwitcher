using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using TypeSwitcher.Tray;
using TypeSwitcher.Localization;

namespace TypeSwitcher;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ConfigStore _store = null!;
    private AppConfig _cfg = null!;
    private Dictionaries _dict = null!;
    private Analyzer _analyzer = null!;
    private InputProcessor _processor = null!;
    private KeyboardHook _hook = null!;
    private MouseHook _mouseHook = null!;
    private Sounds _sounds = null!;
    private TrayApp _tray = null!;
    private LayoutSwitcher _layout = null!;
    private ExclusionManager _exclusions = null!;
    private SuggestionCounter _suggestions = null!;
    private LearnLog _learn = null!;
    private SuggestionWindow? _suggestionWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        L.SetLanguage(ConfigStore.PeekInterfaceLanguage());
        int languageIndex = Array.IndexOf(e.Args, "--language");
        string? language = languageIndex >= 0 && languageIndex + 1 < e.Args.Length ? e.Args[languageIndex + 1].ToLowerInvariant() : null;
        if (languageIndex >= 0 && (language == null || (language != "all" && LanguageProfile.Find(language) == null)))
        {
            Console.Error.WriteLine("--language: en, ru, uk; для --eval также all.");
            Shutdown(2);
            return;
        }

        // Headless-самопроверка логики ядра (без хука/трея).
        if (e.Args.Contains("--selftest"))
        {
            int code = Core.SelfTest.Run();
            Shutdown(code);
            return;
        }

        // Оценка качества на fix/keep-фикстурах (accuracy + FP/FN отчёт).
        if (e.Args.Contains("--eval"))
        {
            int code = Core.Eval.Run(language ?? "ru");
            Shutdown(code);
            return;
        }

        // Диагностика триграммной модели: сравнение обучения .dic vs частотный список
        // (+ опциональный доп. частотный файл: --ngram-probe <путь>).
        int probeIdx = Array.IndexOf(e.Args, "--ngram-probe");
        if (probeIdx >= 0)
        {
            string? extra = probeIdx + 1 < e.Args.Length && !e.Args[probeIdx + 1].StartsWith("--")
                ? e.Args[probeIdx + 1] : null;
            int code = Core.NgramProbe.Run(extra);
            Shutdown(code);
            return;
        }

        // Вердикты всех оракулов по конкретным словам (наполнение словарей-дополнений).
        int checkIdx = Array.IndexOf(e.Args, "--check");
        if (checkIdx >= 0)
        {
            var words = new List<string>();
            for (int i = checkIdx + 1; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--language") { i++; continue; }
                if (e.Args[i].StartsWith("--")) break;
                words.Add(e.Args[i]);
            }
            int code = Core.WordProbe.Run(words, language);
            Shutdown(code);
            return;
        }

        // Подбор порога "естественности" по реальному логу правок (corrections.jsonl).
        int marginIdx = Array.IndexOf(e.Args, "--margin-probe");
        if (marginIdx >= 0)
        {
            string? log = marginIdx + 1 < e.Args.Length && !e.Args[marginIdx + 1].StartsWith("--")
                ? e.Args[marginIdx + 1] : null;
            int code = Core.MarginProbe.Run(log);
            Shutdown(code);
            return;
        }

        // Служебный режим: выгрузить слова, неизвестные Hunspell, для словаря-дополнения.
        int dumpIdx = Array.IndexOf(e.Args, "--dump-unknown");
        if (dumpIdx >= 0)
        {
            int minFreq = 8;
            if (dumpIdx + 1 < e.Args.Length && int.TryParse(e.Args[dumpIdx + 1], out int mf)) minFreq = mf;
            int code = Core.CandidateDump.Run(minFreq);
            Shutdown(code);
            return;
        }

        int filtIdx = Array.IndexOf(e.Args, "--filter-unknown");
        if (filtIdx >= 0 && filtIdx + 2 < e.Args.Length)
        {
            int code = Core.CandidateDump.FilterUnknown(e.Args[filtIdx + 1], e.Args[filtIdx + 2]);
            Shutdown(code);
            return;
        }

        // Предпросмотр окна настроек (dev-режим для вёрстки): без хука, трея и мьютекса -
        // можно открывать рядом с работающей копией. Конфиг/списки - из data/ рядом с exe.
        int settingsIdx = Array.IndexOf(e.Args, "--settings");
        bool dictionarySettings = e.Args.Contains("--dictionary-settings");
        if (settingsIdx >= 0 || dictionarySettings)
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            _store = new ConfigStore();
            _cfg = _store.Load();
            _store.EnsureUserLists();
            Window w = dictionarySettings ? new DictionaryLibraryWindow(_store, _cfg, () => { })
                : new SettingsWindow(_store, _cfg, () => { });
            // Необязательный номер страницы и флаг "тихо": окно за экраном, без активации -
            // для автоматических снимков (PrintWindow) без вмешательства в работу пользователя.
            if (w is SettingsWindow settings && settingsIdx + 1 < e.Args.Length && int.TryParse(e.Args[settingsIdx + 1], out int page))
                settings.SelectPage(page);
            if (e.Args.Contains("--offscreen"))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -w.Width - 200;
                w.Top = 100;
                w.ShowActivated = false;
                w.ShowInTaskbar = false;
            }
            w.Show();
            return;
        }

        // Один экземпляр - два хука дрались бы за один и тот же ввод.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\TypeSwitcher_SingleInstance", out bool created);
        if (!created || UpgradeHistory.PreviousInstanceIsRunning(AppContext.BaseDirectory))
        {
            MessageBox.Show(L.T("TypeSwitcher уже запущен (см. иконку в трее)."), "TypeSwitcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _store = new ConfigStore();
        _cfg = _store.Load();
        string? configProblem = _store.LoadProblem;
        UiPreferences.Initialize(_store, _cfg);
        // Дозаписать новые поля в config.json (миграция схемы). Если файл не прочитался,
        // НЕ пересохраняем: дефолты молча затёрли бы настройки пользователя.
        if (configProblem == null) _store.Save(_cfg);
        _store.EnsureUserLists();

        var dictionaryResult = DictionaryManager.Load(_store, _cfg);
        _dict = dictionaryResult.Dictionaries;

        _layout = new LayoutSwitcher();
        _layout.Detect();

        _exclusions = new ExclusionManager();
        _exclusions.SetBlacklist(_cfg.Exclusions.AppBlacklist);
        if (_cfg.Exclusions.SkipPasswordFields) _exclusions.StartFocusTracking();

        StartHookThread();

        // Поток хука читает только собственный снимок настроек: SettingsWindow правит
        // _cfg на UI-потоке поле за полем, и анализ посреди такой правки видел бы
        // полуобновлённый конфиг. Новый снимок передаётся в ApplySettings.
        _analyzer = new Analyzer(_dict, ConfigStore.Clone(_cfg))
        {
            LayoutDetectionEnabled = _cfg.Layout.Enabled,
            AvailableLayouts = _layout.Available
        };
        _processor = new InputProcessor(_analyzer, _hookDispatcher, _exclusions, _cfg.Exclusions.SkipPasswordFields)
        {
            Enabled = _cfg.General.Enabled,
            HotkeyVk = _cfg.Layout.HotkeyUndoVk,
            SuggestHotkeyVk = _cfg.Layout.HotkeySuggestVk,
            CaseHotkeyVk = _cfg.Case.HotkeyVk,
            CaseHotkeyShift = _cfg.Case.HotkeyShift,
            RuLayout = _layout.RuLayout,
            EnLayout = _layout.EnLayout,
            UkLayout = _layout.UkLayout
        };
        // Обработчики ниже вызываются на потоке хука. Инъекция остаётся там же (сразу
        // после возврата из обработчика хука), всё остальное уходит в UI-поток.
        _processor.ReplacementRequested += OnReplacement;
        _processor.ToggleRequested += OnToggle;
        _processor.SuggestRequested += word => Dispatcher.BeginInvoke(() => ShowSuggestion(word.Word, word.Language));

        _suggestions = new SuggestionCounter
        {
            Enabled = _cfg.Suggestions.Enabled,
            Threshold = _cfg.Suggestions.Threshold
        };
        _learn = new LearnLog(
            System.IO.Path.Combine(_store.DataDir, _cfg.Learning.File), _cfg.Learning.Enabled);
        // WordRejected приходит синхронно из обработчика хука: запись лога и счётчик
        // окна слова - на UI-потоке, не в хуке.
        _processor.WordRejected += word => Dispatcher.BeginInvoke(() => OnWordRejected(word.Word, word.Language));

        _sounds = new Sounds(
            _store.ResolveAppPath(_cfg.Sound.TypoWav),
            _store.ResolveAppPath(_cfg.Sound.LayoutWav));

        _tray = new TrayApp(
            _store.ResolveAppPath(_cfg.Tray.IconIdle),
            _store.ResolveAppPath(_cfg.Tray.IconActive),
            _cfg.Tray.BlinkMs,
            _cfg.General.Enabled);
        _tray.EnabledChanged += OnEnabledChanged;
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += () => Shutdown();

        AutostartManager.Apply(_cfg.General.Autostart); // синхронизировать реестр с конфигом

        _mouseHook = new MouseHook();
        _mouseHook.ButtonDown += () =>
        {
            _processor.ResetWordState();         // клик мышью двигает каретку
            _exclusions.InvalidateFocusCache();  // и может сменить поле ввода
        };
        _hook = new KeyboardHook();
        _hook.KeyAction += _processor.OnKey;

        // Хуки ставятся и обслуживаются на своём потоке: их обработчики выполняются в
        // цикле сообщений того потока, который их установил.
        var hookErrors = (string?)null;
        _hookDispatcher.Invoke(() => hookErrors = InstallHooks());
        Core.Log.Line($"hook installed={_hook.IsInstalled}, mouse={_mouseHook.IsInstalled}, enabled={_processor.Enabled}, ru={_dict.RuLoaded}, en={_dict.EnLoaded}, uk={_dict.UkLoaded}, " +
                      $"layoutDetect={_analyzer.LayoutDetectionEnabled} (ru={_layout.RuLayout:X} en={_layout.EnLayout:X} uk={_layout.UkLayout:X}), " +
                      $"my={_dict.MyWordsCount}/stop={_dict.StopWordsCount}/rules={_dict.RulesCount}, " +
                      $"integrity=0x{ExclusionManager.OwnIntegrityLevel:X}");
        if (hookErrors != null)
            MessageBox.Show(hookErrors, "TypeSwitcher", MessageBoxButton.OK, MessageBoxImage.Error);

        if (configProblem != null)
            MessageBox.Show(configProblem, "TypeSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);

        ShowDictionaryErrors(dictionaryResult);
    }

    // --- поток хуков ---

    // Клавиатурный и мышиный LL-хуки живут на отдельном потоке со своим циклом
    // сообщений, а не на UI-потоке. Обработчик LL-хука выполняется в потоке, который
    // его установил; пока тот поток занят (открытие окна настроек, пересборка модели,
    // модальное окно), ввод тормозит во всей системе, а после LowLevelHooksTimeout
    // Windows молча снимает хук. На отдельном потоке UI-заминки хук не задевают.
    private Thread? _hookThread;
    private Dispatcher _hookDispatcher = null!;

    private void StartHookThread()
    {
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookDispatcher = Dispatcher.CurrentDispatcher;
            _hookDispatcher.UnhandledException += (_, e) =>
            {
                // Исключение в обработчике не должно убивать поток хуков (и весь процесс).
                Core.Log.Line($"hook thread exception: {e.Exception}");
                e.Handled = true;
            };
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "TypeSwitcher hooks",
            Priority = ThreadPriority.AboveNormal
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait();
    }

    /// <summary>Поставить оба хука и watchdog. Выполняется на потоке хуков. Возвращает текст ошибки или null.</summary>
    private string? InstallHooks()
    {
        var errors = new List<string>();
        try
        {
            _mouseHook.Install();
        }
        catch (Exception ex)
        {
            // Без хука мыши клик не сбрасывает набор; watchdog будет пытаться поставить его снова.
            Core.Log.Line($"mouse hook install FAILED: {ex}");
            errors.Add(L.T("Не удалось установить хук мыши:\n{0}\n\nПосле перемещения каретки мышью правка может попасть не туда. Программа будет пытаться установить хук повторно.", ex.Message));
        }
        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            Core.Log.Line($"hook install FAILED: {ex}");
            errors.Add(L.T("Не удалось установить хук клавиатуры:\n{0}", ex.Message));
        }
        StartHookWatchdog();
        return errors.Count > 0 ? string.Join("\n\n", errors) : null;
    }

    // Windows 7+ молча снимает LL-хук, чей обработчик однажды превысил
    // LowLevelHooksTimeout (300 мс по умолчанию; Hunspell.Suggest на слабой машине
    // может). Снаружи это выглядит как "программа перестала работать до перезапуска".
    // Периодическая переустановка возвращает хук; сама операция мгновенна и ввод не теряет.
    // Переустанавливаются ОБА хука: мышиный снимается по той же причине, и без него
    // клик перестаёт сбрасывать набор. Таймер - на потоке хуков (переустановка
    // должна идти из того же потока, что и установка).
    private DispatcherTimer? _hookWatchdog;

    private void StartHookWatchdog()
    {
        _hookWatchdog = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Normal, (_, _) =>
        {
            long slow = _hook.TakeMaxCallbackMs();
            if (slow >= 200) Core.Log.Line($"watchdog: самый долгий обработчик за период {slow} мс");
            try { _hook.Reinstall(); }
            catch (Exception ex) { Core.Log.Line($"watchdog: переустановка хука клавиатуры не удалась: {ex.Message}"); }
            try { _mouseHook.Reinstall(); }
            catch (Exception ex) { Core.Log.Line($"watchdog: переустановка хука мыши не удалась: {ex.Message}"); }
        }, _hookDispatcher);
    }

    private void OnEnabledChanged(bool enabled)
    {
        _hookDispatcher.BeginInvoke(() => _processor.Enabled = enabled);
        _cfg.General.Enabled = enabled;
        _store.Save(_cfg);
        _tray.SetEnabledChecked(enabled);
    }

    // Выполняется на потоке хуков. Окно сменилось между решением и инъекцией -
    // backspace стёрли бы чужой текст, поэтому замену отменяем.
    private bool TargetWindowChanged(IntPtr window)
    {
        if (window == IntPtr.Zero || NativeMethods.GetForegroundWindow() == window) return false;
        Core.Log.Line("инъекция отменена: активное окно сменилось");
        _processor.ResetWordState();
        return true;
    }

    private void OnReplacement(ReplacementRequest req)
    {
        Core.Log.Line($"REPLACE backspaces={req.Backspaces} text='{req.Text}' kind={req.Kind} layout={req.Layout}");
        if (TargetWindowChanged(req.Window)) return;

        // Для смены раскладки сначала переключаем язык ввода окна, затем перенабираем.
        if (req.Kind == ActionKind.Layout)
        {
            IntPtr hkl = req.TargetHkl != IntPtr.Zero ? req.TargetHkl : _layout.Get(req.Layout);
            _layout.Activate(hkl);
        }

        if (!Replacer.Replace(req.Backspaces, req.Text, req.TrailingVk))
        {
            // Система не приняла ввод: текст на экране не соответствует нашему состоянию.
            _processor.ResetWordState();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _learn.LogCorrection(req.Original, req.Corrected, req.Kind);

            if (_cfg.Tray.BlinkOnAction)
                _tray.Blink();

            if (req.Kind == ActionKind.Layout)
            {
                if (_cfg.Sound.BeepOnLayout) _sounds.PlayLayout();
            }
            else if (_cfg.Sound.BeepOnTypo && (req.Kind == ActionKind.Typo || req.Kind == ActionKind.Rule))
            {
                _sounds.PlayTypo();
            }
        });
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_store, _cfg, ApplySettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private CancellationTokenSource? _dictionaryReload;
    private readonly SemaphoreSlim _dictionaryLoadGate = new(1, 1);
    private int _dictionaryGeneration;

    private async void ApplySettings()
    {
        var snapshot = ConfigStore.Clone(_cfg); // _cfg уже нормализован в ConfigStore.Save
        _dictionaryReload?.Cancel();
        _dictionaryReload?.Dispose();
        _dictionaryReload = new CancellationTokenSource();
        CancellationToken cancellation = _dictionaryReload.Token;
        int generation = ++_dictionaryGeneration;
        if (!snapshot.General.Enabled) _ = _hookDispatcher.BeginInvoke(() => _processor.Enabled = false);
        try
        {
            await _dictionaryLoadGate.WaitAsync(cancellation);
            DictionaryLoadResult result;
            try { result = await Task.Run(() => DictionaryManager.Load(_store, snapshot, cancellation), cancellation); }
            finally { _dictionaryLoadGate.Release(); }
            if (cancellation.IsCancellationRequested || generation != _dictionaryGeneration || Dispatcher.HasShutdownStarted) return;
            if (result.Errors.Any()) { ShowDictionaryErrors(result); return; }

            _dict = result.Dictionaries;
            _dict.LoadUserLists(_store.MyWordsPath, _store.StopWordsPath, _store.RulesPath);
            _layout.Detect();
            IntPtr ru = _layout.RuLayout, en = _layout.EnLayout, uk = _layout.UkLayout;
            var available = _layout.Available;
            var dictionaries = _dict;
            _exclusions.SetBlacklist(snapshot.Exclusions.AppBlacklist);
            if (snapshot.Exclusions.SkipPasswordFields) _exclusions.StartFocusTracking();
            _ = _hookDispatcher.BeginInvoke(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                _analyzer.UpdateDictionaries(dictionaries);
                _analyzer.UpdateConfig(snapshot);
                _analyzer.LayoutDetectionEnabled = snapshot.Layout.Enabled;
                _analyzer.AvailableLayouts = available;
                _processor.RuLayout = ru;
                _processor.EnLayout = en;
                _processor.UkLayout = uk;
                _processor.Enabled = snapshot.General.Enabled;
                _processor.SkipPasswordFields = snapshot.Exclusions.SkipPasswordFields;
                _processor.HotkeyVk = snapshot.Layout.HotkeyUndoVk;
                _processor.SuggestHotkeyVk = snapshot.Layout.HotkeySuggestVk;
                _processor.CaseHotkeyVk = snapshot.Case.HotkeyVk;
                _processor.CaseHotkeyShift = snapshot.Case.HotkeyShift;
                _processor.RefreshExclusions();
            });
            _suggestions.Enabled = snapshot.Suggestions.Enabled;
            _suggestions.Threshold = snapshot.Suggestions.Threshold;
            _learn.Enabled = snapshot.Learning.Enabled;
            AutostartManager.Apply(snapshot.General.Autostart);
            _tray.SetEnabledChecked(snapshot.General.Enabled);
            Core.Log.Line("settings and dictionary snapshot applied");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Core.Log.Line($"dictionary reload: {ex}");
            if (!Dispatcher.HasShutdownStarted) MessageBox.Show(L.T("Не удалось применить словари: {0}", ex.Message), "TypeSwitcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ShowDictionaryErrors(DictionaryLoadResult result)
    {
        string errors = string.Join("\n", result.Errors.Select(s => (s.Id.StartsWith("model-") ? L.T("{0} · модель", L.T(LanguageProfile.Find(s.Language)!.Name)) : Guid.TryParseExact(s.Id, "N", out _) ? s.Name : L.T(s.Name)) + ": " + s.Error));
        if (errors.Length > 0) MessageBox.Show(errors, L.T("TypeSwitcher — словари"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnWordRejected(string word, LayoutTarget source)
    {
        _learn.LogRejection(word);
        if (!_suggestions.RegisterRejection(source + ":" + word)) return;
        Dispatcher.BeginInvoke(() => ShowSuggestion(word, source));
    }

    private void ShowSuggestion(string word, LayoutTarget source)
    {
        try { _suggestionWindow?.Close(); } catch { /* окно уже закрыто */ }

        var w = new SuggestionWindow(word, source, _analyzer.LanguageOrder);
        w.Chosen += choice => OnSuggestionChosen(word, source, choice, w.SelectedReplacement);
        w.Closed += (_, _) => { if (ReferenceEquals(_suggestionWindow, w)) _suggestionWindow = null; };
        _suggestionWindow = w;
        w.Show();
    }

    private void OnSuggestionChosen(string word, LayoutTarget source, SuggestionChoice choice, string? replacement)
    {
        string language = LanguageProfile.Find(source)?.Id ?? "global";
        switch (choice)
        {
            case SuggestionChoice.MyWord:
                _store.AppendWord(_store.UserListPath("my_words", language), word);
                _dict.LoadUserLists(_store.MyWordsPath, _store.StopWordsPath, _store.RulesPath);
                break;
            case SuggestionChoice.StopWord:
                _store.AppendWord(_store.StopWordsPath, word);
                _dict.LoadUserLists(_store.MyWordsPath, _store.StopWordsPath, _store.RulesPath);
                break;
            case SuggestionChoice.LayoutRule:
                // Один клик: правило "слово = вариант в другой раскладке".
                string? other = replacement;
                if (other != null)
                {
                    _store.AppendRule(word, other, language);
                    _dict.LoadUserLists(_store.MyWordsPath, _store.StopWordsPath, _store.RulesPath);
                }
                break;
            case SuggestionChoice.Rule:
                var dlg = new RuleDialog(word, (from, to) =>
                {
                    _store.AppendRule(from, to, language);
                    _dict.LoadUserLists(_store.MyWordsPath, _store.StopWordsPath, _store.RulesPath);
                });
                dlg.Show();
                break;
            case SuggestionChoice.Dismiss:
                break;
        }
        _suggestions.Reset(source + ":" + word);
        Core.Log.Line($"suggestion '{word}' -> {choice}");
    }

    // Выполняется на потоке хуков (как и OnReplacement).
    private void OnToggle(ToggleRequest t)
    {
        Core.Log.Line($"TOGGLE backspaces={t.Backspaces} text='{t.Text}' activate={t.ActivateLayout}");
        if (TargetWindowChanged(t.Window)) return;

        if (t.ActivateLayout != LayoutTarget.None)
        {
            IntPtr hkl = t.ActivateHkl != IntPtr.Zero ? t.ActivateHkl : _layout.Get(t.ActivateLayout);
            _layout.Activate(hkl);
        }

        if (!Replacer.Replace(t.Backspaces, t.Text, t.TrailingVk))
        {
            _processor.ResetWordState();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_cfg.Tray.BlinkOnAction)
                _tray.Blink();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Theme.StopWatching();
        _dictionaryReload?.Cancel();
        if (_hookThread != null)
        {
            try
            {
                // Снять хуки из того же потока, что их ставил; не ждать дольше секунды.
                _hookDispatcher.Invoke(() =>
                {
                    _hookWatchdog?.Stop();
                    _hook?.Dispose();
                    _mouseHook?.Dispose();
                }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
                _hookDispatcher.InvokeShutdown();
            }
            catch (Exception ex) { Core.Log.Line($"exit: остановка потока хуков: {ex.Message}"); }
        }
        _exclusions?.Dispose();
        _tray?.Dispose();
        _sounds?.Dispose();
        _singleInstance?.Dispose();
        Core.Log.Flush();
        base.OnExit(e);
    }
}
