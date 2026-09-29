using System;

namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Держит «опасное окно» вокруг вычислений на GPU, пока видеокарта не доказала работоспособность.
    ///
    /// Драйвер может пройти загрузку модели и уронить процесс на первом вычислении — такой краш не
    /// ловится try/catch. Окно открывается на первом входе и закрывается, когда закончились ВСЕ
    /// параллельные вычисления (до трёх streaming-сегментов разом). Если среди них был успех — GPU
    /// считается доказанным: сначала вызывается onProven, потом окно закрывается, и трекер гаснет
    /// навсегда. Если успеха не было (ошибка, отмена) — окно просто закрывается и откроется снова
    /// на следующем вычислении.
    ///
    /// Движок не знает, что стоит за окном (маркер-файл живёт в UI-слое) — только вызывает делегаты.
    /// </summary>
    public sealed class GpuComputeTracker
    {
        private readonly Func<IDisposable> _openWindow;
        private readonly Action _onProven;
        private readonly object _lock = new object();
        private IDisposable? _window;
        private int _active;
        private bool _succeededInWindow;
        private bool _proven;

        public GpuComputeTracker(Func<IDisposable> openWindow, Action onProven)
        {
            _openWindow = openWindow ?? throw new ArgumentNullException(nameof(openWindow));
            _onProven = onProven ?? throw new ArgumentNullException(nameof(onProven));
        }

        /// <summary>True когда хотя бы одно вычисление на GPU завершилось успешно.</summary>
        public bool IsProven
        {
            get { lock (_lock) return _proven; }
        }

        /// <summary>
        /// Войти в вычисление. null — GPU уже доказан, окно больше не нужно.
        /// </summary>
        public Scope? Enter()
        {
            lock (_lock)
            {
                if (_proven) return null;
                if (_active == 0)
                {
                    _window = _openWindow();
                    _succeededInWindow = false;
                }
                _active++;
                return new Scope(this);
            }
        }

        private void Exit(bool success)
        {
            lock (_lock)
            {
                _active--;
                if (success) _succeededInWindow = true;
                if (_active > 0) return;

                if (_succeededInWindow)
                {
                    // Порядок важен: счётчик сбоев обнуляется ДО снятия маркера — умри процесс между
                    // этими шагами, следующий старт увидит маркер, но счётчик уже чист
                    _onProven();
                    _proven = true;
                }
                _window?.Dispose();
                _window = null;
            }
        }

        /// <summary>
        /// Одно вычисление. MarkSuccess — после успешного распознавания; Dispose — всегда (using).
        /// </summary>
        public sealed class Scope : IDisposable
        {
            private readonly GpuComputeTracker _owner;
            private bool _success;
            private bool _disposed;

            internal Scope(GpuComputeTracker owner) => _owner = owner;

            public void MarkSuccess() => _success = true;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.Exit(_success);
            }
        }
    }
}
