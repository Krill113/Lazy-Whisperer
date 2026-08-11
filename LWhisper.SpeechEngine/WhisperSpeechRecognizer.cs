using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine.Diagnostics;
using Whisper.net;
using Whisper.net.Ggml;
using Serilog;

namespace LWhisper.SpeechEngine
{
    /// <summary>
    /// Распознавание речи через Whisper.net
    /// </summary>
    public class WhisperSpeechRecognizer : ISpeechRecognizer, IDisposable
    {
        private WhisperFactory? _factory;
        private WhisperProcessor? _processor;
        private readonly string _modelPath;
        private readonly string _language;
        private readonly bool _gpuFailed;
        private bool _isInitialized;
        private bool _isUsingGpu;
        private readonly StreamingSettings? _settings;
        private readonly string? _initialPrompt;

        // P2: аварийный fallback RecognizeStreamingAsync → RecognizeAsync работает на ЕДИНСТВЕННОМ
        // общем _processor. При параллелизме > 1 два сегмента способны войти в него одновременно —
        // это зона SEHException, и за 8 дней боевых логов путь не сработал ни разу (не оттестирован).
        // Поэтому вход в fallback сериализуется.
        // Dispose у гейта намеренно не вызывается: SemaphoreSlim без AvailableWaitHandle не держит
        // неуправляемых ресурсов, а Dispose на лету уронил бы ожидающий fallback в
        // ObjectDisposedException при переинициализации движка (App.ApplySettings).
        private readonly SemaphoreSlim _fallbackGate = new SemaphoreSlim(1, 1);

        public bool IsReady => _isInitialized;

        /// <summary>
        /// True если при текущей инициализации GPU (Vulkan) не удалось загрузиться.
        /// App.xaml.cs читает это свойство после InitializeAsync() чтобы сохранить флаг в настройках.
        /// </summary>
        public bool GpuInitFailed { get; private set; }

        public WhisperSpeechRecognizer(string modelPath, string language = "auto", bool gpuFailed = false, StreamingSettings? settings = null, string? initialPrompt = null)
        {
            _modelPath = modelPath;
            _language = language;
            _gpuFailed = gpuFailed;
            _settings = settings;
            _initialPrompt = string.IsNullOrWhiteSpace(initialPrompt) ? null : initialPrompt;
        }

        /// <summary>
        /// Инициализировать процессор Whisper с автоматическим выбором GPU/CPU
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await Task.Run(() =>
            {
                try
                {
                    if (_gpuFailed)
                    {
                        // PERF-03: GPU ранее не работала — пропускаем Vulkan/CUDA, используем только CPU
                        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
                            new List<Whisper.net.LibraryLoader.RuntimeLibrary> { Whisper.net.LibraryLoader.RuntimeLibrary.Cpu };
                        Log.Information("GPU ранее не инициализировалась — используется только CPU рантайм (RuntimeLibraryOrder=[Cpu])");
                    }

                    _factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
                    {
                        UseGpu = !_gpuFailed,
                        // PERF-05: Flash Attention оптимизирован для GPU; на CPU может замедлять — отключаем
                        UseFlashAttention = false
                    });

                    // P3: число потоков вычисляется ОДИН раз и переиспользуется в строке лога ниже.
                    // "Whisper runtime: … Threads: N" обязана печатать то, что реально ушло в WithThreads,
                    // иначе smoke CP6 (бюджет потоков) врёт. Значение здесь НЕ меняется — это CP6.
                    var builderThreads = Environment.ProcessorCount;

                    var builder = _factory.CreateBuilder()
                        .WithLanguage(_language)
                        // PERF-02: WithPrompt("") убран — пустая строка это не "нет промпта", а промпт с пустой строкой
                        .WithNoContext()
                        .WithSingleSegment()
                        // PERF-01: использовать все логические ядра CPU вместо дефолтных 4
                        .WithThreads(builderThreads)
                        // W1: antigallucination-параметры
                        .WithTemperature(0.0f)
                        .WithEntropyThreshold(2.4f)
                        .WithLogProbThreshold(-1.0f)
                        // Если «да/нет/ок» начинают теряться — снизить до 0.45f
                        .WithNoSpeechThreshold(0.6f);

                    // D5: vocabulary через WithPrompt — биас Whisper на доменную лексику
                    if (_initialPrompt != null)
                    {
                        builder = builder.WithPrompt(_initialPrompt);
                        Log.Information("Whisper prompt активен: {Length} символов", _initialPrompt.Length);
                    }

                    // W1: beam search toggle
                    if (_settings?.UseBeamSearch == true)
                    {
                        if (builder.WithBeamSearchSamplingStrategy() is BeamSearchSamplingStrategyBuilder beamBuilder)
                            beamBuilder.WithBeamSize(5);
                        Log.Information("Whisper sampling: BeamSearch(5)");
                    }
                    else
                    {
                        builder.WithGreedySamplingStrategy();
                        Log.Information("Whisper sampling: Greedy");
                    }
                    _processor = builder.Build();

                    var runtimeInfo = WhisperFactory.GetRuntimeInfo();
                    _isUsingGpu = runtimeInfo != null &&
                        (runtimeInfo.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                         runtimeInfo.Contains("cuda", StringComparison.OrdinalIgnoreCase));

                    // PERF-03: Если GPU была запрошена но не загрузилась — пометить для сохранения в настройках
                    if (!_gpuFailed && !_isUsingGpu)
                    {
                        GpuInitFailed = true;
                        Log.Warning("GPU (Vulkan/CUDA) запрошена но не активна — флаг GpuInitFailed=true. " +
                            "При следующем запуске будет использован только CPU рантайм");
                    }

                    Log.Information("Whisper runtime: {RuntimeInfo}, GPU: {IsGpu}, Threads: {Threads}",
                        runtimeInfo ?? "unknown", _isUsingGpu, builderThreads);

                    _isInitialized = true;
                    Log.Information("Whisper язык распознавания: {Language}", _language);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Не удалось инициализировать Whisper");
                    throw;
                }
            });
        }

        public async Task<RecognitionResult> RecognizeAsync(AudioData audioData, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized || _processor == null)
            {
                return new RecognitionResult
                {
                    Success = false,
                    ErrorMessage = "Whisper не инициализирован"
                };
            }

            try
            {
                var floatData = ConvertBytesToFloat(audioData.RawData);
                
                var segments = new List<string>();
                await foreach (var segment in _processor.ProcessAsync(floatData, cancellationToken))
                {
                    segments.Add(segment.Text);
                }

                var text = string.Join(" ", segments).Trim();

                return new RecognitionResult
                {
                    Success = true,
                    Text = text,
                    DetectedLanguage = _language,
                    Confidence = 1.0f
                };
            }
            catch (OperationCanceledException)
            {
                // P2: отмена (остановка записи, Dispose менеджера сегментов) — не отказ распознавания.
                // Пробрасываем наверх: SegmentRecognitionManager.cs:185 ловит OCE и пишет Debug
                // «Распознавание отменено». Без этой ветки отмена возвращала бы Success=false
                // и порождала бы Warning «Распознавание не дало результата», засоряя базлайн.
                // Traditional-путь безопасен: App.xaml.cs:753 зовёт RecognizeAsync без токена,
                // а вызов обёрнут общим catch (App.xaml.cs:783).
                throw;
            }
            catch (Exception ex)
            {
                return new RecognitionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Распознавание сегмента в streaming mode с динамическим AudioContextSize.
        /// EXPERIMENTAL (PERF-04): WithAudioContextSize помечен как экспериментальный в Whisper.net.
        /// Вычисляет оптимальный размер контекстного окна по длине аудио сегмента.
        /// В случае ошибки fallback на обычный RecognizeAsync.
        /// </summary>
        public Task<RecognitionResult> RecognizeStreamingAsync(AudioData audioData, CancellationToken cancellationToken = default)
            => RecognizeStreamingAsync(audioData, 0, cancellationToken);

        /// <summary>
        /// То же самое, но с id сегмента для дампа отладки (CP1).
        /// debugSegmentId = 0 — id неизвестен, AudioDumpSink подставит собственный счётчик.
        /// Пока переменная окружения LWHISPER_DEBUG_AUDIO не выставлена, метод сводится
        /// к прямому вызову ядра: ни одного объекта меты не создаётся.
        /// </summary>
        public async Task<RecognitionResult> RecognizeStreamingAsync(AudioData audioData, int debugSegmentId, CancellationToken cancellationToken = default)
        {
            if (!AudioDumpSink.Enabled)
            {
                return await RecognizeStreamingCoreAsync(audioData, null, cancellationToken);
            }

            var meta = new SegmentDumpMeta
            {
                Type = "segment",
                SegmentId = debugSegmentId,
                Kind = "streaming",
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                DurationMs = audioData.Duration.TotalMilliseconds,
                Beam = _settings?.UseBeamSearch == true,
                Language = _language,
                ModelFile = System.IO.Path.GetFileName(_modelPath)
            };

            var startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var result = await RecognizeStreamingCoreAsync(audioData, meta, cancellationToken);

                meta.ElapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
                meta.RawText = result.Text ?? string.Empty;
                if (!result.Success) meta.Error = result.ErrorMessage;

                AudioDumpSink.DumpSegment(audioData, meta);
                return result;
            }
            catch (OperationCanceledException)
            {
                // Отмена записи — не ошибка распознавания, дампа нет.
                throw;
            }
            catch (Exception ex)
            {
                meta.ElapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
                meta.Error = ex.Message;
                AudioDumpSink.DumpSegment(audioData, meta);
                throw;
            }
        }

        /// <summary>
        /// Ядро streaming-распознавания. meta != null только при включённом дампе:
        /// метод заполняет в ней фактически применённые параметры движка.
        /// </summary>
        private async Task<RecognitionResult> RecognizeStreamingCoreAsync(AudioData audioData, SegmentDumpMeta? meta, CancellationToken cancellationToken)
        {
            if (!_isInitialized || _factory == null)
            {
                return new RecognitionResult
                {
                    Success = false,
                    ErrorMessage = "Whisper не инициализирован"
                };
            }

            try
            {
                // C1 (CP5): окно энкодера. Формула и floor — WhisperTuning (D7: константы движка, не в Core).
                // Дефолтный floor 448; LWHISPER_AUDIO_CTX_FLOOR переопределяет его без пересборки,
                // значение 0 — kill-switch (WithAudioContextSize не вызывается вовсе).
                // Guard: при language=auto окно не сужаем — детект языка идёт на полном окне (спека §3.1).
                var durationSec = audioData.Duration.TotalSeconds;
                var audioContextFloor = WhisperTuning.AudioContextFloor;
                var isAutoLanguage = string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase);
                var audioContextSize = isAutoLanguage
                    ? 0
                    : WhisperTuning.ComputeAudioContextSize(durationSec, audioContextFloor);

                // Предохранитель полного окна. whisper.cpp отвергает audio_ctx больше n_audio_ctx (1500)
                // кодом -5, а -5 уходит в общий catch и запускает аварийный fallback — то самое,
                // что спека §5 (правило 4) объявляет невалидным замером. Значение >= 1500 при этом
                // не даёт выигрыша ни при каком раскладе, поэтому окно просто не сужается.
                // В приложении недостижимо (MaxSegmentDurationMs=15000 -> максимум 768),
                // но на стенде DevTools на вход может прийти файл длиннее 30 с.
                var overFullWindow = audioContextSize >= WhisperTuning.FullWindowContext;
                if (overFullWindow) audioContextSize = 0;

                var ctxReason = isAutoLanguage ? "auto-language"
                    : overFullWindow ? "over-window"
                    : audioContextSize == 0 ? "kill-switch" : "floor";

                // P3: значения фиксируются ДО построения builder'а и печатаются в том виде,
                // в каком уходят в native — иначе свипы CP2/CP3 нечем верифицировать.
                // Сами значения здесь НЕ меняются: потоки — CP6, ctx — CP5.
                var streamingThreads = Environment.ProcessorCount;
                var useBeamSearch = _settings?.UseBeamSearch == true;

                Log.Debug("Streaming recognition: duration={Duration:F1}s, audioContextSize={ContextSize}, " +
                    "ctxReason={CtxReason}, floor={Floor}, threads={Threads}, beam={Beam}",
                    durationSec, audioContextSize, ctxReason, audioContextFloor, streamingThreads, useBeamSearch);

                if (overFullWindow)
                {
                    // Штатно недостижимо при MaxSegmentDurationMs=15000 (максимум 768). Если строка
                    // появилась — на вход пришёл кусок длиннее 30 с (сегментация или файл со стенда).
                    // Окно НЕ сужается: см. предохранитель в шаге 1а.
                    Log.Warning("Расчётный AudioContextSize >= полного окна {Full} при duration={Duration:F1}s — " +
                        "сужение окна не применяется (ctxReason=over-window)",
                        WhisperTuning.FullWindowContext, durationSec);
                }

                // CP1: мета дампа берёт ровно те значения, что ушли в native — те же локальные
                // переменные, что и лог выше (их завёл CP0). Второго источника истины нет.
                if (meta != null)
                {
                    meta.AudioContextSize = audioContextSize;
                    meta.Threads = streamingThreads;
                    meta.Beam = useBeamSearch;
                }

                var floatData = ConvertBytesToFloat(audioData.RawData);

                // Создаём отдельный процессор с оптимизированным контекстным окном
                var streamingBuilder = _factory.CreateBuilder()
                    .WithLanguage(_language)
                    .WithNoContext()
                    .WithSingleSegment()
                    .WithThreads(streamingThreads);

                // C1 (CP5): ctx == 0 -> WithAudioContextSize не вызывается вовсе
                // (kill-switch LWHISPER_AUDIO_CTX_FLOOR=0, language=auto либо over-window).
                // Урок W1: снятие явного параметра — тоже изменение поведения, поэтому это явная ветка,
                // а не «просто не передаём».
                if (audioContextSize > 0)
                {
                    streamingBuilder = streamingBuilder.WithAudioContextSize(audioContextSize);
                }

                // D5: vocabulary через WithPrompt — биас Whisper на доменную лексику
                if (_initialPrompt != null)
                {
                    streamingBuilder = streamingBuilder.WithPrompt(_initialPrompt);
                }

                if (useBeamSearch)
                {
                    if (streamingBuilder.WithBeamSearchSamplingStrategy() is BeamSearchSamplingStrategyBuilder beamBuilder)
                        beamBuilder.WithBeamSize(5);
                    Log.Debug("Streaming sampling: BeamSearch(5)");
                }
                else
                {
                    streamingBuilder.WithGreedySamplingStrategy();
                    Log.Debug("Streaming sampling: Greedy");
                }

                using var streamingProcessor = streamingBuilder.Build();

                var segments = new List<string>();
                await foreach (var segment in streamingProcessor.ProcessAsync(floatData, cancellationToken))
                {
                    segments.Add(segment.Text);
                }

                var text = string.Join(" ", segments).Trim();

                return new RecognitionResult
                {
                    Success = true,
                    Text = text,
                    DetectedLanguage = _language,
                    Confidence = 1.0f
                };
            }
            catch (OperationCanceledException)
            {
                // P2: отмена — не отказ streaming-пути. Без этой ветки отмена печатала бы ложный
                // варнинг «Streaming recognition … failed» и уводила бы выполнение в fallback,
                // а по правилу спеки §5 (п.4) любой замер со строкой fallback выбрасывается целиком.
                throw;
            }
            catch (Exception ex)
            {
                if (meta != null) meta.UsedFallback = true;
                Log.Warning(ex, "Streaming recognition с AudioContextSize failed — fallback на стандартный RecognizeAsync");
                // P2: fallback идёт на общий _processor, который один на весь распознаватель.
                // Вход сериализуем — иначе при параллелизме > 1 два сегмента войдут в native одновременно.
                await _fallbackGate.WaitAsync(cancellationToken);
                try
                {
                    return await RecognizeAsync(audioData, cancellationToken);
                }
                finally
                {
                    _fallbackGate.Release();
                }
            }
        }

        private float[] ConvertBytesToFloat(byte[] bytes)
        {
            var floats = new float[bytes.Length / 2];
            for (int i = 0; i < floats.Length; i++)
            {
                short sample = BitConverter.ToInt16(bytes, i * 2);
                floats[i] = sample / 32768.0f;
            }
            return floats;
        }

        public void Dispose()
        {
            _processor?.Dispose();
            _factory?.Dispose();
        }
    }
}





