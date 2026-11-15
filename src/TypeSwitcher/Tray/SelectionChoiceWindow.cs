using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TypeSwitcher.Core;
using TypeSwitcher.Interop;
using TypeSwitcher.Localization;

namespace TypeSwitcher.Tray;

public sealed record SelectionChoice(LayoutTarget Target, string Text);

/// <summary>A mouse-operated preview that does not activate or change the source selection.</summary>
internal sealed class SelectionChoiceWindow : Window
{
    private HwndSource? _source;
    private readonly DispatcherTimer _expiry;
    private MouseHook? _outside;
    public SelectionChoiceWindow(string title, IReadOnlyList<SelectionChoice> choices, Action<SelectionChoice>? apply = null)
    {
        Title = "TypeSwitcher"; Width = 440; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 14;
        Theme.Attach(this);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Foreground = Theme.Fg, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }.Localize(title));
        foreach (var choice in choices)
        {
            var button = new Button { MinWidth = 0, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Focusable = false };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Foreground = Theme.Fg, FontWeight = FontWeights.SemiBold }
                .Localize(LanguageProfile.Find(choice.Target)?.Name ?? "Применить"));
            content.Children.Add(new TextBlock { Text = choice.Text.Length > 180 ? choice.Text[..180] + "…" : choice.Text, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
            button.Content = content;
            button.Click += (_, _) => { Close(); apply?.Invoke(choice); };
            panel.Children.Add(button);
        }
        var close = new Button { HorizontalAlignment = HorizontalAlignment.Right, Focusable = false }.Localize(ContentControl.ContentProperty, "Закрыть");
        close.Click += (_, _) => Close(); panel.Children.Add(close);
        Content = new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(1), Child = panel };
        _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _expiry.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            NativeMethods.GetCursorPos(out var point);
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            Left = Math.Clamp(point.X / scale.M11 + 12, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth);
            Top = Math.Clamp(point.Y / scale.M22 + 12, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight);
            _expiry.Start();
            _outside = new MouseHook();
            _outside.ButtonDown += () =>
            {
                NativeMethods.GetCursorPos(out var cursor);
                if (NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)
                    && (cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom)) Dispatcher.BeginInvoke(Close);
            };
            try { _outside.Install(); } catch { _outside.Dispose(); _outside = null; }
        };
        Closed += (_, _) => { _expiry.Stop(); _outside?.Dispose(); _source?.RemoveHook(WindowMessage); };
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessage);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return (IntPtr)3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        return IntPtr.Zero;
    }
}
