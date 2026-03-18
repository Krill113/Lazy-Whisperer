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
        private bool _isInitialized;
        private bool _isUsingGpu;

        public bool IsReady => _isInitialized;

        public WhisperSpeechRecognizer(string modelPath)
        {
            _modelPath = modelPath;
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
                    var factory = WhisperFactory.FromPath(_modelPath, new WhisperFactoryOptions
                    {
                        UseGpu = true,
                        UseFlashAttention = true
                    });

                    var builder = factory.CreateBuilder()
                        .WithLanguage("auto")
                        .WithPrompt("")
                        .WithNoContext()
                        .WithSingleSegment();
                    builder.WithGreedySamplingStrategy();
                    _processor = builder.Build();

                    var runtimeInfo = WhisperFactory.GetRuntimeInfo();
                    _isUsingGpu = runtimeInfo != null &&
                        (runtimeInfo.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                         runtimeInfo.Contains("cuda", StringComparison.OrdinalIgnoreCase));
                    Log.Information("Whisper runtime: {RuntimeInfo}, GPU: {IsGpu}", runtimeInfo ?? "unknown", _isUsingGpu);

                    _isInitialized = true;
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
                    DetectedLanguage = "auto",
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





