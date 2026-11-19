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

        /// <summary>Локальный архив диктовок в dev-сборке; null сохраняет режим запуска из окружения.</summary>
        public bool? SaveLocalDictations { get; set; }

        /// <summary>Папка локального архива; null означает стандартную папку.</summary>
        public string? LocalDictationsDirectory { get; set; }

        /// <summary>
        /// Горячая клавиша (например, "Ctrl+Shift+Space")
        /// </summary>
        public string? HotkeyBinding { get; set; } = "Ctrl+Shift+Space";

        /// <summary>
        /// Задержка перед автовставкой (секунды)
        /// </summary>
        public int AutoInsertDelaySeconds { get; set; } = 2;

        /// <summary>
        /// Включить автоматическую вставку текста
        /// </summary>
        public bool AutoInsertEnabled { get; set; } = true;

        /// <summary>
        /// ID выбранного устройства записи
        /// </summary>
        public string? SelectedAudioDevice { get; set; }

        /// <summary>
        /// Позиция виджета X (-1 = использовать дефолт)
        /// </summary>
        public double WidgetPositionX { get; set; } = -1;

        /// <summary>
        /// Позиция виджета Y (-1 = использовать дефолт)
        /// </summary>
        public double WidgetPositionY { get; set; } = -1;

        /// <summary>
        /// Язык распознавания (auto/ru/en)
        /// </summary>
        public string RecognitionLanguage { get; set; } = "auto";

        /// <summary>
        /// Размер модели Whisper
        /// </summary>
        public string WhisperModelSize { get; set; } = "small";

        /// <summary>
        /// Выбор пользователя: разрешено ли распознавание на видеокарте (Vulkan). Только чекбокс в
        /// настройках. Защита от сбоев драйвера это поле НЕ трогает — её решение хранится локально
        /// на машине (GpuSafetyGuard), а settings.json перемещается между машинами.
        /// </summary>
        public bool UseGpu { get; set; } = true;

        /// <summary>
        /// Итог последнего определения бэкенда: true — модель работала на CPU.
        /// Только подсказка для расчёта параллелизма до инициализации движка; GPU при этом
        /// пробуется на каждом запуске, флаг обновляется по факту.
        /// </summary>
        public bool GpuFailed { get; set; } = false;

        /// <summary>
        /// Автоматически проверять обновления при запуске приложения
        /// </summary>
        public bool AutoUpdateCheckEnabled { get; set; } = true;

        /// <summary>
        /// Настройки потокового распознавания
        /// </summary>
        public StreamingSettings Streaming { get; set; } = new StreamingSettings();
    }
}

