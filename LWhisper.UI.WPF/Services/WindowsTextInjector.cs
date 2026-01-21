using System.Runtime.InteropServices;
using System.Text;
using LWhisper.Core.Interfaces;
using Serilog;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Вставка текста в активное окно Windows через Clipboard + Ctrl+V
    /// </summary>
    public class WindowsTextInjector : ITextInjector
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint WM_PASTE = 0x0302;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

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

            if (string.IsNullOrEmpty(text))
            {
                Log.Warning("Текст для вставки пустой.");
                return;
            }

            await Task.Run(async () =>
            {
                // Получить название окна для логирования
                var windowTitle = new StringBuilder(256);
                GetWindowText(_targetWindow, windowTitle, 256);
                Log.Information("Начинается вставка текста ({Length} символов) в окно: '{WindowTitle}' (Handle: {Handle})", 
                    text.Length, windowTitle.ToString(), _targetWindow);

                // Попытаться активировать окно с retry-логикой (до 3 попыток)
                bool windowActivated = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    Log.Debug("Попытка #{Attempt}: активация целевого окна...", attempt);
                    var result = SetForegroundWindow(_targetWindow);
                    
                    // Увеличенная задержка для гарантированной активации окна
                    await Task.Delay(800);
                    
                    // Проверить что окно действительно активно
                    var currentForeground = GetForegroundWindow();
                    if (currentForeground == _targetWindow)
                    {
                        Log.Debug("Целевое окно успешно активировано на попытке #{Attempt}", attempt);
                        windowActivated = true;
                        break;
                    }
                    else
                    {
                        var currentWindowTitle = new StringBuilder(256);
                        GetWindowText(currentForeground, currentWindowTitle, 256);
                        Log.Warning("Попытка #{Attempt}: окно не активировано. Текущее окно: '{CurrentWindow}' (Handle: {CurrentHandle})", 
                            attempt, currentWindowTitle.ToString(), currentForeground);
                        
                        if (attempt < 3)
                        {
                            await Task.Delay(300); // Дополнительная задержка перед следующей попыткой
                        }
                    }
                }

                if (!windowActivated)
                {
                    Log.Error("Не удалось активировать целевое окно после 3 попыток. Вставка может не сработать.");
                }

                // Использовать Clipboard + Ctrl+V как основной и единственный метод
                Log.Debug("Вставка текста через Clipboard + Ctrl+V...");
                
                // Установить текст в буфер обмена
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(text);
                        Log.Debug("Текст успешно скопирован в буфер обмена");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Ошибка при копировании текста в буфер обмена");
                        throw;
                    }
                });
                
                // Дополнительная задержка перед вставкой
                await Task.Delay(200);
                
                // Попробовать 3 метода вставки для максимальной совместимости:
                
                // Метод 1: WM_PASTE сообщение (работает для большинства приложений)
                Log.Debug("Метод 1: попытка вставки через WM_PASTE сообщение...");
                var pasteResult = SendMessage(_targetWindow, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                Log.Debug("SendMessage(WM_PASTE) вернул: {Result}", pasteResult);
                
                await Task.Delay(100);
                
                // Метод 2: keybd_event Ctrl+V (старый API, работает даже когда SendInput блокируется)
                Log.Debug("Метод 2: попытка через keybd_event Ctrl+V...");
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero); // Ctrl down
                keybd_event(VK_V, 0, 0, UIntPtr.Zero); // V down
                await Task.Delay(50);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // V up
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl up
                Log.Debug("keybd_event Ctrl+V отправлен");
                
                await Task.Delay(100);
                
                // Метод 3: SendInput Ctrl+V (на случай если keybd_event тоже не сработал)
                Log.Debug("Метод 3: дополнительная попытка через SendInput Ctrl+V...");
                SendCtrlV();
                
                // Небольшая задержка для завершения вставки
                await Task.Delay(100);
                
                Log.Information("Вставка текста завершена (использованы 3 метода)");
            });
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

