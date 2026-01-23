using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using Serilog;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Заглушка для распознавания речи (для тестирования)
    /// ВАЖНО: Используется только когда модель Whisper не загружена!
    /// </summary>
    public class MockSpeechRecognizer : ISpeechRecognizer
    {
        private static int _callCounter = 0;

        public bool IsReady => true;

        public async Task<RecognitionResult> RecognizeAsync(AudioData audioData, CancellationToken cancellationToken = default)
        {
            // Симуляция быстрого распознавания (не 2 секунды!)
            await Task.Delay(100, cancellationToken);
            
            var callNumber = System.Threading.Interlocked.Increment(ref _callCounter);

            // Для очень коротких фрагментов возвращаем пустой результат
            if (audioData.Duration.TotalMilliseconds < 500)
            {
                Log.Debug("Mock: слишком короткий сегмент ({Duration}мс), возвращаем пустой результат", 
                    audioData.Duration.TotalMilliseconds);
                
                return new RecognitionResult
                {
                    Success = true,
                    Text = string.Empty,
                    DetectedLanguage = "ru",
                    Confidence = 0.0f
                };
            }

            // Возвращаем тестовый текст только для первых 2-3 вызовов
            // Остальные - пустые (имитация тишины)
            string mockText = callNumber switch
            {
                1 => "Привет, это тестовая фраза номер один.",
                2 => "Вторая тестовая фраза для проверки.",
                3 => "Третья и последняя фраза.",
                _ => string.Empty // Все остальное - тишина
            };

            if (string.IsNullOrEmpty(mockText))
            {
                Log.Debug("Mock: вызов #{Call}, возвращаем пустой результат (имитация тишины)", callNumber);
            }
            else
            {
                Log.Debug("Mock: вызов #{Call}, возвращаем: \"{Text}\"", callNumber, mockText);
            }

            return new RecognitionResult
            {
                Success = true,
                Text = mockText,
                DetectedLanguage = "ru",
                Confidence = mockText.Length > 0 ? 0.95f : 0.0f
            };
        }
    }
}





