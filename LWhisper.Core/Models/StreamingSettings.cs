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
        /// Рекомендуется: 1500мс для естественных пауз
        /// </summary>
        public int PauseThresholdMs { get; set; } = 1500;

        /// <summary>
        /// Минимальная длительность сегмента для отправки на распознавание (миллисекунды)
        /// Короткие фрагменты будут игнорироваться как шум
        /// </summary>
        public int MinSegmentDurationMs { get; set; } = 2000;

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

        /// <summary>
        /// Агрессивность VAD-фильтрации (0-3). 0=Мягкий, 1=Норма, 2=Строгий, 3=Максимум
        /// </summary>
        public int VadAggressiveness { get; set; } = 2;

        /// <summary>
        /// Задержка после окончания речи перед отсечкой сегмента (мс).
        /// Устраняет обрезание последних слов фразы. Тишина не включается в аудио сегмента.
        /// </summary>
        public int PostSpeechPaddingMs { get; set; } = 400;

        /// <summary>
        /// W1: Использовать beam search вместо greedy. Снижает галлюцинации ценой ~1.5-2× времени.
        /// По дефолту off — скорость важнее, beam включается через UI как опция.
        /// </summary>
        public bool UseBeamSearch { get; set; } = false;

        /// <summary>
        /// Длительность кольцевого pre-roll буфера (мс). Аудио до VAD-триггера, прицепляется
        /// к началу сегмента — лечит потерю первого слова.
        /// </summary>
        public int PreRollBufferMs { get; set; } = 500;

        /// <summary>
        /// Hysteresis: сколько подряд speech-фреймов (по 30 мс) нужно для триггера начала речи.
        /// 1 = старое поведение (мгновенный триггер), 2-3 = устойчивее к шуму.
        /// </summary>
        public int SpeechTriggerFrames { get; set; } = 2;

        /// <summary>
        /// Сколько миллисекунд тишины оставлять в конце сегмента перед отправкой в Whisper.
        /// 0 = старое поведение (полное обрезание), 200 = whisper получает хвостовую тишину
        /// и не рубит последнее слово.
        /// </summary>
        public int EndSilenceKeepMs { get; set; } = 200;

        /// <summary>
        /// Порог compression-ratio (gzip): если text.Length / gzip(text).Length выше — сегмент
        /// считается повторяющимся мусором (галлюцинацией). 2.4 — дефолт OpenAI Whisper.
        /// </summary>
        public double CompressionRatioThreshold { get; set; } = 2.4;
    }
}

