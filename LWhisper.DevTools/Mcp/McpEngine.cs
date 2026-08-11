using LWhisper.SpeechEngine.Diagnostics;

namespace LWhisper.DevTools.Mcp;

/// <summary>
/// Единственный движок на процесс: кэш прогонщиков и глобальная сериализация вызовов.
/// Нативный процессор Whisper к параллелизму не готов (скелет §5.4, закон 2).
///
/// Кэшируется <see cref="TranscribeRunner"/>, а прогретый <c>WhisperSpeechRecognizer</c> внутри него
/// (CP2, ключ (modelPath, language, beam)) — именно это делает закон §5.4 №2 исполненным: модель
/// (large-v3-turbo ~1.5 ГБ) грузится один раз на процесс, а не на каждый вызов инструмента.
/// Если когда-нибудь кэш движка уедет из TranscribeRunner — этот словарь станет мёртвым кодом,
/// вводящим в заблуждение, и его надо будет удалить вместе с законом.
/// </summary>
public static class McpEngine
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Dictionary<EngineKey, TranscribeRunner> _runners = new();

    private readonly record struct EngineKey(string ModelPath, string Language, bool Beam);

    /// <summary>
    /// Детектор аварийного fallback движка. Один на процесс, создаётся вместе с файловым логгером
    /// в <see cref="McpMode.ConfigureFileOnlyLogging"/> и передаётся КАЖДОМУ прогонщику: без него
    /// <c>RunRecord.UsedFallback</c> — константа false, поле <c>usedFallback</c> схемы инструмента
    /// transcribe врёт, а главное стоп-условие волны («замер со строкой fallback выбрасывается
    /// целиком», спека §5 правило 4) через MCP-стенд неисполнимо.
    /// </summary>
    public static FallbackWatchSink? FallbackWatch { get; set; }

    /// <summary>
    /// Резолвит модель: явный путь к .bin, путь с разделителем, либо id → {AppDataRoot}\Models\ggml-{id}.bin.
    /// По умолчанию — WhisperModelSize из settings.json (только чтение!), иначе CliOptions.DefaultModelId.
    /// Логика целиком в CP2 (<see cref="ModelResolver"/>) — здесь только делегат, чтобы у MCP и CLI
    /// не разъехались правила поиска модели.
    /// </summary>
    public static string ResolveModelPath(string? model) => ModelResolver.Resolve(model);

    /// <summary>
    /// Прогон через переиспользуемый TranscribeRunner из CP2. Все вызовы сериализованы.
    /// </summary>
    public static async Task<RunReport> RunAsync(
        string? model, string language, bool beam, string[] argv, CancellationToken cancellationToken)
    {
        var modelPath = ResolveModelPath(model);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Файл модели не найден: {modelPath}", modelPath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = new EngineKey(modelPath, language, beam);
            if (!_runners.TryGetValue(key, out var runner))
            {
                // Четырёхаргументный ctor обязателен: трёхаргументный делегирует с fallbackWatch: null,
                // и тогда record.UsedFallback = _fallbackWatch?.WasFallback(runId) ?? false — всегда false.
                runner = new TranscribeRunner(modelPath, language, gpu: false, FallbackWatch);
                _runners[key] = runner;
            }

            var options = CliOptions.Parse(argv);
            return await runner.RunAsync(options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Путь каталога для артефактов одного вызова инструмента. Каталог НЕ создаётся здесь:
    /// его создаёт <see cref="ReportWriter.Write"/> — и только когда отчёт реально пишется.
    /// Инструмент transcribe отдаёт результат целиком в JSON-ответе и на диск ничего не кладёт,
    /// поэтому создание каталога «на всякий случай» копило бы пустые папки под {DebugRoot}\mcp\.
    /// </summary>
    public static string RunDirectoryPath(string kind) => Path.Combine(
        EnginePaths.DebugRoot, "mcp", kind,
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

    /// <summary>
    /// Освобождает все прогретые движки. Вызывается при завершении MCP-режима:
    /// TranscribeRunner держит WhisperSpeechRecognizer живым весь процесс (закон §5.4 №2).
    /// </summary>
    public static void DisposeAll()
    {
        foreach (var runner in _runners.Values) runner.Dispose();
        _runners.Clear();
    }
}
