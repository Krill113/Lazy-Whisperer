namespace LWhisper.DevTools.Mcp;

/// <summary>
/// Результат инструмента transcribe. Схема выхода зафиксирована скелетом §5.4.
/// Сериализуется SDK в camelCase; свойства со значением null в выводе опускаются.
/// </summary>
public sealed class TranscribeToolResult
{
    /// <summary>Распознанный текст после пост-фильтров.</summary>
    public string Text { get; set; } = "";

    /// <summary>Длительность исходного аудио, мс.</summary>
    public double DurationMs { get; set; }

    /// <summary>Время распознавания, мс.</summary>
    public double ElapsedMs { get; set; }

    /// <summary>Real-time factor = ElapsedMs / DurationMs.</summary>
    public double Rtf { get; set; }

    /// <summary>Применённый размер контекстного окна энкодера. 0 = WithAudioContextSize не вызывался.</summary>
    public int AudioContextSize { get; set; }

    /// <summary>Число потоков, реально переданное в builder.</summary>
    public int Threads { get; set; }

    /// <summary>true = beam search, false = greedy.</summary>
    public bool Beam { get; set; }

    /// <summary>true, если сработал общий fallback-процессор.</summary>
    public bool UsedFallback { get; set; }
}

/// <summary>
/// Результат инструмента sweep. Схема выхода зафиксирована скелетом §5.4.
/// </summary>
public sealed class SweepToolResult
{
    /// <summary>Абсолютный путь к report.json.</summary>
    public string ReportJsonPath { get; set; } = "";

    /// <summary>Абсолютный путь к report.md.</summary>
    public string ReportMarkdownPath { get; set; } = "";

    /// <summary>Раздел summary отчёта — отдаётся как есть, без пересчёта.</summary>
    public object? Summary { get; set; }
}
