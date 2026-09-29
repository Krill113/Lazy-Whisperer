using System.IO;
using Serilog;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Защита от драйвера видеокарты, который роняет процесс. Нативный краш внутри Vulkan не ловится
    /// try/catch — процесс просто исчезает. Поэтому на время «опасных окон» (инициализация модели на
    /// GPU, вычисления до первого успеха — см. GpuComputeTracker) лежит маркер-файл. Маркер, найденный
    /// при старте, значит: процесс умер внутри окна. Между окнами маркера нет, поэтому снятый в
    /// диспетчере задач процесс, пропавшее питание или выход из системы сбоем не считаются.
    ///
    /// GPU выключается после <see cref="DisableThreshold"/> сбоев подряд; счётчик обнуляет только
    /// доказанная работа GPU (или пользователь). Файлы — в LocalAppData: сбой драйвера — свойство
    /// машины, а не профиля; на VDI с перемещаемым профилем он выключил бы GPU на всех машинах.
    /// Все методы не бросают: ошибка файловой системы лишает защиты, но не ломает старт.
    /// </summary>
    public class GpuSafetyGuard
    {
        /// <summary>Сколько сбоев подряд выключают GPU.</summary>
        public const int DisableThreshold = 2;

        private readonly string _markerPath;
        private readonly string _countPath;

        public GpuSafetyGuard(string folder)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            _markerPath = Path.Combine(folder, "gpu-risk.inprogress");
            _countPath = Path.Combine(folder, "gpu-crash-count");
        }

        /// <summary>Сбоев подряд. Нечитаемый файл считается нулём.</summary>
        public int CrashCount
        {
            get
            {
                try
                {
                    if (!File.Exists(_countPath)) return 0;
                    return int.TryParse(File.ReadAllText(_countPath).Trim(), out var count) && count > 0 ? count : 0;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Не удалось прочитать счётчик сбоев GPU");
                    return 0;
                }
            }
        }

        /// <summary>GPU выключен защитой.</summary>
        public bool IsDisabled => CrashCount >= DisableThreshold;

        /// <summary>
        /// Разобрать итог прошлого запуска — ОДИН раз на процесс, после проверки единственного экземпляра.
        /// Возвращает true, если именно этот вызов довёл счётчик до порога (показать уведомление).
        /// </summary>
        public bool EvaluateStartup()
        {
            try
            {
                if (!File.Exists(_markerPath)) return false;

                // Маркер не удалился — не считаем: иначе счётчик рос бы на каждом старте от одного сбоя
                File.Delete(_markerPath);
                var wasDisabled = IsDisabled;
                var count = CrashCount + 1;
                WriteCount(count);
                Log.Warning("Прошлый запуск оборвался во время работы на GPU — сбой {Count} из {Threshold}",
                    count, DisableThreshold);
                return !wasDisabled && count >= DisableThreshold;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось разобрать маркер работы GPU");
                return false;
            }
        }

        /// <summary>
        /// Открыть опасное окно. Dispose снимает маркер, только если он всё ещё свой (не перезаписан
        /// окном более новой инициализации).
        /// </summary>
        public IDisposable OpenWindow()
        {
            var token = Guid.NewGuid().ToString("N");
            try
            {
                var dir = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_markerPath, token);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось записать маркер работы GPU — защита от сбоя драйвера неактивна");
            }
            return new Window(this, token);
        }

        /// <summary>Обнулить счётчик: GPU доказал работу или пользователь включил его вручную.</summary>
        public void ResetCount()
        {
            try
            {
                if (File.Exists(_countPath)) File.Delete(_countPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось обнулить счётчик сбоев GPU");
            }
        }

        private void WriteCount(int count)
        {
            var dir = Path.GetDirectoryName(_countPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_countPath, count.ToString());
        }

        private void CloseWindow(string token)
        {
            try
            {
                if (File.Exists(_markerPath) && File.ReadAllText(_markerPath).Trim() == token)
                    File.Delete(_markerPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось снять маркер работы GPU");
            }
        }

        private sealed class Window : IDisposable
        {
            private readonly GpuSafetyGuard _guard;
            private readonly string _token;
            private bool _disposed;

            public Window(GpuSafetyGuard guard, string token)
            {
                _guard = guard;
                _token = token;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _guard.CloseWindow(_token);
            }
        }
    }

    /// <summary>
    /// Что сохранить из чекбокса «Использовать видеокарту». Сохраняется только ИЗМЕНЕНИЕ относительно
    /// показанного: иначе выключение защитой (чекбокс показан снятым) при сохранении любой другой
    /// настройки превратилось бы в выбор пользователя и уехало в перемещаемый settings.json.
    /// </summary>
    public static class GpuSettingsResolver
    {
        /// <param name="liveUseGpu">UseGpu из текущих (живых) настроек, не из снимка окна.</param>
        /// <param name="shownChecked">Как чекбокс был показан при открытии окна.</param>
        /// <param name="nowChecked">Как он стоит при сохранении.</param>
        public static (bool UseGpu, bool ClearGuard) Resolve(bool liveUseGpu, bool shownChecked, bool nowChecked)
        {
            if (shownChecked == nowChecked) return (liveUseGpu, false);
            return nowChecked ? (true, true) : (false, false);
        }
    }
}
