namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Одна строка meta.jsonl. Сериализуется System.Text.Json в camelCase.
    /// Поле Type различает записи: "segment" (распознавание сегмента),
    /// "postfilter" (текст после пост-фильтров L0-L6), "session" (весь поток сессии).
    /// </summary>
    public sealed class SegmentDumpMeta
    {
        /// <summary>"segment" | "postfilter" | "session".</summary>
        public string Type { get; set; } = "segment";

        /// <summary>Id сегмента из SegmentRecognitionManager. 0 = неизвестен (вызов не из менеджера).</summary>
        public int SegmentId { get; set; }

        /// <summary>"streaming" | "fallback" | "traditional" | "runner".</summary>
        public string Kind { get; set; } = "streaming";

        /// <summary>DateTime.UtcNow.ToString("o").</summary>
        public string TimestampUtc { get; set; } = "";

        /// <summary>Длительность аудио записи, мс.</summary>
        public double DurationMs { get; set; }

        /// <summary>Применённый AudioContextSize. 0 = WithAudioContextSize не вызывался.</summary>
        public int AudioContextSize { get; set; }

        /// <summary>Число потоков, переданное в builder.</summary>
        public int Threads { get; set; }

        /// <summary>true = beam search, false = greedy.</summary>
        public bool Beam { get; set; }

        /// <summary>Язык распознавания, как передан в builder.</summary>
        public string Language { get; set; } = "";

        /// <summary>Имя файла модели (не полный путь).</summary>
        public string ModelFile { get; set; } = "";

        /// <summary>Время распознавания, мс.</summary>
        public double ElapsedMs { get; set; }

        /// <summary>
        /// Для Type="segment" — текст ДО пост-фильтров L0-L6.
        /// Для Type="postfilter" — текст ПОСЛЕ пост-фильтров (различается по Type).
        /// </summary>
        public string RawText { get; set; } = "";

        /// <summary>Текст ошибки, если распознавание не удалось.</summary>
        public string? Error { get; set; }

        /// <summary>true, если сработал аварийный fallback на RecognizeAsync.</summary>
        public bool UsedFallback { get; set; }

        /// <summary>Относительное имя WAV-файла внутри папки сессии, например "seg-0003.wav".</summary>
        public string WavFile { get; set; } = "";
    }
}
