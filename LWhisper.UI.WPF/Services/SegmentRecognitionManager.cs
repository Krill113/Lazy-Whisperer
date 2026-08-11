using LWhisper.Core.Interfaces;
using LWhisper.Core.Models;
using LWhisper.SpeechEngine;
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
        public SegmentRecognitionManager(ISpeechRecognizer recognizer, int maxParallelTasks = 3)
        {
            _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
            _parallelismLimit = new SemaphoreSlim(maxParallelTasks, maxParallelTasks);
            _cancellationTokenSource = new CancellationTokenSource();
            
            Log.Information("SegmentRecognitionManager инициализирован с параллелизмом={Parallel}", maxParallelTasks);
        }

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
            lock (_lockObj)
            {
                activeCount = _activeTasks.Count;
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
                    
                    // Фильтрация на основе МАКСИМАЛЬНОЙ амплитуды (пики в речи)
                    // Порог 0.03 - реальная речь обычно дает пики > 0.05-0.1
                    // Фоновый шум редко превышает 0.01-0.02
                    if (maxAmplitude < 0.03)
                    {
                        Log.Debug("[Segment #{Id}] Сегмент содержит только тишину/фоновый шум " +
                            "(средняя={Avg:F6}, макс={Max:F6}), пропускается",
                            segmentId, avgAmplitude, maxAmplitude);
                        return new RecognitionResult { Success = true, Text = string.Empty };
                    }
                    
                    // PERF-04: Если распознаватель поддерживает streaming-оптимизацию (динамический AudioContextSize),
                    // использовать RecognizeStreamingAsync для ускорения коротких сегментов
                    RecognitionResult result;
                    if (_recognizer is WhisperSpeechRecognizer whisperRecognizer)
                    {
                        result = await whisperRecognizer.RecognizeStreamingAsync(audioData, _cancellationTokenSource.Token);
                    }
                    else
                    {
                        result = await _recognizer.RecognizeAsync(audioData, _cancellationTokenSource.Token);
                    }
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                    if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        // Очистить текст от аннотаций Whisper в скобках
                        var cleanedText = CleanWhisperText(result.Text);

                        cleanedText = CollapsePhraseRepeats(cleanedText);
                        cleanedText = CollapseRepeatedWords(cleanedText);
                        cleanedText = RemoveIntraSegmentDuplicates(cleanedText);

                        if (!string.IsNullOrWhiteSpace(cleanedText))
                        {
                            // Фильтр известных галлюцинаций Whisper (YouTube-боилерплейт).
                            // Выбрасывается на коротких/тихих сегментах с пиком амплитуды выше порога.
                            if (IsKnownHallucination(cleanedText))
                            {
                                Log.Information("[Segment #{Id}] Известная галлюцинация Whisper отфильтрована: \"{Text}\"",
                                    segmentId, cleanedText);
                                return new RecognitionResult { Success = true, Text = string.Empty };
                            }

                            // D6: compression-ratio detector — safety net ПОСЛЕ dedup'ов.
                            // Простые повторы должны быть схлопнуты выше (CollapsePhraseRepeats/RemoveIntraSegmentDuplicates).
                            // Если ratio всё ещё высокий — это дикая галлюцинация, которую не вытащили regex'ы → дроп.
                            const double CompressionThreshold = 2.4;  // дефолт OpenAI Whisper
                            var compressionRatio = GetCompressionRatio(cleanedText);
                            if (compressionRatio >= CompressionThreshold)
                            {
                                Log.Information("[Segment #{Id}] Высокий compression-ratio {Ratio:F2} ≥ {Threshold:F1} после dedup'ов, дроп сегмента: \"{Text}\"",
                                    segmentId, compressionRatio, CompressionThreshold, cleanedText.Length > 100 ? cleanedText.Substring(0, 100) + "..." : cleanedText);
                                return new RecognitionResult { Success = true, Text = string.Empty };
                            }

                            // Проверить на дубликаты с предыдущими сегментами (Whisper "галлюцинирует")
                            var previousText = GetPreviousSegmentsText();
                            var uniqueText = RemoveDuplicatePrefix(cleanedText, previousText);
                            
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
                        }
                    }
                    else
                    {
                        Log.Warning("[Segment #{Id}] Распознавание не дало результата за {Elapsed}мс. " +
                            "Success={Success}, ErrorMessage={Error}",
                            segmentId, (int)elapsed, result.Success, result.ErrorMessage);
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

        /// <summary>
        /// Очистить текст от аннотаций Whisper в скобках ([...] и (...))
        /// </summary>
        private string CleanWhisperText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Удалить любые аннотации в квадратных или круглых скобках ([Стук], (музыка) и т.д.)
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"\s*\[.*?\]\s*|\s*\(.*?\)\s*",
                " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Убрать множественные пробелы
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            return text.Trim();
        }

        // Известные галлюцинации Whisper из YouTube-corpus.
        // Substrings — уверенные бренд-маркеры, ловятся в любом месте сегмента.
        private static readonly string[] HallucinationSubstrings = new[]
        {
            "DimaTorzok",
            "Субтитры создавал",
            "Субтитры подготовил",
            "Субтитры сделал",
            "Субтитры выполнил",
            "Корректор субтитров",
            "Подписывайтесь на канал",
            "Like and subscribe",
        };

        // Full-match — общие фразы, фильтруются только если сегмент состоит ровно из них.
        // Защищает реальную речь («спасибо за просмотр кода») от ложного срабатывания.
        private static readonly string[] HallucinationFullMatches = new[]
        {
            "Спасибо за просмотр",
            "Продолжение следует",
            "Спасибо за внимание",
            "Thanks for watching",
        };

        /// <summary>
        /// Compression-ratio detector: соотношение длины текста к длине его gzip-сжатого варианта.
        /// Большой ratio (≥ CompressionRatioThreshold, дефолт 2.4) означает сильно повторяющийся текст —
        /// типичный признак Whisper-галлюцинации. Универсальная защита поверх regex/HashSet dedup'ов.
        /// </summary>
        private static double GetCompressionRatio(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1.0;
            var rawBytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (rawBytes.Length < 32) return 1.0;  // короткий текст compression-ratio ненадёжен

            using var ms = new System.IO.MemoryStream();
            // leaveOpen: true — GZipStream.Dispose() закрыл бы ms по умолчанию, ms.Length бросил бы ObjectDisposedException
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(rawBytes, 0, rawBytes.Length);
            }
            var compressedLength = ms.Length;
            return compressedLength > 0 ? (double)rawBytes.Length / compressedLength : 1.0;
        }

        /// <summary>
        /// Проверка текста на известные галлюцинации Whisper (YouTube-боилерплейт из training corpus).
        /// Срабатывает на коротких/тихих сегментах, где модель додумывает текст из памяти.
        /// </summary>
        private static bool IsKnownHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var marker in HallucinationSubstrings)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var trimmed = text.Trim().TrimEnd('.', '!', '?', ',', ';', ' ');
            foreach (var phrase in HallucinationFullMatches)
            {
                if (string.Equals(trimmed, phrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Phrase-level залипание: повтор многословной фразы 2+ раз подряд.
        // RemoveIntraSegmentDuplicates ловит это только если копии симметрично заканчиваются знаком препинания —
        // когда копий 3+ и внутри них есть запятая, последняя копия (без trailing запятой) не нормализуется
        // одинаково с предыдущими и HashSet оставляет дубль.
        // 2-словные фразы покрыты — защита от валидных коротких повторов («да да») обеспечивается MinPhraseRepeatChars=10.
        // С порогом 3 была дыра: «сейчас тоже сейчас тоже» не схлопывалось (Whisper-залип на 2-словной фразе).
        private const int MinPhraseRepeatWords = 2;
        private const int MinPhraseRepeatChars = 10;

        /// <summary>
        /// Схлопнуть повторяющиеся подряд фразы из 3+ слов (Whisper «залипает» на длинной фразе).
        /// Token-based алгоритм с перебором окна от большего к меньшему — не regex, чтобы избежать
        /// catastrophic backtracking. Работает первым слоем, до word-collapse и phrase-dedup.
        /// </summary>
        private string CollapsePhraseRepeats(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count < MinPhraseRepeatWords * 2) return text;

            bool modified;
            do
            {
                modified = false;
                var maxWindow = tokens.Count / 2;

                for (int window = maxWindow; window >= MinPhraseRepeatWords; window--)
                {
                    for (int start = 0; start + 2 * window <= tokens.Count; start++)
                    {
                        var firstPhrase = string.Join(" ", tokens.GetRange(start, window));
                        var firstNorm = NormalizeForPhraseCompare(firstPhrase);
                        if (firstNorm.Length < MinPhraseRepeatChars) continue;

                        var secondPhrase = string.Join(" ", tokens.GetRange(start + window, window));
                        var secondNorm = NormalizeForPhraseCompare(secondPhrase);

                        if (firstNorm == secondNorm)
                        {
                            tokens.RemoveRange(start + window, window);
                            Log.Debug("Залипание фразы, схлопнуто {Count} слов: \"{Phrase}\"",
                                window, firstPhrase);
                            modified = true;
                            break;
                        }
                    }
                    if (modified) break;
                }
            } while (modified);

            return string.Join(" ", tokens);
        }

        private static string NormalizeForPhraseCompare(string phrase) =>
            phrase.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ' ');

        // Word-level залипание: 2+ одинаковых слова подряд через пробелы.
        // Whisper нередко удваивает значимое слово («запомнить запомнить», «теперь теперь»).
        // RemoveIntraSegmentDuplicates это не ловит — он работает только если между повторами есть знак препинания.
        private static readonly System.Text.RegularExpressions.Regex RepeatedWordsRegex =
            new System.Text.RegularExpressions.Regex(
                // [\s,;:] — повторы ловятся и через пробел, и через запятую/точку-с-запятой/двоеточие
                // (Whisper-залипания вида «гриди, гриди» раньше утекали — регекс требовал только \s).
                @"\b([\p{L}\p{N}_]+)(?:[\s,;:]+\1){1,}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Граница, ниже которой 2 копии могут быть валидной эмфазой («да-да», «не-не», «ой ой»).
        // Слово ≥ этой длины при 2 копиях считаем залипанием Whisper.
        private const int MinRepeatedWordLength = 4;

        /// <summary>
        /// Схлопнуть подряд идущие одинаковые слова в одно (Whisper «залипает» на коротких/нечётких сегментах).
        /// Значимые слова (≥4 символов) схлопываются с 2 копий, короткие — только с 3+ копий
        /// (защита от эмфазных междометий типа «да-да», «ну ну»).
        /// </summary>
        private string CollapseRepeatedWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            return RepeatedWordsRegex.Replace(text, match =>
            {
                var word = match.Groups[1].Value;
                var copyCount = match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                // Короткое слово при 2 копиях — возможно эмфаза, не трогаем.
                // При 3+ копий — однозначное залипание даже для коротких.
                if (word.Length < MinRepeatedWordLength && copyCount < 3)
                    return match.Value;

                Log.Debug("Обнаружено залипание слова ({Copies} копий), схлопнуто: \"{Original}\" → \"{Replacement}\"",
                    copyCount, match.Value, word);
                return word;
            });
        }

        /// <summary>
        /// Удалить повторяющиеся предложения внутри одного сегмента (Whisper галлюцинирует повторы)
        /// </summary>
        private string RemoveIntraSegmentDuplicates(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            // Разбить на фразы (по знакам препинания, включая запятые — Whisper часто дублирует через запятую)
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?,;])\s+");

            if (parts.Length < 2)
            {
                return text;
            }

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new System.Collections.Generic.List<string>();

            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;

                // Нормализовать для сравнения: trim, lower, убрать крайние знаки препинания
                var normalized = part.Trim().ToLowerInvariant().Trim('.', '!', '?', ',', ' ');

                if (seen.Contains(normalized))
                {
                    Log.Debug("Обнаружен внутрисегментный повтор, удалено: \"{Removed}\"", part);
                }
                else
                {
                    seen.Add(normalized);
                    result.Add(part);
                }
            }

            return string.Join(" ", result);
        }

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
        /// Удалить дублирующийся префикс (Whisper галлюцинирует предыдущий текст)
        /// </summary>
        private string RemoveDuplicatePrefix(string newText, string previousText)
        {
            if (string.IsNullOrWhiteSpace(previousText))
            {
                return newText;
            }

            // Попробовать найти, где заканчивается previousText в newText
            // Whisper может повторить весь или часть previousText в начале newText
            
            // Проверка 1: newText начинается с previousText
            if (newText.StartsWith(previousText, StringComparison.Ordinal))
            {
                var unique = newText.Substring(previousText.Length).Trim();
                if (!string.IsNullOrWhiteSpace(unique))
                {
                    Log.Debug("Обнаружен и удален полный дубликат префикса ({Length} символов)", previousText.Length);
                }
                return unique;
            }

            // Проверка 2: Найти самое длинное совпадение суффикса previousText с префиксом newText
            // Например: previousText="A B C", newText="B C D" -> уникальная часть="D"
            var previousWords = previousText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int maxOverlap = 0;
            for (int i = 1; i <= Math.Min(previousWords.Length, newWords.Length); i++)
            {
                var previousSuffix = string.Join(" ", previousWords.Skip(previousWords.Length - i));
                var newPrefix = string.Join(" ", newWords.Take(i));

                if (previousSuffix == newPrefix)
                {
                    maxOverlap = i;
                }
            }

            if (maxOverlap > 0)
            {
                var unique = string.Join(" ", newWords.Skip(maxOverlap));
                if (!string.IsNullOrWhiteSpace(unique))
                {
                    Log.Debug("Обнаружен и удален частичный дубликат префикса ({Words} слов)", maxOverlap);
                }
                return unique;
            }

            return newText;
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

