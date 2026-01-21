using System;

namespace LWhisper.Core.Models
{
    /// <summary>
    /// Представляет аудио данные для распознавания речи
    /// </summary>
    public class AudioData
    {
        /// <summary>
        /// Сырые аудио данные (PCM 16-bit mono 16kHz)
        /// </summary>
        public byte[] RawData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Частота дискретизации (Hz)
        /// </summary>
        public int SampleRate { get; set; } = 16000;

        /// <summary>
        /// Количество каналов
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// Битность
        /// </summary>
        public int BitsPerSample { get; set; } = 16;

        /// <summary>
        /// Длительность записи
        /// </summary>
        public TimeSpan Duration { get; set; }
    }
}

