using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LWhisper.DevTools;

/// <summary>
/// Сериализация отчёта. JSON — машинный вход для --baseline и для MCP (CP3),
/// Markdown — то, что читает человек. Схема JSON — закон §5.3 скелета.
/// </summary>
public static class ReportWriter
{
    public const string JsonFileName = "report.json";
    public const string MarkdownFileName = "report.md";

    /// <summary>
    /// camelCase по схеме §5.3; UnsafeRelaxedJsonEscaping — чтобы кириллица в транскриптах
    /// осталась читаемой, а не превратилась в \uXXXX. Файл локальный, не отдаётся в HTML.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson(RunReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static RunReport? FromJson(string json) => JsonSerializer.Deserialize<RunReport>(json, JsonOptions);

    public static string ToMarkdown(RunReport report)
    {
        var c = CultureInfo.InvariantCulture;
        var e = report.Engine;
        var s = report.Summary;
        var sb = new StringBuilder();

        sb.AppendLine("# LWhisper DevTools — отчёт замера");
        sb.AppendLine();
        sb.AppendLine($"- **Тег:** {(string.IsNullOrWhiteSpace(report.Tag) ? "—" : Md(report.Tag!))}");
        sb.AppendLine($"- **Модель:** `{Md(e.ModelFile)}` ({Md(e.ModelPath)}, существует: {(e.ModelExists ? "да" : "нет")})");
        sb.AppendLine($"- **Язык:** {Md(e.Language)}");
        sb.AppendLine($"- **GPU:** {(e.Gpu ? "да" : "нет (стенд CPU-only)")}");
        sb.AppendLine($"- **Ядер:** {e.ProcessorCount.ToString(c)}; потоки по умолчанию: {e.DefaultThreads.ToString(c)}; режим: {Md(e.ThreadMode)}");
        sb.AppendLine($"- **Floor окна энкодера (дефолт процесса, не плеча):** {e.CtxFloorDefault.ToString(c)}");
        sb.AppendLine($"- **Whisper.net:** {Md(e.WhisperNet)}");
        sb.AppendLine($"- **Runtime:** {Md(string.IsNullOrWhiteSpace(e.RuntimeInfo) ? "—" : e.RuntimeInfo)}");
        sb.AppendLine($"- **Дамп аудио (LWHISPER_DEBUG_AUDIO):** {(e.DumpEnabled ? "включён → " + Md(e.DumpDirectory ?? "") : "выключен")}");
        sb.AppendLine($"- **Начало (UTC):** {Md(report.StartedUtc)}");
        sb.AppendLine($"- **Конец (UTC):** {Md(report.FinishedUtc)}");
        sb.AppendLine($"- **schemaVersion:** {report.SchemaVersion.ToString(c)}");
        sb.AppendLine();

        var fallbacks = report.Runs.Count(r => r.UsedFallback);
        if (fallbacks > 0)
        {
            sb.AppendLine($"> **ЗАМЕР НЕВАЛИДЕН:** аварийный fallback движка сработал в {fallbacks.ToString(c)} прогонах " +
                          "(спека §5, правило 4 — такой замер выбрасывается целиком).");
            sb.AppendLine();
        }

        sb.AppendLine("## Итог");
        sb.AppendLine();
        sb.AppendLine("| группа | n | tailRate | p10, мс | p25, мс | медиана, мс | p90, мс |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine($"| все | {s.N.ToString(c)} | {s.TailRate.ToString("F3", c)} | {s.P10Ms.ToString("F0", c)} | " +
                      $"{s.P25Ms.ToString("F0", c)} | {s.MedianMs.ToString("F0", c)} | {s.P90Ms.ToString("F0", c)} |");
        sb.AppendLine($"| короткие (<{RunReport.ShortDurationMs.ToString("F0", c)} мс) | {s.ShortN.ToString(c)} | " +
                      $"{s.ShortTailRate.ToString("F3", c)} | {s.ShortP10Ms.ToString("F0", c)} | " +
                      $"{s.ShortP25Ms.ToString("F0", c)} | {s.ShortMedianMs.ToString("F0", c)} | — |");
        sb.AppendLine();

        sb.AppendLine("## По плечам");
        sb.AppendLine();
        sb.AppendLine("| arm | n | tailRate | p10 | p25 | median | p90 | distinctTexts | коротких | p10 кор. | p25 кор. |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var a in s.ByArm)
        {
            sb.AppendLine($"| `{Md(a.Arm)}` | {a.N.ToString(c)} | {a.TailRate.ToString("F3", c)} | " +
                          $"{a.P10Ms.ToString("F0", c)} | {a.P25Ms.ToString("F0", c)} | {a.MedianMs.ToString("F0", c)} | " +
                          $"{a.P90Ms.ToString("F0", c)} | {a.DistinctTexts.ToString(c)} | {a.ShortN.ToString(c)} | " +
                          $"{a.ShortP10Ms.ToString("F0", c)} | {a.ShortP25Ms.ToString("F0", c)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Текст по файлам");
        sb.AppendLine();
        sb.AppendLine("| файл | arm | повтор | длит., мс | elapsed, мс | rtf | ctx | fallback | текст |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var r in report.Runs)
        {
            var cell = r.Error == null ? r.Text : "ОШИБКА: " + r.Error;
            sb.AppendLine($"| {Md(Path.GetFileName(r.File))} | `{Md(r.Arm)}` | {r.RepeatIndex.ToString(c)} | " +
                          $"{r.DurationMs.ToString("F0", c)} | {r.ElapsedMs.ToString("F0", c)} | {r.Rtf.ToString("F3", c)} | " +
                          $"{r.AudioContextSize.ToString(c)} | {(r.UsedFallback ? "ДА" : "")} | {Md(cell)} |");
        }
        sb.AppendLine();

        var diff = s.BaselineDiff;
        if (diff != null)
        {
            sb.AppendLine("## Расхождения текста");
            sb.AppendLine();
            sb.AppendLine($"- **Базлайн:** {Md(diff.BaselineFile)}");
            sb.AppendLine($"- **Файлов с расхождением:** {diff.TextMismatches.ToString(c)}");
            sb.AppendLine($"- **Δ tailRate:** {diff.TailRateDelta.ToString("+0.000;-0.000;0.000", c)}");
            sb.AppendLine($"- **Δ p10:** {diff.P10DeltaMs.ToString("+0;-0;0", c)} мс");
            sb.AppendLine();
            if (diff.Mismatches.Count > 0)
            {
                sb.AppendLine("| файл | baseline | текущий |");
                sb.AppendLine("|---|---|---|");
                foreach (var m in diff.Mismatches)
                    sb.AppendLine($"| {Md(Path.GetFileName(m.File))} | {Md(m.BaselineText)} | {Md(m.CurrentText)} |");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Пишет отчёт в каталог. format: json | md | both. Для невыбранного формата возвращается пустая строка.
    /// </summary>
    public static (string jsonPath, string markdownPath) Write(RunReport report, string outDir, string format)
    {
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("Каталог отчёта не задан", nameof(outDir));

        Directory.CreateDirectory(outDir);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var jsonPath = "";
        var markdownPath = "";

        if (format is "json" or "both")
        {
            jsonPath = Path.Combine(outDir, JsonFileName);
            File.WriteAllText(jsonPath, ToJson(report), encoding);
        }

        if (format is "md" or "both")
        {
            markdownPath = Path.Combine(outDir, MarkdownFileName);
            File.WriteAllText(markdownPath, ToMarkdown(report), encoding);
        }

        return (jsonPath, markdownPath);
    }

    /// <summary>Экранирование ячейки Markdown-таблицы: вертикальная черта и переводы строк.</summary>
    private static string Md(string value)
        => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
