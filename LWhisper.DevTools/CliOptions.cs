using System.Globalization;
using System.Text;

namespace LWhisper.DevTools;

/// <summary>
/// Ошибка разбора аргументов командной строки. Текст сообщения показывается пользователю как есть.
/// </summary>
public sealed class CliParseException : Exception
{
    public CliParseException(string message) : base(message) { }
}

/// <summary>
/// Разобранные аргументы CLI. Набор опций и значения по умолчанию — закон §5.3 скелета плана волны.
/// </summary>
public sealed record CliOptions
{
    /// <summary>transcribe | sweep | engine-info | mcp</summary>
    public string Command { get; init; } = "";

    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();
    public string? Model { get; init; }
    public string Language { get; init; } = DefaultLanguage;
    public int CtxFloor { get; init; } = DefaultCtxFloor;

    /// <summary>null = «число потоков считает движок» (в имени плеча — threads=auto).</summary>
    public int? Threads { get; init; }

    public string ThreadMode { get; init; } = DefaultThreadMode;
    public bool Beam { get; init; }

    /// <summary>
    /// Подсказка Whisper (<c>WithPrompt</c>), которой приложение биасит доменную лексику.
    /// null = прогон без подсказки — историческое поведение стенда.
    /// Нужна, чтобы стенд мог воспроизвести боевой путь: приложение всегда собирает подсказку
    /// из vocabulary.txt, и её ФОРМАТ влияет на стиль вывода (список через запятую провоцирует
    /// перечислительные повторы). Без этой опции A/B «с подсказкой / без» на стенде невозможен.
    /// </summary>
    public string? InitialPrompt { get; init; }
    public int Parallel { get; init; } = 1;
    public int Repeat { get; init; } = 1;
    public string? OutDir { get; init; }
    public string? Tag { get; init; }
    public string Format { get; init; } = "both";
    public bool Quiet { get; init; }

    /// <summary>
    /// Предельная длительность файла корпуса в секундах. Файлы длиннее отбрасываются с предупреждением.
    /// Смысл: папка сессии CP1 содержит не только сегменты, но и длинные записи целиком; сегмент дольше
    /// MaxSegmentDurationMs (15 с) волной не тюнится, а прогон 10-минутного файла × сетку плеч — это часы
    /// впустую и гарантированный ctx > FullWindowContext.
    /// </summary>
    public int MaxDurationSeconds { get; init; } = DefaultMaxDurationSeconds;

    // --- только для sweep ---
    public IReadOnlyList<int>? GridCtx { get; init; }
    public IReadOnlyList<int>? GridThreads { get; init; }
    public IReadOnlyList<bool>? GridBeam { get; init; }
    public string? BaselineReport { get; init; }
    public int MaxRuns { get; init; } = DefaultMaxRuns;

    public const string DefaultLanguage = "ru";
    public const string DefaultThreadMode = "legacy";
    public const int DefaultMaxRuns = 200;
    public const string DefaultModelId = "large-v3-turbo";

    /// <summary>
    /// 30 с = штатное окно whisper (WhisperTuning.FullWindowSeconds). Всё, что длиннее, для тюнинга
    /// streaming-сегментов бессмысленно и опасно (см. MaxDurationSeconds).
    /// </summary>
    public const int DefaultMaxDurationSeconds = 30;

    /// <summary>
    /// Дефолтный floor контекстного окна энкодера.
    /// ЗЕРКАЛО <c>WhisperTuning.DefaultAudioContextFloor</c> (появляется в CP5).
    /// До CP5 движок значение игнорирует, ранер всё равно выставляет LWHISPER_AUDIO_CTX_FLOOR —
    /// чтобы после CP5 отчёты стали правдивыми без единой правки DevTools.
    /// ПРИ СМЕНЕ ЗНАЧЕНИЯ В CP5 ОБЯЗАТЕЛЬНО ПОМЕНЯТЬ И ЗДЕСЬ.
    /// </summary>
    public const int DefaultCtxFloor = 448;

    private static readonly string[] KnownCommands = { "transcribe", "sweep", "engine-info", "mcp" };

    public static CliOptions Parse(string[] args)
    {
        if (args == null || args.Length == 0)
            throw new CliParseException("Не указана команда. Доступны: transcribe, sweep, engine-info, mcp. Подробности: --help");

        var command = args[0];
        if (command == "--mcp")
        {
            command = "mcp";
        }
        else if (command.StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliParseException($"Первым аргументом должна быть команда, получено '{command}'. Подробности: --help");
        }

        if (Array.IndexOf(KnownCommands, command) < 0)
            throw new CliParseException($"Неизвестная команда '{command}'. Доступны: transcribe, sweep, engine-info, mcp.");

        var inputs = new List<string>();
        string? model = null;
        var language = DefaultLanguage;
        var ctxFloor = DefaultCtxFloor;
        int? threads = null;
        var threadMode = DefaultThreadMode;
        var beam = false;
        string? initialPrompt = null;
        var parallel = 1;
        var repeat = 1;
        string? outDir = null;
        string? tag = null;
        var format = "both";
        var quiet = false;
        List<int>? gridCtx = null;
        List<int>? gridThreads = null;
        List<bool>? gridBeam = null;
        string? baseline = null;
        var maxRuns = DefaultMaxRuns;
        var maxDuration = DefaultMaxDurationSeconds;

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--input": inputs.Add(Value(args, ref i, a)); break;
                case "--model": model = Value(args, ref i, a); break;
                case "--language": language = Value(args, ref i, a).ToLowerInvariant(); break;
                case "--ctx-floor": ctxFloor = Int(Value(args, ref i, a), a, 0); break;
                case "--threads": threads = Int(Value(args, ref i, a), a, 1); break;
                case "--thread-mode": threadMode = Value(args, ref i, a).ToLowerInvariant(); break;
                case "--beam": beam = true; break;
                case "--prompt": initialPrompt = Value(args, ref i, a); break;
                case "--prompt-file": initialPrompt = ReadPromptFile(Value(args, ref i, a)); break;
                case "--parallel": parallel = Int(Value(args, ref i, a), a, 1); break;
                case "--repeat": repeat = Int(Value(args, ref i, a), a, 1); break;
                case "--out": outDir = Value(args, ref i, a); break;
                case "--tag": tag = Value(args, ref i, a); break;
                case "--format": format = Value(args, ref i, a).ToLowerInvariant(); break;
                case "--quiet": quiet = true; break;
                case "--max-duration": maxDuration = Int(Value(args, ref i, a), a, 1); break;
                case "--mcp": command = "mcp"; break;
                case "--grid-ctx": gridCtx = IntCsv(Value(args, ref i, a), a, 0); break;
                case "--grid-threads": gridThreads = IntCsv(Value(args, ref i, a), a, 1); break;
                case "--grid-beam": gridBeam = BoolCsv(Value(args, ref i, a), a); break;
                case "--baseline": baseline = Value(args, ref i, a); break;
                case "--max-runs": maxRuns = Int(Value(args, ref i, a), a, 1); break;
                default:
                    throw new CliParseException($"Неизвестная опция '{a}'. Подробности: --help");
            }
        }

        if (language is not ("ru" or "en" or "auto"))
            throw new CliParseException($"Опция --language: допустимы ru, en, auto; получено '{language}'.");
        if (threadMode is not ("legacy" or "divided"))
            throw new CliParseException($"Опция --thread-mode: допустимы legacy, divided; получено '{threadMode}'.");
        if (format is not ("json" or "md" or "both"))
            throw new CliParseException($"Опция --format: допустимы json, md, both; получено '{format}'.");

        if ((command is "transcribe" or "sweep") && inputs.Count == 0)
            throw new CliParseException($"Команде '{command}' нужен хотя бы один --input <файл.wav|папка>.");

        if (command != "sweep" && (gridCtx != null || gridThreads != null || gridBeam != null || baseline != null))
            throw new CliParseException("Опции --grid-ctx/--grid-threads/--grid-beam/--baseline допустимы только для команды sweep.");

        return new CliOptions
        {
            Command = command,
            Inputs = inputs,
            Model = model,
            Language = language,
            CtxFloor = ctxFloor,
            Threads = threads,
            ThreadMode = threadMode,
            Beam = beam,
            InitialPrompt = string.IsNullOrWhiteSpace(initialPrompt) ? null : initialPrompt,
            Parallel = parallel,
            Repeat = repeat,
            OutDir = outDir,
            Tag = tag,
            Format = format,
            Quiet = quiet,
            MaxDurationSeconds = maxDuration,
            GridCtx = gridCtx,
            GridThreads = gridThreads,
            GridBeam = gridBeam,
            BaselineReport = baseline,
            MaxRuns = maxRuns
        };
    }

    private static string Value(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
            throw new CliParseException($"Опция {option} требует значение.");
        return args[++i];
    }

    /// <summary>
    /// Читает подсказку из файла ДОСЛОВНО (UTF-8, обрезаются только крайние пробелы/переводы строк).
    /// Именно дословно: формат подсказки — предмет измерения, поэтому стенд не имеет права
    /// её переупаковывать. Реплику боевой подсказки готовит вызывающий.
    /// </summary>
    private static string ReadPromptFile(string path)
    {
        if (!File.Exists(path))
            throw new CliParseException($"Опция --prompt-file: файл не найден: '{path}'.");
        return File.ReadAllText(path, Encoding.UTF8).Trim();
    }

    private static int Int(string raw, string option, int min)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min)
            throw new CliParseException($"Опция {option}: ожидается целое ≥ {min}, получено '{raw}'.");
        return v;
    }

    private static List<int> IntCsv(string raw, string option, int min)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new CliParseException($"Опция {option}: пустой список значений.");
        var list = new List<int>(parts.Length);
        foreach (var p in parts) list.Add(Int(p, option, min));
        return list;
    }

    private static List<bool> BoolCsv(string raw, string option)
    {
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new CliParseException($"Опция {option}: пустой список значений.");
        var list = new List<bool>(parts.Length);
        foreach (var p in parts)
        {
            if (!bool.TryParse(p, out var b))
                throw new CliParseException($"Опция {option}: ожидается false/true, получено '{p}'.");
            list.Add(b);
        }
        return list;
    }

    /// <summary>Справка. Текст соответствует §5.3 скелета плана волны.</summary>
    public static string Usage { get; } = BuildUsage();

    private static string BuildUsage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("LWhisper.DevTools <command> [options]");
        sb.AppendLine();
        sb.AppendLine("Команды:");
        sb.AppendLine("  transcribe    распознать один или несколько WAV");
        sb.AppendLine("  sweep         прогнать сетку параметров по корпусу");
        sb.AppendLine("  engine-info   напечатать разрешённую конфигурацию движка (JSON) и выйти");
        sb.AppendLine("  mcp           запустить MCP-сервер по stdio (синоним — глобальный флаг --mcp)");
        sb.AppendLine();
        sb.AppendLine("Общие опции:");
        sb.AppendLine("  --input <path>              файл .wav или папка (рекурсивно *.wav); повторяемо");
        sb.AppendLine("  --model <path|id>           путь к ggml-*.bin ИЛИ id модели (-> {AppData}/LWhisper/Models/ggml-{id}.bin)");
        sb.AppendLine($"                              по умолчанию: WhisperModelSize из settings.json, иначе {DefaultModelId}");
        sb.AppendLine($"  --language <ru|en|auto>     по умолчанию {DefaultLanguage}");
        sb.AppendLine($"  --ctx-floor <int>           0 = не вызывать WithAudioContextSize; по умолчанию {DefaultCtxFloor}");
        sb.AppendLine("  --threads <int>             по умолчанию — формула движка");
        sb.AppendLine($"  --thread-mode <legacy|divided>  по умолчанию {DefaultThreadMode}");
        sb.AppendLine("  --prompt <текст>                подсказка Whisper (WithPrompt); по умолчанию без подсказки");
        sb.AppendLine("  --prompt-file <путь>            то же, но текст берётся из файла дословно (UTF-8)");
        sb.AppendLine("  --beam                      включить beam search (по умолчанию greedy)");
        sb.AppendLine("  --parallel <int>            число параллельных распознаваний, по умолчанию 1");
        sb.AppendLine("  --repeat <int>              прогонов на файл, по умолчанию 1");
        sb.AppendLine("  --out <dir>                 каталог отчётов; по умолчанию {repoRoot}/docs/superpowers/measurements/{ts}");
        sb.AppendLine("  --tag <string>              метка прогона, попадает в отчёт");
        sb.AppendLine("  --format <json|md|both>     по умолчанию both");
        sb.AppendLine("  --quiet                     только итоговая сводка в stdout");
        sb.AppendLine($"  --max-duration <sec>        файлы длиннее отбрасываются (по умолчанию {DefaultMaxDurationSeconds});");
        sb.AppendLine("                              session.wav из папок дампа исключается всегда");
        sb.AppendLine();
        sb.AppendLine("Опции sweep (дополнительно):");
        sb.AppendLine("  --grid-ctx <csv>            например 0,256,448,768   (по умолчанию — значение --ctx-floor)");
        sb.AppendLine("  --grid-threads <csv>        например 4,6,8           (по умолчанию — один прогон на формуле)");
        sb.AppendLine("  --grid-beam <csv>           false,true               (по умолчанию — значение --beam)");
        sb.AppendLine("  --baseline <report.json>    сравнить тексты и метрики с предыдущим отчётом");
        sb.AppendLine($"  --max-runs <int>            предохранитель, по умолчанию {DefaultMaxRuns}; превышение = код возврата 2");
        sb.AppendLine();
        sb.AppendLine("Коды возврата: 0 успех; 1 ошибка аргументов/входа; 2 превышен --max-runs; 3 модель/движок недоступны.");
        return sb.ToString();
    }
}
