using System.Runtime.InteropServices;
using LWhisper.Core.Interfaces;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Вставка текста в активное окно Windows через SendInput
    /// </summary>
    public class WindowsTextInjector : ITextInjector
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private IntPtr _targetWindow;

        /// <summary>
        /// Запомнить текущее активное окно для последующей вставки
        /// </summary>
        public void RememberActiveWindow()
        {
            _targetWindow = GetForegroundWindow();
        }

        public async Task InjectTextAsync(string text)
        {
            if (_targetWindow == IntPtr.Zero)
            {
                return;
            }

            await Task.Run(async () =>
            {
                // Вернуть фокус на запомненное окно
                SetForegroundWindow(_targetWindow);
                await Task.Delay(100); // Дать время окну активироваться

                foreach (char c in text)
                {
                    SendUnicodeChar(c);
                    Thread.Sleep(1);
                }
            });
        }

        private void SendUnicodeChar(char c)
        {
            var inputs = new INPUT[2];

            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}

