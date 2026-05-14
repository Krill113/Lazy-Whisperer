using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
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

        // W1: типовые YouTube-следы из тренировочного корпуса Whisper (русский)
        private const string SUPPRESS_REGEX =
            @"(?i)(субтитры подготовил|субтитры сделал|подписывайтесь на канал|спасибо за просмотр|продолжение следует)";

        public bool IsReady => _isInitialized;

        /// <summary>
        /// True если при текущей инициализации GPU (Vulkan) не удалось загрузиться.
        /// App.xaml.cs читает это свойство после InitializeAsync() чтобы сохранить флаг в настройках.
        /// </summary>
        public bool GpuInitFailed { get; private set; }

        public WhisperSpeechRecognizer(string modelPath, string language = "auto", bool gpuFailed = false, StreamingSettings? settings = null)
        {
            _modelPath = modelPath;
            _language = language;
            _gpuFailed = gpuFailed;
            _settings = settings;
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

                    var builder = _factory.CreateBuilder()
                        .WithLanguage(_language)
                        // PERF-02: WithPrompt("") убран — пустая строка это не "нет промпта", а промпт с пустой строкой
                        .WithNoContext()
                        .WithSingleSegment()
                        // PERF-01: использовать все логические ядра CPU вместо дефолтных 4
                        .WithThreads(Environment.ProcessorCount)
                        // W1: antigallucination-параметры
                        .WithTemperature(0.0f)
                        .WithEntropyThreshold(2.4f)
                        .WithLogProbThreshold(-1.0f)
                        // Если «да/нет/ок» начинают теряться — снизить до 0.45f
                        .WithNoSpeechThreshold(0.6f)
                        .WithSuppressRegex(SUPPRESS_REGEX);

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
                        runtimeInfo ?? "unknown", _isUsingGpu, Environment.ProcessorCount);

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
        public async Task<RecognitionResult> RecognizeStreamingAsync(AudioData audioData, CancellationToken cancellationToken = default)
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
                // PERF-04 EXPERIMENTAL: динамический расчёт AudioContextSize
                // Дефолт = 1500 (рассчитан на 30 сек). Для коротких сегментов streaming mode это избыточно.
                // Формула: (duration_sec / 30.0) * 1500, округление вверх до кратного 64, минимум 256
                var durationSec = audioData.Duration.TotalSeconds;
                var rawContextSize = (int)Math.Ceiling((durationSec / 30.0) * 1500);
                var alignedContextSize = ((rawContextSize + 63) / 64) * 64; // округление вверх до кратного 64
                var audioContextSize = Math.Max(256, alignedContextSize);

                Log.Debug("Streaming recognition: duration={Duration:F1}s, audioContextSize={ContextSize} (raw={RawSize})",
                    durationSec, audioContextSize, rawContextSize);

                var floatData = ConvertBytesToFloat(audioData.RawData);

                // Создаём отдельный процессор с оптимизированным контекстным окном
                var streamingBuilder = _factory.CreateBuilder()
                    .WithLanguage(_language)
                    .WithNoContext()
                    .WithSingleSegment()
                    .WithThreads(Environment.ProcessorCount)
                    // W1: antigallucination
                    .WithTemperature(0.0f)
                    .WithEntropyThreshold(2.4f)
                    .WithLogProbThreshold(-1.0f)
                    // Если «да/нет/ок» начинают теряться — снизить до 0.45f
                    .WithNoSpeechThreshold(0.6f)
                    .WithSuppressRegex(SUPPRESS_REGEX)
                    // PERF-04 EXPERIMENTAL: уменьшенное контекстное окно для коротких сегментов
                    .WithAudioContextSize(audioContextSize);

                if (_settings?.UseBeamSearch == true)
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
            catch (Exception ex)
            {
                Log.Warning(ex, "Streaming recognition с AudioContextSize failed — fallback на стандартный RecognizeAsync");
                // Fallback: если экспериментальный метод сломался, использовать обычный
                return await RecognizeAsync(audioData, cancellationToken);
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





