using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

internal sealed class ApplicationPickerWindow : Window
{
    public event Action<string>? Picked;
    public ApplicationPickerWindow(bool useInputProcess = false)
    {
        Title = "TypeSwitcher"; Width = 440; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Attach(this);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var title = new TextBlock { Foreground = Theme.Fg, FontSize = 18, Margin = new Thickness(0, 0, 0, 12) }.Localize("Выберите работающее приложение");
        DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var list = new ListBox { Background = Theme.CtrlBg, Foreground = Theme.Fg, BorderBrush = Theme.Border };
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    string? name = useInputProcess ? FocusedInput.Read(process.MainWindowHandle).ApplicationName() : process.ProcessName + ".exe";
                    if (name != null) names.Add(name);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        list.ItemsSource = names;
        var add = new Button { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }.Localize(ContentControl.ContentProperty, "Добавить");
        add.Click += (_, _) => { if (list.SelectedItem is string name) { Picked?.Invoke(name); Close(); } };
        DockPanel.SetDock(add, Dock.Bottom); panel.Children.Add(add); panel.Children.Add(list);
        Content = panel;
    }
}

internal sealed class WindowPicker : Window
{
    public event Action<string>? Picked;
    public WindowPicker(bool useInputProcess = false)
    {
        Title = "TypeSwitcher"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(22, 0, 103, 192));
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        var label = new TextBlock { Foreground = Theme.Fg, FontSize = 18, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap }
            .Localize("Нажмите на окно приложения. Esc — отмена.");
        Content = new Border { Child = label, Background = Theme.PanelBg, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 0, 0) };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            NativeMethods.GetCursorPos(out var point);
            Hide();
            IntPtr window = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(point), 2);
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            string? name = useInputProcess ? FocusedInput.Read(window).ApplicationName() : ExclusionManager.ExecutableName(window);
            if (pid != Environment.ProcessId && name != null) Picked?.Invoke(name);
            Close();
        };
    }
}
