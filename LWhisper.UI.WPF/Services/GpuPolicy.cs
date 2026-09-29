using LWhisper.SpeechEngine.Diagnostics;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Решения App о видеокарте — чистыми функциями, чтобы каждое было закреплено тестом.
    /// </summary>
    public static class GpuPolicy
    {
        /// <summary>Что делать с защитой после инициализации распознавателя.</summary>
        public enum AfterInitAction
        {
            /// <summary>GPU не пробовали — защита не участвует.</summary>
            None,
            /// <summary>GPU пробовали, модель честно легла на процессор — рисковать нечем, старые сбои не в счёт.</summary>
            ResetCrashCount,
            /// <summary>Модель на GPU (или неизвестно где) — вычисления до первого успеха под опасным окном.</summary>
            AttachComputeTracker
        }

        /// <summary>
        /// GPU выключен выбором пользователя или защитой от сбоев драйвера.
        /// </summary>
        public static bool ResolveForceCpu(bool useGpu, bool guardDisabled) => !useGpu || guardDisabled;

        /// <summary>
        /// Сколько распознаваний идёт параллельно. На процессоре (подсказка прошлого запуска ИЛИ GPU
        /// выключен сейчас) клип до 2; без streaming — всегда 1.
        /// </summary>
        public static int EffectiveParallelism(bool streamingEnabled, int maxParallel, bool gpuFailedHint, bool forceCpu)
        {
            if (!streamingEnabled) return 1;
            return gpuFailedHint || forceCpu ? Math.Min(2, maxParallel) : maxParallel;
        }

        /// <summary>
        /// Успешная инициализация НЕ обнуляет счётчик сбоев: драйвер, падающий только на вычислении,
        /// иначе никогда не набрал бы порог. Обнуляет только честный уход модели на процессор.
        /// </summary>
        public static AfterInitAction AfterInit(bool forceCpu, GpuUsage usage)
        {
            if (forceCpu) return AfterInitAction.None;
            return usage == GpuUsage.Cpu ? AfterInitAction.ResetCrashCount : AfterInitAction.AttachComputeTracker;
        }

        /// <summary>
        /// Обновлять ли подсказку GpuFailed (она лежит в перемещаемом settings.json). Только когда GPU
        /// реально пробовали и результат доказан: выключение пользователем или защитой — не свойство
        /// железа и не должно уезжать на другие машины.
        /// </summary>
        public static bool ShouldUpdateGpuFailedHint(bool gpuAttempted, GpuUsage usage) =>
            gpuAttempted && usage != GpuUsage.Unknown;

        /// <summary>Как показать чекбокс «Использовать видеокарту».</summary>
        public static bool CheckboxShown(bool useGpu, bool guardDisabled) => useGpu && !guardDisabled;
    }
}
