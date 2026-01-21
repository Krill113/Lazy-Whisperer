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

                _speechRecognizer = new MockSpeechRecognizer();
                _textInjector = new WindowsTextInjector();
                _audioRecorder = new NAudioRecorder();
                _hotkeyManager = new WindowsHotkeyManager();

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
                    Left = _settings.WidgetPositionX > 0 ? _settings.WidgetPositionX : defaultX,
                    Top = _settings.WidgetPositionY > 0 ? _settings.WidgetPositionY : defaultY
                };

                _widget.RecordingStarted += OnRecordingStarted;
                _widget.RecordingStopped += OnRecordingStopped;
                _widget.Show();

                var hwnd = new WindowInteropHelper(_widget).Handle;
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

            Log.Information("Настройки применены: режим {Mode}", _settings.RecordingMode);
        }

        private void OnExitRequested()
        {
            if (_widget != null)
            {
                _settings.WidgetPositionX = _widget.Left;
                _settings.WidgetPositionY = _widget.Top;
                _settingsManager?.Save(_settings);
            }

            Log.Information("Выход из приложения");
            Shutdown();
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

        private void OnRecordingStarted()
        {
            if (_isRecording) return;

            try
            {
                // ВАЖНО: Запомнить активное окно ДО начала записи
                if (_textInjector is WindowsTextInjector winInjector)
                {
                    winInjector.RememberActiveWindow();
                    Log.Debug("Запомнено активное окно");
                }

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

                if (result.Success && !string.IsNullOrEmpty(result.Text))
                {
                    ShowPreviewWindow(result.Text);
                }
                else
                {
                    Log.Warning("Распознавание не дало результата");
                    _widget?.SetState(WidgetState.Idle);
                    _trayManager?.SetIcon(TrayIconState.Idle);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при распознавании");
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);
                MessageBox.Show($"Ошибка распознавания: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowPreviewWindow(string text)
        {
            try
            {
                _previewWindow = new PreviewWindow();
                _previewWindow.InsertRequested += async (textToInsert) =>
                {
                    try
                    {
                        Log.Debug("Начало вставки текста");
                        await _textInjector!.InjectTextAsync(textToInsert);
                        Log.Debug("Текст вставлен успешно");
                        
                        _widget?.SetState(WidgetState.Idle);
                        _trayManager?.SetIcon(TrayIconState.Idle);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Ошибка вставки текста");
                        _widget?.SetState(WidgetState.Idle);
                        _trayManager?.SetIcon(TrayIconState.Idle);
                        MessageBox.Show($"Ошибка вставки: {ex.Message}", "LWhisper", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };

                _previewWindow.Closed += (s, e) =>
                {
                    Log.Debug("PreviewWindow закрыто");
                    _widget?.SetState(WidgetState.Idle);
                    _trayManager?.SetIcon(TrayIconState.Idle);
                };

                if (_widget != null)
                {
                    _previewWindow.Left = _widget.Left;
                    _previewWindow.Top = _widget.Top + _widget.Height + 10;
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

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkeyManager?.UnregisterHotkey();
            _trayManager?.Dispose();
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
