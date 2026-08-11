using Serilog;

namespace LWhisper.SpeechEngine
{
    /// <summary>
    /// Режим расчёта бюджета потоков Whisper.
    /// Legacy — поведение прода (все логические ядра). Divided — деление бюджета на параллелизм.
    /// </summary>
    public enum ThreadBudgetMode
    {
        /// <summary>Все логические ядра — поведение до волны, дефолт.</summary>
        Legacy = 0,

        /// <summary>(P - ReservedCores) / effectiveParallelism, минимум 1.</summary>
        Divided = 1
    }

    /// <summary>
    /// Тюнинговые константы и чистые формулы Whisper-движка.
    /// <para>
    /// D7: всё, что специфично для whisper.cpp, живёт здесь, а не в <c>LWhisper.Core</c> —
    /// в <c>StreamingSettings</c>/<c>AppSettings</c> в этой волне не добавляется ничего
    /// (прецедент: поле <c>UseBeamSearch</c> переживёт движок и останется мусором в settings.json навсегда).
    /// </para>
    /// <para>
    /// Отладочные переопределения — только через переменные окружения. Отсутствие переменной =
    /// поведение прода. Любое применённое переопределение логируется один раз со словом <c>override</c>,
    /// иначе случайная переменная в окружении молча меняет прод.
    /// </para>
    /// </summary>
    public static class WhisperTuning
    {
        // ==================== C1: окно энкодера (audio_ctx) ====================

        /// <summary>
        /// Нижняя граница окна энкодера для streaming-пути.
        /// <para>
        /// 448 — кандидат из спеки §3.1: пол ~1975 мс против 3659 мс у 768, при нулевом хвосте
        /// на 60 прогонах базлайна. Финальное значение выбирается A/B на записанном корпусе.
        /// </para>
        /// <para>
        /// Значение <c>0</c> — kill-switch: <c>WithAudioContextSize</c> не вызывается вовсе,
        /// окно остаётся полным (1500), пол вырастает до ~7 с на сегмент. Это НЕ «поведение до CP5»:
        /// до CP5 нижняя граница была 256, и именно 256 возвращает прежнее поведение.
        /// </para>
        /// </summary>
        public const int DefaultAudioContextFloor = 448;

        /// <summary>Кратность окна энкодера: размер округляется вверх до кратного 64.</summary>
        public const int AudioContextAlignment = 64;

        /// <summary>Штатное окно whisper: 30 секунд аудио = 1500 позиций энкодера.</summary>
        public const int FullWindowContext = 1500;

        /// <summary>Длительность штатного окна whisper в секундах.</summary>
        public const double FullWindowSeconds = 30.0;

        private const string AudioContextFloorEnv = "LWHISPER_AUDIO_CTX_FLOOR";

        /// <summary>
        /// Последнее залогированное значение каждой переменной — чтобы строка <c>override</c>
        /// печаталась один раз на каждое НОВОЕ значение, а не на каждый сегмент.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _loggedEnv
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Действующий floor окна энкодера: <see cref="DefaultAudioContextFloor"/> либо переопределение
        /// из переменной окружения <c>LWHISPER_AUDIO_CTX_FLOOR</c>.
        /// <para>
        /// ЧИТАЕТСЯ ПРИ КАЖДОМ ОБРАЩЕНИИ, а не один раз на тип. Это требование стенда:
        /// <c>LWhisper.DevTools</c> меняет переменную между плечами свипа <b>внутри одного процесса</b>
        /// (<c>TranscribeRunner.ApplyTuningEnvironment</c>), и кэш на статическом инициализаторе
        /// выдал бы всем плечам один и тот же ctx — свип стал бы бессмысленным.
        /// Стоимость — один <c>Environment.GetEnvironmentVariable</c> на сегмент, на фоне
        /// секундного инференса неизмерима. Лог <c>override</c> дедуплицирован по значению.
        /// </para>
        /// </summary>
        public static int AudioContextFloor => ResolveAudioContextFloor();

        /// <summary>
        /// Размер окна энкодера для сегмента длительностью <paramref name="durationSeconds"/>.
        /// Чистая функция: окружение не читает, ничего не логирует — вся тестируемость здесь.
        /// </summary>
        /// <param name="durationSeconds">Длительность аудио сегмента в секундах.</param>
        /// <param name="floor">Нижняя граница окна; <c>0</c> и меньше — kill-switch.</param>
        /// <returns>
        /// <c>0</c>, если <c>WithAudioContextSize</c> вызывать НЕ нужно (kill-switch).
        /// Иначе <c>max(floor, align64(ceil(dur / 30 * 1500)))</c>.
        /// <para>
        /// Ни clamp'а по <see cref="FullWindowContext"/>, ни ветки «&gt;= 1500 — не вызывать» здесь нет
        /// намеренно (спека §3.1: при MaxSegmentDurationMs=15000 raw-ctx физически не превышает 768,
        /// ветка недостижима, и оставить её — значит приписать ей результат smoke).
        /// Предупреждение о превышении печатает вызывающий.
        /// </para>
        /// </returns>
        public static int ComputeAudioContextSize(double durationSeconds, int floor)
        {
            if (floor <= 0) return 0;

            var raw = (int)Math.Ceiling(durationSeconds / FullWindowSeconds * FullWindowContext);
            var aligned = ((raw + AudioContextAlignment - 1) / AudioContextAlignment) * AudioContextAlignment;
            return Math.Max(floor, aligned);
        }

        // ==================== C2: бюджет потоков ====================

        /// <summary>
        /// Сколько логических ядер резервируется под ОС и аудио-поток.
        /// Источник: whisper-type (MIT), github.com/kirillrub108/whisper-type,
        /// файл whispertype/config.py, константа RESERVED_CPUS = 2,
        /// коммит c1e58b903f577005a96f51350ac34a02e71702be:
        ///     def resolve_cpu_threads(configured): return max(1, (os.cpu_count() or 4) - RESERVED_CPUS)
        /// ВАЖНО: у источника деления на параллелизм НЕТ — их конвейер последовательный
        /// (один инференс за раз). Деление на effectiveParallelism — наше решение (спека §3.2, плечо 2),
        /// поэтому режим Divided по умолчанию выключен и включается только на время A/B.
        /// </summary>
        public const int ReservedCores = 2;

        /// <summary>
        /// Режим бюджета потоков из LWHISPER_THREAD_MODE (legacy|divided).
        /// Переменная отсутствует или не распознана → Legacy = поведение прода.
        /// <para>
        /// ЧИТАЕТСЯ ПРИ КАЖДОМ ОБРАЩЕНИИ — как и <see cref="AudioContextFloor"/> (CP5) и по той же
        /// причине: <c>LWhisper.DevTools</c> меняет переменные между плечами свипа внутри одного
        /// процесса, и кэш на статическом инициализаторе убил бы <c>--grid-threads</c>/<c>--thread-mode</c>.
        /// Строка <c>override</c> дедуплицирована по значению (<c>ShouldLogEnv</c>).
        /// </para>
        /// </summary>
        public static ThreadBudgetMode Mode => ReadThreadMode();

        /// <summary>
        /// Жёсткое переопределение числа потоков из LWHISPER_WHISPER_THREADS (целое > 0).
        /// null = переопределения нет. Значения ≤ 0 игнорируются с Warning.
        /// Читается при каждом обращении — см. примечание к <see cref="Mode"/>.
        /// </summary>
        public static int? ThreadsOverride => ReadThreadsOverride();

        /// <summary>
        /// Чистая функция расчёта числа потоков — окружение здесь не читается, вся тестируемость тут.
        /// Legacy → processorCount (дефолт волны, поведение прода не меняется).
        /// Divided → clamp((P - ReservedCores) / max(1, parallelism), 1, P).
        /// explicitOverride > 0 → clamp(override, 1, P) и режим игнорируется.
        /// </summary>
        public static int ComputeThreads(ThreadBudgetMode mode, int processorCount,
                                         int effectiveParallelism, int? explicitOverride)
        {
            var cores = Math.Max(1, processorCount);

            if (explicitOverride is int forced && forced > 0)
                return Math.Min(forced, cores);

            var parallelism = Math.Max(1, effectiveParallelism);

            return mode switch
            {
                ThreadBudgetMode.Divided => Math.Clamp((cores - ReservedCores) / parallelism, 1, cores),
                _ => cores
            };
        }

        private const string ThreadModeEnv = "LWHISPER_THREAD_MODE";
        private const string ThreadsOverrideEnv = "LWHISPER_WHISPER_THREADS";

        private static ThreadBudgetMode ReadThreadMode()
        {
            var raw = Environment.GetEnvironmentVariable(ThreadModeEnv);
            if (string.IsNullOrWhiteSpace(raw))
            {
                ShouldLogEnv(ThreadModeEnv, string.Empty);   // сброс дедупа (см. CP5)
                return ThreadBudgetMode.Legacy;
            }

            var normalized = raw.Trim().ToLowerInvariant();
            var shouldLog = ShouldLogEnv(ThreadModeEnv, normalized);

            switch (normalized)
            {
                case "legacy":
                    if (shouldLog) Log.Information("WhisperTuning: {Env} override = legacy", ThreadModeEnv);
                    return ThreadBudgetMode.Legacy;
                case "divided":
                    if (shouldLog) Log.Information("WhisperTuning: {Env} override = divided", ThreadModeEnv);
                    return ThreadBudgetMode.Divided;
                default:
                    if (shouldLog)
                        Log.Warning("WhisperTuning: {Env}={Raw} не распознан — используется legacy", ThreadModeEnv, raw);
                    return ThreadBudgetMode.Legacy;
            }
        }

        private static int? ReadThreadsOverride()
        {
            if (!TryReadIntEnv(ThreadsOverrideEnv, out var value)) return null;

            var shouldLog = ShouldLogEnv(ThreadsOverrideEnv,
                value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (value <= 0)
            {
                if (shouldLog)
                    Log.Warning("WhisperTuning: {Env}={Value} игнорируется (требуется > 0)", ThreadsOverrideEnv, value);
                return null;
            }

            if (shouldLog) Log.Information("WhisperTuning: {Env} override = {Value}", ThreadsOverrideEnv, value);
            return value;
        }

        /// <summary>
        /// Прочитать целочисленную переменную окружения.
        /// Невалидное значение трактуется как отсутствие переменной плюс одна строка <c>Log.Warning</c>
        /// (на каждое новое значение — не на каждый вызов: ридеры дёргаются на каждый сегмент).
        /// </summary>
        internal static bool TryReadIntEnv(string name, out int value)
        {
            value = 0;

            string? raw;
            try
            {
                raw = Environment.GetEnvironmentVariable(name);
            }
            catch (Exception ex)
            {
                if (ShouldLogEnv(name, "<error>"))
                    Log.Warning(ex, "Не удалось прочитать переменную окружения {Name}", name);
                return false;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                ShouldLogEnv(name, string.Empty);   // сброс дедупа: следующее значение снова залогируется
                return false;
            }

            if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                if (ShouldLogEnv(name, raw))
                    Log.Warning("Переменная окружения {Name}=\"{Raw}\" не является целым числом — игнорируется",
                        name, raw);
                value = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// true, если для переменной <paramref name="name"/> значение <paramref name="value"/>
        /// ещё не логировалось (или сменилось). Дедуп нужен потому, что ридеры окружения
        /// вызываются на каждый сегмент, а строка <c>override</c> по §4 скелета должна быть одна.
        /// </summary>
        private static bool ShouldLogEnv(string name, string value)
        {
            var changed = false;
            _loggedEnv.AddOrUpdate(
                name,
                _ => { changed = !string.IsNullOrEmpty(value); return value; },
                (_, previous) => { changed = !string.Equals(previous, value, StringComparison.Ordinal); return value; });
            return changed;
        }

        private static int ResolveAudioContextFloor()
        {
            if (!TryReadIntEnv(AudioContextFloorEnv, out var floor))
            {
                return DefaultAudioContextFloor;
            }

            if (floor < 0)
            {
                Log.Warning("Переменная окружения {Name}={Floor} отрицательна — игнорируется, floor={Default}",
                    AudioContextFloorEnv, floor, DefaultAudioContextFloor);
                return DefaultAudioContextFloor;
            }

            if (ShouldLogEnv(AudioContextFloorEnv, floor.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                Log.Information("Whisper tuning override: {Name}={Floor} (дефолт {Default}){Note}",
                    AudioContextFloorEnv, floor, DefaultAudioContextFloor,
                    floor == 0 ? " — kill-switch: WithAudioContextSize не вызывается" : string.Empty);
            }

            return floor;
        }
    }
}
