namespace LWhisper.Core.Interfaces
{
    /// <summary>
    /// Интерфейс для определения голосовой активности (VAD - Voice Activity Detection)
    /// </summary>
    public interface IVoiceActivityDetector
    {
        /// <summary>
        /// Проверить, содержит ли аудио-фрагмент речь
        /// </summary>
        /// <param name="audioData">Аудио данные (PCM 16-bit)</param>
        /// <param name="sampleRate">Частота дискретизации (Hz)</param>
        /// <returns>True если обнаружена речь, False если тишина</returns>
        bool IsSpeech(byte[] audioData, int sampleRate);

        /// <summary>
        /// Получить уровень уверенности в наличии речи
        /// </summary>
        /// <param name="audioData">Аудио данные (PCM 16-bit)</param>
        /// <param name="sampleRate">Частота дискретизации (Hz)</param>
        /// <returns>Вероятность речи от 0.0 (тишина) до 1.0 (речь)</returns>
        float GetSpeechProbability(byte[] audioData, int sampleRate);
    }
}

