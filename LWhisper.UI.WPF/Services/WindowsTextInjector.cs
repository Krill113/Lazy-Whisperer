using System.Runtime.InteropServices;
using System.Text;
using LWhisper.Core.Interfaces;
using Serilog;

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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private IntPtr _targetWindow;
        private IntPtr _ownWindow; // Окно самого приложения LWhisper

        /// <summary>
        /// Установить окно самого приложения (чтобы не запоминать его как целевое)
        /// </summary>
        public void SetOwnWindow(IntPtr ownWindow)
        {
            _ownWindow = ownWindow;
        }

        /// <summary>
        /// Запомнить текущее активное окно для последующей вставки
        /// </summary>
        public void RememberActiveWindow()
        {
            var foregroundWindow = GetForegroundWindow();
            
            // Логирование для отладки
            var windowTitle = new StringBuilder(256);
            GetWindowText(foregroundWindow, windowTitle, 256);
            
            // Не запоминать собственное окно
            if (foregroundWindow != _ownWindow && foregroundWindow != IntPtr.Zero)
            {
                _targetWindow = foregroundWindow;
                Log.Debug("Запомнено целевое окно: '{WindowTitle}' (Handle: {Handle})", windowTitle.ToString(), foregroundWindow);
            }
            else
            {
                Log.Warning("Попытка запомнить собственное окно или нулевой handle. Игнорируется.");
            }
        }

        public async Task InjectTextAsync(string text)
        {
            if (_targetWindow == IntPtr.Zero)
            {
                Log.Warning("Целевое окно не установлено. Вставка невозможна.");
                return;
            }

            await Task.Run(async () =>
            {
                // Вернуть фокус на запомненное окно
                var result = SetForegroundWindow(_targetWindow);
                Log.Debug("SetForegroundWindow вернул: {Result}", result);
                
                await Task.Delay(500); // Увеличенная задержка для гарантированной активации
                
                // Проверить что окно действительно активно
                var currentForeground = GetForegroundWindow();
                if (currentForeground != _targetWindow)
                {
                    Log.Warning("Не удалось активировать целевое окно. Текущее: {Current}, Целевое: {Target}", 
                        currentForeground, _targetWindow);
                }

                // Метод 1: Попробовать SendInput
                Log.Debug("Попытка вставки через SendInput...");
                bool success = false;
                foreach (char c in text)
                {
                    var sent = SendUnicodeChar(c);
                    if (sent > 0) success = true;
                    Thread.Sleep(5); // Увеличенная задержка между символами
                }
                
                // Метод 2: Если SendInput не сработал, использовать Clipboard + Ctrl+V
                if (!success)
                {
                    Log.Debug("SendInput не сработал, использую Clipboard + Ctrl+V...");
                    await Task.Delay(100);
                    
                    // Установить текст в буфер обмена
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        System.Windows.Clipboard.SetText(text);
                    });
                    
                    await Task.Delay(100);
                    
                    // Отправить Ctrl+V
                    SendCtrlV();
                }
                
                Log.Debug("Вставка текста завершена");
            });
        }

        private uint SendUnicodeChar(char c)
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

            var result = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            return result;
        }

        private void SendCtrlV()
        {
            const ushort VK_CONTROL = 0x11;
            const ushort VK_V = 0x56;
            
            var inputs = new INPUT[4];

            // Ctrl down
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_CONTROL,
                        wScan = 0,
                        dwFlags = 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // V down
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_V,
                        wScan = 0,
                        dwFlags = 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // V up
            inputs[2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_V,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Ctrl up
            inputs[3] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VK_CONTROL,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
            Log.Debug("Отправлен Ctrl+V");
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

