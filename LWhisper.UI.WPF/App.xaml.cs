using System.IO;
using System.Windows;
using System.Windows.Interop;
using LWhisper.UI.WPF.Views;
using LWhisper.UI.WPF.Services;
using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using Serilog;

namespace LWhisper.UI.WPF
{
    /// <summary>
    /// Логика приложения
    /// </summary>
    public partial class App : Application
    {
        private FloatingMicrophoneWidget? _widget;
        private PreviewWindow? _previewWindow;
        private ISpeechRecognizer? _speechRecognizer;
        private ITextInjector? _textInjector;
        private IAudioRecorder? _audioRecorder;
        private IHotkeyManager? _hotkeyManager;
        private TrayIconManager? _trayManager;
        private SettingsManager? _settingsManager;
        private AppSettings _settings;
        private bool _isRecording;
        private string _lastRecognizedText = string.Empty; // Для кнопки показа текста

        public App()
        {
            _settings = new AppSettings();
            InitializeLogging();
        }

        private void InitializeLogging()
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LWhisper", "logs", "log-.txt"
            );

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10_000_000,
                    retainedFileCountLimit: 7)
                .CreateLogger();

            Log.Information("LWhisper запущен");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                _settingsManager = new SettingsManager();
                _settings = _settingsManager.Load();

                // Инициализация речевого движка - Whisper если модель есть, иначе Mock
                InitializeSpeechRecognizer();

                _textInjector = new WindowsTextInjector();
                _audioRecorder = new NAudioRecorder();
                _hotkeyManager = new WindowsHotkeyManager();

                // Применить настройки аудио устройства
                if (!string.IsNullOrEmpty(_settings.SelectedAudioDevice))
                {
                    _audioRecorder.SetDevice(_settings.SelectedAudioDevice);
                }

                _trayManager = new TrayIconManager();
                _trayManager.Initialize();
                _trayManager.ShowMicrophoneRequested += OnShowMicrophoneRequested;
                _trayManager.SettingsRequested += OnSettingsRequested;
                _trayManager.ExitRequested += OnExitRequested;

                // Позиция виджета - правый нижний угол по умолчанию
                double defaultX = SystemParameters.PrimaryScreenWidth - 150;
                double defaultY = SystemParameters.PrimaryScreenHeight - 150;

                _widget = new FloatingMicrophoneWidget
                {
                    Left = _settings.WidgetPositionX >= 0 ? _settings.WidgetPositionX : defaultX,
                    Top = _settings.WidgetPositionY >= 0 ? _settings.WidgetPositionY : defaultY
                };

                _widget.BeforeRecordingStarted += OnBeforeRecordingStarted;
                _widget.RecordingStarted += OnRecordingStarted;
                _widget.RecordingStopped += OnRecordingStopped;
                _widget.PositionChanged += OnWidgetPositionChanged;
                _widget.ShowTextRequested += OnShowTextRequested;
                _widget.RememberTargetWindow += OnRememberTargetWindow;
                _widget.Show();

                var hwnd = new WindowInteropHelper(_widget).Handle;
                
                // Передать окно виджета в TextInjector, чтобы не запоминать его как целевое
                if (_textInjector is WindowsTextInjector winInjector)
                {
                    winInjector.SetOwnWindow(hwnd);
                }
                
                if (_hotkeyManager is WindowsHotkeyManager whm)
                {
                    whm.SetWindowHandle(hwnd);
                }

                if (_settings.RecordingMode == RecordingMode.Hotkey)
                {
                    _hotkeyManager.RegisterHotkey(_settings.HotkeyBinding ?? "Ctrl+Shift+Space", ToggleRecording);
                }

                Log.Information("Приложение инициализировано");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Ошибка при запуске приложения");
                MessageBox.Show($"Ошибка запуска: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        /// <summary>
        /// Инициализировать речевой движок (Whisper или Mock)
        /// </summary>
        private void InitializeSpeechRecognizer()
        {
            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", $"ggml-{_settings.WhisperModelSize}.bin");
            
            if (File.Exists(modelPath))
            {
                try
                {
                    var whisperRecognizer = new LWhisper.SpeechEngine.WhisperSpeechRecognizer(modelPath);
                    
                    // ВАЖНО: Инициализировать асинхронно
                    Task.Run(async () =>
                    {
                        await whisperRecognizer.InitializeAsync();
                        Log.Information("WhisperSpeechRecognizer инициализирован и готов к работе");
                    }).Wait(); // Ждем завершения инициализации
                    
                    _speechRecognizer = whisperRecognizer;
                    Log.Information("Инициализирован WhisperSpeechRecognizer с моделью: {Model} ({Path})", _settings.WhisperModelSize, modelPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка инициализации Whisper, используется Mock");
                    _speechRecognizer = new MockSpeechRecognizer();
                    MessageBox.Show($"Не удалось загрузить модель Whisper: {ex.Message}\n\nИспользуется тестовая заглушка. Скачайте модель через Настройки.", 
                        "LWhisper", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                Log.Warning("Модель Whisper не найдена: {Path}. Используется Mock.", modelPath);
                _speechRecognizer = new MockSpeechRecognizer();
            }
        }

        private void OnShowMicrophoneRequested()
        {
            _widget?.Show();
            _widget?.Activate();
            Log.Debug("Показан виджет микрофона");
        }

        private void OnSettingsRequested()
        {
            try
            {
                var devices = _audioRecorder?.GetAvailableDevices() ?? new List<string>();
                var settingsWindow = new SettingsWindow(_settings, devices);

                if (settingsWindow.ShowDialog() == true)
                {
                    _settings = settingsWindow.Settings;
                    ApplySettings();
                    _settingsManager?.Save(_settings);
                    Log.Information("Настройки сохранены");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при открытии настроек");
                MessageBox.Show($"Ошибка настроек: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplySettings()
        {
            _hotkeyManager?.UnregisterHotkey();

            if (_settings.RecordingMode == RecordingMode.Hotkey)
            {
                _hotkeyManager?.RegisterHotkey(_settings.HotkeyBinding ?? "Ctrl+Shift+Space", ToggleRecording);
            }

            // Переинициализировать речевой движок
            if (_speechRecognizer is IDisposable disposable)
            {
                disposable.Dispose();
            }
            InitializeSpeechRecognizer();

            // Применить настройки аудио устройства
            if (!string.IsNullOrEmpty(_settings.SelectedAudioDevice))
            {
                _audioRecorder?.SetDevice(_settings.SelectedAudioDevice);
            }

            Log.Information("Настройки применены: режим {Mode}, модель {Model}", _settings.RecordingMode, _settings.WhisperModelSize);
        }

        private void OnExitRequested()
        {
            SaveWidgetPosition();
            Log.Information("Выход из приложения");
            Shutdown();
        }

        private void OnWidgetPositionChanged(double x, double y)
        {
            _settings.WidgetPositionX = x;
            _settings.WidgetPositionY = y;
        }

        private void SaveWidgetPosition()
        {
            if (_widget != null)
            {
                _settings.WidgetPositionX = _widget.Left;
                _settings.WidgetPositionY = _widget.Top;
                _settingsManager?.Save(_settings);
                Log.Debug("Позиция виджета сохранена: X={X}, Y={Y}", _widget.Left, _widget.Top);
            }
        }

        private void ToggleRecording()
        {
            if (_isRecording)
            {
                OnRecordingStopped();
            }
            else
            {
                OnRecordingStarted();
            }
        }

        private void OnRememberTargetWindow()
        {
            // Запомнить активное окно при наведении мыши на виджет
            if (_textInjector is WindowsTextInjector winInjector)
            {
                winInjector.RememberActiveWindow();
            }
        }

        private void OnBeforeRecordingStarted()
        {
            // Этот метод больше не используется, но оставлю для совместимости
        }

        private void OnRecordingStarted()
        {
            if (_isRecording) return;

            try
            {
                _isRecording = true;
                _widget?.SetState(WidgetState.Recording);
                _trayManager?.SetIcon(TrayIconState.Recording);
                _audioRecorder?.StartRecording();
                Log.Debug("Запись начата");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка начала записи");
                _isRecording = false;
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);
                MessageBox.Show($"Ошибка записи: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnRecordingStopped()
        {
            if (!_isRecording) return;

            _isRecording = false;
            _widget?.SetState(WidgetState.Processing);
            _trayManager?.SetIcon(TrayIconState.Processing);

            try
            {
                var audioData = await _audioRecorder!.StopRecordingAsync();
                Log.Debug("Запись остановлена, длительность: {Duration}", audioData.Duration);

                var result = await _speechRecognizer!.RecognizeAsync(audioData);
                Log.Information("Распознавание завершено: {Success}, Текст: {Text}", result.Success, result.Text);

                // Сбросить состояние в Idle СРАЗУ после распознавания (микрофон перестает крутиться)
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);

                if (result.Success && !string.IsNullOrEmpty(result.Text))
                {
                    _lastRecognizedText = result.Text; // Сохранить последний текст
                    ShowPreviewWindow(result.Text);
                }
                else
                {
                    Log.Warning("Распознавание не дало результата");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при распознавании");
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Ошибка распознавания: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private void ShowPreviewWindow(string text)
        {
            try
            {
                // Если окно уже открыто, просто обновить текст
                if (_previewWindow != null && _previewWindow.IsVisible)
                {
                    _previewWindow.UpdateText(text);
                    return;
                }

                _previewWindow = new PreviewWindow();
                _previewWindow.InsertRequested += async (textToInsert) =>
                {
                    try
                    {
                        Log.Debug("Начало вставки текста");
                        
                        // Увеличить задержку перед вставкой для гарантированного возврата фокуса
                        await Task.Delay(200);
                        await _textInjector!.InjectTextAsync(textToInsert);
                        Log.Debug("Текст вставлен успешно");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Ошибка вставки текста");
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Ошибка вставки: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                };

                _previewWindow.Closed += (s, e) =>
                {
                    Log.Debug("PreviewWindow закрыто");
                    _previewWindow = null;
                };

                // Умное позиционирование окна предпросмотра
                if (_widget != null)
                {
                    PositionPreviewWindow();
                }

                Log.Debug("Показ PreviewWindow");
                _previewWindow.ShowWithText(text, _settings.AutoInsertDelaySeconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка показа окна предпросмотра");
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);
            }
        }

        /// <summary>
        /// Умное позиционирование окна предпросмотра относительно виджета
        /// </summary>
        private void PositionPreviewWindow()
        {
            if (_widget == null || _previewWindow == null) return;

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var widgetCenterX = _widget.Left + _widget.Width / 2;
            var widgetCenterY = _widget.Top + _widget.Height / 2;

            // Определить, в какой четверти экрана находится виджет
            bool isRight = widgetCenterX > screenWidth / 2;
            bool isBottom = widgetCenterY > screenHeight / 2;

            double previewX, previewY;
            const double margin = 10;

            if (isRight && isBottom)
            {
                // Виджет справа снизу -> окно слева сверху от виджета
                previewX = _widget.Left - _previewWindow.Width - margin;
                previewY = _widget.Top - _previewWindow.ActualHeight - margin;
            }
            else if (isRight && !isBottom)
            {
                // Виджет справа сверху -> окно слева снизу от виджета
                previewX = _widget.Left - _previewWindow.Width - margin;
                previewY = _widget.Top + _widget.Height + margin;
            }
            else if (!isRight && isBottom)
            {
                // Виджет слева снизу -> окно справа сверху от виджета
                previewX = _widget.Left + _widget.Width + margin;
                previewY = _widget.Top - _previewWindow.ActualHeight - margin;
            }
            else
            {
                // Виджет слева сверху -> окно справа снизу от виджета
                previewX = _widget.Left + _widget.Width + margin;
                previewY = _widget.Top + _widget.Height + margin;
            }

            // Убедиться, что окно не выходит за границы экрана
            previewX = Math.Max(0, Math.Min(previewX, screenWidth - _previewWindow.Width));
            previewY = Math.Max(0, Math.Min(previewY, screenHeight - _previewWindow.ActualHeight));

            _previewWindow.Left = previewX;
            _previewWindow.Top = previewY;
        }

        /// <summary>
        /// Обработчик кнопки показа/скрытия текста на виджете
        /// </summary>
        private void OnShowTextRequested()
        {
            if (string.IsNullOrEmpty(_lastRecognizedText))
            {
                Log.Debug("Нет распознанного текста для показа");
                return;
            }

            if (_previewWindow != null && _previewWindow.IsVisible)
            {
                // Скрыть окно
                _previewWindow.Close();
                Log.Debug("PreviewWindow скрыто по запросу");
            }
            else
            {
                // Показать окно с последним текстом
                ShowPreviewWindow(_lastRecognizedText);
                Log.Debug("PreviewWindow показано по запросу");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SaveWidgetPosition();
            _hotkeyManager?.UnregisterHotkey();
            _trayManager?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
