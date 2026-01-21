using System.Windows;
using System.Windows.Interop;
using LWhisper.UI.WPF.Views;
using LWhisper.UI.WPF.Services;
using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;

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
        private AppSettings _settings;
        private bool _isRecording;

        public App()
        {
            _settings = new AppSettings();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _speechRecognizer = new MockSpeechRecognizer();
            _textInjector = new WindowsTextInjector();
            _audioRecorder = new NAudioRecorder();
            _hotkeyManager = new WindowsHotkeyManager();

            _trayManager = new TrayIconManager();
            _trayManager.Initialize();
            _trayManager.ShowMicrophoneRequested += OnShowMicrophoneRequested;
            _trayManager.SettingsRequested += OnSettingsRequested;
            _trayManager.ExitRequested += OnExitRequested;

            _widget = new FloatingMicrophoneWidget
            {
                Left = _settings.WidgetPositionX,
                Top = _settings.WidgetPositionY
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
        }

        private void OnShowMicrophoneRequested()
        {
            _widget?.Show();
            _widget?.Activate();
        }

        private void OnSettingsRequested()
        {
            var devices = _audioRecorder?.GetAvailableDevices() ?? new List<string>();
            var settingsWindow = new SettingsWindow(_settings, devices);

            if (settingsWindow.ShowDialog() == true)
            {
                _settings = settingsWindow.Settings;
                ApplySettings();
            }
        }

        private void ApplySettings()
        {
            _hotkeyManager?.UnregisterHotkey();

            if (_settings.RecordingMode == RecordingMode.Hotkey)
            {
                _hotkeyManager?.RegisterHotkey(_settings.HotkeyBinding ?? "Ctrl+Shift+Space", ToggleRecording);
            }
        }

        private void OnExitRequested()
        {
            if (_widget != null)
            {
                _settings.WidgetPositionX = _widget.Left;
                _settings.WidgetPositionY = _widget.Top;
            }

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

            _isRecording = true;
            _widget?.SetState(WidgetState.Recording);
            _trayManager?.SetIcon(TrayIconState.Recording);
            _audioRecorder?.StartRecording();
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
                var result = await _speechRecognizer!.RecognizeAsync(audioData);

                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);

                if (result.Success && !string.IsNullOrEmpty(result.Text))
                {
                    ShowPreviewWindow(result.Text);
                }
            }
            catch
            {
                _widget?.SetState(WidgetState.Idle);
                _trayManager?.SetIcon(TrayIconState.Idle);
            }
        }

        private void ShowPreviewWindow(string text)
        {
            _previewWindow = new PreviewWindow();
            _previewWindow.InsertRequested += async (textToInsert) =>
            {
                await _textInjector!.InjectTextAsync(textToInsert);
            };

            if (_widget != null)
            {
                _previewWindow.Left = _widget.Left;
                _previewWindow.Top = _widget.Top + _widget.Height + 10;
            }

            _previewWindow.ShowWithText(text, _settings.AutoInsertDelaySeconds);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkeyManager?.UnregisterHotkey();
            _trayManager?.Dispose();
            base.OnExit(e);
        }
    }
}
