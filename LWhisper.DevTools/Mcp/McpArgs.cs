using System.Globalization;

namespace LWhisper.DevTools.Mcp;

/// <summary>
/// Валидация входа MCP-инструментов и сборка argv для CliOptions.Parse (CP2).
/// Ни файловых операций, ни зависимостей от пакета MCP — тип полностью юнит-тестируем.
/// </summary>
public static class McpArgs
{
    /// <summary>
    /// Предохранитель числа прогонов sweep по умолчанию (скелет §5.4, закон 3).
    /// Зеркалит CLI-дефолт — собственной константы не заводим (§5.4, закон 5).
    /// </summary>
    public const int DefaultMaxRuns = CliOptions.DefaultMaxRuns;

    /// <summary>Язык распознавания по умолчанию — тот же, что у CLI (скелет §5.3).</summary>
    public const string DefaultLanguage = CliOptions.DefaultLanguage;

    private static readonly string[] AllowedLanguages = { "ru", "en", "auto" };
    private static readonly string[] AllowedThreadModes = { "legacy", "divided" };

    /// <summary>Приводит язык к каноническому виду и проверяет его допустимость.</summary>
    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return DefaultLanguage;
        var value = language.Trim().ToLowerInvariant();
        if (Array.IndexOf(AllowedLanguages, value) < 0)
            throw new ArgumentException($"language: допустимо ru | en | auto, получено '{language}'");
        return value;
    }

    /// <summary>Приводит режим бюджета потоков к каноническому виду и проверяет его допустимость.</summary>
    public static string? NormalizeThreadMode(string? threadMode)
    {
        if (string.IsNullOrWhiteSpace(threadMode)) return null;
        var value = threadMode.Trim().ToLowerInvariant();
        if (Array.IndexOf(AllowedThreadModes, value) < 0)
            throw new ArgumentException($"threadMode: допустимо legacy | divided, получено '{threadMode}'");
        return value;
    }

    /// <summary>Эффективный предохранитель прогонов.</summary>
    public static int EffectiveMaxRuns(int? maxRuns)
    {
        if (maxRuns is null) return DefaultMaxRuns;
        if (maxRuns.Value <= 0)
            throw new ArgumentException($"maxRuns должен быть > 0, получено {maxRuns.Value}");
        return maxRuns.Value;
    }

    /// <summary>Число прогонов сетки: файлы × ctx × threads × beam × repeat. Пустое измерение считается за 1.</summary>
    public static int CountRuns(int fileCount, int[]? ctxFloors, int[]? threads, bool[]? beam, int? repeat)
    {
        if (fileCount <= 0)
            throw new ArgumentException("Не найдено ни одного .wav по указанным путям");
        var repeats = repeat ?? 1;
        if (repeats <= 0)
            throw new ArgumentException($"repeat должен быть > 0, получено {repeats}");
        return fileCount * Dimension(ctxFloors?.Length) * Dimension(threads?.Length)
                         * Dimension(beam?.Length) * repeats;

        static int Dimension(int? length) => length is > 0 ? length.Value : 1;
    }

    /// <summary>CSV из целых, инвариантная культура.</summary>
    public static string Csv(IEnumerable<int> values) =>
        string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>CSV из булевых в нижнем регистре — CLI ждёт false,true.</summary>
    public static string Csv(IEnumerable<bool> values) =>
        string.Join(",", values.Select(v => v ? "true" : "false"));

    /// <summary>
    /// Собирает argv для команды transcribe. Язык уже нормализован вызывающим.
    /// </summary>
    public static string[] BuildTranscribeArgs(
        string path,
        string language,
        int? ctxFloor,
        int? threads,
        string? threadMode,
        bool? beam,
        string? model,
        string outDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path обязателен и не может быть пустым");
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("outDir обязателен и не может быть пустым");
        if (ctxFloor is < 0)
            throw new ArgumentException($"ctxFloor не может быть отрицательным, получено {ctxFloor}");
        if (threads is <= 0)
            throw new ArgumentException($"threads должен быть > 0, получено {threads}");

        var mode = NormalizeThreadMode(threadMode);

        var args = new List<string>
        {
            "transcribe",
            "--input", path,
            "--language", language,
            "--out", outDir,
            "--format", "json",
            "--tag", "mcp-transcribe",
            "--quiet"
        };

        if (ctxFloor.HasValue)
        {
            args.Add("--ctx-floor");
            args.Add(ctxFloor.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (threads.HasValue)
        {
            args.Add("--threads");
            args.Add(threads.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (mode is not null)
        {
            args.Add("--thread-mode");
            args.Add(mode);
        }
        if (beam == true)
        {
            args.Add("--beam");
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model.Trim());
        }

        return args.ToArray();
    }

    /// <summary>
    /// Собирает argv для команды sweep. Пути уже развёрнуты и проверены вызывающим.
    /// </summary>
    public static string[] BuildSweepArgs(
        IReadOnlyList<string> paths,
        int[]? ctxFloors,
        int[]? threads,
        bool[]? beam,
        int? repeat,
        int? parallel,
        int maxRuns,
        string outDir)
    {
        if (paths is null || paths.Count == 0)
            throw new ArgumentException("paths обязателен и не может быть пустым");
        if (string.IsNullOrWhiteSpace(outDir))
            throw new ArgumentException("outDir обязателен и не может быть пустым");
        if (ctxFloors is not null && ctxFloors.Any(v => v < 0))
            throw new ArgumentException("ctxFloors: значения не могут быть отрицательными");
        if (threads is not null && threads.Any(v => v <= 0))
            throw new ArgumentException("threads: значения должны быть > 0");
        if (parallel is <= 0)
            throw new ArgumentException($"parallel должен быть > 0, получено {parallel}");
        if (repeat is <= 0)
            throw new ArgumentException($"repeat должен быть > 0, получено {repeat}");

        var args = new List<string> { "sweep" };
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("paths: пустой путь недопустим");
            args.Add("--input");
            args.Add(path);
        }

        args.Add("--out");
        args.Add(outDir);
        args.Add("--format");
        args.Add("both");
        args.Add("--tag");
        args.Add("mcp-sweep");
        args.Add("--quiet");
        args.Add("--max-runs");
        args.Add(maxRuns.ToString(CultureInfo.InvariantCulture));

        if (ctxFloors is { Length: > 0 })
        {
            args.Add("--grid-ctx");
            args.Add(Csv(ctxFloors));
        }
        if (threads is { Length: > 0 })
        {
            args.Add("--grid-threads");
            args.Add(Csv(threads));
        }
        if (beam is { Length: > 0 })
        {
            args.Add("--grid-beam");
            args.Add(Csv(beam));
        }
        if (repeat.HasValue)
        {
            args.Add("--repeat");
            args.Add(repeat.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (parallel.HasValue)
        {
            args.Add("--parallel");
            args.Add(parallel.Value.ToString(CultureInfo.InvariantCulture));
        }

        return args.ToArray();
    }
}
