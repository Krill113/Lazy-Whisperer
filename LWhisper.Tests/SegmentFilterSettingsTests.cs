using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// Регрессия на класс дефекта «мёртвая настройка»: параметр объявлен в StreamingSettings,
    /// лежит в settings.json, но до кода фильтрации не доходит и на поведение не влияет.
    ///
    /// Именно так до 2026-08-11 вёл себя CompressionRatioThreshold: конструктор
    /// SegmentRecognitionManager настройки вообще не принимал, а порог был локальной константой.
    /// Тест проверяет ОБА порога через наблюдаемое поведение, а не через чтение поля.
    /// </summary>
    public class SegmentFilterSettingsTests
    {
        /// <summary>Распознаватель-заглушка: отдаёт заранее заданный текст, не трогая Whisper.</summary>
        private sealed class StubRecognizer : ISpeechRecognizer
        {
            private readonly string _text;
            public StubRecognizer(string text) => _text = text;
            public bool IsReady => true;
            public Task<RecognitionResult> RecognizeAsync(AudioData audioData, CancellationToken cancellationToken = default)
                => Task.FromResult(new RecognitionResult { Success = true, Text = _text });
        }

        /// <summary>
        /// PCM 16 кГц моно 16 бит с заданной ПИКОВОЙ амплитудой (доля от full-scale).
        /// Меандр, а не тишина: важен именно пик, по нему работает гейт.
        /// </summary>
        private static AudioData MakeAudio(double peakAmplitude, int durationMs = 2000)
        {
            const int sampleRate = 16000;
            int samples = sampleRate * durationMs / 1000;
            var bytes = new byte[samples * 2];
            short peak = (short)(peakAmplitude * short.MaxValue);

            for (int i = 0; i < samples; i++)
            {
                short value = (i % 100 < 50) ? peak : (short)(-peak);
                bytes[i * 2] = (byte)(value & 0xFF);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return new AudioData
            {
                RawData = bytes,
                SampleRate = sampleRate,
                Channels = 1,
                BitsPerSample = 16,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            };
        }

        [Fact]
        public async Task SilenceThreshold_FromSettings_DropsSegmentAboveDefaultButBelowCustom()
        {
            // Пик 0.10 — заведомо выше дефолтного 0.03, но ниже заданного 0.30.
            var settings = new StreamingSettings { SilenceMaxAmplitudeThreshold = 0.30 };
            using var manager = new SegmentRecognitionManager(new StubRecognizer("текст есть"), 1, settings);

            await manager.ProcessSegmentAsync(MakeAudio(0.10));
            await manager.WaitAllAsync();

            Assert.Equal(string.Empty, manager.GetFullText());
            Assert.Equal(1, manager.SkippedSilentCount);
        }

        [Fact]
        public async Task SilenceThreshold_FromSettings_KeepsQuietSegmentWhenLowered()
        {
            // Пик 0.02 — НИЖЕ дефолтного 0.03: на старом коде сегмент был бы отброшен всегда.
            var settings = new StreamingSettings { SilenceMaxAmplitudeThreshold = 0.005 };
            using var manager = new SegmentRecognitionManager(new StubRecognizer("тихая речь"), 1, settings);

            await manager.ProcessSegmentAsync(MakeAudio(0.02));
            await manager.WaitAllAsync();

            Assert.Contains("тихая речь", manager.GetFullText());
            Assert.Equal(0, manager.SkippedSilentCount);
        }

        [Fact]
        public async Task CompressionRatioThreshold_FromSettings_IsActuallyApplied()
        {
            // Текст обязан ПЕРЕЖИТЬ dedup'ы и дойти до compression-ratio: тот стоит в цепочке
            // ПОСЛЕ них и ловит только то, что регулярки схлопнуть не смогли. Дословный повтор
            // одной фразы здесь не годится — его снимет CollapsePhraseRepeats, и фильтр не увидит
            // ничего подозрительного (именно на этом первая версия теста и упала).
            // Поэтому: все токены разные (дедупу зацепиться не за что), но энтропия низкая
            // из-за общего префикса, и gzip сжимает такой текст в разы.
            var lowEntropy = string.Join(' ', Enumerable.Range(1, 400).Select(i => $"слово{i:d4}"));

            var strict = new StreamingSettings { CompressionRatioThreshold = 1.5 };
            using (var manager = new SegmentRecognitionManager(new StubRecognizer(lowEntropy), 1, strict))
            {
                await manager.ProcessSegmentAsync(MakeAudio(0.20));
                await manager.WaitAllAsync();
                Assert.Equal(string.Empty, manager.GetFullText());
            }

            // Тот же текст при заведомо недостижимом пороге обязан пройти —
            // если бы настройка не доходила до кода, оба случая вели бы себя одинаково.
            var permissive = new StreamingSettings { CompressionRatioThreshold = 1000.0 };
            using (var manager = new SegmentRecognitionManager(new StubRecognizer(lowEntropy), 1, permissive))
            {
                await manager.ProcessSegmentAsync(MakeAudio(0.20));
                await manager.WaitAllAsync();
                Assert.NotEqual(string.Empty, manager.GetFullText());
            }
        }
    }
}
