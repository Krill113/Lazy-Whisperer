using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Заглушка для распознавания речи (для тестирования)
    /// </summary>
    public class MockSpeechRecognizer : ISpeechRecognizer
    {
        public bool IsReady => true;

        public async Task<RecognitionResult> RecognizeAsync(AudioData audioData, CancellationToken cancellationToken = default)
        {
            await Task.Delay(2000, cancellationToken);

            return new RecognitionResult
            {
                Success = true,
                Text = "Тестовая фраза",
                DetectedLanguage = "ru",
                Confidence = 0.95f
            };
        }
    }
}





