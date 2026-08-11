using LWhisper.SpeechEngine;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// CP5 (C1): чистая формула окна энкодера WhisperTuning.ComputeAudioContextSize.
    /// Закон (скелет §5.2):
    ///   floor &lt;= 0            -> 0 (kill-switch, WithAudioContextSize не вызывается)
    ///   raw     = ceil(dur / 30 * 1500)
    ///   aligned = align64(raw)
    ///   результат = max(floor, aligned)
    /// Ни clamp'а по 1500, ни ветки «>= 1500 -> не вызывать» в функции нет — это забота вызывающего.
    /// </summary>
    public class WhisperTuningTests
    {
        // Значения подобраны так, что двоичное представление double не влияет на результат:
        // сдвиг raw на ±1 не меняет ни выбранный 64-кратный бакет, ни исход max(floor, aligned).
        [Theory]
        // durationSeconds, floor, expected
        [InlineData(0.9, 448, 448)]    // raw=45   -> aligned=64   -> побеждает floor
        [InlineData(3.0, 256, 256)]    // raw=150  -> aligned=192  -> побеждает floor
        [InlineData(5.12, 448, 448)]   // граница «коротких» сегментов из спеки §5
        [InlineData(7.68, 448, 448)]   // aligned=384/448 -> floor всё ещё не ниже
        [InlineData(9.0, 448, 512)]    // raw=450  -> aligned=512  -> побеждает формула
        [InlineData(10.0, 448, 512)]
        [InlineData(12.0, 448, 640)]
        [InlineData(15.0, 448, 768)]   // MaxSegmentDurationMs=15000 — практический максимум
        [InlineData(1.0, 768, 768)]    // консервативный floor из спеки
        [InlineData(1.0, 0, 0)]        // kill-switch
        [InlineData(30.0, 0, 0)]       // kill-switch не зависит от длительности
        [InlineData(30.0, 448, 1536)]  // >= полного окна: возвращается как есть, без clamp и без исключения
        [InlineData(60.0, 448, 3008)]  // подтверждение отсутствия clamp'а
        public void ComputeAudioContextSize_Table(double durationSeconds, int floor, int expected)
        {
            Assert.Equal(expected, WhisperTuning.ComputeAudioContextSize(durationSeconds, floor));
        }

        [Fact]
        public void ComputeAudioContextSize_NegativeFloor_DisablesLikeKillSwitch()
        {
            Assert.Equal(0, WhisperTuning.ComputeAudioContextSize(5.0, -1));
        }

        [Fact]
        public void ComputeAudioContextSize_ZeroDuration_ReturnsFloor()
        {
            Assert.Equal(448, WhisperTuning.ComputeAudioContextSize(0.0, 448));
        }

        [Fact]
        public void ComputeAudioContextSize_AboveFullWindow_IsNotClamped()
        {
            // Ветка недостижима в проде (спека §3.1), но функция обязана вести себя предсказуемо:
            // не бросать, не зажимать в 1500.
            var ctx = WhisperTuning.ComputeAudioContextSize(30.0, WhisperTuning.DefaultAudioContextFloor);
            Assert.True(ctx > WhisperTuning.FullWindowContext, $"ожидалось > 1500, получено {ctx}");
        }

        [Fact]
        public void ComputeAudioContextSize_ResultIsAlwaysAlignedTo64()
        {
            for (var d = 0.1; d <= 20.0; d += 0.1)
            {
                var ctx = WhisperTuning.ComputeAudioContextSize(d, WhisperTuning.DefaultAudioContextFloor);
                Assert.True(ctx % WhisperTuning.AudioContextAlignment == 0,
                    $"ctx={ctx} при duration={d:F1}с не кратен {WhisperTuning.AudioContextAlignment}");
            }
        }

        [Fact]
        public void ComputeAudioContextSize_IsMonotonicInDuration()
        {
            var previous = 0;
            for (var d = 0.0; d <= 20.0; d += 0.05)
            {
                var ctx = WhisperTuning.ComputeAudioContextSize(d, WhisperTuning.DefaultAudioContextFloor);
                Assert.True(ctx >= previous, $"ctx упал с {previous} до {ctx} при duration={d:F2}с");
                previous = ctx;
            }
        }

        [Fact]
        public void ComputeAudioContextSize_NeverBelowFloor_OnRealisticSegments()
        {
            // 0.1..15.0 с — весь диапазон, который может отдать StreamingAudioRecorder
            // (MinSegmentDurationMs=2000, MaxSegmentDurationMs=15000 плюс финальные огрызки).
            for (var d = 0.1; d <= 15.0; d += 0.1)
            {
                Assert.True(WhisperTuning.ComputeAudioContextSize(d, 448) >= 448,
                    $"floor нарушен при duration={d:F1}с");
            }
        }

        [Fact]
        public void Constants_MatchSkeletonLaw()
        {
            Assert.Equal(448, WhisperTuning.DefaultAudioContextFloor);
            Assert.Equal(64, WhisperTuning.AudioContextAlignment);
            Assert.Equal(1500, WhisperTuning.FullWindowContext);
            Assert.Equal(30.0, WhisperTuning.FullWindowSeconds, 10);
        }

        [Fact]
        public void AudioContextFloor_WithoutEnvOverride_EqualsDefault()
        {
            // Переменная читается при каждом обращении (это нужно свипам DevTools), но менять её
            // внутри теста нельзя — xUnit гоняет классы параллельно, будет гонка.
            // Поэтому проверяем только консистентность с фактическим окружением.
            var raw = Environment.GetEnvironmentVariable("LWHISPER_AUDIO_CTX_FLOOR");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                Assert.True(WhisperTuning.AudioContextFloor >= 0);
                return;
            }

            Assert.Equal(WhisperTuning.DefaultAudioContextFloor, WhisperTuning.AudioContextFloor);
        }
    }
}
