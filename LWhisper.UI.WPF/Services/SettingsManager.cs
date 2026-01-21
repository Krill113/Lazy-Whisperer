using System.IO;
using System.Text.Json;
using LWhisper.Core.Models;

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
                    return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
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

