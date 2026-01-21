using System.Runtime.InteropServices;
using System.Windows.Interop;
using LWhisper.Core.Interfaces;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Управление глобальными горячими клавишами Windows
    /// </summary>
    public class WindowsHotkeyManager : IHotkeyManager, IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_ALT = 0x0001;

        private IntPtr _hwnd;
        private HwndSource? _source;
        private Action? _callback;

        public bool IsRegistered { get; private set; }

        public void SetWindowHandle(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }

        public bool RegisterHotkey(string hotkeyBinding, Action callback)
        {
            if (IsRegistered) UnregisterHotkey();

            _callback = callback;

            var (modifiers, key) = ParseHotkey(hotkeyBinding);
            bool success = RegisterHotKey(_hwnd, HOTKEY_ID, modifiers, key);
            IsRegistered = success;

            return success;
        }

        public void UnregisterHotkey()
        {
            if (IsRegistered)
            {
                UnregisterHotKey(_hwnd, HOTKEY_ID);
                IsRegistered = false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                _callback?.Invoke();
                handled = true;
            }

            return IntPtr.Zero;
        }

        private (uint modifiers, uint key) ParseHotkey(string hotkeyBinding)
        {
            uint modifiers = 0;
            uint key = 0x20; // Space по умолчанию

            var parts = hotkeyBinding.Split('+');
            foreach (var part in parts)
            {
                var normalized = part.Trim().ToLower();
                if (normalized == "ctrl" || normalized == "control")
                    modifiers |= MOD_CONTROL;
                else if (normalized == "shift")
                    modifiers |= MOD_SHIFT;
                else if (normalized == "alt")
                    modifiers |= MOD_ALT;
                else if (normalized == "space")
                    key = 0x20;
            }

            return (modifiers, key);
        }

        public void Dispose()
        {
            UnregisterHotkey();
            _source?.RemoveHook(WndProc);
        }
    }
}

