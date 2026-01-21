using System.Collections.Generic;
using System.Threading.Tasks;
using LWhisper.Core.Models;

namespace LWhisper.Core.Interfaces
{
    /// <summary>
    /// Интерфейс для записи аудио
    /// </summary>
    public interface IAudioRecorder
    {
        /// <summary>
        /// Начать запись
        /// </summary>
        void StartRecording();

        /// <summary>
        /// Остановить запись и получить аудио данные
        /// </summary>
        Task<AudioData> StopRecordingAsync();

        /// <summary>
        /// Идет ли запись
        /// </summary>
        bool IsRecording { get; }

        /// <summary>
        /// Получить список доступных устройств записи
        /// </summary>
        List<string> GetAvailableDevices();

        /// <summary>
        /// Установить устройство записи
        /// </summary>
        void SetDevice(string deviceName);
    }
}

