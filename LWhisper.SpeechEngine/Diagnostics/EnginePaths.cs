using System;
using System.IO;
using Serilog;

namespace LWhisper.SpeechEngine.Diagnostics
{
    /// <summary>
    /// Кроссплатформенные пути движка (D7: никакого Windows-API — только Environment.SpecialFolder).
    /// Геттеры каталогов НЕ создают: создание — задача того, кто пишет (AudioDumpSink).
    /// </summary>
    public static class EnginePaths
    {
        /// <summary>Имя переменной окружения, переопределяющей корень дампов.</summary>
        internal const string DebugDirEnvName = "LWHISPER_DEBUG_AUDIO_DIR";

        /// <summary>Корень пользовательских данных: %APPDATA%\LWhisper (Windows), ~/.config/LWhisper (Linux).</summary>
        public static string AppDataRoot { get; }

        /// <summary>Каталог моделей ggml-*.bin.</summary>
        public static string ModelsFolder { get; }

        /// <summary>Файл настроек приложения. ТОЛЬКО ЧТЕНИЕ — движок его никогда не пишет.</summary>
        public static string SettingsFile { get; }

        /// <summary>Корень дампов отладки: LWHISPER_DEBUG_AUDIO_DIR ?? AppDataRoot/debug.</summary>
        public static string DebugRoot { get; }

        static EnginePaths()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                // На части Unix-конфигураций SpecialFolder.ApplicationData пуст — не даём собрать
                // относительный путь из пустой строки.
                appData = Directory.GetCurrentDirectory();
            }

            AppDataRoot = Path.Combine(appData, "LWhisper");
            ModelsFolder = Path.Combine(AppDataRoot, "Models");
            SettingsFile = Path.Combine(AppDataRoot, "settings.json");

            var custom = Environment.GetEnvironmentVariable(DebugDirEnvName);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                DebugRoot = custom!.Trim();
                // §4 скелета: любое применённое переопределение логируется один раз со словом override.
                Log.Information("EnginePaths override: {Env}={Dir}", DebugDirEnvName, DebugRoot);
            }
            else
            {
                DebugRoot = Path.Combine(AppDataRoot, "debug");
            }
        }
    }
}
