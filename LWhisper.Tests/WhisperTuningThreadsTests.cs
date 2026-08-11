using LWhisper.SpeechEngine;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// CP6 / C2: табличные тесты чистой функции бюджета потоков.
    /// Окружение здесь не читается — ComputeThreads по контракту чистая.
    /// </summary>
    public class WhisperTuningThreadsTests
    {
        [Theory]
        // Legacy — дефолт волны: сколько бы ни было параллелизма, отдаём все ядра
        [InlineData(ThreadBudgetMode.Legacy, 8, 2, 8)]
        [InlineData(ThreadBudgetMode.Legacy, 8, 1, 8)]
        [InlineData(ThreadBudgetMode.Legacy, 4, 3, 4)]
        [InlineData(ThreadBudgetMode.Legacy, 1, 1, 1)]
        // Divided — (P - ReservedCores) / parallelism
        [InlineData(ThreadBudgetMode.Divided, 8, 2, 3)]
        [InlineData(ThreadBudgetMode.Divided, 8, 1, 6)]
        [InlineData(ThreadBudgetMode.Divided, 16, 3, 4)]
        // Divided — clamp снизу: результат никогда не 0 и не отрицательный
        [InlineData(ThreadBudgetMode.Divided, 2, 3, 1)]
        [InlineData(ThreadBudgetMode.Divided, 2, 1, 1)]
        [InlineData(ThreadBudgetMode.Divided, 1, 1, 1)]
        public void ComputeThreads_WithoutOverride_FollowsMode(
            ThreadBudgetMode mode, int processorCount, int parallelism, int expected)
        {
            Assert.Equal(expected, WhisperTuning.ComputeThreads(mode, processorCount, parallelism, null));
        }

        [Theory]
        // Явный override побеждает режим, но clamp'ится сверху числом ядер
        [InlineData(ThreadBudgetMode.Legacy, 8, 2, 4, 4)]
        [InlineData(ThreadBudgetMode.Divided, 8, 2, 4, 4)]
        [InlineData(ThreadBudgetMode.Legacy, 8, 1, 99, 8)]
        [InlineData(ThreadBudgetMode.Divided, 8, 2, 99, 8)]
        public void ComputeThreads_WithPositiveOverride_UsesOverrideClampedByCores(
            ThreadBudgetMode mode, int processorCount, int parallelism, int over, int expected)
        {
            Assert.Equal(expected, WhisperTuning.ComputeThreads(mode, processorCount, parallelism, over));
        }

        [Theory]
        // Невалидный override игнорируется — работает формула режима
        [InlineData(ThreadBudgetMode.Legacy, 8, 2, 0, 8)]
        [InlineData(ThreadBudgetMode.Legacy, 8, 2, -1, 8)]
        [InlineData(ThreadBudgetMode.Divided, 8, 2, 0, 3)]
        [InlineData(ThreadBudgetMode.Divided, 8, 2, -5, 3)]
        public void ComputeThreads_WithNonPositiveOverride_IgnoresIt(
            ThreadBudgetMode mode, int processorCount, int parallelism, int over, int expected)
        {
            Assert.Equal(expected, WhisperTuning.ComputeThreads(mode, processorCount, parallelism, over));
        }

        [Theory]
        // Нулевой/отрицательный parallelism не должен обнулять бюджет
        [InlineData(0)]
        [InlineData(-3)]
        public void ComputeThreads_NonPositiveParallelism_TreatedAsOne(int parallelism)
        {
            Assert.Equal(6, WhisperTuning.ComputeThreads(ThreadBudgetMode.Divided, 8, parallelism, null));
        }

        [Fact]
        public void ComputeThreads_LegacyIsProductionDefault_ReturnsProcessorCount()
        {
            // Главный тест волны: дефолтный режим не меняет поведение прода
            var expected = System.Environment.ProcessorCount;
            Assert.Equal(expected, WhisperTuning.ComputeThreads(ThreadBudgetMode.Legacy, expected, 1, null));
            Assert.Equal(expected, WhisperTuning.ComputeThreads(ThreadBudgetMode.Legacy, expected, 3, null));
        }

        [Fact]
        public void ReservedCores_MatchesSourceConstant()
        {
            // whisper-type, whispertype/config.py, RESERVED_CPUS = 2,
            // коммит c1e58b903f577005a96f51350ac34a02e71702be
            Assert.Equal(2, WhisperTuning.ReservedCores);
        }

        [Fact]
        public void Mode_WithoutEnvironmentVariable_IsLegacy()
        {
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LWHISPER_THREAD_MODE")))
                return; // в окружении выставлен override — проверка неприменима

            Assert.Equal(ThreadBudgetMode.Legacy, WhisperTuning.Mode);
        }

        [Fact]
        public void ThreadsOverride_WithoutEnvironmentVariable_IsNull()
        {
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LWHISPER_WHISPER_THREADS")))
                return; // в окружении выставлен override — проверка неприменима

            Assert.Null(WhisperTuning.ThreadsOverride);
        }
    }
}
