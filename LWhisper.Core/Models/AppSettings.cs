namespace LWhisper.Core.Models
{
    /// <summary>
    /// Настройки приложения
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// Режим записи
        /// </summary>
        public RecordingMode RecordingMode { get; set; } = RecordingMode.Toggle;

        /// <summary>
        /// Горячая клавиша (например, "Ctrl+Shift+Space")
        /// </summary>
        public string? HotkeyBinding { get; set; } = "Ctrl+Shift+Space";

        /// <summary>
        /// Задержка перед автовставкой (секунды)
        /// </summary>
        public int AutoInsertDelaySeconds { get; set; } = 2;

        /// <summary>
        /// ID выбранного устройства записи
        /// </summary>
        public string? SelectedAudioDevice { get; set; }

        /// <summary>
        /// Позиция виджета X (0 = использовать дефолт)
        /// </summary>
        public double WidgetPositionX { get; set; } = 0;

        /// <summary>
        /// Позиция виджета Y (0 = использовать дефолт)
        /// </summary>
        public double WidgetPositionY { get; set; } = 0;

        /// <summary>
        /// Язык распознавания (auto/ru/en)
        /// </summary>
        public string RecognitionLanguage { get; set; } = "auto";

        /// <summary>
        /// Размер модели Whisper
        /// </summary>
        public string WhisperModelSize { get; set; } = "small";
    }
}

