using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeSwitcher.Interop;

namespace TypeSwitcher.Acceptance;

internal static class InputHost
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new HostWindow();
        app.Run(window);
        return 0;
    }

    private sealed class NativeEdit(bool password) : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            uint style = 0x50000000 | 0x00800000 | 0x0080 | (password ? 0x0020u : 0u);
            IntPtr handle = Native.CreateWindowEx(0, "EDIT", "", style, 0, 0, 600, 35, parent.Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Native EDIT creation failed.");
            return new(this, handle);
        }
        protected override void DestroyWindowCore(HandleRef window) => Native.DestroyWindow(window.Handle);
    }

    private sealed class HostWindow : Window
    {
        private readonly TextBox _text = new() { Height = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        private readonly PasswordBox _password = new() { Height = 30, Password = "acceptance-only" };
        private readonly NativeEdit _native = new(false) { Height = 35, Focusable = true };
        private readonly NativeEdit _nativePassword = new(true) { Height = 35, Focusable = true };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly KeyboardHook _keyboard = new();
        private readonly MouseHook _mouse = new();
        private readonly Native.HookCallback _probe;
        private IntPtr _probeHandle;
        private IntPtr _window;
        private int _injected, _physical, _productionEvents, _mouseClicks, _reinstalls;
        private int _statusVersion;
        private int _focusVersion;

        public HostWindow()
        {
            Title = "TypeSwitcher Acceptance Input Host";
            Width = 780; Height = 570; Left = 80; Top = 80;
            ShowActivated = false;
            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = "TypeSwitcher — isolated acceptance fields", FontSize = 20 });
            AutomationProperties.SetName(_text, "Acceptance WPF text");
            AutomationProperties.SetAutomationId(_text, "acceptance.text");
            AutomationProperties.SetName(_password, "Acceptance WPF password");
            AutomationProperties.SetAutomationId(_password, "acceptance.password");
            AutomationProperties.SetAutomationId(_status, "acceptance.status");
            root.Children.Add(_text);
            root.Children.Add(_password);
            root.Children.Add(new TextBlock { Text = "Native EDIT" }); root.Children.Add(_native);
            root.Children.Add(new TextBlock { Text = "Native password EDIT" }); root.Children.Add(_nativePassword);
            Button(root, "Focus WPF text", () => FocusOwned(() => { Native.SetFocus(_window); _text.Focus(); System.Windows.Input.Keyboard.Focus(_text); _text.Select(_text.Text.Length, 0); }));
            Button(root, "Focus WPF password", () => FocusOwned(() => { Native.SetFocus(_window); _password.Focus(); System.Windows.Input.Keyboard.Focus(_password); }));
            Button(root, "Focus native text", () => FocusOwned(() => { _native.Focus(); System.Windows.Input.Keyboard.Focus(_native); Native.SetFocus(_native.Handle); IntPtr length = Native.SendMessage(_native.Handle, 0x000E, IntPtr.Zero, IntPtr.Zero); Native.SendMessage(_native.Handle, 0x00B1, length, length); }));
            Button(root, "Focus native password", () => FocusOwned(() => { _nativePassword.Focus(); System.Windows.Input.Keyboard.Focus(_nativePassword); Native.SetFocus(_nativePassword.Handle); }));
            Button(root, "Read hook status", UpdateStatus);
            Button(root, "Reinstall hooks", () => { _keyboard.Reinstall(); _mouse.Reinstall(); _reinstalls++; UpdateStatus(); });
            root.Children.Add(_status);
            Content = root;
            _probe = Probe;
            Loaded += (_, _) =>
            {
                _window = new WindowInteropHelper(this).Handle;
                _keyboard.KeyAction += (_, _) => { if (Native.GetForegroundWindow() == _window) _productionEvents++; };
                _mouse.ButtonDown += () => { if (Native.GetForegroundWindow() == _window) _mouseClicks++; };
                _keyboard.Install(); _mouse.Install();
                _probeHandle = Native.SetWindowsHookEx(13, _probe, Native.GetModuleHandle(null), 0);
                Native.SetWindowText(_nativePassword.Handle, "acceptance-only");
                UpdateStatus();
            };
            Closed += (_, _) =>
            {
                _keyboard.Dispose(); _mouse.Dispose();
                if (_probeHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(_probeHandle);
            };
        }

        private void FocusOwned(Action action)
        {
            Native.SetForegroundWindow(_window);
            if (Native.GetForegroundWindow() == _window) action();
            _focusVersion++;
            UpdateStatus();
        }
        private static void Button(Panel panel, string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => action(); panel.Children.Add(button);
        }
        private IntPtr Probe(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && Native.GetForegroundWindow() == _window)
            {
                uint flags = Marshal.PtrToStructure<Native.KeyData>(data).Flags;
                if ((flags & 0x12) != 0) _injected++; else _physical++;
            }
            return Native.CallNextHookEx(_probeHandle, code, message, data);
        }
        private void UpdateStatus() => _status.Text = JsonSerializer.Serialize(new
        {
            statusVersion = ++_statusVersion,
            focusVersion = _focusVersion,
            keyboardInstalled = _keyboard.IsInstalled, mouseInstalled = _mouse.IsInstalled, probeInstalled = _probeHandle != IntPtr.Zero,
            injected = _injected, physical = _physical, productionEvents = _productionEvents, mouseClicks = _mouseClicks,
            reinstalls = _reinstalls, native = _native.Handle.ToInt64(), nativePassword = _nativePassword.Handle.ToInt64(),
            dpi = Native.GetDpiForWindow(_window)
        });
    }
}
