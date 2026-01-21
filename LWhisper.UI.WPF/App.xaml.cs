using System.Windows;
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
        private ISpeechRecognizer? _speechRecognizer;
        private ITextInjector? _textInjector;
        private bool _isRecording;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _speechRecognizer = new MockSpeechRecognizer();
            _textInjector = new WindowsTextInjector();

            _widget = new FloatingMicrophoneWidget
            {
                Left = 100,
                Top = 100
            };

            _widget.RecordingStarted += OnRecordingStarted;
            _widget.RecordingStopped += OnRecordingStopped;

            _widget.Show();
        }

        private async void OnRecordingStarted()
        {
            if (_isRecording) return;

            _isRecording = true;
            _widget?.SetState(WidgetState.Recording);
        }

        private async void OnRecordingStopped()
        {
            if (!_isRecording) return;

            _isRecording = false;
            _widget?.SetState(WidgetState.Processing);

            try
            {
                var audioData = new AudioData();
                var result = await _speechRecognizer!.RecognizeAsync(audioData);

                if (result.Success)
                {
                    await _textInjector!.InjectTextAsync(result.Text);
                }

                _widget?.SetState(WidgetState.Idle);
            }
            catch
            {
                _widget?.SetState(WidgetState.Idle);
            }
        }
    }
}
