using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public sealed partial class SettingsWindow
{
    private HotkeysConfig _draftHotkeys = null!;
    private readonly List<LayoutRule> _draftLayoutRules = new();
    private readonly Dictionary<InputCommand, TextBox> _hotkeyEditors = new();
    private CheckBox _englishEnabled = null!, _respectManual = null!, _correctIdle = null!, _earlySwitch = null!, _layoutIndicator = null!;
    private TextBox _idleDelay = null!, _earlyLength = null!, _historyLimit = null!;
    private CheckBox _caretIndicator = null!, _useApplicationLayouts = null!;
    private readonly Dictionary<string, ComboBox> _applicationLanguages = new(StringComparer.OrdinalIgnoreCase);
    private ComboBox _contextMode = null!, _layoutTap = null!, _convertTap = null!, _selectionTarget = null!, _transliteration = null!;
    private CancellationTokenSource? _diagnosticCancellation;

    private void InitializeFeatureState()
    {
        _draftHotkeys = ConfigStore.Clone(_cfg).Hotkeys;
        _draftLayoutRules.AddRange(_cfg.Layout.Rules.Select(r => r with { }));
        Deactivated += (_, _) => UiPreferences.CapturingHotkey = false;
        L.Changed += FeatureLanguageChanged;
        Closed += (_, _) => L.Changed -= FeatureLanguageChanged;
    }

    private void FeatureLanguageChanged(object? sender, EventArgs e)
    {
        if (!UiPreferences.CapturingHotkey) foreach (var item in _hotkeyEditors) item.Value.Text = ChordText(_draftHotkeys.Get(item.Key));
        RefreshRules(); RefreshHistory(); FormatDiagnostics();
    }

    internal static string CommandTitle(InputCommand command) => command switch
    {
        InputCommand.ConvertWord => "Откат правки / смена раскладки", InputCommand.ChangeCase => "Смена регистра",
        InputCommand.WordActions => "Окно слова", InputCommand.ToggleEnabled => "Глобальная пауза",
        InputCommand.Undo => "Отменить исправление", InputCommand.SelectionLayout => "Раскладка выделения",
        InputCommand.SelectionCase => "Регистр выделения", _ => "Транслитерация выделения"
    };

    private void BuildHotkeys(StackPanel card)
    {
        foreach (var command in Enum.GetValues<InputCommand>())
        {
            var box = new TextBox { Text = ChordText(_draftHotkeys.Get(command)), IsReadOnly = true, Width = 165, TextAlignment = TextAlignment.Center, Cursor = Cursors.Hand };
            AutomationProperties.SetAutomationId(box, "hotkey." + command);
            box.GotKeyboardFocus += (_, _) => { UiPreferences.CapturingHotkey = true; box.Text = L.T("нажмите клавишу…"); };
            box.LostKeyboardFocus += (_, _) => { UiPreferences.CapturingHotkey = false; box.Text = ChordText(_draftHotkeys.Get(command)); };
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { box.Text = ChordText(_draftHotkeys.Get(command)); Keyboard.ClearFocus(); return; }
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (KeyChord.IsModifierKey(vk)) return;
                KeyModifiers modifiers = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? KeyModifiers.Control : 0)
                    | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : 0)
                    | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : 0)
                    | (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) ? KeyModifiers.Windows : 0);
                if (vk is >= 0x30 and <= 0x5A or >= 0xBA and <= 0xE2 && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows)) == 0)
                {
                    L.Bind(_status, TextBlock.TextProperty, "Для букв и знаков добавьте Ctrl, Alt или Win.");
                    return;
                }
                var chord = key == Key.Back && modifiers == 0 ? new KeyChord() : new(vk, modifiers);
                _draftHotkeys.Set(command, chord);
                box.Text = ChordText(chord);
                Keyboard.ClearFocus();
            };
            string description = command switch
            {
                InputCommand.ToggleEnabled => "Включает и приостанавливает обработку из любого приложения.",
                InputCommand.ConvertWord => "Отменяет последнюю правку или меняет раскладку текущего слова.",
                InputCommand.ChangeCase => "Меняет регистр текущего или последнего слова.",
                InputCommand.WordActions => "Открывает действия для последнего слова.",
                InputCommand.Undo => "Только отмена последней доступной правки, без принудительной смены раскладки.",
                _ => "Работает с выделением через UI Automation. Буфер обмена не используется."
            };
            Row(card, CommandTitle(command), description, box);
            _hotkeyEditors[command] = box;
        }
        var taps = new[] { new UiChoice("0", "Выключено"), new UiChoice("160", "Левый Shift"), new UiChoice("161", "Правый Shift"), new UiChoice("162", "Левый Ctrl"), new UiChoice("163", "Правый Ctrl") };
        _layoutTap = Choice(card, "Одна клавиша для раскладки", "Короткое нажатие и отпускание без других клавиш, щелчков и прокрутки.", taps, _draftHotkeys.LayoutTapKey.ToString());
        _convertTap = Choice(card, "Одна клавиша для слова", "Выполняет ручное преобразование слова. Долгое удержание и сочетания не срабатывают.", taps, _draftHotkeys.ConvertTapKey.ToString());
        _layoutTap.SelectionChanged += (_, _) => _draftHotkeys.LayoutTapKey = ParseInt(_layoutTap.SelectedValue as string ?? "0", 0);
        _convertTap.SelectionChanged += (_, _) => _draftHotkeys.ConvertTapKey = ParseInt(_convertTap.SelectedValue as string ?? "0", 0);
    }

    private static string ChordText(KeyChord chord) => chord.IsAssigned ? chord.ToString() : L.T("Не назначено");

    private void BuildSelectionPreferences(Panel page)
    {
        var card = Card(page, "Выделенный текст");
        var choices = new[] { new UiChoice("auto", "Автоматически / выбор"), new UiChoice("en", "English", true), new UiChoice("ru", "Русский", true), new UiChoice("uk", "Українська", true) };
        _selectionTarget = Choice(card, "Раскладка результата", "При неоднозначности RU/UK предлагается выбор с предпросмотром.", choices, _cfg.Selection.TargetLanguage);
        _transliteration = Choice(card, "Правила транслитерации", "Русская и украинская таблицы различаются. Обратное преобразование требует предпросмотра.", choices.Where(c => c.Id != "en").ToArray(), _cfg.Selection.TransliterationLanguage);
    }

    private void BuildAutomationPreferences(Panel page)
    {
        var card = Card(page, "Дополнительная автоматика");
        _respectManual = Toggle(card, "Уважать ручной выбор раскладки", _cfg.Layout.RespectManualLayout, "Первое слово после системного переключения языка остаётся в выбранной раскладке.");
        _correctIdle = Toggle(card, "Исправлять после паузы", _cfg.Layout.CorrectOnIdle, "Проверяет незавершённое слово после остановки ввода. Отменяется при смене поля или курсора.");
        _idleDelay = Number(card, "Пауза перед проверкой, мс", _cfg.Layout.IdleDelayMs.ToString(), "От 350 до 5000 мс. По умолчанию 900 мс.");
        _earlySwitch = Toggle(card, "Раннее переключение раскладки", _cfg.Layout.EarlySwitch, "Переключает префикс только без известных продолжений в исходном языке и с несколькими продолжениями в целевом.");
        _earlyLength = Number(card, "Букв до ранней проверки", _cfg.Layout.EarlyMinLength.ToString(), "От 4 до 12. При неоднозначности программа ждёт окончания слова.");
        _contextMode = Choice(card, "Контекстная проверка", "Учитывает язык последних шести слов только в памяти, до 30 секунд. Режим наблюдения не меняет решения.",
            [new("off", "Выключено"), new("observe", "Наблюдать"), new("assist", "Применять")], _cfg.Layout.ContextMode);
        AutomationProperties.SetAutomationId(_contextMode, "context.mode");
    }

    private static ComboBox Choice(StackPanel card, string title, string description, IReadOnlyList<UiChoice> choices, string value)
    {
        var control = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = value, Width = 185 };
        Row(card, title, description, control);
        return control;
    }

    private void ReadFeaturePreferences(AppConfig config)
    {
        config.Typo.EnglishEnabled = _englishEnabled.IsChecked == true;
        config.Layout.RespectManualLayout = _respectManual.IsChecked == true;
        config.Layout.CorrectOnIdle = _correctIdle.IsChecked == true;
        config.Layout.IdleDelayMs = ParseInt(_idleDelay.Text, _cfg.Layout.IdleDelayMs);
        config.Layout.EarlySwitch = _earlySwitch.IsChecked == true;
        config.Layout.EarlyMinLength = ParseInt(_earlyLength.Text, _cfg.Layout.EarlyMinLength);
        config.Layout.ContextMode = _contextMode.SelectedValue as string ?? "off";
        config.Layout.Rules = _draftLayoutRules.Select(r => r with { }).ToList();
        config.Tray.ShowLayoutIndicator = _layoutIndicator.IsChecked == true;
        config.Tray.ShowCaretIndicator = _caretIndicator.IsChecked == true;
        config.Languages.UseApplicationLayouts = _useApplicationLayouts.IsChecked == true;
        config.Languages.ApplicationLayouts = _applicationLanguages.ToDictionary(p => p.Key, p => (string)p.Value.SelectedValue, StringComparer.OrdinalIgnoreCase);
        config.Learning.MaxEntries = ParseInt(_historyLimit.Text, _cfg.Learning.MaxEntries);
        config.Selection.TargetLanguage = _selectionTarget.SelectedValue as string ?? "auto";
        config.Selection.TransliterationLanguage = _transliteration.SelectedValue as string ?? "auto";
    }

    private void InstallDefaultTracking()
    {
        var defaults = new AppConfig();
        void ToggleDefault(CheckBox control, bool value, string id) => TrackDefault(control, value, () => control.IsChecked == true, () => control.IsChecked = value, id);
        void NumberDefault(TextBox control, object value, string id) => TrackDefault(control, value, () => control.Text, () => control.Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", id);
        void ChoiceDefault(ComboBox control, string value, string id) => TrackDefault(control, value, () => control.SelectedValue, () => control.SelectedValue = value, id);
        ToggleDefault(_enabled, defaults.General.Enabled, "general.enabled");
        ToggleDefault(_autostart, defaults.General.Autostart, "general.autostart");
        ToggleDefault(_beepTypo, defaults.Sound.BeepOnTypo, "sound.typo");
        ToggleDefault(_beepLayout, defaults.Sound.BeepOnLayout, "sound.layout");
        ToggleDefault(_suggestEnabled, defaults.Suggestions.Enabled, "suggestions.enabled");
        ToggleDefault(_learnLog, defaults.Learning.Enabled, "history.enabled");
        ToggleDefault(_layoutIndicator, defaults.Tray.ShowLayoutIndicator, "tray.layout");
        ToggleDefault(_caretIndicator, defaults.Tray.ShowCaretIndicator, "tray.caret");
        ToggleDefault(_useApplicationLayouts, defaults.Languages.UseApplicationLayouts, "languages.applications");
        ToggleDefault(_typoEnabled, defaults.Typo.Enabled, "typo.enabled");
        ToggleDefault(_englishEnabled, defaults.Typo.EnglishEnabled, "typo.english");
        ToggleDefault(_twoCaps, defaults.Case.FixTwoCapitals, "case.two-capitals");
        ToggleDefault(_ngramEnabled, defaults.Ngram.Enabled, "ngram.enabled");
        ToggleDefault(_ngramFreq, defaults.Ngram.WeightByFrequency, "ngram.frequency");
        ToggleDefault(_layoutEnabled, defaults.Layout.Enabled, "layout.enabled");
        ToggleDefault(_fixTypoOnSwitch, defaults.Layout.FixTypoOnSwitch, "layout.fix-typo");
        ToggleDefault(_normMixed, defaults.Layout.NormalizeMixedScript, "layout.mixed");
        ToggleDefault(_domainExt, defaults.Layout.SwitchKnownDomainExt, "layout.domains");
        ToggleDefault(_protectTech, defaults.Layout.ProtectTechnical, "layout.technical");
        ToggleDefault(_respectManual, defaults.Layout.RespectManualLayout, "layout.manual");
        ToggleDefault(_correctIdle, defaults.Layout.CorrectOnIdle, "layout.idle");
        ToggleDefault(_earlySwitch, defaults.Layout.EarlySwitch, "layout.early");
        ToggleDefault(_skipPw, defaults.Exclusions.SkipPasswordFields, "exclusions.password");
        NumberDefault(_threshold, defaults.Suggestions.Threshold, "suggestions.threshold");
        NumberDefault(_historyLimit, defaults.Learning.MaxEntries, "history.limit");
        NumberDefault(_minWordLen, defaults.Typo.MinWordLength, "typo.minimum-length");
        NumberDefault(_scoreGap, defaults.Typo.ScoreGap, "typo.confidence");
        NumberDefault(_dominance, defaults.Typo.DominanceRatio, "typo.dominance");
        NumberDefault(_minFreq, defaults.Typo.MinFrequency, "typo.frequency");
        NumberDefault(_layoutMinLen, defaults.Layout.MinWordLength, "layout.minimum-length");
        NumberDefault(_fixTypoMinLen, defaults.Layout.FixTypoMinLength, "layout.typo-length");
        NumberDefault(_idleDelay, defaults.Layout.IdleDelayMs, "layout.idle-delay");
        NumberDefault(_earlyLength, defaults.Layout.EarlyMinLength, "layout.early-length");
        NumberDefault(_languageOrder, string.Join(", ", defaults.Languages.Order), "languages.order");
        ChoiceDefault(_contextMode, "off", "context.mode");
        ChoiceDefault(_layoutTap, "0", "hotkeys.layout-tap");
        ChoiceDefault(_convertTap, "0", "hotkeys.convert-tap");
        ChoiceDefault(_selectionTarget, "auto", "selection.target");
        ChoiceDefault(_transliteration, "auto", "selection.transliteration");
        foreach (var language in _languageChecks) ToggleDefault(language.Value, defaults.Languages.Enabled.Contains(language.Key), "languages." + language.Key);
        foreach (var item in _hotkeyEditors)
        {
            var command = item.Key; var control = item.Value; var chord = defaults.Hotkeys.Get(command);
            TrackDefault(control, chord.ToString(), () => _draftHotkeys.Get(command).ToString(), () => { _draftHotkeys.Set(command, chord); control.Text = ChordText(chord); }, "hotkeys." + command);
        }
        if (_ed1.Parent is FrameworkElement group)
        {
            var refresh = TrackDefault(group, 1, () => _ed2.IsChecked == true ? 2 : 1, () => _ed1.IsChecked = true, "typo.distance");
            _ed1.Checked += (_, _) => refresh(); _ed2.Checked += (_, _) => refresh();
        }
    }

    private static Action TrackDefault(FrameworkElement control, object? defaultValue, Func<object?> value, Action reset, string id)
    {
        if (control.Parent is not Grid parent) return () => { };
        int column = Grid.GetColumn(control);
        parent.Children.Remove(control);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(panel, column); parent.Children.Add(panel); panel.Children.Add(control);
        var button = new Button { MinWidth = 26, Width = 26, Height = 26, Padding = new Thickness(4), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Content = new System.Windows.Shapes.Path { Width = 14, Height = 14, Stretch = Stretch.Uniform, Data = Geometry.Parse("M3,7 A6,6 0 1 1 3,13 M3,3 V7 H7"), Stroke = Theme.Muted, StrokeThickness = 1.6 };
        L.Bind(button, ToolTipService.ToolTipProperty, "Вернуть значение по умолчанию");
        L.Bind(button, AutomationProperties.NameProperty, "Сбросить настройку");
        AutomationProperties.SetAutomationId(button, "reset." + id);
        var marker = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Theme.Accent, Margin = new Thickness(6, 0, 0, 0) };
        L.Bind(marker, ToolTipService.ToolTipProperty, "Отличается от значения по умолчанию");
        panel.Children.Add(button); panel.Children.Add(marker);
        string Canonical(object? item)
        {
            string text = Convert.ToString(item, CultureInfo.InvariantCulture)?.Trim() ?? "";
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number.ToString("R", CultureInfo.InvariantCulture) : text;
        }
        void Refresh() { bool changed = Canonical(value()) != Canonical(defaultValue); button.IsEnabled = changed; marker.Visibility = changed ? Visibility.Visible : Visibility.Hidden; }
        button.Click += (_, _) => { reset(); Refresh(); };
        if (control is TextBox text) text.TextChanged += (_, _) => Refresh();
        if (control is CheckBox check) { check.Checked += (_, _) => Refresh(); check.Unchecked += (_, _) => Refresh(); }
        if (control is ComboBox combo) combo.SelectionChanged += (_, _) => Refresh();
        Refresh();
        return Refresh;
    }
}
