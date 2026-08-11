using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine.Diagnostics;
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
        private int _silenceBufferBytes = 0;
        // Hysteresis: накапливаем подряд speech-кадры для устойчивого триггера
        private int _consecutiveSpeechFrames = 0;
        private bool _speechActive = false; // True после успешного hysteresis-триггера

        // Pre-roll: кольцевой буфер аудио до VAD-триггера, прицепляется к началу сегмента.
        // Лечит «потерю первого слова», когда VAD триггерится после начала речи.
        private readonly Queue<byte[]> _preRollBuffer = new Queue<byte[]>();
        private int _preRollBufferBytes = 0;

        // События для потокового режима
        public event Action<AudioData>? SegmentReady;
        public event Action<AudioData>? FinalSegmentReady;
        public event Action? RecordingAutoStopped;
        public event Action? RecorderReady;

        // Флаг чтобы поднять RecorderReady только один раз
        private bool _recorderReadyRaised;
        private DateTime _recordingStartTime;

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
            _silenceBufferBytes = 0;
            _consecutiveSpeechFrames = 0;
            _speechActive = false;
            _preRollBuffer.Clear();
            _preRollBufferBytes = 0;
            _recorderReadyRaised = false;
            _recordingStartTime = DateTime.Now;

            // CP1: закрыть предыдущую сессию дампа — следующая запись пойдёт в новую папку.
            AudioDumpSink.ResetSession();

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

            // CP1: сырой поток сессии, до VAD и до pre-roll — то, что VAD выбросил, тоже сохраняется.
            // При выключенном LWHISPER_DEBUG_AUDIO это один статический bool-чек, без аллокаций.
            AudioDumpSink.AppendSessionPcm(e.Buffer, 0, e.BytesRecorded);

            // W0: поднять RecorderReady на первом фрейме (driver реально запустился)
            if (!_recorderReadyRaised)
            {
                _recorderReadyRaised = true;
                var elapsed = (DateTime.Now - _recordingStartTime).TotalMilliseconds;
                Log.Debug("[Recorder] Первый фрейм получен через {Elapsed}мс после StartRecording", (int)elapsed);
                Task.Run(() =>
                {
                    try { RecorderReady?.Invoke(); }
                    catch (Exception ex) { Log.Error(ex, "[Recorder] Ошибка в обработчике RecorderReady"); }
                });
            }

            // Pre-roll: до hysteresis-триггера накапливаем аудио в кольцевой очереди,
            // не пишем в основной сегмент. После триггера сначала высыпем pre-roll, потом текущий фрейм.
            int preRollMaxBytes = _settings.PreRollBufferMs * (_sampleRate * _channels * _bitsPerSample / 8 / 1000);
            byte[] frameCopy = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, frameCopy, e.BytesRecorded);

            if (!_speechActive && _currentSegmentBuffer.Length == 0)
            {
                // Накапливаем в pre-roll очередь, сбрасываем старые кадры
                _preRollBuffer.Enqueue(frameCopy);
                _preRollBufferBytes += frameCopy.Length;
                while (_preRollBufferBytes > preRollMaxBytes && _preRollBuffer.Count > 0)
                {
                    var dropped = _preRollBuffer.Dequeue();
                    _preRollBufferBytes -= dropped.Length;
                }
            }
            else if (_speechActive && _currentSegmentBuffer.Length == 0 && _preRollBuffer.Count > 0)
            {
                // Первый speech-кадр после hysteresis-триггера: высыпем pre-roll в начало сегмента
                int dumped = 0;
                while (_preRollBuffer.Count > 0)
                {
                    var preFrame = _preRollBuffer.Dequeue();
                    _currentSegmentBuffer.Write(preFrame, 0, preFrame.Length);
                    dumped += preFrame.Length;
                }
                _preRollBufferBytes = 0;
                Log.Debug("[Pre-roll] Высыпано {Bytes} байт ({Ms} мс) перед началом сегмента",
                    dumped, dumped / (_sampleRate * _channels * _bitsPerSample / 8 / 1000));
                // Текущий фрейм
                _currentSegmentBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            else
            {
                // Обычное поведение — сегмент уже активен, просто пишем
                _currentSegmentBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            }

            // Принудительная нарезка при превышении максимальной длительности (независимо от VAD)
            var currentDuration = GetSegmentDurationMs();
            if (currentDuration >= _settings.MaxSegmentDurationMs)
            {
                Log.Information("[Segment] Принудительная нарезка: длительность {Duration}мс >= лимит {Max}мс (непрерывная речь)",
                    (int)currentDuration, _settings.MaxSegmentDurationMs);
                EmitSegment();
                return; // Фрейм уже записан в буфер, эмитирован — следующий фрейм начнёт новый сегмент
            }

            // Проверить VAD на этом фрейме
            byte[] frameData = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, frameData, e.BytesRecorded);

            bool isSpeech = _vad.IsSpeech(frameData, _sampleRate);

            // Hysteresis VAD: фрейм считается реальной речью только после N подряд speech-кадров.
            // До триггера — даже если VAD сказал «speech», обрабатываем как silence.
            bool effectiveSpeech;
            if (isSpeech)
            {
                _consecutiveSpeechFrames++;
                if (!_speechActive && _consecutiveSpeechFrames >= _settings.SpeechTriggerFrames)
                {
                    _speechActive = true;
                    Log.Debug("[VAD] Hysteresis trigger: {Frames} подряд speech-кадров → speech_active=true",
                        _consecutiveSpeechFrames);
                }
                effectiveSpeech = _speechActive;
            }
            else
            {
                _consecutiveSpeechFrames = 0;
                effectiveSpeech = false;
            }

            if (effectiveSpeech)
            {
                // Речь обнаружена - сбросить счётчики тишины
                _lastSpeechTime = DateTime.Now;
                _consecutiveSilenceMs = 0;
                _totalSilenceSinceLastSpeechMs = 0; // Сбросить счетчик для автостопа
                _silenceBufferBytes = 0;
            }
            else
            {
                // Тишина (или speech до hysteresis-триггера) - увеличить счётчики
                _silenceBufferBytes += e.BytesRecorded;
                _consecutiveSilenceMs += 30; // BufferMilliseconds = 30
                _totalSilenceSinceLastSpeechMs += 30;

                var segmentDuration = GetSegmentDurationMs();

                // Post-speech padding: ждём перед отсечкой сегмента (D-08)
                if (_consecutiveSilenceMs < _settings.PostSpeechPaddingMs)
                {
                    return;
                }

                // Проверить условия для завершения сегмента
                bool isSignificantPause = _consecutiveSilenceMs >= _settings.PauseThresholdMs;

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

                // Обрезать тишину из конца сегмента, но оставить EndSilenceKeepMs мс хвоста
                // — Whisper использует хвостовую тишину как маркер «конец фразы» и не рубит последнее слово.
                if (_silenceBufferBytes > 0 && _currentSegmentBuffer.Length > _silenceBufferBytes)
                {
                    int bytesPerMs = _sampleRate * _channels * _bitsPerSample / 8 / 1000; // 32 байта/мс
                    int keepBytes = Math.Min(_settings.EndSilenceKeepMs * bytesPerMs, _silenceBufferBytes);
                    int trimBytes = _silenceBufferBytes - keepBytes;
                    if (trimBytes > 0)
                    {
                        _currentSegmentBuffer.SetLength(_currentSegmentBuffer.Length - trimBytes);
                        Log.Debug("[Segment] Обрезано {Trim} байт тишины, оставлено {Keep} байт хвоста ({KeepMs} мс)",
                            trimBytes, keepBytes, _settings.EndSilenceKeepMs);
                    }
                }

                // Пересчитать длительность после обрезки тишины
                var trimmedDuration = GetSegmentDurationMs();

                var segmentId = System.Threading.Interlocked.Increment(ref _segmentCounter);
                var audioData = new AudioData
                {
                    RawData = _currentSegmentBuffer.ToArray(),
                    SampleRate = _sampleRate,
                    Channels = _channels,
                    BitsPerSample = _bitsPerSample,
                    Duration = TimeSpan.FromMilliseconds(trimmedDuration)
                };

                Log.Information("[Segment #{Id}] Сегмент готов: длительность={Duration}мс, размер={Size} bytes",
                    segmentId, (int)trimmedDuration, audioData.RawData.Length);

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
                _silenceBufferBytes = 0;
                _consecutiveSpeechFrames = 0;
                _speechActive = false;
                _preRollBuffer.Clear();
                _preRollBufferBytes = 0;
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

            await Task.Delay(100); // Дать время in-flight OnDataAvailable/EmitSegment завершиться

            Log.Debug("Потоковая запись остановлена");

            // B1-фикс: снять снимок буфера и освободить его под _segmentLock — исключаем гонку с
            // EmitSegment (он тоже держит этот лок). Раньше StopRecordingAsync читал/диспозил буфер без лока.
            byte[] finalBytes;
            lock (_segmentLock)
            {
                finalBytes = (_currentSegmentBuffer != null && _currentSegmentBuffer.Length > 0)
                    ? _currentSegmentBuffer.ToArray()
                    : Array.Empty<byte>();

                _waveIn?.Dispose();
                _currentSegmentBuffer?.Dispose();
                _waveIn = null;
                _currentSegmentBuffer = null;
            }

            // CP1: записать session.wav до всех ранних возвратов ниже.
            AudioDumpSink.FlushSession(_sampleRate, _channels, _bitsPerSample);

            // Дальше работаем только с локальным finalBytes — общего состояния больше нет, лок не нужен.
            if (finalBytes.Length == 0)
            {
                return new AudioData();
            }

            int bytesPerSample = _sampleRate * _channels * _bitsPerSample / 8; // 32000 байт/с
            double totalDuration = (finalBytes.Length / (double)bytesPerSample) * 1000.0;
            if (totalDuration < _settings.MinSegmentDurationMs)
            {
                Log.Debug("Финальный сегмент слишком короткий ({Duration}мс), игнорируется", (int)totalDuration);
                return new AudioData();
            }

            int bytesPerMs = bytesPerSample / 1000; // 32 байта/мс
            int maxBytes = _settings.MaxSegmentDurationMs * bytesPerMs;

            AudioData? lastSegmentData = null;
            int offset = 0;
            while (offset < finalBytes.Length)
            {
                int chunkSize = Math.Min(maxBytes, finalBytes.Length - offset);
                double chunkDurationMs = (chunkSize / (double)bytesPerSample) * 1000.0;
                if (chunkDurationMs < _settings.MinSegmentDurationMs)
                {
                    Log.Debug("Финальный остаток {Duration}мс слишком короткий, пропускается", (int)chunkDurationMs);
                    break;
                }

                var segmentId = System.Threading.Interlocked.Increment(ref _segmentCounter);
                var chunkBytes = new byte[chunkSize];
                Array.Copy(finalBytes, offset, chunkBytes, 0, chunkSize);

                var segmentData = new AudioData
                {
                    RawData = chunkBytes,
                    SampleRate = _sampleRate,
                    Channels = _channels,
                    BitsPerSample = _bitsPerSample,
                    Duration = TimeSpan.FromMilliseconds(chunkDurationMs)
                };

                bool isLastChunk = (offset + chunkSize >= finalBytes.Length);

                // B2-фикс: СИНХРОННО (не Task.Run!) — обработчик синхронно регистрирует задачу
                // распознавания в SegmentRecognitionManager._activeTasks ДО возврата из метода.
                // Иначе WaitAllAsync в App.OnRecordingStopped мог отработать раньше и потерять последний сегмент.
                if (isLastChunk)
                {
                    Log.Information("[Segment #{Id}] Финальный сегмент: длительность={Duration}мс", segmentId, (int)chunkDurationMs);
                    lastSegmentData = segmentData;
                    try { FinalSegmentReady?.Invoke(segmentData); }
                    catch (Exception ex) { Log.Error(ex, "[Segment #{Id}] Ошибка при обработке FinalSegmentReady", segmentId); }
                }
                else
                {
                    Log.Information("[Segment #{Id}] Нарезка финального буфера: длительность={Duration}мс", segmentId, (int)chunkDurationMs);
                    try { SegmentReady?.Invoke(segmentData); }
                    catch (Exception ex) { Log.Error(ex, "[Segment #{Id}] Ошибка при обработке SegmentReady", segmentId); }
                }

                offset += chunkSize;
            }

            return lastSegmentData ?? new AudioData();
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

