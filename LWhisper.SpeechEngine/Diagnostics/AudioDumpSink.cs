using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using LWhisper.Core.Models;
using Serilog;

namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Статический фасад дампа отладочного аудио (P4 спеки).
    /// Полностью no-op, пока не выставлена переменная окружения LWHISPER_DEBUG_AUDIO.
    /// Потокобезопасен: сегменты пишутся из параллельных задач распознавания,
    /// буфер сессии — из потока NAudio.
    ///
    /// Раскладка: {EnginePaths.DebugRoot}/{yyyyMMdd-HHmmss}/
    ///   seg-{id:D4}.wav — PCM сегмента ровно в том виде, в каком он ушёл в whisper
    ///   session.wav     — весь сырой поток сессии (включая выброшенное VAD'ом)
    ///   meta.jsonl      — append-only, одна JSON-строка на запись, поле type различает вид записи
    /// </summary>
    public static class AudioDumpSink
    {
        /// <summary>Жёсткий предохранитель на размер session.wav.</summary>
        public const int MaxSessionSeconds = 600;

        internal const string FlagEnvName = "LWHISPER_DEBUG_AUDIO";

        // Формат потока рекордера зафиксирован (PCM 16 kHz mono 16-bit) => 32000 байт/с.
        // Используется только для предохранителя; реальные параметры приходят в FlushSession.
        private const int AssumedBytesPerSecond = 32000;
        private const long MaxSessionBytes = (long)MaxSessionSeconds * AssumedBytesPerSecond;

        private static readonly bool EnabledFlag = ParseFlag(Environment.GetEnvironmentVariable(FlagEnvName));

        private static readonly object SessionLock = new object();
        private static readonly object MetaLock = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Кириллица в meta.jsonl должна читаться глазами, а не как Аб.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };

        private static string? _sessionDirectory;
        private static MemoryStream? _sessionPcm;
        private static bool _sessionLimitLogged;
        private static bool _overrideLogged;
        private static bool _writeErrorLogged;

        // База внутреннего счётчика заведомо выше любого реального id сегмента за сессию
        // (15-секундные сегменты: 5000 штук — это 20+ часов диктовки), чтобы имена файлов
        // не столкнулись при смешанном использовании.
        private static int _internalSegmentCounter = 5000;

        /// <summary>Включён ли дамп. Читается один раз при загрузке типа.</summary>
        public static bool Enabled => EnabledFlag;

        /// <summary>
        /// Парсер флага: "1"/"true"/"yes"/"on" (регистронезависимо, с обрезкой пробелов) = включено,
        /// всё прочее, включая null и мусор, = выключено.
        /// </summary>
        public static bool ParseFlag(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var v = value!.Trim();
            return v.Equals("1", StringComparison.OrdinalIgnoreCase)
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Каталог текущей сессии; создаётся лениво. null, если дамп выключен.</summary>
        public static string? SessionDirectory => EnabledFlag ? EnsureSessionDirectory() : null;

        /// <summary>Закрыть текущую сессию: следующая запись начнёт новую папку.</summary>
        public static void ResetSession()
        {
            if (!EnabledFlag) return;

            lock (SessionLock)
            {
                _sessionDirectory = null;
                _sessionPcm?.Dispose();
                _sessionPcm = null;
                _sessionLimitLogged = false;
            }
        }

        /// <summary>Записать WAV сегмента и строку type="segment" в meta.jsonl.</summary>
        public static void DumpSegment(AudioData audio, SegmentDumpMeta meta)
        {
            if (!EnabledFlag || audio == null || meta == null) return;

            try
            {
                var dir = EnsureSessionDirectory();
                if (dir == null) return;

                var id = meta.SegmentId > 0
                    ? meta.SegmentId
                    : Interlocked.Increment(ref _internalSegmentCounter);

                meta.Type = "segment";
                meta.SegmentId = id;
                if (string.IsNullOrEmpty(meta.TimestampUtc)) meta.TimestampUtc = DateTime.UtcNow.ToString("o");
                meta.WavFile = $"seg-{id:D4}.wav";

                // Файл пишется ВНЕ локов: имена уникальны, а держать SessionLock на время I/O нельзя —
                // его ждёт поток NAudio.
                WavWriter.WriteFile(Path.Combine(dir, meta.WavFile), audio);
                AppendMeta(dir, meta);
            }
            catch (Exception ex)
            {
                LogWriteError(ex, nameof(DumpSegment));
            }
        }

        /// <summary>Дописать строку type="postfilter" — текст сегмента после фильтров L0-L6.</summary>
        public static void RecordPostFilterText(int segmentId, string text)
        {
            if (!EnabledFlag) return;

            try
            {
                var dir = EnsureSessionDirectory();
                if (dir == null) return;

                AppendMeta(dir, new SegmentDumpMeta
                {
                    Type = "postfilter",
                    SegmentId = segmentId,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    RawText = text ?? string.Empty
                });
            }
            catch (Exception ex)
            {
                LogWriteError(ex, nameof(RecordPostFilterText));
            }
        }

        /// <summary>
        /// Дописать сырой PCM в буфер сессии. Вызывается из потока NAudio каждые 30 мс:
        /// при выключенном флаге здесь выполняется ровно один статический bool-чек.
        /// </summary>
        public static void AppendSessionPcm(byte[] buffer, int offset, int count)
        {
            if (!EnabledFlag) return;
            if (buffer == null || count <= 0) return;

            try
            {
                lock (SessionLock)
                {
                    _sessionPcm ??= new MemoryStream(AssumedBytesPerSecond * 30);

                    if (_sessionPcm.Length + count > MaxSessionBytes)
                    {
                        if (!_sessionLimitLogged)
                        {
                            _sessionLimitLogged = true;
                            Log.Warning("[AudioDump] Достигнут предохранитель session.wav ({Seconds} с) — " +
                                "дальнейший поток сессии не пишется", MaxSessionSeconds);
                        }
                        return;
                    }

                    _sessionPcm.Write(buffer, offset, count);
                }
            }
            catch (Exception ex)
            {
                LogWriteError(ex, nameof(AppendSessionPcm));
            }
        }

        /// <summary>Сбросить накопленный поток сессии в session.wav и записать строку type="session".</summary>
        public static void FlushSession(int sampleRate, int channels, int bitsPerSample)
        {
            if (!EnabledFlag) return;

            try
            {
                byte[] pcm;
                lock (SessionLock)
                {
                    if (_sessionPcm == null || _sessionPcm.Length == 0) return;
                    pcm = _sessionPcm.ToArray();
                    _sessionPcm.Dispose();
                    _sessionPcm = null;
                }

                var dir = EnsureSessionDirectory();
                if (dir == null) return;

                var path = Path.Combine(dir, "session.wav");
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    WavWriter.Write(fs, pcm, 0, pcm.Length, sampleRate, channels, bitsPerSample);
                }

                var bytesPerSecond = Math.Max(1, sampleRate * channels * bitsPerSample / 8);
                AppendMeta(dir, new SegmentDumpMeta
                {
                    Type = "session",
                    SegmentId = 0,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    DurationMs = pcm.Length * 1000.0 / bytesPerSecond,
                    WavFile = "session.wav"
                });

                Log.Information("[AudioDump] session.wav записан: {Bytes} байт, каталог {Dir}", pcm.Length, dir);
            }
            catch (Exception ex)
            {
                LogWriteError(ex, nameof(FlushSession));
            }
        }

        private static string? EnsureSessionDirectory()
        {
            if (!EnabledFlag) return null;

            var existing = _sessionDirectory;
            if (existing != null) return existing;

            // Directory.CreateDirectory идемпотентен и безопасен при параллельном вызове (не бросает,
            // если каталог уже есть) — создаём ВНЕ SessionLock. За тот же лок на потоке NAudio
            // конкурирует AppendSessionPcm (каждые 30 мс, StreamingAudioRecorder.OnDataAvailable);
            // держать его на время файлового I/O (особенно под антивирусом на %APPDATA%) означало бы
            // рисковать потерянным фреймом микрофона в момент создания ПЕРВОЙ папки сессии.
            var dir = Path.Combine(EnginePaths.DebugRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);

            lock (SessionLock)
            {
                // Другой поток мог создать каталог раньше (гонка на первом обращении) — тогда
                // используем его результат, а не наш только что созданный (безвредный дубль-каталог
                // просто останется пустым и будет проигнорирован).
                _sessionDirectory ??= dir;

                if (!_overrideLogged)
                {
                    _overrideLogged = true;
                    // §4 скелета: применённое переопределение логируется один раз со словом override.
                    Log.Information("AudioDumpSink override: {Env} включён — дамп аудио пишется в {Dir}",
                        FlagEnvName, EnginePaths.DebugRoot);
                }

                return _sessionDirectory;
            }
        }

        private static void AppendMeta(string dir, SegmentDumpMeta meta)
        {
            // JSONL: append-only строка исключает read-modify-write гонку между
            // параллельно завершающимися сегментами.
            var line = JsonSerializer.Serialize(meta, JsonOptions);
            lock (MetaLock)
            {
                File.AppendAllText(Path.Combine(dir, "meta.jsonl"), line + "\n");
            }
        }

        private static void LogWriteError(Exception ex, string operation)
        {
            if (_writeErrorLogged) return;
            _writeErrorLogged = true;
            Log.Warning(ex, "[AudioDump] Ошибка дампа в {Operation} — последующие ошибки не логируются", operation);
        }
    }
}
