using System.Globalization;
using System.Text;
using System.Text.Json;
using LWhisper.SpeechEngine.Diagnostics;
using Serilog;
using Serilog.Events;

namespace LWhisper.DevTools;

/// <summary>
/// Точка входа консольного стенда. Коды возврата — закон §5.3 скелета:
/// 0 успех; 1 ошибка аргументов/входа; 2 превышен --max-runs; 3 модель/движок недоступны.
/// </summary>
public static class Program
{
    public const int ExitOk = 0;
    public const int ExitBadArguments = 1;
    public const int ExitMaxRunsExceeded = 2;
    public const int ExitEngineUnavailable = 3;

    public static async Task<int> Main(string[] args)
    {
        DisableInheritedAudioDump();

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* перенаправленный stdout */ }

        if (args.Length == 0)
        {
            Console.Error.WriteLine(CliOptions.Usage);
            return ExitBadArguments;
        }

        if (args.Any(a => a is "--help" or "-h" or "help"))
        {
            Console.WriteLine(CliOptions.Usage);
            return ExitOk;
        }

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliParseException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitBadArguments;
        }

        if (options.Command == "mcp")
        {
            // CP3 заменяет эту ветку запуском stdio-сервера ModelContextProtocol.
            Console.Error.WriteLine("MCP-режим появится в CP3 (план 04-cp3-mcp.md).");
            return ExitBadArguments;
        }

        var modelPath = ModelResolver.Resolve(options.Model);

        return options.Command == "engine-info"
            ? RunEngineInfo(options, modelPath)
            : await RunMeasurementAsync(options, modelPath);
    }

    /// <summary>
    /// Гасит дамп аудио (CP1) для СВОЕГО процесса — до первого обращения к <c>AudioDumpSink</c>,
    /// потому что <c>AudioDumpSink.Enabled</c> кэшируется при первом чтении.
    ///
    /// Зачем: владелец диктует корпус с <c>LWHISPER_DEBUG_AUDIO=1</c> (в том числе постоянной
    /// User-переменной) и тем же пользователем запускает свип. Унаследованный флаг заставил бы
    /// <c>RecognizeStreamingAsync</c> писать WAV и строку <c>meta.jsonl</c> ВНУТРИ измеряемого окна,
    /// а глобальный <c>MetaLock</c> ещё и сериализовал бы параллельные прогоны — p10/p25 коротких
    /// (главная метрика выбора floor) стали бы невоспроизводимыми.
    ///
    /// Только запись переменной, без чтения: закон §4 скелета — читает окружение исключительно
    /// <c>LWhisper.SpeechEngine</c>. Следствие: <c>engine-info</c> в DevTools всегда печатает
    /// <c>dumpEnabled: false</c>, и это правда про процесс стенда.
    /// </summary>
    private static void DisableInheritedAudioDump()
        => Environment.SetEnvironmentVariable("LWHISPER_DEBUG_AUDIO", null);

    /// <summary>
    /// engine-info печатает в stdout ТОЛЬКО JSON — его парсят скриптами и (в CP3) MCP-клиент.
    /// Поэтому console-sink Serilog здесь не подключается вовсе.
    /// </summary>
    private static int RunEngineInfo(CliOptions options, string modelPath)
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();
        var info = EngineInfo.Collect(options, modelPath, EngineInfo.SafeRuntimeInfo());
        Console.Out.WriteLine(JsonSerializer.Serialize(info, ReportWriter.JsonOptions));
        return ExitOk;
    }

    private static async Task<int> RunMeasurementAsync(CliOptions options, string modelPath)
    {
        var outDir = ResolveOutDir(options);
        Directory.CreateDirectory(outDir);

        var fallbackWatch = new FallbackWatchSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Sink(fallbackWatch)
            .WriteTo.Console(restrictedToMinimumLevel: options.Quiet ? LogEventLevel.Warning : LogEventLevel.Information)
            .WriteTo.File(Path.Combine(outDir, "devtools-log.txt"), restrictedToMinimumLevel: LogEventLevel.Debug)
            .CreateLogger();

        try
        {
            using var runner = new TranscribeRunner(modelPath, options.Language, gpu: false, fallbackWatch);
            var report = await runner.RunAsync(options);
            var (jsonPath, markdownPath) = ReportWriter.Write(report, outDir, options.Format);
            PrintSummary(report, jsonPath, markdownPath);
            return ExitOk;
        }
        catch (CliParseException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitBadArguments;
        }
        catch (MaxRunsExceededException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitMaxRunsExceeded;
        }
        catch (EngineUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitEngineUnavailable;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// По умолчанию отчёты ложатся в {repoRoot}/docs/superpowers/measurements/{ts} — папка целиком
    /// в .gitignore, поэтому транскрипты речи владельца не утекают в публичный репозиторий.
    /// Если репозиторий не найден (запуск из папки поставки) — {DebugRoot}/reports/{ts}.
    /// </summary>
    private static string ResolveOutDir(CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutDir))
            return Path.GetFullPath(options.OutDir!);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var repoRoot = FindRepoRoot();
        return repoRoot != null
            ? Path.Combine(repoRoot, "docs", "superpowers", "measurements", timestamp)
            : Path.Combine(EnginePaths.DebugRoot, "reports", timestamp);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LWhisperer.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Итоговая сводка печатается всегда, в том числе при --quiet.</summary>
    private static void PrintSummary(RunReport report, string jsonPath, string markdownPath)
    {
        var c = CultureInfo.InvariantCulture;
        var s = report.Summary;

        Console.WriteLine($"Прогонов: {s.N.ToString(c)} (коротких <{RunReport.ShortDurationMs.ToString("F0", c)} мс: {s.ShortN.ToString(c)}), плеч: {s.ByArm.Count.ToString(c)}");
        Console.WriteLine($"все:      tailRate={s.TailRate.ToString("F3", c)}  p10={s.P10Ms.ToString("F0", c)}мс  p25={s.P25Ms.ToString("F0", c)}мс  median={s.MedianMs.ToString("F0", c)}мс  p90={s.P90Ms.ToString("F0", c)}мс");
        Console.WriteLine($"короткие: tailRate={s.ShortTailRate.ToString("F3", c)}  p10={s.ShortP10Ms.ToString("F0", c)}мс  p25={s.ShortP25Ms.ToString("F0", c)}мс  median={s.ShortMedianMs.ToString("F0", c)}мс");

        foreach (var arm in s.ByArm)
        {
            Console.WriteLine($"  {arm.Arm}: n={arm.N.ToString(c)} tail={arm.TailRate.ToString("F3", c)} " +
                              $"p10={arm.P10Ms.ToString("F0", c)}мс median={arm.MedianMs.ToString("F0", c)}мс " +
                              $"distinctTexts={arm.DistinctTexts.ToString(c)}");
        }

        var fallbacks = report.Runs.Count(r => r.UsedFallback);
        if (fallbacks > 0)
            Console.WriteLine($"ВНИМАНИЕ: fallback движка в {fallbacks.ToString(c)} прогонах — замер невалиден (спека §5, правило 4).");

        var errors = report.Runs.Count(r => r.Error != null);
        if (errors > 0)
            Console.WriteLine($"Ошибок распознавания: {errors.ToString(c)}");

        if (jsonPath.Length > 0) Console.WriteLine($"JSON:     {jsonPath}");
        if (markdownPath.Length > 0) Console.WriteLine($"Markdown: {markdownPath}");
    }
}
