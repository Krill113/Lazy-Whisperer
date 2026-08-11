using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace LWhisper.DevTools;

/// <summary>
/// Ловит аварийный fallback движка («Streaming recognition с AudioContextSize failed»)
/// и помечает прогон, внутри которого он произошёл.
/// Спека §5, правило 4: замер со сработавшим fallback невалиден и выбрасывается целиком.
/// Привязка к прогону — через Serilog LogContext (свойство runId), поэтому корректна и при --parallel &gt; 1.
/// Требует .Enrich.FromLogContext() в конфигурации логгера.
/// </summary>
public sealed class FallbackWatchSink : ILogEventSink
{
    public const string RunIdProperty = "runId";

    /// <summary>Подстрока шаблона сообщения движка. Полный шаблон менять нельзя — по нему парсится базлайн.</summary>
    public const string FallbackMarker = "AudioContextSize failed";

    private readonly ConcurrentDictionary<int, int> _hits = new();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning) return;
        if (!logEvent.MessageTemplate.Text.Contains(FallbackMarker, StringComparison.Ordinal)) return;
        if (!logEvent.Properties.TryGetValue(RunIdProperty, out var property)) return;
        if (property is not ScalarValue scalar || scalar.Value is not int runId) return;

        _hits.AddOrUpdate(runId, 1, (_, count) => count + 1);
    }

    public bool WasFallback(int runId) => _hits.ContainsKey(runId);

    public int TotalHits => _hits.Values.Sum();
}
