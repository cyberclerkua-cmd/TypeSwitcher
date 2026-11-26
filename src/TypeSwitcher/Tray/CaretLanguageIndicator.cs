using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TypeSwitcher.Core;
using static TypeSwitcher.Interop.NativeMethods;

namespace TypeSwitcher.Tray;

internal sealed class CaretLanguageIndicator : Window
{
    private readonly TextBlock _label = new() { Foreground = Theme.Fg, FontSize = 12, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public CaretLanguageIndicator()
    {
        Title = "TypeSwitcher · EN/RU/UK";
        Width = 34; Height = 22;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Focusable = false; IsHitTestVisible = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Border { Background = Theme.PanelBg, BorderBrush = Theme.Accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Child = _label };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int extendedStyle = -20, noActivate = 0x08000000, transparent = 0x20, toolWindow = 0x80;
            SetWindowLongPtr(hwnd, extendedStyle, GetWindowLongPtr(hwnd, extendedStyle) | noActivate | transparent | toolWindow);
        };
    }

    public void ShowAt(RECT caret, double scale, LanguageProfile language)
    {
        var anchor = new POINT { X = caret.Right + (int)Math.Ceiling(3 * scale), Y = caret.Bottom + (int)Math.Ceiling(2 * scale) };
        var size = new SIZE { Width = (int)Math.Ceiling(Width * scale), Height = (int)Math.Ceiling(Height * scale) };
        if (!CalculatePopupWindowPosition(ref anchor, ref size, 0x10000 /* TPM_WORKAREA */, ref caret, out var position)) { Hide(); return; }
        _label.Text = language.Id.ToUpperInvariant();
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, position.Left, position.Top, size.Width, size.Height, 0x14 /* NOACTIVATE | NOZORDER */);
        if (!IsVisible) Show();
    }
}
