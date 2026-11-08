using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using Microsoft.Win32;
using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using System.Windows.Automation;
using System.Windows.Data;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

/// <summary>Управление поставляемыми и импортированными словарями.</summary>
public sealed class DictionaryLibraryWindow : Window
{
    private sealed record Row(string Id, string Name, string Language, string Kind, bool Enabled, string Detail, AppConfig.DictionaryEntry? Entry)
    {
        public override string ToString() => $"{(Enabled ? "●" : "○")} {Name}  [{Language}, {Kind}]";
    }

    private readonly ConfigStore _store;
    private readonly AppConfig _cfg;
    private readonly Action _onSaved;
    private readonly ListBox _list;
    private readonly ComboBox _language;
    private readonly ComboBox _kind;
    private readonly CheckBox _enabled;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;
    private readonly WrapPanel _actions;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _refreshing;
    private bool _closed;
    private bool _busy;

    public DictionaryLibraryWindow(ConfigStore store, AppConfig cfg, Action onSaved)
    {
        _store = store; _cfg = cfg; _onSaved = onSaved;
        UiPreferences.Initialize(store, cfg);
        _list = new ListBox { Background = Theme.PanelBg, Foreground = Theme.Fg, BorderBrush = Theme.Border, Padding = new Thickness(8) };
        L.Bind(this, TitleProperty, "TypeSwitcher — подключённые словари");
        Width = 880; Height = 560; MinWidth = 720; MinHeight = 440;
        Background = Theme.WinBg; Foreground = Theme.Fg; FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Attach(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { FontSize = 24, Margin = new Thickness(0, 0, 0, 8) }.Localize("Подключённые словари"));
        header.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 12)
        }.Localize("Hunspell: .dic + .aff в UTF-8. Список слов: одно слово на строку. Частоты: слово число.\nЯзыки обработки включаются на странице «Языки». Частоты дополняют базовые; поздний импорт имеет приоритет."));
        _actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _language = new ComboBox { Width = 145, ItemsSource = LanguageProfile.All.Select(p => new UiChoice(p.Id, p.Name)).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = "uk", Margin = new Thickness(0, 0, 8, 0) };
        _kind = new ComboBox { Width = 165, ItemsSource = new[] { new UiChoice("words", "Список слов"), new UiChoice("hunspell", "Словарь Hunspell"), new UiChoice("frequency", "Частотный словарь") }, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        L.Bind(_language, AutomationProperties.NameProperty, "Язык словаря");
        var languageItemStyle = new Style(typeof(ComboBoxItem), (Style)FindResource(typeof(ComboBoxItem)));
        languageItemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("Label")));
        _language.ItemContainerStyle = languageItemStyle;
        _kind.ItemContainerStyle = languageItemStyle;
        L.Bind(_kind, AutomationProperties.NameProperty, "Формат словаря");
        L.Bind(_list, AutomationProperties.NameProperty, "Подключённые словари");
        _actions.Children.Add(_language); _actions.Children.Add(_kind);
        AddButton("Импорт…", () => Import(null));
        AddButton("Обновить…", () => { if (_list.SelectedItem is Row { Entry: not null } row) Import(row.Entry); });
        AddButton("Удалить", Remove);
        header.Children.Add(_actions);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _enabled = new CheckBox { Margin = new Thickness(0, 0, 0, 8) }.Localize(ContentControl.ContentProperty, "Использовать выбранный словарь");
        _enabled.Checked += (_, _) => SetEnabled(true);
        _enabled.Unchecked += (_, _) => SetEnabled(false);
        _detail = new TextBlock { Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_enabled); footer.Children.Add(_detail); footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _list.SelectionChanged += (_, _) => ShowSelection();
        root.Children.Add(_list); Content = root;
        L.Changed += LanguageChanged;
        Closed += (_, _) => { _closed = true; _cancellation.Cancel(); L.Changed -= LanguageChanged; };
        Refresh();
    }

    private void AddButton(string label, Action action)
    {
        var button = new Button { Margin = new Thickness(0, 0, 8, 0) }.Localize(ContentControl.ContentProperty, label);
        button.Click += (_, _) => action();
        _actions.Children.Add(button);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.UpdateCaption(this);
    }

    private void LanguageChanged(object? sender, EventArgs e) => Refresh((_list.SelectedItem as Row)?.Id);

    private void Refresh(string? selected = null)
    {
        _refreshing = true;
        var rows = DictionaryManager.Builtins(_cfg).Select(d => new Row(d.Id, L.T(d.Name), d.Language, d.Kind,
            !_cfg.Dictionaries.DisabledBuiltins.Contains(d.Id), L.T("Поставляется с приложением. {0}", _store.ResolveAppPath(d.File)), null)).ToList();
        rows.AddRange(_cfg.Dictionaries.Installed.Select(d => new Row(d.Id, d.Name, d.Language, d.Kind, d.Enabled,
            L.T("Импорт: {0:g}. Записей: {1}. SHA256: {2}", d.ImportedAt.LocalDateTime, d.WordCount, d.Sha256), d)));
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        _refreshing = false;
        ShowSelection();
    }

    private void ShowSelection()
    {
        if (_refreshing) return;
        _refreshing = true;
        if (_list.SelectedItem is Row row)
        {
            _enabled.IsEnabled = !_busy; _enabled.IsChecked = row.Enabled;
            _detail.Text = row.Detail + (_cfg.Languages.Enabled.Contains(row.Language) ? "" : "\n" + L.T("Этот язык сейчас выключен в настройках обработки."));
        }
        else { _enabled.IsEnabled = false; _detail.Text = ""; }
        _refreshing = false;
    }

    private void SetEnabled(bool enabled)
    {
        if (_refreshing || _list.SelectedItem is not Row row) return;
        var disabled = _cfg.Dictionaries.DisabledBuiltins.ToList();
        bool old = row.Entry?.Enabled ?? row.Enabled;
        try
        {
            if (row.Entry != null) row.Entry.Enabled = enabled;
            else if (enabled) _cfg.Dictionaries.DisabledBuiltins.Remove(row.Id);
            else if (!_cfg.Dictionaries.DisabledBuiltins.Contains(row.Id)) _cfg.Dictionaries.DisabledBuiltins.Add(row.Id);
            _store.Save(_cfg); _onSaved(); Refresh(row.Id);
            L.Bind(_status, TextBlock.TextProperty, "Сохранено. Рабочий набор обновляется в фоне.");
        }
        catch (Exception ex)
        {
            _cfg.Dictionaries.DisabledBuiltins = disabled;
            if (row.Entry != null) row.Entry.Enabled = old;
            Refresh(row.Id); _status.Text = ex.Message;
        }
    }

    private async void Import(AppConfig.DictionaryEntry? replace)
    {
        string language = replace?.Language ?? (string)_language.SelectedValue;
        string kind = replace?.Kind ?? (string)_kind.SelectedValue;
        var dialog = new OpenFileDialog { Title = L.T("Выберите словарь"), Filter = kind == "hunspell" ? "Hunspell (*.dic)|*.dic" : L.T("Текстовый словарь (*.txt)|*.txt|Все файлы|*.*") };
        if (dialog.ShowDialog(this) != true) return;
        string? aff = null;
        if (kind == "hunspell")
        {
            aff = Path.ChangeExtension(dialog.FileName, ".aff");
            if (!File.Exists(aff))
            {
                var affDialog = new OpenFileDialog { Title = L.T("Выберите соответствующий .aff"), Filter = "Hunspell (*.aff)|*.aff" };
                if (affDialog.ShowDialog(this) != true) return;
                aff = affDialog.FileName;
            }
        }
        _busy = true;
        _actions.IsEnabled = false; _list.IsEnabled = false; _enabled.IsEnabled = false;
        L.Bind(_status, TextBlock.TextProperty, "Проверка и импорт…");
        AppConfig.DictionaryEntry? imported = null;
        bool committed = false;
        var before = _cfg.Dictionaries.Installed.ToList();
        try
        {
            imported = await Task.Run(() => DictionaryManager.Import(_store, dialog.FileName, language, kind, aff, _cancellation.Token));
            if (_closed) { DictionaryManager.RemoveFiles(_store, imported); return; }
            if (replace != null)
            {
                imported.Enabled = replace.Enabled;
                int index = _cfg.Dictionaries.Installed.IndexOf(replace);
                if (index >= 0) _cfg.Dictionaries.Installed[index] = imported;
                else _cfg.Dictionaries.Installed.Add(imported);
            }
            else _cfg.Dictionaries.Installed.Add(imported);
            _store.Save(_cfg);
            committed = true;
            _onSaved(); Refresh(imported.Id);
            L.Bind(_status, TextBlock.TextProperty, "Импортирован «{0}»: {1} записей. Набор обновляется в фоне.", imported.Name, imported.WordCount);
            if (replace != null)
            {
                try { DictionaryManager.RemoveFiles(_store, replace); }
                catch (IOException) { /* Старый набор ещё может загружаться; он уже отключён. */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!committed) _cfg.Dictionaries.Installed = before;
            if (!committed && imported != null)
            {
                try { DictionaryManager.RemoveFiles(_store, imported); } catch (IOException) { }
            }
            if (!_closed) L.Bind(_status, TextBlock.TextProperty, "Импорт не выполнен: {0}", ex.Message);
        }
        finally
        {
            _busy = false;
            if (!_closed) { _actions.IsEnabled = true; _list.IsEnabled = true; ShowSelection(); }
        }
    }

    private void Remove()
    {
        if (_list.SelectedItem is not Row { Entry: not null } row)
        {
            L.Bind(_status, TextBlock.TextProperty, "Поставляемый словарь можно отключить. Удаление доступно для импортированных наборов.");
            return;
        }
        var before = _cfg.Dictionaries.Installed.ToList();
        try
        {
            _cfg.Dictionaries.Installed.Remove(row.Entry);
            _store.Save(_cfg);
        }
        catch (Exception ex) { _cfg.Dictionaries.Installed = before; _status.Text = ex.Message; return; }
        _onSaved(); Refresh();
        try { DictionaryManager.RemoveFiles(_store, row.Entry); L.Bind(_status, TextBlock.TextProperty, "Словарь удалён."); }
        catch (Exception ex) { L.Bind(_status, TextBlock.TextProperty, "Словарь отключён, но файлы удалить не удалось: {0}", ex.Message); }
    }
}
