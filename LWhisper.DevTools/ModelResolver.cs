using System.Text.Json;
using LWhisper.SpeechEngine.Diagnostics;

namespace LWhisper.DevTools;

/// <summary>
/// Разрешение значения --model в путь к файлу модели.
/// ЗАКОН: settings.json владельца открывается ТОЛЬКО на чтение и только с FileShare.ReadWrite —
/// приложение может быть запущено, и DevTools не имеет права ни блокировать файл, ни писать в него.
/// </summary>
public static class ModelResolver
{
    /// <summary>
    /// Порядок: явный --model (путь или id) -> WhisperModelSize из settings.json -> CliOptions.DefaultModelId.
    /// </summary>
    public static string Resolve(string? modelOption)
    {
        var value = string.IsNullOrWhiteSpace(modelOption) ? null : modelOption!.Trim();
        value ??= ReadModelIdFromSettings() ?? CliOptions.DefaultModelId;

        var looksLikePath = value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            || value.Contains(Path.DirectorySeparatorChar)
                            || value.Contains(Path.AltDirectorySeparatorChar);

        return looksLikePath
            ? Path.GetFullPath(value)
            : Path.Combine(EnginePaths.ModelsFolder, $"ggml-{value}.bin");
    }

    /// <summary>
    /// Читает WhisperModelSize из settings.json. Любая проблема (нет файла, битый JSON,
    /// файл занят) — не ошибка стенда: возвращается null и работает дефолт.
    /// </summary>
    public static string? ReadModelIdFromSettings()
    {
        try
        {
            var path = EnginePaths.SettingsFile;
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "WhisperModelSize", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var id = property.Value.GetString();
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }
        catch
        {
            // Настройки владельца не обязаны быть валидными для стенда
        }

        return null;
    }
}
