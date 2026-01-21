using System;

namespace LWhisper.Core.Interfaces
{
    /// <summary>
    /// Интерфейс для управления глобальными горячими клавишами
    /// </summary>
    public interface IHotkeyManager
    {
        /// <summary>
        /// Зарегистрировать горячую клавишу
        /// </summary>
        bool RegisterHotkey(string hotkeyBinding, Action callback);

        /// <summary>
        /// Отменить регистрацию горячей клавиши
        /// </summary>
        void UnregisterHotkey();

        /// <summary>
        /// Зарегистрирована ли горячая клавиша
        /// </summary>
        bool IsRegistered { get; }
    }
}

