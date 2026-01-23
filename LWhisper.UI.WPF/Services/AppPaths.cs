using System;
using System.IO;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Централизованное управление путями приложения
    /// </summary>
    public static class AppPaths
    {
        private static readonly string AppDataFolder = 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LWhisper");
        
        /// <summary>
        /// Папка для моделей Whisper
        /// </summary>
        public static string ModelsFolder
        {
            get
            {
                var path = Path.Combine(AppDataFolder, "Models");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return path;
            }
        }
        
        /// <summary>
        /// Папка для логов
        /// </summary>
        public static string LogsFolder
        {
            get
            {
                var path = Path.Combine(AppDataFolder, "logs");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return path;
            }
        }
        
        /// <summary>
        /// Файл настроек
        /// </summary>
        public static string SettingsFile => Path.Combine(AppDataFolder, "settings.json");
        
        /// <summary>
        /// Корневая папка данных приложения
        /// </summary>
        public static string AppDataRootFolder
        {
            get
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }
                return AppDataFolder;
            }
        }
    }
}




