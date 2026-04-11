using System.IO;
using System.Text.Json;
using LWhisper.Core.Models;
using Serilog;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Управление сохранением и загрузкой настроек
    /// </summary>
    public class SettingsManager
    {
        private readonly string _settingsPath;
        private readonly JsonSerializerOptions _jsonOptions;

        public SettingsManager()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LWhisper"
            );

            Directory.CreateDirectory(appDataPath);
            _settingsPath = Path.Combine(appDataPath, "settings.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };
        }

        /// <summary>
        /// Загрузить настройки из файла
        /// </summary>
        public AppSettings Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
                    
                    // Миграция: если позиция 0,0 - считать её "не установленной" (старые настройки)
                    if (settings.WidgetPositionX == 0 && settings.WidgetPositionY == 0)
                    {
                        settings.WidgetPositionX = -1;
                        settings.WidgetPositionY = -1;
                        Log.Information("Миграция настроек: сброшена позиция виджета 0,0 -> -1,-1");
                    }

                    // Миграция: устаревшие модели (tiny убрана из каталога)
                    if (ModelCatalog.DeprecatedMigrations.TryGetValue(settings.WhisperModelSize, out var replacementModelId))
                    {
                        Log.Information("Миграция настроек: модель {OldModel} заменена на {NewModel}",
                            settings.WhisperModelSize, replacementModelId);
                        settings.WhisperModelSize = replacementModelId;
                        Save(settings);
                    }

                    // Валидация: если модель не найдена в каталоге, сброс на дефолт
                    if (!ModelCatalog.IsValidModelId(settings.WhisperModelSize))
                    {
                        Log.Warning("Неизвестная модель {Model}, сброс на {Default}",
                            settings.WhisperModelSize, ModelCatalog.DefaultModelId);
                        settings.WhisperModelSize = ModelCatalog.DefaultModelId;
                        Save(settings);
                    }

                    return settings;
                }
            }
            catch
            {
                // Если не удалось загрузить, возвращаем настройки по умолчанию
            }

            return new AppSettings();
        }

        /// <summary>
        /// Сохранить настройки в файл
        /// </summary>
        public void Save(AppSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, _jsonOptions);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Ошибка сохранения будет залогирована позже
            }
        }
    }
}

