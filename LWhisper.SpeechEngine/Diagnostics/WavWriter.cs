using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using LWhisper.Core.Models;

namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Минимальный writer канонического RIFF/WAVE (PCM integer).
    /// Чистый и полностью юнит-тестируемый: ни платформы, ни состояния движка.
    /// </summary>
    public static class WavWriter
    {
        /// <summary>Размер канонического заголовка RIFF/WAVE PCM.</summary>
        public const int HeaderSize = 44;

        /// <summary>
        /// Собрать 44-байтовый заголовок. Все поля — little-endian.
        /// </summary>
        public static byte[] BuildHeader(int pcmByteLength, int sampleRate, int channels, int bitsPerSample)
        {
            if (pcmByteLength < 0) throw new ArgumentOutOfRangeException(nameof(pcmByteLength));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (bitsPerSample <= 0 || bitsPerSample % 8 != 0) throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

            var blockAlign = channels * bitsPerSample / 8;
            var byteRate = sampleRate * blockAlign;

            var header = new byte[HeaderSize];
            var span = header.AsSpan();

            Encoding.ASCII.GetBytes("RIFF", span.Slice(0, 4));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + pcmByteLength);
            Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));

            Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);              // размер fmt-чанка
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);               // audioFormat = PCM
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)channels);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)blockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), (short)bitsPerSample);

            Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), pcmByteLength);

            return header;
        }

        /// <summary>
        /// Записать заголовок и PCM-данные в поток.
        /// </summary>
        public static void Write(Stream destination, byte[] pcm, int offset, int count,
                                 int sampleRate, int channels, int bitsPerSample)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > pcm.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var header = BuildHeader(count, sampleRate, channels, bitsPerSample);
            destination.Write(header, 0, header.Length);
            destination.Write(pcm, offset, count);
        }

        /// <summary>
        /// Записать AudioData в WAV-файл. Недостающие каталоги создаются.
        /// </summary>
        public static void WriteFile(string path, AudioData audio)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (audio == null) throw new ArgumentNullException(nameof(audio));

            var pcm = audio.RawData ?? Array.Empty<byte>();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            Write(fs, pcm, 0, pcm.Length, audio.SampleRate, audio.Channels, audio.BitsPerSample);
        }
    }
}
