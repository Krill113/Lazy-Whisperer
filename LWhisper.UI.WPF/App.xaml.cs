using System.Windows;
using LWhisper.UI.WPF.Views;
using LWhisper.UI.WPF.Services;
using LWhisper.Core.Interfaces;

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
        private IAudioRecorder? _audioRecorder;
        private bool _isRecording;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _speechRecognizer = new MockSpeechRecognizer();
            _textInjector = new WindowsTextInjector();
            _audioRecorder = new NAudioRecorder();

            _widget = new FloatingMicrophoneWidget
            {
                Left = 100,
                Top = 100
            };

            _widget.RecordingStarted += OnRecordingStarted;
            _widget.RecordingStopped += OnRecordingStopped;

            _widget.Show();
        }

        private void OnRecordingStarted()
        {
            if (_isRecording) return;

            _isRecording = true;
            _widget?.SetState(WidgetState.Recording);
            _audioRecorder?.StartRecording();
        }

        private async void OnRecordingStopped()
        {
            if (!_isRecording) return;

            _isRecording = false;
            _widget?.SetState(WidgetState.Processing);

            try
            {
                var audioData = await _audioRecorder!.StopRecordingAsync();
                var result = await _speechRecognizer!.RecognizeAsync(audioData);

                if (result.Success && !string.IsNullOrEmpty(result.Text))
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
