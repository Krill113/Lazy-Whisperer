using LWhisper.Core.Interfaces;
using Serilog;
using WebRtcVadSharp;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Определение голосовой активности через WebRTC VAD
    /// </summary>
    public class WebRtcVoiceActivityDetector : IVoiceActivityDetector, IDisposable
    {
        private readonly WebRtcVad _vad;
        private readonly OperatingMode _operatingMode;
        private bool _disposed;

        /// <summary>
        /// Создать детектор голосовой активности
        /// </summary>
        /// <param name="aggressiveness">Агрессивность фильтрации (0-3). 
        /// 0 = наименее агрессивный (пропускает больше), 
        /// 3 = наиболее агрессивный (фильтрует сильнее).
        /// Рекомендуется: 2 для баланса</param>
        public WebRtcVoiceActivityDetector(int aggressiveness = 2)
        {
            if (aggressiveness < 0 || aggressiveness > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(aggressiveness), 
                    "Агрессивность должна быть в диапазоне 0-3");
            }

            try
            {
                _vad = new WebRtcVad();
                _operatingMode = (OperatingMode)aggressiveness;
                
                Log.Information("WebRTC VAD инициализирован с агрессивностью {Aggressiveness}", aggressiveness);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка инициализации WebRTC VAD");
                throw new InvalidOperationException("Не удалось инициализировать WebRTC VAD", ex);
            }
        }

        /// <summary>
        /// Проверить, содержит ли аудио-фрагмент речь
        /// </summary>
        public bool IsSpeech(byte[] audioData, int sampleRate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WebRtcVoiceActivityDetector));
            }

            if (audioData == null || audioData.Length == 0)
            {
                return false;
            }

            try
            {
                // Преобразовать sampleRate в enum
                WebRtcVadSharp.SampleRate vadSampleRate = sampleRate switch
                {
                    8000 => WebRtcVadSharp.SampleRate.Is8kHz,
                    16000 => WebRtcVadSharp.SampleRate.Is16kHz,
                    32000 => WebRtcVadSharp.SampleRate.Is32kHz,
                    48000 => WebRtcVadSharp.SampleRate.Is48kHz,
                    _ => throw new ArgumentException($"Неподдерживаемая частота дискретизации {sampleRate}. Поддерживаются: 8000, 16000, 32000, 48000 Hz")
                };
                
                // WebRTC VAD требует определённые размеры фреймов
                // Используем автоопределение длины фрейма
                var frameLength = CalculateFrameLength(audioData.Length, sampleRate);
                
                bool hasSpeech = _vad.HasSpeech(audioData, vadSampleRate, frameLength);
                return hasSpeech;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка VAD при проверке аудио (размер: {Size}, частота: {Rate})", 
                    audioData.Length, sampleRate);
                return false;
            }
        }
        
        /// <summary>
        /// Вычислить длину фрейма на основе размера буфера
        /// </summary>
        private WebRtcVadSharp.FrameLength CalculateFrameLength(int bufferSize, int sampleRate)
        {
            // Размер в samples = bufferSize / 2 (16-bit PCM)
            int samples = bufferSize / 2;
            
            // Длительность в мс = (samples / sampleRate) * 1000
            double durationMs = (samples / (double)sampleRate) * 1000.0;
            
            // Выбрать ближайшую поддерживаемую длину фрейма
            if (durationMs <= 10)
                return WebRtcVadSharp.FrameLength.Is10ms;
            else if (durationMs <= 20)
                return WebRtcVadSharp.FrameLength.Is20ms;
            else
                return WebRtcVadSharp.FrameLength.Is30ms;
        }

        /// <summary>
        /// Получить уровень уверенности в наличии речи
        /// </summary>
        public float GetSpeechProbability(byte[] audioData, int sampleRate)
        {
            // WebRTC VAD возвращает только bool, поэтому возвращаем 0.0 или 1.0
            return IsSpeech(audioData, sampleRate) ? 1.0f : 0.0f;
        }

        /// <summary>
        /// Освободить ресурсы
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _vad?.Dispose();
                Log.Debug("WebRTC VAD освобожден");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка при освобождении WebRTC VAD");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}

