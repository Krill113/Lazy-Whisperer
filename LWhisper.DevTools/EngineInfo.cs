using System.Reflection;
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
    /// Сколько потоков получит движок при текущих опциях. При thread-mode=divided
    /// фактическое число считает WhisperTuning (CP6) — здесь показано legacy-значение.
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

    public static EngineInfo Collect(CliOptions options, string modelPath, string? runtimeInfo)
    {
        return new EngineInfo
        {
            ModelFile = Path.GetFileName(modelPath),
            ModelPath = modelPath,
            ModelExists = File.Exists(modelPath),
            Language = options.Language,
            ProcessorCount = Environment.ProcessorCount,
            DefaultThreads = options.Threads ?? Environment.ProcessorCount,
            CtxFloorDefault = options.CtxFloor,
            ThreadMode = options.ThreadMode,
            BeamDefault = options.Beam,
            WhisperNet = WhisperNetVersion(),
            RuntimeInfo = runtimeInfo ?? "",
            Gpu = false,
            DumpEnabled = AudioDumpSink.Enabled,
            DumpDirectory = AudioDumpSink.Enabled ? AudioDumpSink.SessionDirectory : null
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
