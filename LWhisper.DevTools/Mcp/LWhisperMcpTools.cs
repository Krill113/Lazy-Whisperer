using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LWhisper.DevTools.Mcp;

/// <summary>
/// Инструменты MCP-сервера поверх LWhisper.SpeechEngine.
/// Пайплайн тот же, что в проде: CliOptions.Parse → TranscribeRunner → RunReport.
/// </summary>
[McpServerToolType]
public sealed class LWhisperMcpTools
{
    /// <summary>Распознать один WAV с явными параметрами движка.</summary>
    [McpServerTool(Name = "transcribe", ReadOnly = true)]
    [Description("Распознать один WAV-файл движком LWhisper (Whisper.net, CPU) и вернуть текст с метриками времени.")]
    public static async Task<TranscribeToolResult> TranscribeAsync(
        [Description("Абсолютный путь к WAV-файлу (PCM 16 кГц, моно, 16 бит).")] string path,
        [Description("Язык распознавания: ru | en | auto. По умолчанию ru.")] string? language = null,
        [Description("Floor контекстного окна энкодера. 0 = не вызывать WithAudioContextSize. По умолчанию — дефолт движка.")] int? ctxFloor = null,
        [Description("Число потоков Whisper. По умолчанию — формула движка.")] int? threads = null,
        [Description("Режим бюджета потоков: legacy | divided. По умолчанию legacy.")] string? threadMode = null,
        [Description("Beam search вместо greedy. По умолчанию false.")] bool? beam = null,
        [Description("Путь к ggml-*.bin либо id модели (large-v3-turbo, medium, small). По умолчанию — модель из settings.json.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedLanguage = McpArgs.NormalizeLanguage(language);
            // Каталог не создаётся: transcribe отдаёт результат в JSON-ответе, на диск ничего не пишет.
            // --out в argv нужен только чтобы CliOptions.Parse получила согласованный набор опций.
            var outDir = McpEngine.RunDirectoryPath("transcribe");
            var argv = McpArgs.BuildTranscribeArgs(
                path, normalizedLanguage, ctxFloor, threads, threadMode, beam, model, outDir);

            var report = await McpEngine.RunAsync(model, normalizedLanguage, beam ?? false, argv, cancellationToken);

            var run = report.Runs.FirstOrDefault()
                      ?? throw new McpException("Прогон не дал ни одной записи — проверьте путь к WAV");
            if (!string.IsNullOrWhiteSpace(run.Error))
                throw new McpException($"Ошибка распознавания: {run.Error}");

            return new TranscribeToolResult
            {
                Text = run.Text ?? "",
                DurationMs = run.DurationMs,
                ElapsedMs = run.ElapsedMs,
                Rtf = run.Rtf,
                AudioContextSize = run.AudioContextSize,
                // RunRecord.Threads — int? (null = «считает движок», уточнение 3 CP2),
                // а схема §5.4 требует число: подставляем разрешённое значение из EngineInfo.
                Threads = run.Threads ?? report.Engine.DefaultThreads,
                Beam = run.Beam,
                UsedFallback = run.UsedFallback
            };
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (CliParseException ex)
        {
            // TranscribeRunner бросает её на ненайденном пути/пустом наборе .wav
            throw new McpException(ex.Message);
        }
        catch (EngineUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Прогнать сетку параметров по корпусу и вернуть пути отчётов.</summary>
    [McpServerTool(Name = "sweep", ReadOnly = true)]
    [Description("Прогнать сетку параметров (ctx floor / threads / beam) по набору WAV и записать отчёты JSON и Markdown.")]
    public static async Task<SweepToolResult> SweepAsync(
        [Description("Пути к WAV-файлам или каталогам (каталоги обходятся рекурсивно по *.wav).")] string[] paths,
        [Description("Плечи по floor контекстного окна, например [0,256,448].")] int[]? ctxFloors = null,
        [Description("Плечи по числу потоков, например [4,6,8].")] int[]? threads = null,
        [Description("Плечи по beam search, например [false,true].")] bool[]? beam = null,
        [Description("Прогонов на файл. По умолчанию 1.")] int? repeat = null,
        [Description("Число параллельных распознаваний. По умолчанию 1.")] int? parallel = null,
        [Description("Каталог для отчётов. По умолчанию — подкаталог debug-корня.")] string? reportDir = null,
        [Description("Предохранитель: максимум прогонов. По умолчанию 200.")] int? maxRuns = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (paths is null || paths.Length == 0)
                throw new ArgumentException("paths обязателен и не может быть пустым");

            var files = ExpandWavInputs(paths);
            var effectiveMaxRuns = McpArgs.EffectiveMaxRuns(maxRuns);
            var runs = McpArgs.CountRuns(files.Count, ctxFloors, threads, beam, repeat);
            if (runs > effectiveMaxRuns)
                throw new ArgumentException(
                    $"Прогонов {runs} превышает предохранитель maxRuns={effectiveMaxRuns}. " +
                    "Сузьте сетку или поднимите maxRuns явно.");

            var outDir = string.IsNullOrWhiteSpace(reportDir)
                ? McpEngine.RunDirectoryPath("sweep")   // создаст ReportWriter.Write при записи отчёта
                : Directory.CreateDirectory(reportDir).FullName;

            var argv = McpArgs.BuildSweepArgs(
                files, ctxFloors, threads, beam, repeat, parallel, effectiveMaxRuns, outDir);

            var normalizedLanguage = McpArgs.NormalizeLanguage(null);
            var report = await McpEngine.RunAsync(null, normalizedLanguage, beam?.Contains(true) == true, argv, cancellationToken);

            var (jsonPath, markdownPath) = ReportWriter.Write(report, outDir, "both");

            return new SweepToolResult
            {
                ReportJsonPath = jsonPath,
                ReportMarkdownPath = markdownPath,
                Summary = report.Summary
            };
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (CliParseException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (MaxRunsExceededException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (EngineUnavailableException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// Разрешённая конфигурация движка без загрузки модели.
    /// Возвращает DTO CP2 (<see cref="LWhisper.DevTools.EngineInfo"/>) — тот же тип, что печатает
    /// команда engine-info и что лежит в блоке "engine" отчёта. Второй копии схемы быть не должно.
    /// Метод назван GetEngineInfo, а не EngineInfo: одноимённый с типом член сделал бы
    /// обращение EngineInfo.Collect(...) внутри класса неразрешимым (CS0119).
    /// </summary>
    [McpServerTool(Name = "engine_info", ReadOnly = true)]
    [Description("Вернуть разрешённую конфигурацию движка LWhisper: модель, потоки, floor контекста, состояние дампа аудио.")]
    public static LWhisper.DevTools.EngineInfo GetEngineInfo()
    {
        var modelPath = McpEngine.ResolveModelPath(null);

        // CliOptions с одними дефолтами: язык ru, ctx-floor 448, thread-mode legacy, beam выкл.
        // После CP5/CP6 EngineInfo.Collect берёт фактические значения из WhisperTuning,
        // поэтому engine_info автоматически перестаёт врать при выставленных env-переменных.
        var options = new CliOptions { Command = "engine-info" };

        return LWhisper.DevTools.EngineInfo.Collect(
            options, modelPath, LWhisper.DevTools.EngineInfo.SafeRuntimeInfo());
    }

    private static IReadOnlyList<string> ExpandWavInputs(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("paths: пустой путь недопустим");

            if (Directory.Exists(input))
            {
                files.AddRange(Directory.GetFiles(input, "*.wav", SearchOption.AllDirectories));
            }
            else if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
            }
            else
            {
                throw new ArgumentException($"Путь не найден: {input}");
            }
        }

        return files
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
