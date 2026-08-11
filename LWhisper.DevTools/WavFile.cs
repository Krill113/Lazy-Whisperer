using System.Buffers.Binary;
using System.Text;
using LWhisper.Core.Models;

namespace LWhisper.DevTools;

/// <summary>
/// Чтение WAV в <see cref="AudioData"/>. Ожидается ровно тот формат, в котором работает
/// пайплайн приложения и пишет дамп CP1: PCM 16000 Гц, моно, 16 бит.
/// Любое отклонение — исключение с понятным текстом: молчаливый ресемплинг исказил бы замер.
/// </summary>
public static class WavFile
{
    public const int ExpectedSampleRate = 16000;
    public const int ExpectedChannels = 1;
    public const int ExpectedBitsPerSample = 16;

    public static AudioData Read(string path) => Parse(File.ReadAllBytes(path), path);

    public static AudioData Parse(byte[] bytes, string sourceName)
    {
        if (bytes == null || bytes.Length < 44)
            throw new InvalidDataException($"{sourceName}: файл короче минимального заголовка WAV (44 байта).");

        var span = bytes.AsSpan();
        if (Ascii(span.Slice(0, 4)) != "RIFF" || Ascii(span.Slice(8, 4)) != "WAVE")
            throw new InvalidDataException($"{sourceName}: это не RIFF/WAVE-файл.");

        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var dataOffset = -1;
        var dataLength = 0;

        // pos/body — long: чанк с size около int.MaxValue переполнил бы int-арифметику
        // (body + size + выравнивание) и увёл бы pos в отрицательные числа, а следующая
        // итерация упала бы на span.Slice(pos, 4) с ArgumentOutOfRangeException — необработанным
        // и никак не связанным с текстом "испорченный WAV" из шапки файла. В long эти суммы
        // не переполняются (size ограничен int.MaxValue), а условие цикла естественно
        // останавливается, когда pos выходит за пределы файла.
        long pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var posInt = (int)pos;
            var id = Ascii(span.Slice(posInt, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(posInt + 4, 4));
            if (size < 0)
                throw new InvalidDataException($"{sourceName}: некорректный размер чанка '{id}'.");

            long body = pos + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > bytes.Length)
                    throw new InvalidDataException($"{sourceName}: испорченный чанк fmt.");
                var bodyInt = (int)body;
                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(bodyInt, 2));
                if (audioFormat != 1 && audioFormat != unchecked((short)0xFFFE))
                    throw new InvalidDataException($"{sourceName}: поддерживается только PCM, получен формат {audioFormat}.");
                channels = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(bodyInt + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(bodyInt + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(bodyInt + 14, 2));
            }
            else if (id == "data")
            {
                dataOffset = (int)body;
                dataLength = (int)Math.Min(size, bytes.Length - body);
            }

            pos = body + size + (size % 2); // чанки выравнены по 2 байта
        }

        if (dataOffset < 0)
            throw new InvalidDataException($"{sourceName}: не найден чанк data.");

        if (sampleRate != ExpectedSampleRate || channels != ExpectedChannels || bits != ExpectedBitsPerSample)
            throw new InvalidDataException(
                $"{sourceName}: ожидается PCM {ExpectedSampleRate} Гц / {ExpectedChannels} канал / {ExpectedBitsPerSample} бит, " +
                $"получено {sampleRate} Гц / {channels} / {bits}. Переконвертируйте файл — стенд не ресемплит.");

        dataLength -= dataLength % 2;
        var pcm = new byte[dataLength];
        Buffer.BlockCopy(bytes, dataOffset, pcm, 0, dataLength);

        var bytesPerSecond = sampleRate * channels * (bits / 8);
        return new AudioData
        {
            RawData = pcm,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bits,
            Duration = TimeSpan.FromSeconds((double)dataLength / bytesPerSecond)
        };
    }

    private static string Ascii(ReadOnlySpan<byte> value) => Encoding.ASCII.GetString(value);
}
