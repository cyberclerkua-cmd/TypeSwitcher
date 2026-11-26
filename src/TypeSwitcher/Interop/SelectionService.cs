using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using TypeSwitcher.Core;

namespace TypeSwitcher.Interop;

public sealed class SelectionUnavailableException(string message) : Exception(message);

/// <summary>Bounded UI Automation operations on one background COM thread. Does not use the clipboard.</summary>
public sealed class SelectionService : IDisposable
{
    public sealed class Snapshot
    {
        public string Text { get; }
        internal readonly AutomationElement Element;
        internal readonly TextPatternRange Range;
        internal readonly int[] RuntimeId;
        internal readonly InputTarget Target;
        internal readonly long Revision;
        internal Snapshot(string text, AutomationElement element, TextPatternRange range, InputTarget target, long revision)
        {
            Text = text; Element = element; Range = range.Clone(); RuntimeId = element.GetRuntimeId(); Target = target; Revision = revision;
        }
    }

    private readonly ExclusionManager _exclusions;
    private readonly Func<long> _revision;
    private readonly BlockingCollection<Action> _work = new(1);
    private bool _disposed;

    public SelectionService(ExclusionManager exclusions, Func<long> revision)
    {
        _exclusions = exclusions; _revision = revision;
        var thread = new Thread(() => { foreach (var action in _work.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "TypeSwitcher selection" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<Snapshot> CaptureAsync(InputTarget target, CancellationToken cancellation = default)
    {
        long revision = _revision();
        return RunAsync(token =>
        {
            CheckWindow(target);
            var element = AutomationElement.FocusedElement ?? throw Refuse("Поле ввода недоступно.");
            CheckElement(element);
            var range = GetRange(element);
            if (range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true) throw Refuse("Поле доступно только для чтения.");
            if (element.Current.ControlType == ControlType.Document && range.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false
                && element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true) is not false)
                throw Refuse("Выделение должно находиться в редактируемом поле.");
            string text = range.GetText(SelectionText.MaximumCharacters + 1);
            if (!SelectionText.IsSupported(text)) throw Refuse("Выделите одну строку длиной до 2048 символов.");
            token.ThrowIfCancellationRequested();
            if (_revision() != revision) throw Refuse("Ввод изменился. Повторите команду.");
            CheckWindow(target);
            return new Snapshot(text, element, range, target, revision);
        }, cancellation);
    }

    public Task<bool> MatchesBeforeCaretAsync(InputTarget target, string expected)
        => RunAsync(token =>
        {
            if (!_exclusions.IsCurrentTarget(target) || _exclusions.IsExcludedTarget(target) || _exclusions.IsPasswordFast()) return false;
            var element = AutomationElement.FocusedElement;
            if (element == null || element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false ||
                !element.Current.IsEnabled || element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true) return false;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return true;
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges.Length != 1) return false;
            var span = ranges[0];
            if (span.CompareEndpoints(TextPatternRangeEndpoint.Start, span, TextPatternRangeEndpoint.End) != 0 ||
                span.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true) return false;
            span.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -expected.Length);
            token.ThrowIfCancellationRequested();
            return span.GetText(expected.Length + 1) == expected && _exclusions.IsCurrentTarget(target);
        }, CancellationToken.None);

    public Task<bool> ReplaceAsync(Snapshot snapshot, string replacement, IntPtr targetLayout, bool confirmedPreview = false, CancellationToken cancellation = default)
        => RunAsync(token =>
        {
            if (!SelectionText.IsSupported(replacement)) throw Refuse("Результат слишком велик или содержит управляющие символы.");
            long expected = confirmedPreview ? _revision() : snapshot.Revision;
            CheckSnapshot(snapshot, expected);
            token.ThrowIfCancellationRequested();
            if (replacement == snapshot.Text) return false;
            IntPtr handle = (IntPtr)snapshot.Element.Current.NativeWindowHandle;
            var name = new System.Text.StringBuilder(128);
            if (handle != IntPtr.Zero) NativeMethods.GetClassName(handle, name, name.Capacity);
            bool nativeEdit = name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase) || name.ToString().StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
            bool replaced;
            if (nativeEdit)
            {
                CheckSnapshot(snapshot, expected);
                token.ThrowIfCancellationRequested();
                replaced = NativeMethods.SendTextMessage(handle, 0x00C2, (IntPtr)1, replacement, 0x0002, 250, out _) != IntPtr.Zero;
            }
            else
            {
                CheckSnapshot(snapshot, expected);
                token.ThrowIfCancellationRequested();
                replaced = Replacer.Replace(0, replacement);
            }
            if (!replaced) throw Refuse("Замена не завершена. Проверьте текст в приложении.");
            if (targetLayout != IntPtr.Zero && SameFocusedElement(snapshot)) LayoutSwitcher.ActivateFor(snapshot.Target, targetLayout);

            // Ask the provider to select the inserted range, and only accept a range with exactly that text.
            // Different providers count UTF-16, Unicode scalars or graphemes differently.
            var deadline = Environment.TickCount64 + 500;
            do
            {
                token.ThrowIfCancellationRequested();
                if (_revision() != expected || !SameFocusedElement(snapshot)) return true;
                var caret = GetRange(snapshot.Element);
                if (caret.GetText(SelectionText.MaximumCharacters + 1) == replacement) return true;
                if (caret.GetText(1).Length == 0)
                {
                    foreach (int count in new[] { replacement.Length, replacement.EnumerateRunes().Count(), new StringInfo(replacement).LengthInTextElements }.Distinct())
                    {
                        var candidate = caret.Clone();
                        candidate.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -count);
                        if (candidate.GetText(SelectionText.MaximumCharacters + 1) != replacement) continue;
                        if (_revision() == expected && SameFocusedElement(snapshot)) candidate.Select();
                        return true;
                    }
                }
                Thread.Sleep(15);
            } while (Environment.TickCount64 < deadline);
            return true;
        }, cancellation);

    private void CheckWindow(InputTarget target)
    {
        if (target.Window == IntPtr.Zero || !_exclusions.IsCurrentTarget(target)) throw Refuse("Поле или положение курсора изменилось.");
        if (_exclusions.IsExcludedTarget(target))
            throw Refuse("В этом приложении преобразование выделения недоступно.");
        var name = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(target.Window, name, name.Capacity);
        if (name.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") throw Refuse("Преобразование выделения в терминале отключено.");
    }

    private static void CheckElement(AutomationElement element)
    {
        if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false)
            throw Refuse("Защищённое поле не обрабатывается.");
        var type = element.Current.ControlType;
        if (!element.Current.IsEnabled || !element.Current.HasKeyboardFocus || type != ControlType.Edit && type != ControlType.Document)
            throw Refuse("Выделение должно находиться в редактируемом поле.");
        if (element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true) is true) throw Refuse("Поле доступно только для чтения.");
    }

    private static TextPatternRange GetRange(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) throw Refuse("Приложение не предоставляет выделение через UI Automation.");
        var ranges = ((TextPattern)pattern).GetSelection();
        if (ranges.Length != 1) throw Refuse("Нужно одно непрерывное выделение.");
        return ranges[0];
    }

    private bool SameFocusedElement(Snapshot snapshot)
    {
        if (NativeMethods.GetForegroundWindow() != snapshot.Target.Window) return false;
        var current = AutomationElement.FocusedElement;
        return current != null && current.GetRuntimeId().SequenceEqual(snapshot.RuntimeId);
    }

    private void CheckSnapshot(Snapshot snapshot, long revision)
    {
        CheckWindow(snapshot.Target);
        if (_revision() != revision || !SameFocusedElement(snapshot)) throw Refuse("Ввод изменился. Повторите команду.");
        CheckElement(snapshot.Element);
        var current = GetRange(snapshot.Element);
        if (current.GetText(SelectionText.MaximumCharacters + 1) != snapshot.Text
            || current.CompareEndpoints(TextPatternRangeEndpoint.Start, snapshot.Range, TextPatternRangeEndpoint.Start) != 0
            || current.CompareEndpoints(TextPatternRangeEndpoint.End, snapshot.Range, TextPatternRangeEndpoint.End) != 0)
            throw Refuse("Выделение изменилось. Повторите команду.");
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.TryAdd(() =>
        {
            try { timeout.Token.ThrowIfCancellationRequested(); completion.TrySetResult(action(timeout.Token)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) throw Refuse("Предыдущая команда ещё выполняется.");
        try { return await completion.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw Refuse("Поле не ответило вовремя. Повторите команду."); }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException)
        { throw Refuse("Поле ввода недоступно."); }
    }

    private static SelectionUnavailableException Refuse(string message) => new(message);
    public void Dispose() { _disposed = true; _work.CompleteAdding(); }
}
