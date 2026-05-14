using System;
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

        /// <summary>
        /// Событие готовности промежуточного сегмента (для потокового режима)
        /// </summary>
        event Action<AudioData>? SegmentReady;

        /// <summary>
        /// Событие готовности финального сегмента (для потокового режима)
        /// </summary>
        event Action<AudioData>? FinalSegmentReady;

        /// <summary>
        /// Событие автоматической остановки записи (для потокового режима с AutoStop)
        /// </summary>
        event Action? RecordingAutoStopped;

        /// <summary>
        /// Событие готовности рекордера к захвату (первый фрейм получен от драйвера).
        /// Поднимается один раз после StartRecording, когда WaveIn реально начал писать в буфер.
        /// </summary>
        event Action? RecorderReady;
    }
}

