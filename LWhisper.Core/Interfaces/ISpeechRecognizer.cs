using System.Threading;
using System.Threading.Tasks;
using LWhisper.Core.Models;

namespace LWhisper.Core.Interfaces
{
    /// <summary>
    /// Интерфейс для распознавания речи
    /// </summary>
    public interface ISpeechRecognizer
    {
        /// <summary>
        /// Распознать речь из аудио данных
        /// </summary>
        Task<RecognitionResult> RecognizeAsync(AudioData audioData, CancellationToken cancellationToken = default);

        /// <summary>
        /// Проверить готовность распознавателя
        /// </summary>
        bool IsReady { get; }
    }
}

