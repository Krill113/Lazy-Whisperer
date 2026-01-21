using System.Threading.Tasks;

namespace LWhisper.Core.Interfaces
{
    /// <summary>
    /// Интерфейс для вставки текста в активное окно
    /// </summary>
    public interface ITextInjector
    {
        /// <summary>
        /// Вставить текст в активное окно
        /// </summary>
        Task InjectTextAsync(string text);
    }
}

