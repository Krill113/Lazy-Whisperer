using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using LWhisper.SpeechEngine.Diagnostics;
using Serilog;

namespace LWhisper.DevTools.Mcp;

/// <summary>
/// Режим MCP-сервера по stdio. Запуск: подкоманда mcp либо глобальный флаг --mcp.
/// stdout целиком занят JSON-RPC: console-sink не подключается, Console.Out уводится в stderr.
/// </summary>
public static class McpMode
{
    /// <summary>Имя сервера, которое видит MCP-клиент.</summary>
    public const string ServerName = "lwhisper-transcribe";

    /// <summary>Проверяет, запрошен ли MCP-режим: подкоманда mcp или флаг --mcp.</summary>
    public static bool IsRequested(string[]? args)
    {
        if (args is null || args.Length == 0) return false;
        if (string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--mcp", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Поднимает stdio-сервер и блокируется до закрытия stdin клиентом.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        // Закон 1: ни одна чужая строка не должна попасть в stdout.
        // Транспорт SDK работает с сырым Console.OpenStandardOutput(), поэтому подмена
        // Console.Out на stderr безопасна и ловит любые случайные Console.WriteLine.
        Console.SetOut(Console.Error);
        ConfigureFileOnlyLogging();

        var trace = args.Any(a => string.Equals(a, "--trace", StringComparison.OrdinalIgnoreCase));

        try
        {
            Log.Information("MCP-режим запускается: сервер {Server} {Version}, trace={Trace}",
                ServerName, ServerVersion, trace);

            // Пустой билдер: без appsettings, без переменных окружения как конфигурации
            // и без провайдеров логирования по умолчанию (иначе консольный писал бы в stdout).
            // Host.CreateApplicationBuilder(args) здесь неприменим: провайдер командной строки
            // падает на позиционном аргументе "mcp".
            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Logging.ClearProviders();
            if (trace)
            {
                builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Logging.SetMinimumLevel(LogLevel.Debug);
            }

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion };
                })
                .WithStdioServerTransport()
                .WithTools<LWhisperMcpTools>();

            using var host = builder.Build();
            await host.RunAsync();

            Log.Information("MCP-режим завершён штатно");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "MCP-режим завершился с ошибкой");
            return 1;
        }
        finally
        {
            // Прогретые движки живут весь процесс (закон §5.4 №2) — освобождаем их явно.
            McpEngine.DisposeAll();
            Log.CloseAndFlush();
        }
    }

    private static string ServerVersion =>
        typeof(McpMode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Публичный (а не private) метод намеренно: юнит-тест CP3 проверяет, что вместе с файловым
    /// логгером устанавливается детектор аварийного fallback.
    /// </summary>
    public static void ConfigureFileOnlyLogging()
    {
        var directory = Path.Combine(EnginePaths.DebugRoot, "mcp");
        Directory.CreateDirectory(directory);

        // Детектор аварийного fallback обязан работать и в MCP-режиме: иначе поле usedFallback
        // схемы инструмента transcribe — константа false, и стоп-условие волны («замер со строкой
        // fallback выбрасывается целиком») через MCP-стенд неисполнимо. Enrich.FromLogContext
        // обязателен — без него сток не увидит runId, который RunOneAsync кладёт в LogContext.
        var fallbackWatch = new FallbackWatchSink();
        McpEngine.FallbackWatch = fallbackWatch;

        // Гасим логгер, который мог настроить CLI-путь CP2 (там есть console-sink).
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Sink(fallbackWatch)
            .WriteTo.File(
                Path.Combine(directory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }
}
