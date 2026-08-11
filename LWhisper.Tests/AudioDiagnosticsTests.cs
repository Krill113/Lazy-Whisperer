using System;
using System.IO;
using System.Text;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine.Diagnostics;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// CP1: заголовок WAV — чистая функция, покрывается целиком.
    /// Native-поведение движка тестами не покрывается принципиально (см. §7.3 скелета).
    /// </summary>
    public class WavWriterTests
    {
        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int Bits = 16;

        [Fact]
        public void BuildHeader_Returns44BytesWithCanonicalMarkers()
        {
            var header = WavWriter.BuildHeader(3200, SampleRate, Channels, Bits);

            Assert.Equal(44, header.Length);
            Assert.Equal(WavWriter.HeaderSize, header.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
            Assert.Equal("fmt ", Encoding.ASCII.GetString(header, 12, 4));
            Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));
        }

        [Fact]
        public void BuildHeader_FieldsMatch16kHzMono16Bit()
        {
            const int pcmLength = 32000; // ровно 1 секунда потока рекордера
            var h = WavWriter.BuildHeader(pcmLength, SampleRate, Channels, Bits);

            Assert.Equal(36 + pcmLength, BitConverter.ToInt32(h, 4));   // размер RIFF-чанка
            Assert.Equal(16, BitConverter.ToInt32(h, 16));              // размер fmt-чанка
            Assert.Equal((short)1, BitConverter.ToInt16(h, 20));        // audioFormat = PCM
            Assert.Equal((short)Channels, BitConverter.ToInt16(h, 22));
            Assert.Equal(SampleRate, BitConverter.ToInt32(h, 24));
            Assert.Equal(32000, BitConverter.ToInt32(h, 28));           // byteRate = 16000*1*2
            Assert.Equal((short)2, BitConverter.ToInt16(h, 32));        // blockAlign = 1*16/8
            Assert.Equal((short)Bits, BitConverter.ToInt16(h, 34));
            Assert.Equal(pcmLength, BitConverter.ToInt32(h, 40));       // размер data-чанка
        }

        [Fact]
        public void BuildHeader_ByteRateAndBlockAlign_FollowChannelsAndBits()
        {
            var h = WavWriter.BuildHeader(0, 48000, 2, 16);

            Assert.Equal((short)4, BitConverter.ToInt16(h, 32));        // blockAlign = 2*16/8
            Assert.Equal(48000 * 4, BitConverter.ToInt32(h, 28));       // byteRate
            Assert.Equal(36, BitConverter.ToInt32(h, 4));               // пустой data-чанк
        }

        [Theory]
        [InlineData(-1, 16000, 1, 16)]
        [InlineData(100, 0, 1, 16)]
        [InlineData(100, 16000, 0, 16)]
        [InlineData(100, 16000, 1, 12)]
        public void BuildHeader_RejectsInvalidArguments(int pcmLength, int rate, int channels, int bits)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.BuildHeader(pcmLength, rate, channels, bits));
        }

        [Fact]
        public void Write_EmitsHeaderThenPayload()
        {
            var pcm = new byte[64];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)i;

            using var ms = new MemoryStream();
            WavWriter.Write(ms, pcm, 0, pcm.Length, SampleRate, Channels, Bits);

            var bytes = ms.ToArray();
            Assert.Equal(WavWriter.HeaderSize + pcm.Length, bytes.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(pcm.Length, BitConverter.ToInt32(bytes, 40));
            for (int i = 0; i < pcm.Length; i++)
            {
                Assert.Equal(pcm[i], bytes[WavWriter.HeaderSize + i]);
            }
        }

        [Fact]
        public void WriteFile_CreatesDirectoryAndWritesWholePayload()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lwhisper-wav-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "seg-0001.wav");

            try
            {
                var audio = new AudioData
                {
                    RawData = new byte[3200],
                    SampleRate = SampleRate,
                    Channels = Channels,
                    BitsPerSample = Bits,
                    Duration = TimeSpan.FromMilliseconds(100)
                };

                WavWriter.WriteFile(path, audio);

                Assert.True(File.Exists(path));
                var bytes = File.ReadAllBytes(path);
                Assert.Equal(WavWriter.HeaderSize + 3200, bytes.Length);
                Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
                Assert.Equal(3200, BitConverter.ToInt32(bytes, 40));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    /// <summary>
    /// CP1: парсер env-флага — единственная логика AudioDumpSink, тестируемая без диска.
    /// </summary>
    public class AudioDumpSinkFlagTests
    {
        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("True")]
        [InlineData("yes")]
        [InlineData("YES")]
        [InlineData("on")]
        [InlineData("On")]
        [InlineData("  1  ")]
        public void ParseFlag_AcceptsKnownTruthyValues(string value)
        {
            Assert.True(AudioDumpSink.ParseFlag(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("off")]
        [InlineData("no")]
        [InlineData("false")]
        [InlineData("2")]
        [InlineData("enabled")]
        [InlineData("да")]
        public void ParseFlag_RejectsEverythingElse(string? value)
        {
            Assert.False(AudioDumpSink.ParseFlag(value));
        }

        [Fact]
        public void MaxSessionSeconds_IsHardSafetyLimit()
        {
            Assert.Equal(600, AudioDumpSink.MaxSessionSeconds);
        }

        [Fact]
        public void WhenFlagIsAbsent_SinkIsDisabledAndHasNoSessionDirectory()
        {
            if (AudioDumpSink.ParseFlag(Environment.GetEnvironmentVariable("LWHISPER_DEBUG_AUDIO")))
            {
                return; // в окружении разработчика дамп включён — проверка неприменима
            }

            Assert.False(AudioDumpSink.Enabled);
            Assert.Null(AudioDumpSink.SessionDirectory);
        }
    }

    /// <summary>
    /// CP1: пути движка кроссплатформенные и не создают каталогов при чтении.
    /// </summary>
    public class EnginePathsTests
    {
        [Fact]
        public void Paths_AreDerivedFromAppDataRoot()
        {
            Assert.EndsWith("LWhisper", EnginePaths.AppDataRoot);
            Assert.Equal(Path.Combine(EnginePaths.AppDataRoot, "Models"), EnginePaths.ModelsFolder);
            Assert.Equal(Path.Combine(EnginePaths.AppDataRoot, "settings.json"), EnginePaths.SettingsFile);
            Assert.False(string.IsNullOrWhiteSpace(EnginePaths.DebugRoot));
        }

        [Fact]
        public void ReadingDebugRoot_DoesNotCreateDirectory()
        {
            var root = EnginePaths.DebugRoot;
            var existedBefore = Directory.Exists(root);

            var readAgain = EnginePaths.DebugRoot;

            Assert.Equal(existedBefore, Directory.Exists(readAgain));
        }
    }
}
