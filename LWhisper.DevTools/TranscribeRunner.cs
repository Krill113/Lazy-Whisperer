using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine;
using Serilog;
using Serilog.Context;

namespace LWhisper.DevTools;

/// <summary>Превышен предохранитель --max-runs. Код возврата 2.</summary>
public sealed class MaxRunsExceededException : Exception
{
    public MaxRunsExceededException(string message) : base(message) { }
}

/// <summary>Модель не найдена или движок не инициализировался. Код возврата 3.</summary>
public sealed class EngineUnavailableException : Exception
{
    public EngineUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Единственная реализация пайплайна замера. CP2 (CLI) и CP3 (MCP) используют её как есть —
/// второй реализации быть не должно.
///
/// Идёт всегда по streaming-пути (RecognizeStreamingAsync) — это ровно тот путь, который тюнит волна.
/// Стенд CPU-only: распознаватель создаётся с gpuFailed:true, Vulkan-рантайм в проект не подключён.
/// Рычаги ctx/threads передаются движку через переменные окружения §4 скелета; до CP5/CP6
/// движок их игнорирует, но ранер выставляет их уже сейчас — чтобы заработать без правок.
/// </summary>
public sealed class TranscribeRunner : IDisposable
{
    private sealed class AudioSource
    {
        public string Path { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public AudioData Audio { get; init; } = new();
    }

    private readonly string _modelPath;
    private readonly string _language;
    private readonly bool _gpu;
    private readonly FallbackWatchSink? _fallbackWatch;
    private int _nextRunId;

    /// <summary>
    /// Кэш прогретых движков по ключу (modelPath, language, beam) — закон §5.4 №2 скелета
    /// («один движок на процесс»). Живёт ЗДЕСЬ, а не в McpEngine: движок создаётся и диспозится
    /// внутри RunAsync, поэтому кэш прогонщиков сам по себе ничего бы не давал — каждый вызов
    /// MCP-инструмента transcribe заново делал бы WhisperFactory.FromPath и грузил модель
    /// (large-v3-turbo ~1.5 ГБ), а интерактивный режим ради которого и вводится CP3, стал бы
    /// непригодным. Побочная выгода для CLI: sweep --grid-ctx/--grid-threads больше не
    /// перезагружает модель на каждое плечо — модель одна на все плечи с одинаковым beam.
    ///
    /// Ключ включает beam, потому что стратегия сэмплинга задаётся StreamingSettings в
    /// конструкторе распознавателя. Ctx и threads в ключ НЕ входят: streaming-путь строит
    /// builder заново на каждый вызов (WhisperSpeechRecognizer.cs:202-228), поэтому смена
    /// env-переменных между плечами действует и на прогретом движке.
    /// </summary>
    // C2 (CP6): параллелизм задаётся конструктором распознавателя и влияет на ResolveThreads(),
    // поэтому он часть ключа — иначе прогон с --parallel 3 получил бы движок, прогретый под 1.
    private readonly Dictionary<(string Model, string Language, bool Beam, int Parallelism), WhisperSpeechRecognizer> _engines = new();
    private bool _disposed;

    public TranscribeRunner(string modelPath, string language, bool gpu = false)
        : this(modelPath, language, gpu, null)
    {
    }

    public TranscribeRunner(string modelPath, string language, bool gpu, FallbackWatchSink? fallbackWatch)
    {
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _language = string.IsNullOrWhiteSpace(language) ? CliOptions.DefaultLanguage : language;
        _gpu = gpu;
        _fallbackWatch = fallbackWatch;
    }

    /// <summary>
    /// Возвращает прогретый распознаватель для плеча. Вызовы RunAsync сериализованы снаружи
    /// (CLI — один прогон за раз, MCP — SemaphoreSlim(1,1) в McpEngine), поэтому словарь без блокировки.
    /// </summary>
    private async Task<WhisperSpeechRecognizer> GetOrCreateRecognizerAsync(bool beam, int effectiveParallelism)
    {
        var key = (_modelPath, _language, beam, effectiveParallelism);
        if (_engines.TryGetValue(key, out var cached)) return cached;

        var settings = new StreamingSettings { UseBeamSearch = beam };
        // C2 (CP6): стенд отдаёт движку тот же параллелизм, с каким сам гоняет прогоны —
        // иначе режим divided на стенде и в приложении считал бы разный бюджет.
        var recognizer = new WhisperSpeechRecognizer(
            _modelPath, _language, gpuFailed: !_gpu, settings: settings,
            initialPrompt: null, effectiveParallelism: effectiveParallelism);
        try
        {
            await recognizer.InitializeAsync();
        }
        catch (Exception ex)
        {
            recognizer.Dispose();
            throw new EngineUnavailableException(
                $"Движок не инициализировался на модели {_modelPath}: {ex.Message}", ex);
        }

        _engines[key] = recognizer;
        Log.Information("Движок прогрет: модель {Model}, язык {Language}, beam {Beam}",
            Path.GetFileName(_modelPath), _language, beam);
        return recognizer;
    }

    /// <summary>Освобождает все прогретые движки. Для CLI — в конце процесса, для MCP — при выходе.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var engine in _engines.Values) engine.Dispose();
        _engines.Clear();
    }

    /// <summary>
    /// Фактически применяемый движком AudioContextSize (CP5, WhisperSpeechRecognizer.cs:292-306):
    /// вызывает ровно ту же чистую формулу (WhisperTuning.ComputeAudioContextSize) и повторяет
    /// ОБА guard'а движка, а не только floor:
    ///   1) language=auto → 0 (детект языка идёт на полном окне, спека §3.1);
    ///   2) вычисленный размер ≥ FullWindowContext (1500) → 0 (предохранитель от whisper.cpp -5).
    /// При совпадении входов (duration, floor, language) с тем, что ушло в движок на данном
    /// плече, результат идентичен применённому — второго источника истины здесь нет, это то же
    /// уравнение с теми же аргументами.
    /// </summary>
    public static int ExpectedAudioContextSize(double durationSeconds, int floor, string language = CliOptions.DefaultLanguage)
    {
        if (string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) return 0;

        var size = WhisperTuning.ComputeAudioContextSize(durationSeconds, floor);
        return size >= WhisperTuning.FullWindowContext ? 0 : size;
    }

    public async Task<RunReport> RunAsync(CliOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(_modelPath))
            throw new EngineUnavailableException($"Модель не найдена: {_modelPath}. Укажите --model <путь|id>.");

        var sources = LoadSources(options.Inputs, options.MaxDurationSeconds);

        var ctxArms = options.GridCtx ?? new[] { options.CtxFloor };
        var threadArms = options.GridThreads == null
            ? new List<int?> { options.Threads }
            : options.GridThreads.Select(t => (int?)t).ToList();
        var beamArms = options.GridBeam ?? new[] { options.Beam };

        var totalRuns = sources.Count * ctxArms.Count * threadArms.Count * beamArms.Count * options.Repeat;
        if (options.Command == "sweep" && totalRuns > options.MaxRuns)
            throw new MaxRunsExceededException(
                $"Запрошено {totalRuns} прогонов при --max-runs {options.MaxRuns}. Сузьте сетку или поднимите предохранитель.");

        Log.Information("Стенд: файлов {Files}, плеч {Arms}, повторов {Repeat}, всего прогонов {Total}",
            sources.Count, ctxArms.Count * threadArms.Count * beamArms.Count, options.Repeat, totalRuns);

        var report = new RunReport
        {
            Tag = options.Tag,
            StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        string? runtimeInfo = null;

        // C2/C1 (CP6): блок engine — чистая функция options (см. EngineInfo.Collect), поэтому его
        // можно снять в любой момент; ДО цикла — чтобы не путать с пер-прогонными значениями
        // (runs[].ctxFloor / runs[].threads / runs[].threadMode), которые у свипа разные на плечо.
        var engineInfo = EngineInfo.Collect(options, _modelPath, null);
        var threadModeEnum = WhisperTuning.ParseMode(options.ThreadMode);

        try
        {
            foreach (var beam in beamArms)
            {
                foreach (var ctx in ctxArms)
                {
                    foreach (var threads in threadArms)
                    {
                        ct.ThrowIfCancellationRequested();
                        ApplyTuningEnvironment(ctx, threads, options.ThreadMode);

                        // C2 (CP6): та же формула и те же входы, что видит движок на этом плече
                        // (ApplyTuningEnvironment только что выставила их в окружение) — arm и
                        // RunRecord.Threads показывают РАЗРЕШЁННОЕ число потоков, а не запрос.
                        var appliedThreads = WhisperTuning.ComputeThreads(
                            threadModeEnum, Environment.ProcessorCount, Math.Max(1, options.Parallel), threads);
                        var arm = BuildArmName(ctx, appliedThreads, options.ThreadMode, beam);
                        Log.Information("Плечо {Arm}", arm);

                        // Движок берётся из кэша: модель грузится один раз на (модель, язык, beam),
                        // а не на каждое плечо сетки. Диспозится в TranscribeRunner.Dispose().
                        var recognizer = await GetOrCreateRecognizerAsync(beam, Math.Max(1, options.Parallel));

                        runtimeInfo ??= EngineInfo.SafeRuntimeInfo();

                        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
                        var tasks = new List<Task<RunRecord>>();

                        for (var repeat = 0; repeat < options.Repeat; repeat++)
                        {
                            var repeatIndex = repeat;
                            foreach (var source in sources)
                            {
                                var runId = Interlocked.Increment(ref _nextRunId);
                                var captured = source;
                                tasks.Add(Task.Run(async () =>
                                {
                                    await gate.WaitAsync(ct);
                                    try
                                    {
                                        return await RunOneAsync(recognizer, captured, arm, ctx, appliedThreads,
                                            options.ThreadMode, beam, options.Parallel, repeatIndex, runId, ct);
                                    }
                                    finally
                                    {
                                        gate.Release();
                                    }
                                }, ct));
                            }
                        }

                        var records = await Task.WhenAll(tasks);
                        report.Runs.AddRange(records
                            .OrderBy(r => r.RepeatIndex)
                            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase));
                    }
                }
            }
        }
        finally
        {
            // C2 (CP6): рычаги плеча не должны переживать прогон. Переменные СНИМАЮТСЯ, а не
            // «восстанавливаются в исходное»: ApplyTuningEnvironment и так затирает унаследованное
            // значение на первом же плече (оно задаётся опциями --ctx-floor/--threads/--thread-mode),
            // поэтому снятие возвращает ровно дефолты кода — то, что и обязан показывать engine_info
            // между вызовами. Читать окружение здесь нельзя: закон §4 скелета — GetEnvironmentVariable
            // живёт только в LWhisper.SpeechEngine.
            ClearTuningEnvironment();
        }

        // Строка рантайма доступна только после загрузки нативной библиотеки — дописываем в снимок.
        engineInfo.RuntimeInfo = runtimeInfo ?? "";
        engineInfo.Gpu = _gpu;
        report.Engine = engineInfo;
        report.FinishedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        report.Summary = Statistics.Summarize(report.Runs);

        if (!string.IsNullOrWhiteSpace(options.BaselineReport))
            report.Summary.BaselineDiff = LoadAndCompareBaseline(report, options.BaselineReport!);

        var fallbacks = report.Runs.Count(r => r.UsedFallback);
        if (fallbacks > 0)
            Log.Warning("Аварийный fallback движка сработал в {Count} прогонах — замер невалиден (спека §5, правило 4)", fallbacks);

        return report;
    }

    private async Task<RunRecord> RunOneAsync(WhisperSpeechRecognizer recognizer, AudioSource source, string arm,
        int ctxFloor, int appliedThreads, string threadMode, bool beam, int parallel, int repeatIndex, int runId, CancellationToken ct)
    {
        var record = new RunRecord
        {
            File = source.Path,
            FileSha256 = source.Sha256,
            DurationMs = source.Audio.Duration.TotalMilliseconds,
            Arm = arm,
            CtxFloor = ctxFloor,
            AudioContextSize = ExpectedAudioContextSize(source.Audio.Duration.TotalSeconds, ctxFloor, _language),
            Threads = appliedThreads,
            ThreadMode = threadMode,
            Beam = beam,
            Parallel = parallel,
            RepeatIndex = repeatIndex
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using (LogContext.PushProperty(FallbackWatchSink.RunIdProperty, runId))
            {
                var result = await recognizer.RecognizeStreamingAsync(source.Audio, ct);
                sw.Stop();
                record.Text = result.Text ?? "";
                if (!result.Success)
                    record.Error = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "распознавание не удалось" : result.ErrorMessage;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            record.Error = ex.Message;
        }

        record.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        record.Rtf = record.DurationMs > 0 ? record.ElapsedMs / record.DurationMs : 0;
        record.TextSha256 = Sha256Hex(Encoding.UTF8.GetBytes(record.Text));
        record.UsedFallback = _fallbackWatch?.WasFallback(runId) ?? false;

        Log.Information("{File} [{Arm}] повтор {Repeat}: {Elapsed:F0}мс, rtf={Rtf:F2}{Fallback}",
            Path.GetFileName(record.File), arm, repeatIndex, record.ElapsedMs, record.Rtf,
            record.UsedFallback ? " FALLBACK" : "");

        return record;
    }

    /// <summary>
    /// Рычаги движка живут в переменных окружения (§4 скелета). Ранер выставляет их перед каждым
    /// плечом. LWHISPER_WHISPER_THREADS выставляется ТОЛЬКО при явном --threads/--grid-threads:
    /// иначе жёсткий override победил бы режим --thread-mode divided и рычаг стал бы мёртвым.
    /// </summary>
    private static void ApplyTuningEnvironment(int ctxFloor, int? threads, string threadMode)
    {
        Environment.SetEnvironmentVariable("LWHISPER_AUDIO_CTX_FLOOR", ctxFloor.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("LWHISPER_THREAD_MODE", threadMode);
        Environment.SetEnvironmentVariable("LWHISPER_WHISPER_THREADS",
            threads.HasValue ? threads.Value.ToString(CultureInfo.InvariantCulture) : null);
    }

    private static void ClearTuningEnvironment()
    {
        Environment.SetEnvironmentVariable("LWHISPER_AUDIO_CTX_FLOOR", null);
        Environment.SetEnvironmentVariable("LWHISPER_THREAD_MODE", null);
        Environment.SetEnvironmentVariable("LWHISPER_WHISPER_THREADS", null);
    }

    private static string BuildArmName(int ctxFloor, int appliedThreads, string threadMode, bool beam)
        => $"ctx={ctxFloor.ToString(CultureInfo.InvariantCulture)}," +
           $"threads={appliedThreads.ToString(CultureInfo.InvariantCulture)}," +
           $"mode={threadMode}," +
           $"beam={(beam ? "true" : "false")}";

    /// <summary>
    /// Имя файла полной записи сессии, который CP1 кладёт рядом с сегментами. В корпус для свипа он
    /// попадать не должен: это до MaxSessionSeconds = 600 с, то есть ctx = 30016 (много больше
    /// FullWindowContext), гарантированный аварийный fallback и часы прогона на каждое плечо.
    /// </summary>
    public const string SessionWavName = "session.wav";

    /// <summary>Чистый предикат отбора: тестируется без файловой системы.</summary>
    public static bool IsCorpusCandidate(string path)
        => !string.Equals(Path.GetFileName(path), SessionWavName, StringComparison.OrdinalIgnoreCase);

    private static List<AudioSource> LoadSources(IReadOnlyList<string> inputs, int maxDurationSeconds)
    {
        var files = new List<string>();
        foreach (var raw in inputs)
        {
            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.wav", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
            else
                throw new CliParseException($"Не найден путь --input: {raw}");
        }

        var distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        distinct.Sort(StringComparer.OrdinalIgnoreCase);
        if (distinct.Count == 0)
            throw new CliParseException("По указанным --input не найдено ни одного .wav");

        var sources = new List<AudioSource>(distinct.Count);
        var skippedSession = 0;
        var skippedLong = 0;

        foreach (var file in distinct)
        {
            if (!IsCorpusCandidate(file))
            {
                skippedSession++;
                Log.Warning("Пропущен {File}: полная запись сессии в корпус свипа не берётся", file);
                continue;
            }

            var bytes = File.ReadAllBytes(file);
            AudioData audio;
            try
            {
                audio = WavFile.Parse(bytes, file);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
            {
                // Битый/неподдерживаемый WAV не должен ронять процесс необработанным исключением
                // (README: код возврата 1 — ошибка входа) и не должен обезличиваться в MCP до
                // "An error occurred invoking 'transcribe'." — CliParseException уже маппится
                // в обоих местах (Program.cs, LWhisperMcpTools.cs).
                throw new CliParseException($"Не удалось прочитать {file}: {ex.Message}");
            }

            if (audio.Duration.TotalSeconds > maxDurationSeconds)
            {
                skippedLong++;
                Log.Warning("Пропущен {File}: длительность {Seconds:F1} с > --max-duration {Limit} с",
                    file, audio.Duration.TotalSeconds, maxDurationSeconds);
                continue;
            }

            sources.Add(new AudioSource
            {
                Path = file,
                Sha256 = Sha256Hex(bytes),
                Audio = audio
            });
        }

        if (skippedSession > 0 || skippedLong > 0)
            Log.Information("Отбор корпуса: взято {Taken}, отброшено session.wav {Session}, длинных {Long}",
                sources.Count, skippedSession, skippedLong);

        if (sources.Count == 0)
            throw new CliParseException(
                $"После отбора не осталось ни одного файла: все .wav либо session.wav, либо длиннее --max-duration {maxDurationSeconds} с.");

        return sources;
    }

    private static BaselineDiff LoadAndCompareBaseline(RunReport current, string baselinePath)
    {
        var full = Path.GetFullPath(baselinePath);
        if (!File.Exists(full))
            throw new CliParseException($"Файл базлайна не найден: {baselinePath}");

        var baseline = ReportWriter.FromJson(File.ReadAllText(full))
                       ?? throw new CliParseException($"Не удалось разобрать базлайн: {baselinePath}");

        return Statistics.CompareToBaseline(current, baseline, full);
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
