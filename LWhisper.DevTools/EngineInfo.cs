using System.Reflection;
using LWhisper.SpeechEngine;
using LWhisper.SpeechEngine.Diagnostics;
using Whisper.net;

namespace LWhisper.DevTools;

/// <summary>
/// Разрешённая конфигурация движка. Один и тот же тип печатает команда engine-info,
/// заполняет блок "engine" в report.json и отдаёт MCP-инструмент engine_info (CP3).
/// Второй копии этого DTO быть не должно.
/// </summary>
public sealed class EngineInfo
{
    /// <summary>Имя файла модели без пути, например ggml-large-v3-turbo.bin</summary>
    public string ModelFile { get; set; } = "";

    public string ModelPath { get; set; } = "";
    public bool ModelExists { get; set; }
    public string Language { get; set; } = CliOptions.DefaultLanguage;
    public int ProcessorCount { get; set; }

    /// <summary>
    /// Сколько потоков получит движок при текущих опциях (options.ThreadMode/Threads/Parallel) —
    /// та же формула WhisperTuning.ComputeThreads, что использует сам движок.
    /// </summary>
    public int DefaultThreads { get; set; }

    public int CtxFloorDefault { get; set; } = CliOptions.DefaultCtxFloor;
    public string ThreadMode { get; set; } = CliOptions.DefaultThreadMode;
    public bool BeamDefault { get; set; }
    public string WhisperNet { get; set; } = "";

    /// <summary>Строка WhisperFactory.GetRuntimeInfo(); пуста, пока нативный рантайм не загружен.</summary>
    public string RuntimeInfo { get; set; } = "";

    /// <summary>Стенд всегда CPU-only: распознаватель создаётся с gpuFailed:true.</summary>
    public bool Gpu { get; set; }

    public bool DumpEnabled { get; set; }
    public string? DumpDirectory { get; set; }

    /// <summary>
    /// Длина подсказки Whisper (--prompt/--prompt-file) в символах; 0 = прогон без подсказки.
    /// В отчёте обязателен: тексты плеча с подсказкой и без несравнимы, а по одному транскрипту
    /// это неотличимо.
    /// </summary>
    public int PromptChars { get; set; }

    public static EngineInfo Collect(CliOptions options, string modelPath, string? runtimeInfo)
    {
        return new EngineInfo
        {
            ModelFile = Path.GetFileName(modelPath),
            ModelPath = modelPath,
            ModelExists = File.Exists(modelPath),
            Language = options.Language,
            ProcessorCount = Environment.ProcessorCount,
            // C2/C1 (CP6): блок engine описывает КОНФИГУРАЦИЮ ПРОЦЕССА, разрешённую из options —
            // то есть ровно то, что запросили --ctx-floor/--thread-mode/--threads (или дефолты
            // CliOptions, если флаги не переданы). НЕ читаем WhisperTuning.* (ambient-окружение):
            // до первого ApplyTuningEnvironment оно пустое, а команда engine-info ApplyTuningEnvironment
            // не зовёт вовсе — чтение окружения здесь всегда возвращало бы дефолты движка и молча
            // игнорировало явные --ctx-floor/--thread-mode. Пер-прогонные значения лежат в
            // runs[].ctxFloor / runs[].threads — дублировать их в шапке при свипе нельзя: плеч
            // несколько, и «последнее выигравшее» вводило бы в заблуждение.
            DefaultThreads = WhisperTuning.ComputeThreads(
                WhisperTuning.ParseMode(options.ThreadMode), Environment.ProcessorCount,
                Math.Max(1, options.Parallel), options.Threads),
            CtxFloorDefault = options.CtxFloor,
            ThreadMode = options.ThreadMode,
            BeamDefault = options.Beam,
            WhisperNet = WhisperNetVersion(),
            RuntimeInfo = runtimeInfo ?? "",
            Gpu = false,
            DumpEnabled = AudioDumpSink.Enabled,
            DumpDirectory = AudioDumpSink.Enabled ? AudioDumpSink.SessionDirectory : null,
            PromptChars = options.InitialPrompt?.Length ?? 0
        };
    }

    /// <summary>Строка WhisperFactory.GetRuntimeInfo() без падения, если рантайм ещё не поднят.</summary>
    public static string SafeRuntimeInfo()
    {
        try { return WhisperFactory.GetRuntimeInfo() ?? ""; }
        catch { return ""; }
    }

    private static string WhisperNetVersion()
    {
        var assembly = typeof(WhisperFactory).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational!.IndexOf('+');
            return plus > 0 ? informational.Substring(0, plus) : informational;
        }
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
