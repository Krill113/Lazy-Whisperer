namespace LWhisper.DevTools;

/// <summary>Один прогон: один файл × одно плечо × один повтор.</summary>
public sealed class RunRecord
{
    /// <summary>Полный путь к WAV. Сопоставление с базлайном идёт по имени файла.</summary>
    public string File { get; set; } = "";

    public string FileSha256 { get; set; } = "";
    public double DurationMs { get; set; }

    /// <summary>Имя плеча, например ctx=448,threads=auto,beam=false</summary>
    public string Arm { get; set; } = "";

    public int CtxFloor { get; set; }

    /// <summary>
    /// Размер окна энкодера по формуле §5.2 скелета. До CP5 движок формулу не применяет —
    /// поле показывает ЗАПРОШЕННОЕ значение, а не фактическое (см. раздел «что работает в CP2»).
    /// </summary>
    public int AudioContextSize { get; set; }

    /// <summary>null = число потоков выбирает движок (в имени плеча — threads=auto).</summary>
    public int? Threads { get; set; }

    public bool Beam { get; set; }
    public int Parallel { get; set; }
    public int RepeatIndex { get; set; }
    public double ElapsedMs { get; set; }

    /// <summary>rtf = elapsedMs / durationMs (спека §5).</summary>
    public double Rtf { get; set; }

    public string Text { get; set; } = "";
    public string TextSha256 { get; set; } = "";

    /// <summary>Сработал аварийный fallback движка — по спеке §5 (правило 4) замер невалиден.</summary>
    public bool UsedFallback { get; set; }

    public string? Error { get; set; }
}

/// <summary>Сводка по одному плечу.</summary>
public sealed class ArmSummary
{
    public string Arm { get; set; } = "";
    public int N { get; set; }
    public double TailRate { get; set; }
    public double P10Ms { get; set; }
    public double P25Ms { get; set; }
    public double MedianMs { get; set; }
    public double P90Ms { get; set; }

    /// <summary>Число различных textSha256 внутри плеча — детектор недетерминизма.</summary>
    public int DistinctTexts { get; set; }

    public int ShortN { get; set; }
    public double ShortTailRate { get; set; }
    public double ShortP10Ms { get; set; }
    public double ShortP25Ms { get; set; }
    public double ShortMedianMs { get; set; }
}

/// <summary>Расхождение текста по одному файлу между базлайном и текущим прогоном.</summary>
public sealed class BaselineTextMismatch
{
    public string File { get; set; } = "";
    public string BaselineText { get; set; } = "";
    public string CurrentText { get; set; } = "";
}

/// <summary>Сравнение с предыдущим отчётом (--baseline).</summary>
public sealed class BaselineDiff
{
    public string BaselineFile { get; set; } = "";
    public int TextMismatches { get; set; }
    public List<string> MismatchFiles { get; set; } = new();
    public List<BaselineTextMismatch> Mismatches { get; set; } = new();
    public double TailRateDelta { get; set; }
    public double P10DeltaMs { get; set; }
}

/// <summary>Итоговая сводка отчёта: по всем прогонам, по группе коротких и по плечам.</summary>
public sealed class RunSummary
{
    public int N { get; set; }
    public double TailRate { get; set; }
    public double P10Ms { get; set; }
    public double P25Ms { get; set; }
    public double MedianMs { get; set; }
    public double P90Ms { get; set; }
    public int ShortN { get; set; }
    public double ShortTailRate { get; set; }
    public double ShortP10Ms { get; set; }
    public double ShortP25Ms { get; set; }
    public double ShortMedianMs { get; set; }
    public List<ArmSummary> ByArm { get; set; } = new();
    public BaselineDiff? BaselineDiff { get; set; }
}

/// <summary>Отчёт замера. Схема — закон §5.3 скелета, schemaVersion 1, camelCase.</summary>
public sealed class RunReport
{
    /// <summary>Граница «коротких» из спеки §5: durationMs &lt; 5120.</summary>
    public const double ShortDurationMs = 5120;

    public int SchemaVersion { get; set; } = 1;
    public string Tool { get; set; } = "LWhisper.DevTools";
    public string? Tag { get; set; }
    public string StartedUtc { get; set; } = "";
    public string FinishedUtc { get; set; } = "";
    public EngineInfo Engine { get; set; } = new();
    public List<RunRecord> Runs { get; set; } = new();
    public RunSummary Summary { get; set; } = new();
}
