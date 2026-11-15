using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public sealed partial class SettingsWindow
{
    public const string GitHubUrl = "https://github.com/cyberclerkua-cmd/TypeSwitcher";
    private ListBox _layoutRuleList = null!, _historyList = null!;
    private TextBox _rulePattern = null!, _diagnosticInput = null!, _diagnosticOutput = null!;
    private ComboBox _ruleSource = null!, _ruleTarget = null!, _ruleMatch = null!, _ruleAction = null!, _diagnosticSource = null!;
    private CheckBox _ruleCase = null!, _ruleEnabled = null!;
    private TextBlock _historySummary = null!;
    private bool _refreshingRules;
    private IReadOnlyList<WordDiagnostic> _diagnosticResults = [];
    private sealed record RuleItem(LayoutRule Rule)
    {
        public override string ToString() => $"{(Rule.Enabled ? "●" : "○")} {Rule.Pattern} · {L.T(Rule.Match switch { "prefix" => "Начинается с", "contains" => "Содержит", _ => "Совпадает целиком" })} · {L.T(Rule.Action == "keep" ? "Не переключать" : "Переключать")} · {Rule.Source.ToUpperInvariant()} → {Rule.Target.ToUpperInvariant()}";
    }
    private sealed record HistoryItem(CorrectionEntry Entry)
    {
        public override string ToString() => $"{Entry.Timestamp} · {(Entry.Event == "reject" ? L.T("Откат") : L.T("Исправление"))}\n{Entry.Original}{(Entry.Result == null ? "" : " → " + Entry.Result)}";
    }

    private static Button FeatureButton(string text, string id, Action clicked)
    {
        var button = new Button { MinWidth = 90, Margin = new Thickness(0, 0, 8, 6) }.Localize(ContentControl.ContentProperty, text);
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => UiPreferences.Save(clicked);
        return button;
    }

    private UIElement BuildLayoutRules()
    {
        var page = Page();
        page.Children.Add(new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }
            .Localize("Правила управляют раскладкой целого слова. Точное совпадение сильнее начала и вхождения; стоп-слова имеют высший приоритет."));
        _layoutRuleList = new ListBox { Height = 155, Background = Theme.CtrlBg, Foreground = Theme.Fg, BorderBrush = Theme.Border };
        AutomationProperties.SetAutomationId(_layoutRuleList, "rules.list");
        page.Children.Add(_layoutRuleList);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        buttons.Children.Add(FeatureButton("Новое правило", "rules.new", () => { _layoutRuleList.SelectedIndex = -1; LoadRule(new()); }));
        buttons.Children.Add(FeatureButton("Удалить правило", "rules.delete", () =>
        {
            if (_layoutRuleList.SelectedItem is RuleItem item) { _draftLayoutRules.Remove(item.Rule); RefreshRules(); LoadRule(new()); }
        }));
        page.Children.Add(buttons);
        var editor = Card(page, "Редактор правила");
        _rulePattern = new TextBox { Width = 185 };
        Row(editor, "Сочетание букв", "Одно слово или его часть, до 64 символов.", _rulePattern);
        _ruleSource = Choice(editor, "Исходный язык", "Область действия правила.", ScopeChoices(), "global");
        _ruleTarget = Choice(editor, "Целевая раскладка", "Используется для действия «Переключать».", LanguageChoices(), "ru");
        _ruleMatch = Choice(editor, "Условие", "При равной точности более длинное правило имеет приоритет.",
            [new("exact", "Совпадает целиком"), new("prefix", "Начинается с"), new("contains", "Содержит")], "exact");
        _ruleAction = Choice(editor, "Действие", "При полном совпадении приоритет имеет запрет переключения.",
            [new("keep", "Не переключать"), new("convert", "Переключать")], "keep");
        _ruleCase = Toggle(editor, "Учитывать регистр", false, "Различать прописные и строчные буквы в образце.");
        _ruleEnabled = Toggle(editor, "Правило включено", true, "Выключенное правило сохраняется в списке.");
        foreach (var pair in new (FrameworkElement Control, string Id)[] { (_rulePattern, "pattern"), (_ruleSource, "source"), (_ruleTarget, "target"), (_ruleMatch, "match"), (_ruleAction, "action"), (_ruleCase, "case"), (_ruleEnabled, "enabled") })
            AutomationProperties.SetAutomationId(pair.Control, "rules." + pair.Id);
        page.Children.Add(FeatureButton("Добавить / обновить правило", "rules.save", SaveRule));
        _layoutRuleList.SelectionChanged += (_, _) => { if (!_refreshingRules && _layoutRuleList.SelectedItem is RuleItem item) LoadRule(item.Rule); };
        RefreshRules();
        return page;
    }

    private static UiChoice[] LanguageChoices() => LanguageProfile.All.Select(p => new UiChoice(p.Id, p.Name, true)).ToArray();
    private static UiChoice[] ScopeChoices() => [new("global", "Все языки"), .. LanguageChoices()];
    private void LoadRule(LayoutRule rule)
    {
        _rulePattern.Text = rule.Pattern; _ruleSource.SelectedValue = rule.Source; _ruleTarget.SelectedValue = rule.Target;
        _ruleMatch.SelectedValue = rule.Match; _ruleAction.SelectedValue = rule.Action;
        _ruleCase.IsChecked = rule.CaseSensitive; _ruleEnabled.IsChecked = rule.Enabled;
    }
    private void SaveRule()
    {
        var rule = new LayoutRule
        {
            Pattern = _rulePattern.Text.Trim(), Source = _ruleSource.SelectedValue as string ?? "global", Target = _ruleTarget.SelectedValue as string ?? "ru",
            Match = _ruleMatch.SelectedValue as string ?? "exact", Action = _ruleAction.SelectedValue as string ?? "keep",
            CaseSensitive = _ruleCase.IsChecked == true, Enabled = _ruleEnabled.IsChecked == true
        };
        if (!rule.IsValid()) { L.Bind(_status, TextBlock.TextProperty, "Проверьте образец и языки правила."); return; }
        int index = _layoutRuleList.SelectedItem is RuleItem selected ? _draftLayoutRules.IndexOf(selected.Rule)
            : _draftLayoutRules.FindIndex(r => r.Pattern == rule.Pattern && r.Source == rule.Source && r.Match == rule.Match && r.CaseSensitive == rule.CaseSensitive);
        if (index >= 0) _draftLayoutRules[index] = rule;
        else if (_draftLayoutRules.Count < 1000) _draftLayoutRules.Add(rule);
        RefreshRules();
        L.Bind(_status, TextBlock.TextProperty, "Правило подготовлено. Нажмите «Сохранить».");
    }
    private void RefreshRules()
    {
        if (_layoutRuleList == null) return;
        var selected = (_layoutRuleList.SelectedItem as RuleItem)?.Rule;
        _refreshingRules = true;
        _layoutRuleList.ItemsSource = _draftLayoutRules.Select(r => new RuleItem(r)).ToArray();
        _layoutRuleList.SelectedIndex = selected == null ? -1 : _draftLayoutRules.IndexOf(selected);
        _refreshingRules = false;
    }

    private UIElement BuildDiagnostics()
    {
        var page = Page();
        page.Children.Add(new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }
            .Localize("Проверка анализатора на введённом примере. Используются значения формы и личных списков; пример не сохраняется и никуда не отправляется."));
        _diagnosticInput = new TextBox { Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = "ghbdtn vbh" };
        AutomationProperties.SetAutomationId(_diagnosticInput, "diagnostics.input");
        page.Children.Add(_diagnosticInput);
        var card = Card(page, "Параметры проверки");
        _diagnosticSource = Choice(card, "Язык примера", "Автоматическое определение использует алфавит каждого слова.", [new("auto", "По алфавиту"), .. LanguageChoices()], "auto");
        AutomationProperties.SetAutomationId(_diagnosticSource, "diagnostics.language");
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        buttons.Children.Add(FeatureButton("Проверить", "diagnostics.run", RunDiagnostics));
        buttons.Children.Add(FeatureButton("Экспорт отчёта…", "diagnostics.export", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "TypeSwitcher-check.txt", Filter = L.T("Текстовый список (*.txt)|*.txt"), InitialDirectory = _store.DataDir };
            if (dialog.ShowDialog(this) == true) _store.SaveText(dialog.FileName, _diagnosticOutput.Text);
        }));
        page.Children.Add(buttons);
        _diagnosticOutput = new TextBox { MinHeight = 230, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(_diagnosticOutput, "diagnostics.result");
        page.Children.Add(_diagnosticOutput);
        return page;
    }

    private async void RunDiagnostics()
    {
        _diagnosticCancellation?.Cancel(); _diagnosticCancellation?.Dispose(); _diagnosticCancellation = new();
        var token = _diagnosticCancellation.Token;
        try
        {
            string sample = _diagnosticInput.Text;
            if (sample.Length > 4096) { L.Bind(_status, TextBlock.TextProperty, "Пример должен содержать не более 4096 символов."); return; }
            var cfg = ReadFormConfiguration();
            CaptureUserEditors();
            var texts = new Dictionary<(string Kind, string Scope), string>(_userTexts);
            var language = LanguageProfile.Find(_diagnosticSource.SelectedValue as string)?.Target ?? LayoutTarget.None;
            L.Bind(_diagnosticOutput, TextBox.TextProperty, "Проверка…");
            var results = await Task.Run(() =>
            {
                var loaded = DictionaryManager.Load(_store, cfg, token);
                if (loaded.Errors.Any()) throw new InvalidDataException(string.Join("; ", loaded.Errors.Select(e => e.Error)));
                loaded.Dictionaries.LoadUserTexts(texts);
                return Core.Diagnostics.Analyze(loaded.Dictionaries, cfg, sample, language, token);
            }, token);
            if (token.IsCancellationRequested) return;
            _diagnosticResults = results; FormatDiagnostics();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) L.Bind(_diagnosticOutput, TextBox.TextProperty, "Ошибка проверки: {0}", ex.Message); }
    }

    private void FormatDiagnostics()
    {
        if (_diagnosticOutput == null || _diagnosticResults.Count == 0) return;
        var text = new StringBuilder();
        foreach (var item in _diagnosticResults)
        {
            var decision = item.Analysis.Applied;
            text.AppendLine($"{item.Original} → {(decision.Kind == ActionKind.None ? item.Original : decision.Replacement)}");
            text.AppendLine(L.T("Источник: {0}; результат: {1}", LanguageProfile.Find(item.Source)?.Id.ToUpperInvariant(), LanguageProfile.Find(decision.Layout)?.Id.ToUpperInvariant() ?? "—"));
            text.AppendLine(L.T("Причина: {0}", L.T(ReasonText(decision.Reason))));
            if (item.Analysis.Action == ContextAction.Suggest) text.AppendLine(L.T("Предложение контекста: {0}", item.Analysis.Proposed.Replacement));
            if (item.Analysis.Action == ContextAction.Wait) text.AppendLine(L.T("Контекст ожидает продолжения."));
            if (item.Prefix.Kind != ActionKind.None) text.AppendLine(L.T("Ранняя проверка: {0}", item.Prefix.Replacement));
            foreach (var candidate in item.Candidates) text.AppendLine(L.T("  {0}: отличий {1}, частота {2}, балл {3:0.00}", candidate.Word, candidate.Distance, candidate.Frequency, candidate.Score));
            text.AppendLine();
        }
        _diagnosticOutput.Text = text.ToString();
    }

    internal static string ReasonText(string? reason) => reason switch
    {
        "empty" => "Пустой ввод", "language-disabled" => "Язык обработки выключен", "stop-word" => "Стоп-слово",
        "personal-word" => "Слово в личном словаре", "known-word" => "Слово уже правильное", "technical-token" => "Защищённый технический токен",
        "too-short" => "Слово короче настроенного порога", "typos-disabled" => "Исправление опечаток выключено", "english-disabled" => "Английская коррекция выключена",
        "protected-shape" or "combining-marks" => "Защищённая форма слова", "low-confidence" => "Недостаточная уверенность",
        "ambiguous-layout" => "Неоднозначная раскладка", "layout-rule-keep" => "Правило запрещает переключение", "layout-rule-unavailable" => "Целевая раскладка правила недоступна",
        "layout-rule" => "Правило раскладки", "context-layout" => "Контекст разрешил неоднозначность", "early-prefix" => "Уверенный префикс другой раскладки",
        "dictionary-layout" or "raw-layout" => "Словарь подтвердил другую раскладку", "domain-layout" => "Домен или имя файла",
        "layout-and-spelling" => "Раскладка и опечатка", "spelling" => "Словарное исправление опечатки", "case" => "Исправление регистра",
        "mixed-script" => "Смешанные алфавиты", "rule" => "Пользовательская замена", _ => "Автоматическая замена не требуется"
    };

    private string HistoryPath => Path.Combine(_store.DataDir, _cfg.Learning.File);
    private UIElement BuildHistory()
    {
        var panel = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap }.Localize("Локальная история только выполненных исправлений и откатов. Включается в разделе «Общие»."));
        _historySummary = new TextBlock { Foreground = Theme.Muted, Margin = new Thickness(0, 8, 0, 8) };
        header.Children.Add(_historySummary);
        var buttons = new WrapPanel();
        buttons.Children.Add(FeatureButton("Обновить", "history.refresh", RefreshHistory));
        buttons.Children.Add(FeatureButton("Очистить историю", "history.clear", () => { CorrectionHistory.Clear(HistoryPath); RefreshHistory(); }));
        buttons.Children.Add(FeatureButton("В стоп-слова", "history.protect", ProtectHistoryWord));
        buttons.Children.Add(FeatureButton("Создать замену…", "history.rule", CreateHistoryRule));
        header.Children.Add(buttons); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        _historyList = new ListBox { Background = Theme.CtrlBg, Foreground = Theme.Fg, BorderBrush = Theme.Border };
        AutomationProperties.SetAutomationId(_historyList, "history.list");
        panel.Children.Add(_historyList);
        RefreshHistory();
        return panel;
    }

    private void RefreshHistory()
    {
        if (_historyList == null) return;
        try
        {
            var entries = CorrectionHistory.Read(HistoryPath, ParseInt(_historyLimit.Text, _cfg.Learning.MaxEntries));
            _historyList.ItemsSource = entries.Reverse().Select(e => new HistoryItem(e)).ToArray();
            L.Bind(_historySummary, TextBlock.TextProperty, "Записей: {0}", entries.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { L.Bind(_historySummary, TextBlock.TextProperty, "История временно недоступна."); }
    }
    private void ProtectHistoryWord()
    {
        if (_historyList.SelectedItem is not HistoryItem item) return;
        CaptureUserEditors();
        _userTexts[("stopwords", "global")] = ConfigStore.MergeUserList(_userTexts[("stopwords", "global")], item.Entry.Original + "\n", false);
        if (_listScope == "global") _stopWords.Text = _userTexts[("stopwords", "global")];
        L.Bind(_status, TextBlock.TextProperty, "Слово добавлено в форму. Нажмите «Сохранить».");
    }
    private void CreateHistoryRule()
    {
        if (_historyList.SelectedItem is not HistoryItem item) return;
        new RuleDialog(item.Entry.Original, (from, to) =>
        {
            CaptureUserEditors();
            _userTexts[("rules", "global")] = ConfigStore.MergeUserList(_userTexts[("rules", "global")], $"{from} = {to}\n", true);
            if (_ruleScope == "global") _rules.Text = _userTexts[("rules", "global")];
            L.Bind(_status, TextBlock.TextProperty, "Правило подготовлено. Нажмите «Сохранить».");
        }) { Owner = this }.ShowDialog();
    }

    private UIElement BuildAbout()
    {
        var page = Page();
        page.Children.Add(new TextBlock { Text = "TypeSwitcher", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Theme.Fg });
        string version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var versionText = new TextBlock { Foreground = Theme.Muted, Margin = new Thickness(0, 6, 0, 18) }.Localize("Версия {0}", version);
        AutomationProperties.SetAutomationId(versionText, "about.version"); page.Children.Add(versionText);
        foreach (string message in new[]
        {
            "Помощник ввода для Windows: исправление опечаток, переключение раскладки RU/EN/UK, смена регистра и собственные правила.",
            "Работает локально: без облачных сервисов, телеметрии и отправки набранного текста. Словари и языковые модели входят в комплект.",
            "Переносимое приложение. Настройки, личные словари и включённая пользователем история хранятся в папке data рядом с программой.",
            "Лицензия кода: PolyForm Noncommercial 1.0.0. Словари распространяются по собственным лицензиям; тексты находятся в LICENSE и dict."
        }) page.Children.Add(new TextBlock { Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) }.Localize(message));
        var link = new Hyperlink(new Run("GitHub · cyberclerkua-cmd/TypeSwitcher")) { NavigateUri = new Uri(GitHubUrl), Foreground = Theme.Accent };
        AutomationProperties.SetAutomationId(link, "about.github");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true }); }
            catch (Exception ex) { L.Bind(_status, TextBlock.TextProperty, "Не удалось открыть ссылку: {0}", ex.Message); }
            e.Handled = true;
        };
        var linkText = new TextBlock { Margin = new Thickness(0, 10, 0, 12), Foreground = Theme.Accent, TextWrapping = TextWrapping.Wrap };
        linkText.Inlines.Add(link); page.Children.Add(linkText);
        page.Children.Add(new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap }.Localize("GitHub открывается во внешнем браузере только после нажатия на ссылку."));
        return page;
    }
}
