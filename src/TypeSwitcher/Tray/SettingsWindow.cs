using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TypeSwitcher.Config;
using TypeSwitcher.Interop;
using TypeSwitcher.Core;
using Microsoft.Win32;
using System.Windows.Automation;
using System.Windows.Data;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

/// <summary>
/// Окно настроек в стиле параметров Windows 11: слева навигация по разделам, справа
/// страница из карточек «заголовок + пояснение + контрол». Шаблоны контролов (тумблеры,
/// поля, скроллбары) - в DarkTheme.xaml. Сохранение пишет config.json и пользовательские
/// списки, затем вызывает onSaved для применения на лету.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly AppConfig _cfg;
    private readonly Action _onSaved;

    // Контролы, значения которых читаются при сохранении.
    private CheckBox _enabled = null!, _autostart = null!, _learnLog = null!;
    private CheckBox _beepTypo = null!, _beepLayout = null!;
    private CheckBox _suggestEnabled = null!;
    private TextBox _threshold = null!;
    private CheckBox _twoCaps = null!;

    private CheckBox _typoEnabled = null!;
    private RadioButton _ed1 = null!, _ed2 = null!;
    private TextBox _minWordLen = null!, _dominance = null!, _minFreq = null!, _scoreGap = null!;
    private CheckBox _ngramEnabled = null!, _ngramFreq = null!;

    private CheckBox _layoutEnabled = null!, _protectTech = null!, _normMixed = null!, _fixTypoOnSwitch = null!, _domainExt = null!;
    private TextBox _layoutMinLen = null!, _fixTypoMinLen = null!;

    private CheckBox _skipPw = null!;
    private TextBox _blacklist = null!;
    private TextBox _myWords = null!, _stopWords = null!, _rules = null!;
    private TextBlock _status = null!;
    private readonly Dictionary<string, CheckBox> _languageChecks = new();
    private TextBox _languageOrder = null!;
    private readonly Dictionary<(string Kind, string Scope), string> _userTexts = new();
    private string _listScope = "global", _ruleScope = "global";
    private static readonly string[] Scopes = ["global", "ru", "en", "uk"];

    private readonly StackPanel _nav;
    private readonly TextBlock _pageTitle;
    private readonly ContentControl _pageHost;
    private readonly Dictionary<RadioButton, (string Title, UIElement Page)> _pages = new();

    public SettingsWindow(ConfigStore store, AppConfig cfg, Action onSaved)
    {
        _store = store;
        _cfg = cfg;
        _onSaved = onSaved;
        UiPreferences.Initialize(store, cfg);
        foreach (string scope in Scopes)
            foreach (string kind in new[] { "my_words", "stopwords", "rules" })
                _userTexts[(kind, scope)] = ReadFile(store.UserListPath(kind, scope));
        InitializeFeatureState();

        Title = "TypeSwitcher";
        Width = Math.Max(cfg.Ui.SettingsWidth, 760);
        Height = Math.Max(cfg.Ui.SettingsHeight, 540);
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.WinBg;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Theme.Attach(this);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // --- Навигация ---
        _nav = new StackPanel { Margin = new Thickness(12, 16, 12, 12) };
        _nav.Children.Add(new TextBlock
        {
            Text = "TypeSwitcher",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Fg,
            Margin = new Thickness(12, 4, 0, 20)
        });
        var navBorder = new Border { Background = Theme.NavBg, Child = new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false } };
        Grid.SetColumn(navBorder, 0);
        root.Children.Add(navBorder);

        // --- Страница ---
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        _pageTitle = new TextBlock
        {
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Fg,
            Margin = new Thickness(32, 22, 32, 14)
        };
        var pageHeader = new DockPanel();
        var themeButton = UiPreferences.ThemeButton();
        themeButton.Margin = new Thickness(0, 20, 32, 10);
        DockPanel.SetDock(themeButton, Dock.Right);
        pageHeader.Children.Add(themeButton);
        pageHeader.Children.Add(_pageTitle);
        Grid.SetRow(pageHeader, 0);
        right.Children.Add(pageHeader);

        _pageHost = new ContentControl { Margin = new Thickness(32, 0, 32, 16), Focusable = false };
        Grid.SetRow(_pageHost, 1);
        right.Children.Add(_pageHost);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        right.Children.Add(footer);

        AddPage("Общие", BuildGeneral(), scroll: true);
        AddPage("Опечатки", BuildTypo(), scroll: true);
        AddPage("Раскладка", BuildLayout(), scroll: true);
        AddPage("Языки", BuildLanguages(), scroll: true);
        AddPage("Словари", BuildDictionaries(), scroll: false);
        AddPage("Правила", BuildRules(), scroll: false);
        AddPage("Исключения", BuildExclusions(), scroll: false);
        AddPage("Язык интерфейса", BuildInterfaceLanguage(), scroll: true);
        AddPage("Правила раскладки", BuildLayoutRules(), scroll: true);
        AddPage("Проверка", BuildDiagnostics(), scroll: true);
        AddPage("История", BuildHistory(), scroll: false);
        AddPage("О программе", BuildAbout(), scroll: true);
        InstallDefaultTracking();

        Content = root;
        _pages.Keys.First().IsChecked = true;

        Closing += (_, _) =>
        {
            UiPreferences.CapturingHotkey = false;
            _diagnosticCancellation?.Cancel();
            // Запомнить размер окна (без сохранения остальных правок формы).
            if (WindowState == WindowState.Normal)
            {
                _cfg.Ui.SettingsWidth = Math.Round(Width);
                _cfg.Ui.SettingsHeight = Math.Round(Height);
                try { _store.Save(_cfg); } catch { /* не критично */ }
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.UpdateCaption(this);
    }

    // --- навигация ---

    private void AddPage(string title, UIElement page, bool scroll)
    {
        UIElement host = scroll
            ? new ScrollViewer { Content = page, Style = (Style)FindResource("PageScroll"), Focusable = false }
            : page;
        var item = new RadioButton
        {
            GroupName = "nav",
            Style = (Style)FindResource("NavItem")
        };
        L.Bind(item, ContentControl.ContentProperty, title);
        item.Checked += (_, _) => ShowPage(item);
        _pages[item] = (title, host);
        _nav.Children.Add(item);
    }

    /// <summary>Открыть страницу по индексу (порядок навигации).</summary>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        _pages.Keys.ElementAt(index).IsChecked = true;
    }
    public int PageCount => _pages.Count;
    public const int AboutPage = 11;
    internal void SetEngineEnabled(bool value) => _enabled.IsChecked = value;

    private void ShowPage(RadioButton item)
    {
        var (title, page) = _pages[item];
        L.Bind(_pageTitle, TextBlock.TextProperty, title);
        _pageHost.Content = page;
    }

    // --- страницы ---

    private UIElement BuildGeneral()
    {
        var p = Page();

        var main = Card(p, "Программа");
        _enabled = Toggle(main, "Программа включена", _cfg.General.Enabled,
            "Главный выключатель. Когда выключено, программа не вмешивается в ввод. То же делает клик по иконке в трее.");
        _autostart = Toggle(main, "Запускать вместе с Windows", _cfg.General.Autostart,
            "Автозапуск через реестр текущего пользователя, без прав администратора. При переносе папки включите заново.");

        var keys = Card(p, "Горячие клавиши");
        BuildHotkeys(keys);
        BuildSelectionPreferences(p);

        var sound = Card(p, "Звук");
        _beepTypo = Toggle(sound, "Сигнал при исправлении опечатки", _cfg.Sound.BeepOnTypo, "");
        _beepLayout = Toggle(sound, "Сигнал при смене раскладки", _cfg.Sound.BeepOnLayout, "");

        var learn = Card(p, "Обучение");
        _suggestEnabled = Toggle(learn, "Показывать окно слова после откатов", _cfg.Suggestions.Enabled,
            "Если правку одного и того же слова несколько раз откатили, всплывает окно с предложением занести слово в стоп-слова или задать правило.");
        _threshold = Number(learn, "Сколько откатов до показа окна", _cfg.Suggestions.Threshold.ToString(), "Обычно 2-5.");
        _learnLog = Toggle(learn, "Копить локальный лог правок", _cfg.Learning.Enabled,
            "В data/corrections.jsonl пишутся только события автозамены и откатов, без потока набора, паролей и содержимого окон. " +
            "Помогает настраивать качество на реальных данных. Никуда не отправляется.");
        _historyLimit = Number(learn, "Лимит записей истории", _cfg.Learning.MaxEntries.ToString(), "Хранятся только последние исправления. Запись истории включается отдельно.");
        _layoutIndicator = Toggle(main, "Показывать раскладку в трее", _cfg.Tray.ShowLayoutIndicator, "Индикатор EN/RU/UK отражает язык ввода активного окна.");
        return p;
    }

    private UIElement BuildTypo()
    {
        var p = Page();

        var main = Card(p, "Исправление");
        _typoEnabled = Toggle(main, "Исправлять опечатки", _cfg.Typo.Enabled,
            "Слово, которого нет в словаре, заменяется ближайшим словарным, если выбор однозначен. Существующие слова не трогаются.");
        _englishEnabled = Toggle(main, "Исправлять английские опечатки", _cfg.Typo.EnglishEnabled, "Отдельная опция для английского текста. Идентификаторы и технические термины защищены.");
        _minWordLen = Number(main, "Минимальная длина слова", _cfg.Typo.MinWordLength.ToString(),
            "Более короткие слова не исправляются: слишком велик риск ошибиться.");
        _twoCaps = Toggle(main, "Исправлять ДВе заглавные", _cfg.Case.FixTwoCapitals,
            "Две прописные в начале слова: ДВе становится Две, THe становится The. Только если получается словарное слово; " +
            "аббревиатуры (USB, IDs) и тех-токены (MHz, OAuth) не трогаются. Работает независимо от исправления опечаток.");

        var dist = new StackPanel();
        _ed1 = new RadioButton { GroupName = "ed", IsChecked = _cfg.Typo.MaxEditDistance <= 1, Margin = new Thickness(0, 0, 0, 6) }.Localize(ContentControl.ContentProperty, "1 отличие, консервативно");
        _ed2 = new RadioButton { GroupName = "ed", IsChecked = _cfg.Typo.MaxEditDistance >= 2 }.Localize(ContentControl.ContentProperty, "2 отличия, агрессивнее");
        dist.Children.Add(_ed1);
        dist.Children.Add(_ed2);
        Row(main, "Допустимое число отличий", "Вставка, удаление, замена или перестановка буквы считаются одним отличием. Два отличия чинят больше опечаток, но и ошибаются чаще.", dist);

        var rank = Card(p, "Выбор замены");
        _ngramEnabled = Toggle(rank, "Оценивать естественность слова", _cfg.Ngram.Enabled,
            "Кандидаты ранжируются по триграммной модели русского языка и частоте, а не только по частоте. Точнее на редких формах.");
        _ngramFreq = Toggle(rank, "Учить модель на частотах", _cfg.Ngram.WeightByFrequency,
            "Модель строится по частотному списку с весом по частоте, что заметно лучше отделяет живой язык от мусора. Смена пересобирает кеш модели.");
        _scoreGap = Number(rank, "Порог уверенности", Fmt(_cfg.Typo.ScoreGap),
            "Замена происходит, только если лучший кандидат обгоняет второго на столько баллов. Больше значит строже.");

        var fallback = Card(p, "Резервный режим (если естественность выключена)");
        _dominance = Number(fallback, "Порог доминирования по частоте", Fmt(_cfg.Typo.DominanceRatio),
            "Лучший кандидат должен быть частотнее второго минимум во столько раз.");
        _minFreq = Number(fallback, "Минимальная частота кандидата", _cfg.Typo.MinFrequency.ToString(),
            "Кандидат должен встречаться в частотном списке не реже этого.");
        return p;
    }

    private UIElement BuildLayout()
    {
        var p = Page();

        var main = Card(p, "Автопереключение");
        _layoutEnabled = Toggle(main, "Переключать раскладку автоматически", _cfg.Layout.Enabled,
            "Если слово набрано не в той раскладке (ghbdtn) и в другой раскладке это настоящее слово (привет), раскладка переключается, а слово перенабирается.");
        _layoutMinLen = Number(main, "Минимальная длина слова", _cfg.Layout.MinWordLength.ToString(),
            "Короткие слова часто случайно совпадают со словом другой раскладки (yt и не). На ручную клавишу не влияет.");

        var extra = Card(p, "Дополнительно");
        _fixTypoOnSwitch = Toggle(extra, "Чинить опечатку вместе с раскладкой", _cfg.Layout.FixTypoOnSwitch,
            "Слово набрано не в той раскладке и ещё с опечаткой (gbhdtn): переключить раскладку и сразу исправить (привет).");
        _fixTypoMinLen = Number(extra, "Минимальная длина для этого случая", _cfg.Layout.FixTypoMinLength.ToString(),
            "На коротких токенах (tcp, url, php) такая догадка портит текст, поэтому порог отдельный и выше обычного.");
        _normMixed = Toggle(extra, "Чинить слова со смешанными буквами", _cfg.Layout.NormalizeMixedScript,
            "Одна буква из чужой раскладки (привеn) приводится к остальным, если получается настоящее слово.");
        _domainExt = Toggle(extra, "Переключать домены и имена файлов", _cfg.Layout.SwitchKnownDomainExt,
            "Токены вида имя.ext с известным доменом или расширением (пщщпдуюсщь в google.com, куфвьуюьв в readme.md).");
        _protectTech = Toggle(extra, "Не трогать технические токены", _cfg.Layout.ProtectTechnical,
            "Аббревиатуры (USB, API), бренды с заглавными внутри (GitHub, iPhone) и слова с цифрами (win10) не перекладываются, даже если похожи на слово другой раскладки.");
        BuildAutomationPreferences(p);
        return p;
    }

    private UIElement BuildDictionaries()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        toolbar.Children.Add(ScopePicker(_listScope, scope =>
        {
            CaptureUserEditors(); _listScope = scope;
            _myWords.Text = _userTexts[("my_words", scope)];
            _stopWords.Text = _userTexts[("stopwords", scope)];
        }));
        var manage = new Button { Margin = new Thickness(10, 0, 0, 0) }.Localize(ContentControl.ContentProperty, "Подключённые словари…");
        manage.Click += (_, _) => new DictionaryLibraryWindow(_store, _cfg, _onSaved) { Owner = this }.ShowDialog();
        toolbar.Children.Add(manage);
        Add(g, 0, toolbar);
        _myWords = Editor(_userTexts[("my_words", _listScope)]);
        Add(g, 1, ListHeader("Мой словарь", "Правильные слова выбранного языка. По одному на строку.", _myWords, "my_words"));
        Add(g, 2, _myWords);

        _stopWords = Editor(_userTexts[("stopwords", _listScope)]);
        var h2 = ListHeader("Стоп-слова", "Запрет правок в выбранном языке. Общий список действует во всех языках.", _stopWords, "stopwords");
        h2.Margin = new Thickness(0, 16, 0, 8);
        Add(g, 3, h2);
        Add(g, 4, _stopWords);
        return g;
    }

    private UIElement BuildRules()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _rules = Editor(_userTexts[("rules", _ruleScope)]);
        var header = new StackPanel();
        header.Children.Add(ScopePicker(_ruleScope, scope =>
        {
            CaptureUserEditors(); _ruleScope = scope; _rules.Text = _userTexts[("rules", scope)];
        }));
        header.Children.Add(ListHeader("Правила замены", "Формат: что = на что. Общие правила имеют приоритет перед языковыми.", _rules, "rules"));
        Add(g, 0, header);
        Add(g, 1, _rules);

        var hint = new TextBlock
        {
            Foreground = Theme.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        }.Localize("Примеры: «тлф = телефон», «;jhbr = жорик» (имя, набранное в английской раскладке: ключ может содержать знаки). " +
                   "Правило сильнее словаря, но стоп-слово сильнее правила.");
        Add(g, 2, hint);
        return g;
    }

    private UIElement BuildExclusions()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel();
        var card = Card(top, null);
        _skipPw = Toggle(card, "Не трогать поля паролей", _cfg.Exclusions.SkipPasswordFields,
            "В полях ввода пароля программа ничего не правит и не запоминает (определение через Win32 и UI Automation).");
        Add(g, 0, top);

        var h = EditorHeader("Чёрный список приложений",
            "В этих приложениях программа полностью бездействует. Имя процесса по одному на строку, например chrome.exe. Для терминала Windows это WindowsTerminal.exe.");
        h.Margin = new Thickness(0, 20, 0, 8);
        h.Children.Add(BuildExclusionButtons());
        Add(g, 1, h);
        _blacklist = Editor(string.Join("\n", _cfg.Exclusions.AppBlacklist));
        L.Bind(_blacklist, AutomationProperties.NameProperty, "Чёрный список приложений");
        Add(g, 2, _blacklist);
        return g;
    }

    // --- сохранение ---

    private UIElement BuildFooter()
    {
        var bar = new Border
        {
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(32, 12, 32, 14)
        };
        var dock = new DockPanel { LastChildFill = false };
        bar.Child = dock;

        _status = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_status, Dock.Left);
        dock.Children.Add(_status);

        var close = new Button { Margin = new Thickness(8, 0, 0, 0) }.Localize(ContentControl.ContentProperty, "Закрыть");
        close.Click += (_, _) => Close();
        var save = new Button { Style = (Style)FindResource("AccentButton"), IsDefault = true }.Localize(ContentControl.ContentProperty, "Сохранить");
        save.Click += (_, _) =>
        {
            try { Save(); }
            catch (Exception ex) { L.Bind(_status, TextBlock.TextProperty, "Не удалось сохранить: {0}", ex.Message); }
        };

        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        dock.Children.Add(close);
        dock.Children.Add(save);
        return bar;
    }

    internal AppConfig ReadFormConfiguration()
    {
        var config = ConfigStore.Clone(_cfg);
        var order = _languageOrder.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (order.Count == 0 || order.Any(id => LanguageProfile.Find(id) == null))
            throw new InvalidDataException(L.T("Порядок языков: en, ru, uk (через запятую)."));
        config.Languages.Order = order;
        config.Languages.Enabled = _languageChecks.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        config.General.Enabled = _enabled.IsChecked == true;
        config.General.Autostart = _autostart.IsChecked == true;
        config.Learning.Enabled = _learnLog.IsChecked == true;
        config.Sound.BeepOnTypo = _beepTypo.IsChecked == true;
        config.Sound.BeepOnLayout = _beepLayout.IsChecked == true;
        config.Suggestions.Enabled = _suggestEnabled.IsChecked == true;
        config.Suggestions.Threshold = ParseInt(_threshold.Text, _cfg.Suggestions.Threshold);
        config.Hotkeys = _draftHotkeys;
        if (config.Hotkeys.HasConflicts()) throw new InvalidDataException(L.T("Одно сочетание назначено нескольким командам."));
        config.Layout.HotkeyUndoVk = _draftHotkeys.ConvertWord.Vk;
        config.Layout.HotkeySuggestVk = _draftHotkeys.WordActions.Vk;
        config.Case.HotkeyVk = _draftHotkeys.ChangeCase.Vk;
        config.Case.HotkeyShift = _draftHotkeys.ChangeCase.Modifiers.HasFlag(KeyModifiers.Shift);
        config.Case.FixTwoCapitals = _twoCaps.IsChecked == true;

        config.Typo.Enabled = _typoEnabled.IsChecked == true;
        config.Typo.MaxEditDistance = _ed2.IsChecked == true ? 2 : 1;
        config.Typo.MinWordLength = ParseInt(_minWordLen.Text, _cfg.Typo.MinWordLength);
        config.Ngram.Enabled = _ngramEnabled.IsChecked == true;
        config.Ngram.WeightByFrequency = _ngramFreq.IsChecked == true;
        config.Typo.ScoreGap = ParseDouble(_scoreGap.Text, _cfg.Typo.ScoreGap);
        config.Typo.DominanceRatio = ParseDouble(_dominance.Text, _cfg.Typo.DominanceRatio);
        config.Typo.MinFrequency = ParseLong(_minFreq.Text, _cfg.Typo.MinFrequency);

        config.Layout.Enabled = _layoutEnabled.IsChecked == true;
        config.Layout.MinWordLength = ParseInt(_layoutMinLen.Text, _cfg.Layout.MinWordLength);
        config.Layout.FixTypoOnSwitch = _fixTypoOnSwitch.IsChecked == true;
        config.Layout.FixTypoMinLength = ParseInt(_fixTypoMinLen.Text, _cfg.Layout.FixTypoMinLength);
        config.Layout.ProtectTechnical = _protectTech.IsChecked == true;
        config.Layout.NormalizeMixedScript = _normMixed.IsChecked == true;
        config.Layout.SwitchKnownDomainExt = _domainExt.IsChecked == true;

        config.Exclusions.SkipPasswordFields = _skipPw.IsChecked == true;
        config.Exclusions.AppBlacklist = SplitLines(_blacklist.Text);
        ReadFeaturePreferences(config);
        var result = ConfigStore.Clone(config);
        result.Normalize();
        return result;
    }

    private void Save()
    {
        var config = ReadFormConfiguration();

        // Пользовательские списки - в файлы.
        CaptureUserEditors();
        foreach (var entry in _userTexts)
            _store.SaveText(_store.UserListPath(entry.Key.Kind, entry.Key.Scope), entry.Value);

        _store.Save(config);
        _cfg.CopyFrom(config);
        _onSaved();

        // Окно остаётся открытым - показываем подтверждение.
        L.Bind(_status, TextBlock.TextProperty, "Сохранено в {0:HH:mm:ss}", DateTime.Now);
    }

    private UIElement BuildLanguages()
    {
        var page = Page();
        var card = Card(page, "Языки обработки текста");
        var layouts = new LayoutSwitcher();
        layouts.Detect();
        foreach (var profile in LanguageProfile.All)
        {
            string state = layouts.Get(profile.Target) != IntPtr.Zero ? "Раскладка установлена." : "Раскладка отсутствует: добавьте её в языковых настройках Windows.";
            _languageChecks[profile.Id] = Toggle(card, profile.Name, _cfg.Languages.Enabled.Contains(profile.Id), state);
        }
        var manual = Card(page, "Ручное переключение");
        _languageOrder = new TextBox { Text = string.Join(", ", _cfg.Languages.Order), Width = 165 };
        Row(manual, "Порядок раскладок", "Pause переключает по кругу; выключенные и отсутствующие раскладки пропускаются. При откате возвращается исходная раскладка.", _languageOrder);
        page.Children.Add(new TextBlock
        {
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0)
        }.Localize("Украинский словарь ВЕСУМ и частотный список поставляются с программой. Если разные языки дают несколько допустимых вариантов, автоматическая замена пропускается. Настройки применяются после кнопки «Сохранить»."));
        return page;
    }

    private UIElement BuildInterfaceLanguage()
    {
        var page = Page();
        var languageCard = Card(page, "Язык интерфейса");
        var language = new ComboBox { Width = 185, ItemsSource = UiPreferences.Languages, DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        language.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(L.LanguageState.Language)) { Source = L.State, Mode = BindingMode.OneWay });
        AutomationProperties.SetAutomationId(language, "appearance.language");
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedValue is string id && id != L.Language) UiPreferences.Save(() => UiPreferences.SetLanguage(id));
        };
        Row(languageCard, "Выберите язык интерфейса", "Язык меню, окон, подсказок и сообщений. Языки обработки текста настраиваются отдельно на странице «Языки».", language);
        TrackDefault(language, "ru", () => language.SelectedValue, () => UiPreferences.Save(() => UiPreferences.SetLanguage("ru")), "appearance.language");
        var themeCard = Card(page, "Оформление");
        var theme = new ComboBox { Width = 185, ItemsSource = UiPreferences.Themes, DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        theme.SetBinding(ComboBox.SelectedValueProperty, new Binding(nameof(Theme.ThemeState.Mode)) { Source = Theme.State, Mode = BindingMode.OneWay });
        AutomationProperties.SetAutomationId(theme, "appearance.theme-mode");
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedValue is string id && id != Theme.State.Mode) UiPreferences.Save(() => UiPreferences.SetTheme(id));
        };
        Row(themeCard, "Тема", "Системная тема следует настройке оформления приложений Windows.", theme);
        TrackDefault(theme, "dark", () => theme.SelectedValue, () => UiPreferences.Save(() => UiPreferences.SetTheme("dark")), "appearance.theme");
        page.Children.Add(new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) }
            .Localize("Язык и тема применяются сразу и сохраняются автоматически. Незавершённые изменения остальных настроек сохраняются кнопкой «Сохранить»."));
        return page;
    }

    private ComboBox ScopePicker(string selected, Action<string> changed)
    {
        var items = Scopes.Select(id => new UiChoice(id, id == "global" ? "Общие списки" : LanguageProfile.Find(id)!.Name)).ToArray();
        var box = new ComboBox { ItemsSource = items, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = selected, Width = 170, Margin = new Thickness(0, 0, 0, 8) };
        L.Bind(box, AutomationProperties.NameProperty, "Область списка");
        var itemStyle = new Style(typeof(ComboBoxItem), (Style)FindResource(typeof(ComboBoxItem)));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("Label")));
        box.ItemContainerStyle = itemStyle;
        box.SelectionChanged += (_, _) => { if (box.SelectedValue is string value) changed(value); };
        return box;
    }

    private void CaptureUserEditors()
    {
        if (_myWords != null) _userTexts[("my_words", _listScope)] = _myWords.Text;
        if (_stopWords != null) _userTexts[("stopwords", _listScope)] = _stopWords.Text;
        if (_rules != null) _userTexts[("rules", _ruleScope)] = _rules.Text;
    }

    private StackPanel ListHeader(string title, string description, TextBox editor, string kind)
    {
        L.Bind(editor, AutomationProperties.NameProperty, title);
        L.Bind(editor, AutomationProperties.HelpTextProperty, description);
        var panel = EditorHeader(title, description);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        var import = new Button { Margin = new Thickness(0, 0, 8, 0) }.Localize(ContentControl.ContentProperty, "Импорт…");
        var export = new Button().Localize(ContentControl.ContentProperty, "Экспорт…");
        import.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = L.T("Текстовый список (*.txt)|*.txt") };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) throw new InvalidDataException(L.T("Для файлов больше 2 МБ используйте подключённые словари."));
                string incoming = File.ReadAllText(dialog.FileName, new System.Text.UTF8Encoding(false, true));
                string merged = ConfigStore.MergeUserList(editor.Text, incoming, kind == "rules");
                if (merged.Length > 2 * 1024 * 1024) throw new InvalidDataException(L.T("Личный список слишком велик для редактора."));
                editor.Text = merged;
                L.Bind(_status, TextBlock.TextProperty, "Список объединён. Существующие записи сохранены. Нажмите «Сохранить».");
            }
            catch (Exception ex) { L.Bind(_status, TextBlock.TextProperty, "Ошибка импорта: {0}", ex.Message); }
        };
        export.Click += (_, _) =>
        {
            string scope = kind == "rules" ? _ruleScope : _listScope;
            var dialog = new SaveFileDialog { Filter = L.T("Текстовый список (*.txt)|*.txt"), FileName = $"{kind}.{scope}.txt", DefaultExt = ".txt" };
            if (dialog.ShowDialog(this) != true) return;
            try { _store.SaveText(dialog.FileName, editor.Text); L.Bind(_status, TextBlock.TextProperty, "Список экспортирован."); }
            catch (Exception ex) { L.Bind(_status, TextBlock.TextProperty, "Ошибка экспорта: {0}", ex.Message); }
        };
        buttons.Children.Add(import); buttons.Children.Add(export); panel.Children.Add(buttons);
        return panel;
    }

    // --- хелперы построения ---

    private static StackPanel Page() => new() { Margin = new Thickness(0, 0, 8, 0) };

    /// <summary>Карточка настроек: заголовок группы (опционально) + строки с разделителями.</summary>
    private static StackPanel Card(Panel parent, string? title)
    {
        if (title != null)
        {
            parent.Children.Add(new TextBlock
            {
                Foreground = Theme.Fg,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, parent.Children.Count == 0 ? 0 : 22, 0, 8)
            }.Localize(title));
        }
        var rows = new StackPanel();
        parent.Children.Add(new Border
        {
            Background = Theme.PanelBg,
            CornerRadius = new CornerRadius(8),
            Child = rows,
            Margin = new Thickness(0, title == null && parent.Children.Count > 0 ? 12 : 0, 0, 0)
        });
        return rows;
    }

    /// <summary>Строка карточки: заголовок и пояснение слева, контрол справа.</summary>
    private static void Row(StackPanel card, string title, string desc, UIElement control)
    {
        L.Bind(control, AutomationProperties.NameProperty, title);
        L.Bind(control, AutomationProperties.HelpTextProperty, desc);
        if (card.Children.Count > 0)
            card.Children.Add(new Border { Height = 1, Background = Theme.Divider, Margin = new Thickness(16, 0, 16, 0) });

        var g = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap }.Localize(title));
        if (!string.IsNullOrEmpty(desc))
            text.Children.Add(new TextBlock
            {
                Foreground = Theme.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            }.Localize(desc));
        g.Children.Add(text);

        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(24, 0, 0, 0);
        }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        card.Children.Add(g);
    }

    private static CheckBox Toggle(StackPanel card, string title, bool value, string desc)
    {
        var c = new CheckBox { IsChecked = value };
        Row(card, title, desc, c);
        return c;
    }

    private static TextBox Number(StackPanel card, string title, string value, string desc)
    {
        var tb = new TextBox { Text = value, Width = 90, TextAlignment = TextAlignment.Right };
        Row(card, title, desc, tb);
        return tb;
    }

    private TextBox HotkeyRow(StackPanel card, string title, int vk, bool shift, bool allowShift,
        Action<int, bool> onCaptured, string desc)
    {
        var box = new TextBox
        {
            Text = HotkeyName(vk, shift),
            IsReadOnly = true,
            Width = 160,
            TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Hand,
        };
        L.Bind(box, ToolTipService.ToolTipProperty, "Кликните и нажмите клавишу");
        box.GotKeyboardFocus += (_, _) => { box.Text = L.T("нажмите клавишу…"); };
        box.LostKeyboardFocus += (_, _) => { box.Text = HotkeyName(vk, shift); };
        box.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
            vk = KeyInterop.VirtualKeyFromKey(key);
            shift = allowShift && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            onCaptured(vk, shift);
            box.Text = HotkeyName(vk, shift);
            Keyboard.ClearFocus();
        };
        Row(card, title, desc, box);
        return box;
    }

    private static StackPanel EditorHeader(string title, string desc)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        p.Children.Add(new TextBlock { Foreground = Theme.Fg, FontSize = 15, FontWeight = FontWeights.SemiBold }.Localize(title));
        p.Children.Add(new TextBlock
        {
            Foreground = Theme.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        }.Localize(desc));
        return p;
    }

    private TextBox Editor(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("Editor")
    };

    private static void Add(Grid g, int row, UIElement el)
    {
        Grid.SetRow(el, row);
        g.Children.Add(el);
    }

    private static string HotkeyName(int vk, bool shift) => shift ? "Shift+" + KeyName(vk) : KeyName(vk);

    private static string KeyName(int vk)
    {
        try
        {
            Key k = KeyInterop.KeyFromVirtualKey(vk);
            return k switch
            {
                Key.None => $"VK 0x{vk:X2}",
                Key.Scroll => "Scroll Lock",
                Key.Pause => "Pause",
                Key.Insert => "Insert",
                Key.Apps => "Menu",
                _ => k.ToString()
            };
        }
        catch { return $"VK 0x{vk:X2}"; }
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static List<string> SplitLines(string text) => text
        .Replace("\r", "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out var v) ? v : fallback;
    private static long ParseLong(string s, long fallback) => long.TryParse(s.Trim(), out var v) ? v : fallback;
    private static double ParseDouble(string s, double fallback)
        => double.TryParse(s.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
