using TypeSwitcher.Config;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;
using TypeSwitcher.Tray;

namespace TypeSwitcher;

public partial class App
{
    private SelectionService? _selectionService;
    private SelectionChoiceWindow? _selectionChoice;
    private CancellationTokenSource? _selectionCancellation;

    private async void ExecuteSelection(InputCommand command, InputTarget target)
    {
        _selectionCancellation?.Cancel(); _selectionCancellation?.Dispose();
        _selectionCancellation = new();
        CancellationToken cancellation = _selectionCancellation.Token;
        _selectionChoice?.Close();
        try
        {
            _selectionService ??= new(_exclusions, () => _processor.Revision);
            var selected = await _selectionService.CaptureAsync(target, cancellation);
            if (cancellation.IsCancellationRequested || !_cfg.General.Enabled) return;
            var active = LanguageProfile.FromLayout(KeyTranslator.GetForegroundLayout());
            var choices = new List<SelectionChoice>();
            bool preview = false;
            switch (command)
            {
                case InputCommand.SelectionLayout:
                    foreach (var language in SelectionText.Targets(selected.Text, active, _cfg).Where(l => _layout.Get(l) != IntPtr.Zero))
                        choices.Add(new(language, SelectionText.ConvertLayout(selected.Text, language, active, _dict.IsValid)));
                    break;
                case InputCommand.SelectionCase:
                    choices.Add(new(LayoutTarget.None, SelectionText.ChangeCase(selected.Text)));
                    break;
                case InputCommand.SelectionTransliterate:
                    var source = LanguageProfile.Find(_cfg.Selection.TransliterationLanguage)?.Target ?? SelectionText.SourceLanguage(selected.Text, active);
                    if (source == LayoutTarget.En) source = active == LayoutTarget.Uk ? LayoutTarget.Uk : LayoutTarget.Ru;
                    choices.Add(new(LayoutTarget.None, SelectionText.Transliterate(selected.Text, source)));
                    preview = !selected.Text.Any(LanguageProfile.IsCyrillic);
                    break;
            }
            if (choices.Count == 0) { SelectionNotice("Нет подходящей установленной раскладки."); return; }
            if (choices.Count == 1 && !preview) await ApplySelection(selected, choices[0], false, cancellation);
            else
            {
                _selectionChoice = new(preview ? "Проверьте обратную транслитерацию" : "Выберите раскладку результата", choices,
                    async choice => await ApplySelection(selected, choice, true, cancellation));
                _selectionChoice.Show();
            }
        }
        catch (SelectionUnavailableException ex) { if (!cancellation.IsCancellationRequested) SelectionNotice(ex.Message); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!cancellation.IsCancellationRequested) SelectionNotice("Поле ввода недоступно."); }
    }

    private async Task ApplySelection(SelectionService.Snapshot snapshot, SelectionChoice choice, bool confirmed, CancellationToken cancellation)
    {
        try
        {
            if (!_cfg.General.Enabled || cancellation.IsCancellationRequested) return;
            bool changed = await _selectionService!.ReplaceAsync(snapshot, choice.Text, _layout.Get(choice.Target), confirmed, cancellation);
            if (changed && _cfg.Tray.BlinkOnAction) _tray.Blink();
            else if (!changed && !cancellation.IsCancellationRequested) SelectionNotice("Выделение не изменилось.");
        }
        catch (SelectionUnavailableException ex) { if (!cancellation.IsCancellationRequested) SelectionNotice(ex.Message); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!cancellation.IsCancellationRequested) SelectionNotice("Поле ввода недоступно."); }
    }

    private void SelectionNotice(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _selectionChoice?.Close();
        _selectionChoice = new(message, []);
        _selectionChoice.Show();
    }
}
