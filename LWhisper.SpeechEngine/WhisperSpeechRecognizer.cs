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
        private WhisperProcessor? _processor;
        private readonly string _modelPath;
        private readonly string _language;
        private readonly bool _gpuFailed;
        private bool _isInitialized;
        private bool _isUsingGpu;

        public bool IsReady => _isInitialized;

        /// <summary>
        /// True если при текущей инициализации GPU (Vulkan) не удалось загрузиться.
        /// App.xaml.cs читает это свойство после InitializeAsync() чтобы сохранить флаг в настройках.
        /// </summary>
        public bool GpuInitFailed { get; private set; }

        public WhisperSpeechRecognizer(string modelPath, string language = "auto", bool gpuFailed = false)
        {
            _modelPath = modelPath;
            _language = language;
            _gpuFailed = gpuFailed;
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

                    var factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
                    {
                        UseGpu = !_gpuFailed,
                        // PERF-05: Flash Attention оптимизирован для GPU; на CPU может замедлять — отключаем
                        UseFlashAttention = false
                    });

                    var builder = factory.CreateBuilder()
                        .WithLanguage(_language)
                        // PERF-02: WithPrompt("") убран — пустая строка это не "нет промпта", а промпт с пустой строкой
                        .WithNoContext()
                        .WithSingleSegment()
                        // PERF-01: использовать все логические ядра CPU вместо дефолтных 4
                        .WithThreads(Environment.ProcessorCount);
                    builder.WithGreedySamplingStrategy();
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
        }
    }
}





