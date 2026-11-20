using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine;
using LWhisper.SpeechEngine.Diagnostics;
using Serilog;
using System.Collections.Concurrent;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Менеджер для параллельной обработки сегментов речи
    /// </summary>
    public class SegmentRecognitionManager : IDisposable
    {
        private readonly ISpeechRecognizer _recognizer;
        private readonly SemaphoreSlim _parallelismLimit;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly object _lockObj = new object();
        
        private readonly List<Task<RecognitionResult>> _activeTasks = new List<Task<RecognitionResult>>();
        private readonly Dictionary<int, string> _recognizedSegments = new Dictionary<int, string>();
        private int _segmentCounter = 0;
        private bool _disposed;

        // События для обновления UI
        public event Action<int, string>? SegmentRecognized; // (segmentId, text)
        public event Action<string>? FullTextUpdated; // Полный собранный текст

        /// <summary>
        /// Создать менеджер распознавания сегментов
        /// </summary>
        /// <param name="recognizer">Распознаватель речи</param>
        /// <param name="maxParallelTasks">Максимальное количество параллельных задач</param>
        /// <param name="settings">
        /// Настройки потокового режима. Отсюда берутся пороги фильтрации: амплитудный и
        /// compression-ratio. null = дефолты <see cref="StreamingSettings"/>.
        /// Раньше оба порога были захардкожены здесь, из-за чего объявленный
        /// <c>StreamingSettings.CompressionRatioThreshold</c> был мёртвым: он лежал в settings.json,
        /// но на поведение не влиял.
        /// </param>
        public SegmentRecognitionManager(ISpeechRecognizer recognizer, int maxParallelTasks = 3,
            StreamingSettings? settings = null)
        {
            _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
            _parallelismLimit = new SemaphoreSlim(maxParallelTasks, maxParallelTasks);
            _cancellationTokenSource = new CancellationTokenSource();

            var effective = settings ?? new StreamingSettings();
            _silenceMaxAmplitudeThreshold = effective.SilenceMaxAmplitudeThreshold;
            _compressionRatioThreshold = effective.CompressionRatioThreshold;

            Log.Information("SegmentRecognitionManager инициализирован с параллелизмом={Parallel}, " +
                "порог тишины={Silence:F4}, порог compression-ratio={Compression:F1}",
                maxParallelTasks, _silenceMaxAmplitudeThreshold, _compressionRatioThreshold);
        }

        /// <summary>Порог максимальной амплитуды: ниже — сегмент не отправляется в движок.</summary>
        private readonly double _silenceMaxAmplitudeThreshold;

        /// <summary>Порог compression-ratio для отбраковки повторяющегося мусора после dedup'ов.</summary>
        private readonly double _compressionRatioThreshold;

        private int _skippedSilentCount;

        /// <summary>
        /// Сколько сегментов текущей записи отброшено амплитудным гейтом как тишина/шум.
        /// Нужно, чтобы пользователю можно было назвать ПРИЧИНУ пустого результата, а не молча
        /// закрыть окно предпросмотра.
        /// </summary>
        public int SkippedSilentCount => Volatile.Read(ref _skippedSilentCount);

        /// <summary>Сколько сегментов вообще дошло до менеджера в текущей записи.</summary>
        public int ReceivedSegmentCount => Volatile.Read(ref _segmentCounter);

        /// <summary>
        /// Добавить сегмент в очередь на распознавание
        /// </summary>
        public async Task ProcessSegmentAsync(AudioData audioData)
        {
            if (_disposed)
            {
                Log.Warning("Попытка обработать сегмент после Dispose");
                return;
            }

            int segmentId = System.Threading.Interlocked.Increment(ref _segmentCounter);

            // Проверка переполнения очереди
            int activeCount;
            // Считаем только НЕзавершённые задачи: _activeTasks очищается лишь в Reset(), поэтому
            // _activeTasks.Count — это все сегменты с начала записи, а не текущая очередь.
            // Из-за этого предупреждение ниже срабатывало на любой длинной диктовке при пустой
            // очереди (замер 2026-08-11: реальное ожидание на каждом сегменте было wait=0мс).
            lock (_lockObj)
            {
                activeCount = _activeTasks.Count(t => !t.IsCompleted);
            }

            if (activeCount > 10)
            {
                Log.Warning("[Segment #{Id}] Очередь распознавания перегружена ({Count} задач). " +
                    "Возможно, пользователь говорит слишком долго без пауз.", segmentId, activeCount);
            }

            // Запустить распознавание в фоне с ограничением параллелизма
            var task = Task.Run(async () =>
            {
                // P3: startTime берётся ПОСЛЕ семафора, поэтому {Elapsed} в строке
                // «Распознано за …мс» не включает ожидание в очереди (в боевых логах сегмент
                // ждал здесь до 6 с, и в цифрах это нигде не было видно). Замеряем отдельно.
                var queuedAt = DateTime.Now;
                await _parallelismLimit.WaitAsync(_cancellationTokenSource.Token);
                var waitMs = (int)(DateTime.Now - queuedAt).TotalMilliseconds;

                var startTime = DateTime.Now;
                Log.Debug("[Segment #{Id}] Начало распознавания (длительность аудио={Duration}мс, wait={Wait}мс)",
                    segmentId, audioData.Duration.TotalMilliseconds, waitMs);

                try
                {
                    // Проверить уровень громкости перед распознаванием
                    var (avgAmplitude, maxAmplitude) = CalculateAmplitudes(audioData.RawData);
                    Log.Debug("[Segment #{Id}] Амплитуда: средняя={Avg:F4}, максимальная={Max:F4}", 
                        segmentId, avgAmplitude, maxAmplitude);
                    
                    // Фильтрация на основе МАКСИМАЛЬНОЙ амплитуды (пики в речи).
                    // Дефолт 0.03: реальная речь обычно даёт пики > 0.05-0.1, фоновый шум
                    // редко превышает 0.01-0.02. Порог настраиваемый — см. StreamingSettings.
                    if (maxAmplitude < _silenceMaxAmplitudeThreshold)
                    {
                        Interlocked.Increment(ref _skippedSilentCount);
                        Log.Debug("[Segment #{Id}] Сегмент содержит только тишину/фоновый шум " +
                            "(средняя={Avg:F6}, макс={Max:F6}, порог={Threshold:F4}), пропускается",
                            segmentId, avgAmplitude, maxAmplitude, _silenceMaxAmplitudeThreshold);
                        return new RecognitionResult { Success = true, Text = string.Empty };
                    }
                    
                    // PERF-04: Если распознаватель поддерживает streaming-оптимизацию (динамический AudioContextSize),
                    // использовать RecognizeStreamingAsync для ускорения коротких сегментов
                    RecognitionResult result;
                    if (_recognizer is WhisperSpeechRecognizer whisperRecognizer)
                    {
                        result = await whisperRecognizer.RecognizeStreamingAsync(audioData, segmentId, _cancellationTokenSource.Token);
                    }
                    else
                    {
                        result = await _recognizer.RecognizeAsync(audioData, _cancellationTokenSource.Token);
                    }
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                    if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        // Очистить текст от аннотаций Whisper в скобках
                        var cleanedText = SegmentTextFilter.CleanRepeats(result.Text);

                        if (!string.IsNullOrWhiteSpace(cleanedText))
                        {
                            // Фильтр известных галлюцинаций Whisper (YouTube-боилерплейт).
                            // Выбрасывается на коротких/тихих сегментах с пиком амплитуды выше порога.
                            if (IsKnownHallucination(cleanedText))
                            {
                                Log.Information("[Segment #{Id}] Известная галлюцинация Whisper отфильтрована: \"{Text}\"",
                                    segmentId, cleanedText);
                                AudioDumpSink.RecordPostFilterText(segmentId, string.Empty);
                                return new RecognitionResult { Success = true, Text = string.Empty };
                            }

                            // D6: compression-ratio detector — safety net ПОСЛЕ dedup'ов.
                            // Подряд идущие повторы уже обработаны CollapsePhraseRepeats/CollapseRepeatedWords.
                            // Если ratio всё ещё высокий — это дикая галлюцинация, которую не вытащили regex'ы → дроп.
                            var compressionRatio = SegmentTextFilter.GetCompressionRatio(cleanedText);
                            if (compressionRatio >= _compressionRatioThreshold)
                            {
                                Log.Information("[Segment #{Id}] Высокий compression-ratio {Ratio:F2} ≥ {Threshold:F1} после dedup'ов, дроп сегмента: \"{Text}\"",
                                    segmentId, compressionRatio, _compressionRatioThreshold, cleanedText.Length > 100 ? cleanedText.Substring(0, 100) + "..." : cleanedText);
                                AudioDumpSink.RecordPostFilterText(segmentId, string.Empty);
                                return new RecognitionResult { Success = true, Text = string.Empty };
                            }

                            // Проверить на дубликаты с предыдущими сегментами (Whisper "галлюцинирует")
                            var previousText = GetPreviousSegmentsText();
                            var uniqueText = SegmentTextFilter.RemoveDuplicatePrefix(cleanedText, previousText);

                            // CP1: текст после всей цепочки L0-L6 — для сверки с rawText из meta.jsonl.
                            // Пустая строка здесь тоже информативна: значит RemoveDuplicatePrefix съел
                            // сегмент целиком (полный повтор предыдущего). Более ранние дропы
                            // (известная галлюцинация, высокий compression-ratio, только аннотации,
                            // неуспешное распознавание) пишут свою постфильтр-строку сами, каждый
                            // на своей ветке return — иначе на такой сегмент есть type="segment" line
                            // с rawText, но нет ни одной type="postfilter", и он неотличим в дампе
                            // от "задача ещё не завершилась".
                            AudioDumpSink.RecordPostFilterText(segmentId, uniqueText);
                            
                            if (!string.IsNullOrWhiteSpace(uniqueText))
                            {
                                // Обрезать текст для логирования (не более 100 символов)
                                var logText = uniqueText.Length > 100 
                                    ? uniqueText.Substring(0, 100) + "..." 
                                    : uniqueText;

                                Log.Information("[Segment #{Id}] Распознано за {Elapsed}мс: \"{Text}\"",
                                    segmentId, (int)elapsed, logText);

                                // Сохранить результат
                                lock (_lockObj)
                                {
                                    _recognizedSegments[segmentId] = uniqueText;
                                }

                                // Уведомить UI о новом сегменте
                                SegmentRecognized?.Invoke(segmentId, uniqueText);

                                // Обновить полный текст
                                UpdateFullText();
                            }
                            else
                            {
                                Log.Debug("[Segment #{Id}] Сегмент полностью совпадает с предыдущими, игнорируется", segmentId);
                            }
                        }
                        else
                        {
                            Log.Debug("[Segment #{Id}] Сегмент содержит только аннотации, игнорируется", segmentId);
                            AudioDumpSink.RecordPostFilterText(segmentId, string.Empty);
                        }
                    }
                    else
                    {
                        Log.Warning("[Segment #{Id}] Распознавание не дало результата за {Elapsed}мс. " +
                            "Success={Success}, ErrorMessage={Error}",
                            segmentId, (int)elapsed, result.Success, result.ErrorMessage);
                        AudioDumpSink.RecordPostFilterText(segmentId, string.Empty);
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("[Segment #{Id}] Распознавание отменено", segmentId);
                    return new RecognitionResult { Success = false, ErrorMessage = "Отменено" };
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Segment #{Id}] Ошибка при распознавании", segmentId);
                    return new RecognitionResult { Success = false, ErrorMessage = ex.Message };
                }
                finally
                {
                    _parallelismLimit.Release();
                }
            }, _cancellationTokenSource.Token);

            // КОНТРАКТ (фикс B2): задача регистрируется СИНХРОННО — никаких await до этой точки.
            // StreamingAudioRecorder.StopRecordingAsync синхронно зовёт FinalSegmentReady → этот метод
            // и рассчитывает, что задача окажется в _activeTasks ДО возврата, чтобы WaitAllAsync её дождался.
            lock (_lockObj)
            {
                _activeTasks.Add(task);
            }
        }

        /// <summary>
        /// Собрать полный текст из всех распознанных сегментов в порядке
        /// </summary>
        private void UpdateFullText()
        {
            string fullText;
            lock (_lockObj)
            {
                // Собрать текст в порядке ID сегментов
                fullText = string.Join(" ",
                    _recognizedSegments.OrderBy(kv => kv.Key).Select(kv => kv.Value.Trim())
                );
            }

            if (!string.IsNullOrWhiteSpace(fullText))
            {
                FullTextUpdated?.Invoke(fullText);
            }
        }

        /// <summary>
        /// Дождаться завершения всех активных задач распознавания
        /// </summary>
        public async Task WaitAllAsync()
        {
            Task[] tasks;
            lock (_lockObj)
            {
                tasks = _activeTasks.ToArray();
            }

            if (tasks.Length > 0)
            {
                Log.Debug("Ожидание завершения {Count} задач распознавания...", tasks.Length);
                try
                {
                    await Task.WhenAll(tasks);
                    Log.Debug("Все задачи распознавания завершены");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка при ожидании завершения задач распознавания");
                }
            }
        }

        /// <summary>
        /// Очистить состояние для новой записи
        /// </summary>
        public void Reset()
        {
            lock (_lockObj)
            {
                _activeTasks.Clear();
                _recognizedSegments.Clear();
                _segmentCounter = 0;
                _skippedSilentCount = 0;
            }

            Log.Debug("SegmentRecognitionManager сброшен");
        }

        /// <summary>
        /// Получить текущий полный текст
        /// </summary>
        public string GetFullText()
        {
            lock (_lockObj)
            {
                return string.Join(" ",
                    _recognizedSegments.OrderBy(kv => kv.Key).Select(kv => kv.Value.Trim())
                );
            }
        }

        internal static bool IsKnownHallucination(string text) => SegmentTextFilter.IsKnownHallucination(text);

        /// <summary>
        /// Получить текст всех предыдущих сегментов
        /// </summary>
        private string GetPreviousSegmentsText()
        {
            lock (_lockObj)
            {
                if (_recognizedSegments.Count == 0)
                {
                    return string.Empty;
                }
                
                return string.Join(" ", _recognizedSegments.OrderBy(kv => kv.Key).Select(kv => kv.Value.Trim()));
            }
        }

        /// <summary>
        /// Вычислить среднюю и максимальную амплитуду аудио (для определения тишины)
        /// </summary>
        private (double average, double max) CalculateAmplitudes(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length < 2)
            {
                return (0, 0);
            }

            double sum = 0;
            double max = 0;
            int sampleCount = audioBytes.Length / 2; // 16-bit PCM = 2 bytes per sample
            
            for (int i = 0; i < audioBytes.Length - 1; i += 2)
            {
                // Преобразовать 2 байта в 16-bit signed sample
                short sample = (short)(audioBytes[i] | (audioBytes[i + 1] << 8));
                // Нормализовать к диапазону [0, 1]
                double normalized = Math.Abs(sample) / 32768.0;
                sum += normalized;
                
                if (normalized > max)
                {
                    max = normalized;
                }
            }
            
            double average = sum / sampleCount;
            return (average, max);
        }

        /// <summary>
        /// Освободить ресурсы и отменить активные задачи
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Log.Debug("Остановка SegmentRecognitionManager...");
                
                // Отменить все активные задачи
                _cancellationTokenSource.Cancel();

                // Дать время на корректную остановку
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.WhenAny(WaitAllAsync(), Task.Delay(5000));
                    }
                    catch { }
                }).Wait(6000);

                _cancellationTokenSource.Dispose();
                _parallelismLimit.Dispose();

                Log.Debug("SegmentRecognitionManager освобожден");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ошибка при освобождении SegmentRecognitionManager");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}

