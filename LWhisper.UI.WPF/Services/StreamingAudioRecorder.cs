using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using NAudio.Wave;
using Serilog;
using System.IO;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Запись аудио с потоковой сегментацией по паузам через VAD
    /// </summary>
    public class StreamingAudioRecorder : IAudioRecorder, IDisposable
    {
        private WaveInEvent? _waveIn;
        private MemoryStream? _currentSegmentBuffer;
        private readonly int _sampleRate = 16000;
        private readonly int _channels = 1;
        private readonly int _bitsPerSample = 16;
        private volatile bool _isRecording;
        private volatile bool _isStopping; // Флаг для предотвращения дублированной остановки
        private readonly object _segmentLock = new object(); // Блокировка для EmitSegment
        private int _deviceNumber = 0;

        // VAD компонент
        private readonly IVoiceActivityDetector _vad;
        private readonly StreamingSettings _settings;

        // Отслеживание пауз
        private DateTime _lastSpeechTime = DateTime.Now;
        private int _consecutiveSilenceMs = 0;
        private int _totalSilenceSinceLastSpeechMs = 0; // Для автостопа
        private int _segmentCounter = 0;

        // События для потокового режима
        public event Action<AudioData>? SegmentReady;
        public event Action<AudioData>? FinalSegmentReady;
        public event Action? RecordingAutoStopped;

        public bool IsRecording => _isRecording;

        /// <summary>
        /// Создать потоковый рекордер с VAD
        /// </summary>
        /// <param name="vad">Детектор голосовой активности</param>
        /// <param name="settings">Настройки потоковой обработки</param>
        public StreamingAudioRecorder(IVoiceActivityDetector vad, StreamingSettings settings)
        {
            _vad = vad ?? throw new ArgumentNullException(nameof(vad));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            
            Log.Information("StreamingAudioRecorder инициализирован с настройками: " +
                "PauseThreshold={Pause}ms, MinSegment={Min}ms, MaxSegment={Max}ms, AutoStop={AutoStop}",
                settings.PauseThresholdMs, settings.MinSegmentDurationMs, 
                settings.MaxSegmentDurationMs, settings.AutoStopOnLongPause);
        }

        public void StartRecording()
        {
            if (_isRecording) return;

            _isRecording = true;
            _isStopping = false;
            _currentSegmentBuffer = new MemoryStream();
            _lastSpeechTime = DateTime.Now;
            _consecutiveSilenceMs = 0;
            _totalSilenceSinceLastSpeechMs = 0;
            _segmentCounter = 0;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(_sampleRate, _bitsPerSample, _channels),
                DeviceNumber = _deviceNumber,
                // КРИТИЧНО: 30мс для WebRTC VAD (не 100мс!)
                BufferMilliseconds = 30
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            
            Log.Debug("Потоковая запись начата (буфер 30мс)");
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isRecording || _currentSegmentBuffer == null) return;

            // Добавить данные в текущий сегмент
            _currentSegmentBuffer.Write(e.Buffer, 0, e.BytesRecorded);

            // Проверить VAD на этом фрейме
            byte[] frameData = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, frameData, e.BytesRecorded);

            bool isSpeech = _vad.IsSpeech(frameData, _sampleRate);

            if (isSpeech)
            {
                // Речь обнаружена - сбросить счётчики тишины
                _lastSpeechTime = DateTime.Now;
                _consecutiveSilenceMs = 0;
                _totalSilenceSinceLastSpeechMs = 0; // Сбросить счетчик для автостопа
            }
            else
            {
                // Тишина - увеличить счётчики
                _consecutiveSilenceMs += 30; // BufferMilliseconds = 30
                _totalSilenceSinceLastSpeechMs += 30;

                var segmentDuration = GetSegmentDurationMs();

                // КРИТИЧНО: Игнорировать микропаузы (< 300мс) между словами
                if (_consecutiveSilenceMs < 300)
                {
                    // Это просто дыхание между словами - продолжаем накапливать
                    return;
                }

                // Проверить условия для завершения сегмента
                bool isSignificantPause = _consecutiveSilenceMs >= _settings.PauseThresholdMs;
                bool isSegmentTooLong = segmentDuration >= _settings.MaxSegmentDurationMs;
                
                // ВАЖНО: Для автостопа используем _totalSilenceSinceLastSpeechMs, а не _consecutiveSilenceMs
                bool isLongPauseForAutoStop = _settings.AutoStopOnLongPause && 
                                              _totalSilenceSinceLastSpeechMs >= _settings.AutoStopPauseDurationMs;

                // Проверить автостоп в первую очередь
                if (isLongPauseForAutoStop)
                {
                    // Проверить флаг - возможно уже остановка идет
                    if (_isStopping)
                    {
                        return; // Уже останавливаемся, не нужно повторно
                    }
                    
                    _isStopping = true; // Установить флаг
                    _isRecording = false; // КРИТИЧНО: Немедленно остановить обработку новых фреймов
                    
                    Log.Information("[AutoStop] Обнаружена длинная пауза {TotalPause}мс с момента последней речи, автоматическая остановка записи", 
                        _totalSilenceSinceLastSpeechMs);
                    
                    // КРИТИЧНО: Остановить WaveIn НЕМЕДЛЕННО в текущем потоке
                    if (_waveIn != null)
                    {
                        _waveIn.StopRecording();
                        _waveIn.DataAvailable -= OnDataAvailable;
                        Log.Debug("[AutoStop] Микрофон остановлен немедленно");
                    }
                    
                    // Уведомить UI о автостопе СРАЗУ (UI вызовет StopRecordingAsync)
                    Task.Run(() =>
                    {
                        try
                        {
                            RecordingAutoStopped?.Invoke();
                            Log.Debug("[AutoStop] UI уведомлен о автостопе");
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[AutoStop] Ошибка при уведомлении UI");
                        }
                    });
                    
                    return; // Выход, UI сам вызовет StopRecordingAsync для обработки финальных сегментов
                }
                else if (isSegmentTooLong)
                {
                    Log.Debug("[Segment] Принудительное завершение сегмента (макс. длительность {Max}мс достигнута)", 
                        _settings.MaxSegmentDurationMs);
                    EmitSegment();
                }
                else if (isSignificantPause && segmentDuration >= _settings.MinSegmentDurationMs)
                {
                    Log.Debug("[Segment] Обнаружена пауза {Pause}мс, завершение сегмента (общая тишина={TotalSilence}мс)", 
                        _consecutiveSilenceMs, _totalSilenceSinceLastSpeechMs);
                    EmitSegment();
                }
            }
        }

        private double GetSegmentDurationMs()
        {
            if (_currentSegmentBuffer == null) return 0;
            
            return (_currentSegmentBuffer.Length / (double)(_sampleRate * _channels * _bitsPerSample / 8)) * 1000.0;
        }

        private void EmitSegment()
        {
            lock (_segmentLock) // КРИТИЧНО: Блокировка для предотвращения гонки потоков
            {
                if (_currentSegmentBuffer == null || _currentSegmentBuffer.Length == 0)
                {
                    Log.Debug("[Segment] Пустой сегмент, игнорируется");
                    return;
                }

                var duration = GetSegmentDurationMs();
                
                // Проверка минимальной длительности
                if (duration < _settings.MinSegmentDurationMs)
                {
                    Log.Debug("[Segment] Сегмент слишком короткий ({Duration}мс < {Min}мс), игнорируется", 
                        duration, _settings.MinSegmentDurationMs);
                    _currentSegmentBuffer.SetLength(0);
                    _consecutiveSilenceMs = 0;
                    return;
                }

                var segmentId = System.Threading.Interlocked.Increment(ref _segmentCounter);
                var audioData = new AudioData
                {
                    RawData = _currentSegmentBuffer.ToArray(),
                    SampleRate = _sampleRate,
                    Channels = _channels,
                    BitsPerSample = _bitsPerSample,
                    Duration = TimeSpan.FromMilliseconds(duration)
                };

                Log.Information("[Segment #{Id}] Сегмент готов: длительность={Duration}мс, размер={Size} bytes",
                    segmentId, (int)duration, audioData.RawData.Length);

                // Отправить событие в фоновом потоке для неблокирующей работы
                Task.Run(() =>
                {
                    try
                    {
                        SegmentReady?.Invoke(audioData);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Segment #{Id}] Ошибка при обработке события SegmentReady", segmentId);
                    }
                });

                // Очистить буфер для следующего сегмента
                _currentSegmentBuffer = new MemoryStream();
                _consecutiveSilenceMs = 0;
            }
        }

        public async Task<AudioData> StopRecordingAsync()
        {
            if (_waveIn == null || _currentSegmentBuffer == null)
            {
                return new AudioData();
            }

            if (!_isRecording && !_isStopping) // Изменено: разрешить вызов если _isStopping=true
            {
                return new AudioData();
            }

            // Остановить WaveIn если еще не остановлен (может быть уже остановлен в AutoStop)
            if (_waveIn != null && _isRecording)
            {
                _waveIn.StopRecording();
                _waveIn.DataAvailable -= OnDataAvailable;
                Log.Debug("WaveIn остановлен");
            }
            
            _isRecording = false;

            await Task.Delay(100); // Дать время для завершения обработки

            Log.Debug("Потоковая запись остановлена");

            // Отправить последний сегмент, если есть данные
            if (_currentSegmentBuffer.Length > 0)
            {
                var duration = GetSegmentDurationMs();
                
                if (duration >= _settings.MinSegmentDurationMs)
                {
                    var segmentId = System.Threading.Interlocked.Increment(ref _segmentCounter);
                    var finalData = new AudioData
                    {
                        RawData = _currentSegmentBuffer.ToArray(),
                        SampleRate = _sampleRate,
                        Channels = _channels,
                        BitsPerSample = _bitsPerSample,
                        Duration = TimeSpan.FromMilliseconds(duration)
                    };

                    Log.Information("[Segment #{Id}] Финальный сегмент: длительность={Duration}мс", 
                        segmentId, (int)duration);

                    // Отправить событие финального сегмента
                    Task.Run(() =>
                    {
                        try
                        {
                            FinalSegmentReady?.Invoke(finalData);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Segment #{Id}] Ошибка при обработке FinalSegmentReady", segmentId);
                        }
                    });

                    _waveIn?.Dispose();
                    _currentSegmentBuffer?.Dispose();
                    _waveIn = null;
                    _currentSegmentBuffer = null;

                    return finalData;
                }
                else
                {
                    Log.Debug("Финальный сегмент слишком короткий ({Duration}мс), игнорируется", duration);
                }
            }

            _waveIn?.Dispose();
            _currentSegmentBuffer?.Dispose();
            _waveIn = null;
            _currentSegmentBuffer = null;

            return new AudioData();
        }

        public List<string> GetAvailableDevices()
        {
            var devices = new List<string>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                devices.Add(capabilities.ProductName);
            }
            return devices;
        }

        public void SetDevice(string deviceName)
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                if (capabilities.ProductName == deviceName)
                {
                    _deviceNumber = i;
                    Log.Information("Устройство записи установлено: {Device}", deviceName);
                    break;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                _currentSegmentBuffer?.Dispose();
                _currentSegmentBuffer = null;

                Log.Debug("StreamingAudioRecorder освобожден");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка при освобождении StreamingAudioRecorder");
            }
        }
    }
}

