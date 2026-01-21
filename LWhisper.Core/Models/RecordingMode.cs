namespace LWhisper.Core.Models
{
    /// <summary>
    /// Режим записи аудио
    /// </summary>
    public enum RecordingMode
    {
        /// <summary>
        /// Клик для начала, клик для остановки
        /// </summary>
        Toggle,

        /// <summary>
        /// Зажатие кнопки мыши
        /// </summary>
        PushToTalk,

        /// <summary>
        /// Глобальная горячая клавиша
        /// </summary>
        Hotkey
    }
}



