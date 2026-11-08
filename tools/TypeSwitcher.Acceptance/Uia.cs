using System.Diagnostics;
using System.Windows.Automation;

namespace TypeSwitcher.Acceptance;

internal static class Uia
{
    public static PropertyCondition Type(ControlType type) => new(AutomationElement.ControlTypeProperty, type);
    public static PropertyCondition Name(string name) => new(AutomationElement.NameProperty, name);
    public static PropertyCondition Id(string id) => new(AutomationElement.AutomationIdProperty, id);
    public static IEnumerable<AutomationElement> Children(AutomationElement parent, Condition condition)
        => parent.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>();

    public static T Wait<T>(Func<T?> probe, string description, int milliseconds = 15000) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            try { if (probe() is { } result) return result; }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            Thread.Sleep(80);
        }
        throw new TimeoutException(description);
    }

    public static void Until(Func<bool> probe, string description, int milliseconds = 15000)
        => Wait(() => probe() ? new object() : null, description, milliseconds);

    public static IEnumerable<AutomationElement> Windows(int processId)
    {
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId)).Cast<AutomationElement>().ToArray();
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Children(root, new AndCondition(Type(ControlType.Window),
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))) yield return child;
        }
    }

    public static AutomationElement Window(int processId, string title) => Wait(() =>
        Windows(processId).FirstOrDefault(w => w.Current.Name.Contains(title, StringComparison.Ordinal)), "Window: " + title);

    public static AutomationElement Find(AutomationElement parent, Condition condition, string description)
        => Wait(() => parent.FindFirst(TreeScope.Descendants, condition), description);

    public static AutomationElement ByName(AutomationElement parent, string name, ControlType? type = null)
        => Find(parent, type == null ? Name(name) : new AndCondition(Name(name), Type(type)), name);

    public static Task Invoke(AutomationElement element)
        => Task.Run(() => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke());

    public static void Click(AutomationElement parent, string name)
    {
        var task = Invoke(ByName(parent, name, ControlType.Button));
        if (!task.Wait(1000)) return; // A modal file picker may keep Invoke outstanding.
        task.GetAwaiter().GetResult();
    }

    public static void Select(AutomationElement element) => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    public static void Navigate(AutomationElement window, string page) => Select(ByName(window, page, ControlType.RadioButton));
    public static string Value(AutomationElement edit) => ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    public static void SetValue(AutomationElement edit, string value) => ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    public static bool Checked(AutomationElement element) => ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
    public static void Toggle(AutomationElement element, bool value)
    {
        if (Checked(element) != value) ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Until(() => Checked(element) == value, "Toggle did not change.");
    }

    public static AutomationElement RowControl(AutomationElement window, string label, ControlType controlType)
    {
        var named = window.FindFirst(TreeScope.Descendants, new AndCondition(Name(label), Type(controlType)));
        if (named != null) return named;
        AutomationElement? current = ByName(window, label, ControlType.Text);
        for (int i = 0; current != null && i < 6; i++)
        {
            current = TreeWalker.RawViewWalker.GetParent(current);
            if (current == null) break;
            var controls = Children(current, Type(controlType)).ToArray();
            if (controls.Length == 1) return controls[0];
        }
        throw new InvalidOperationException("Control next to label: " + label);
    }

    public static void Choose(AutomationElement combo, string itemName, int processId)
    {
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var item = Wait(() =>
        {
            var found = combo.FindFirst(TreeScope.Descendants, new AndCondition(Name(itemName), Type(ControlType.ListItem)));
            if (found != null) return found;
            return Windows(processId).SelectMany(w => Children(w, new AndCondition(Name(itemName), Type(ControlType.ListItem)))).FirstOrDefault();
        }, "Combo item: " + itemName);
        Select(item);
        expand.Collapse();
    }

    public static void Close(AutomationElement window, Process process)
    {
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Until(() => process.HasExited, "Application did not close.");
    }

    public static void FileDialog(int processId, string path, string title = "Выберите")
    {
        var dialog = Window(processId, title);
        var edit = Wait(() =>
        {
            var direct = dialog.FindFirst(TreeScope.Descendants, new AndCondition(Type(ControlType.Edit), Id("1148")));
            if (direct != null) return direct;
            var container = dialog.FindFirst(TreeScope.Descendants, Id("1148"));
            if (container != null) return container.FindFirst(TreeScope.Descendants, Type(ControlType.Edit));
            return Children(dialog, Type(ControlType.Edit)).FirstOrDefault(e => e.Current.Name.Contains("Имя файла", StringComparison.OrdinalIgnoreCase)
                || e.Current.Name.Contains("File name", StringComparison.OrdinalIgnoreCase));
        }, "File name edit");
        SetValue(edit, path);
        var open = dialog.FindFirst(TreeScope.Descendants, new AndCondition(Type(ControlType.Button), Id("1")))
            ?? dialog.FindFirst(TreeScope.Descendants, new AndCondition(Type(ControlType.Button), Name("Открыть")))
            ?? throw new InvalidOperationException("Open button not found.");
        _ = Invoke(open);
        Until(() => !Windows(processId).Any(w => w.Current.Name.StartsWith(title, StringComparison.Ordinal)), "File picker did not close.");
    }
}
