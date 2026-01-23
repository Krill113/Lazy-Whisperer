namespace LWhisper.Core.Models
{
    /// <summary>
    /// Настройки потокового распознавания с VAD
    /// </summary>
    public class StreamingSettings
    {
        /// <summary>
        /// Включить потоковую обработку с определением пауз
        /// ВАЖНО: Рекомендуется включать только с реальным Whisper, не с Mock!
        /// </summary>
        public bool Enabled { get; set; } = false;  // ← Выключено по умолчанию!

        /// <summary>
        /// Длительность паузы для завершения сегмента (миллисекунды)
        /// Рекомендуется: 1000мс для запятых
        /// </summary>
        public int PauseThresholdMs { get; set; } = 1000;

        /// <summary>
        /// Минимальная длительность сегмента для отправки на распознавание (миллисекунды)
        /// Короткие фрагменты будут игнорироваться как шум
        /// </summary>
        public int MinSegmentDurationMs { get; set; } = 1500;

        /// <summary>
        /// Максимальная длительность сегмента (миллисекунды)
        /// При достижении будет принудительное завершение сегмента
        /// </summary>
        public int MaxSegmentDurationMs { get; set; } = 15000;

        /// <summary>
        /// Автоматически останавливать запись при длинной паузе
        /// </summary>
        public bool AutoStopOnLongPause { get; set; } = false;

        /// <summary>
        /// Длительность паузы для автоматической остановки записи (миллисекунды)
        /// Рекомендуется: 3000мс для точек/конца предложения
        /// </summary>
        public int AutoStopPauseDurationMs { get; set; } = 3000;

        /// <summary>
        /// Максимальное количество параллельных задач распознавания
        /// </summary>
        public int MaxParallelRecognitions { get; set; } = 3;
    }
}

