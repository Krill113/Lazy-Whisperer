using LWhisper.Core.Models;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Реестр доступных моделей Whisper с метаданными
    /// </summary>
    public static class ModelCatalog
    {
        private static readonly List<WhisperModelInfo> _models = new()
        {
            new("base",                "Base",                  "ggml-base.bin",                  148,   500, false, null,   10),
            new("base-q5_1",          "Base (Q5)",              "ggml-base-q5_1.bin",              60,   250, true,  "Q5_1", 11),
            new("small",               "Small",                  "ggml-small.bin",                 488,  1000, false, null,   20),
            new("small-q5_1",         "Small (Q5)",             "ggml-small-q5_1.bin",            190,   500, true,  "Q5_1", 21),
            new("medium",              "Medium",                 "ggml-medium.bin",               1530,  3000, false, null,   30),
            new("medium-q5_0",        "Medium (Q5)",            "ggml-medium-q5_0.bin",           539,  1500, true,  "Q5_0", 31),
            new("large-v3-turbo",     "Large v3 Turbo",         "ggml-large-v3-turbo.bin",       1620,  3500, false, null,   40),
            new("large-v3-turbo-q5_0","Large v3 Turbo (Q5)",   "ggml-large-v3-turbo-q5_0.bin",  574,  1500, true,  "Q5_0", 41),
        };

        private const string BASE_URL = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        /// <summary>
        /// Все доступные модели, отсортированные по SortOrder
        /// </summary>
        public static IReadOnlyList<WhisperModelInfo> All => _models.AsReadOnly();

        /// <summary>
        /// Найти модель по Id. Возвращает null если не найдена.
        /// </summary>
        public static WhisperModelInfo? GetById(string id)
            => _models.Find(m => m.Id == id);

        /// <summary>
        /// Проверить, существует ли модель с данным Id в каталоге
        /// </summary>
        public static bool IsValidModelId(string id)
            => _models.Exists(m => m.Id == id);

        /// <summary>
        /// URL для скачивания модели с HuggingFace
        /// </summary>
        public static string GetDownloadUrl(WhisperModelInfo model)
            => BASE_URL + model.FileName;

        /// <summary>
        /// Полный путь к файлу модели на диске
        /// </summary>
        public static string GetModelPath(WhisperModelInfo model)
            => System.IO.Path.Combine(AppPaths.ModelsFolder, model.FileName);

        /// <summary>
        /// Id модели по умолчанию
        /// </summary>
        public const string DefaultModelId = "small";

        /// <summary>
        /// Список Id моделей, которые были удалены и требуют миграции
        /// </summary>
        public static readonly Dictionary<string, string> DeprecatedMigrations = new()
        {
            { "tiny", "base" }
        };
    }
}
