namespace LWhisper.Core.Models
{
    /// <summary>
    /// Результат распознавания речи
    /// </summary>
    public class RecognitionResult
    {
        /// <summary>
        /// Распознанный текст
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Успешность распознавания
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Сообщение об ошибке (если есть)
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Определенный язык
        /// </summary>
        public string? DetectedLanguage { get; set; }

        /// <summary>
        /// Уверенность распознавания (0.0 - 1.0)
        /// </summary>
        public float Confidence { get; set; }
    }
}


