using Serilog;

namespace LWhisper.UI.WPF.Services
{
    // Связан как исходник в DevTools; не зависит от WPF и состояния записи.
    internal static class SegmentTextFilter
    {
        internal static string CleanRepeats(string text) =>
            CollapseRepeatedWords(CollapsePhraseRepeats(CleanWhisperText(text)));

        internal static string Replay(string raw, string previous, double threshold)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var text = CleanRepeats(raw);
            return string.IsNullOrWhiteSpace(text) || IsKnownHallucination(text) || GetCompressionRatio(text) >= threshold
                ? string.Empty : RemoveDuplicatePrefix(text, previous);
        }

        /// <summary>
        /// Очистить текст от аннотаций Whisper в скобках ([...] и (...))
        /// </summary>
        private static string CleanWhisperText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Удалить любые аннотации в квадратных или круглых скобках ([Стук], (музыка) и т.д.)
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"\s*\[.*?\]\s*|\s*\(.*?\)\s*",
                " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Убрать множественные пробелы
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            return text.Trim();
        }

        // Известные галлюцинации Whisper из YouTube-corpus.
        // Substrings — уверенные бренд-маркеры, ловятся в любом месте сегмента.
        //
        // Часть фраз заимствована из проекта whisper-type (MIT, Copyright (c) 2026 kirillrub108):
        //   репозиторий github.com/kirillrub108/whisper-type
        //   файл whispertype/config.py, константа DEFAULT_HALLUCINATION_PATTERNS
        //   коммит c1e58b903f577005a96f51350ac34a02e71702be (2026-08-06)
        // Лицензия — THIRD-PARTY-NOTICES.txt.
        // Разбиение по тирам (substring / full-match) — НАШЕ: у источника все паттерны вырезаются
        // как подстроки, у нас общие фразы фильтруются только целым сегментом (спека §3.6).
        // Семантика тоже наша: источник вырезает подстроку, мы дропаем сегмент целиком.
        private static readonly string[] HallucinationSubstrings = new[]
        {
            "DimaTorzok",
            "Субтитры создавал",
            "Субтитры подготовил",
            "Субтитры сделал",
            "Субтитры выполнил",
            "Корректор субтитров",
            "Подписывайтесь на канал",
            "Like and subscribe",
            // C6, whisper-type DEFAULT_HALLUCINATION_PATTERNS @ c1e58b90 (в скобках — дословный паттерн источника)
            "Субтитры делал",          // "субтитры делал"
            "Редактор субтитров",      // "редактор субтитров"
            "Amara.org",               // "amara.org"
            "Subtitles by",            // "subtitles by"
            "Переведено сообществом",  // "переведено сообществом"
            "Thank you for watching",  // "thank you for watching"
        };

        // Full-match — общие фразы, фильтруются только если сегмент состоит ровно из них.
        // Защищает реальную речь («спасибо за просмотр кода») от ложного срабатывания.
        private static readonly string[] HallucinationFullMatches = new[]
        {
            "Спасибо за просмотр",
            "Продолжение следует",
            "Спасибо за внимание",
            "Thanks for watching",
            // C6, whisper-type DEFAULT_HALLUCINATION_PATTERNS @ c1e58b90.
            // У источника это подстроки; у нас понижены в full-match — обе фразы могут быть
            // настоящей речью («до новых встреч на объекте», «ставьте лайк на чертеже»).
            "До новых встреч",         // "до новых встреч"
            "Ставьте лайк",            // "ставьте лайк"
        };

        /// <summary>
        /// Compression-ratio detector: соотношение длины текста к длине его gzip-сжатого варианта.
        /// Большой ratio (≥ CompressionRatioThreshold, дефолт 2.4) означает сильно повторяющийся текст —
        /// типичный признак Whisper-галлюцинации. Универсальная защита поверх regex/HashSet dedup'ов.
        /// </summary>
        internal static double GetCompressionRatio(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1.0;
            var rawBytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (rawBytes.Length < 32) return 1.0;  // короткий текст compression-ratio ненадёжен

            using var ms = new System.IO.MemoryStream();
            // leaveOpen: true — GZipStream.Dispose() закрыл бы ms по умолчанию, ms.Length бросил бы ObjectDisposedException
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(rawBytes, 0, rawBytes.Length);
            }
            var compressedLength = ms.Length;
            return compressedLength > 0 ? (double)rawBytes.Length / compressedLength : 1.0;
        }

        /// <summary>
        /// Проверка текста на известные галлюцинации Whisper (YouTube-боилерплейт из training corpus).
        /// Срабатывает на коротких/тихих сегментах, где модель додумывает текст из памяти.
        /// </summary>
        /// <remarks>
        /// internal, а не private: фильтр покрыт прямыми тестами в LWhisper.Tests
        /// (InternalsVisibleTo в LWhisper.UI.WPF.csproj). Вызывающий код не меняется.
        /// </remarks>
        internal static bool IsKnownHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var marker in HallucinationSubstrings)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var trimmed = text.Trim().TrimEnd('.', '!', '?', ',', ';', ' ');
            foreach (var phrase in HallucinationFullMatches)
            {
                if (string.Equals(trimmed, phrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Схлопываем только соседние копии: повтор после другой фразы может быть намеренным.
        // Минимум 10 символов защищает короткие повторы вроде «да да».
        private const int MinPhraseRepeatWords = 2;
        private const int MinPhraseRepeatChars = 10;

        /// <summary>
        /// Схлопнуть повторяющиеся подряд фразы из 2+ слов (Whisper «залипает» на длинной фразе).
        /// Token-based алгоритм с перебором окна от большего к меньшему — не regex, чтобы избежать
        /// catastrophic backtracking. Работает до схлопывания отдельных слов.
        /// </summary>
        private static string CollapsePhraseRepeats(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count < MinPhraseRepeatWords * 2) return text;

            // Привязка к исходным позициям: после первого удаления тройка не становится рестартом.
            var positions = Enumerable.Range(0, tokens.Count).ToList();
            var tripleStarts = new HashSet<int>();
            for (int i = 0; i + 6 <= tokens.Count; i++)
            {
                var pair = NormalizeForPhraseCompare(string.Join(" ", tokens.GetRange(i, 2)));
                if (pair == NormalizeForPhraseCompare(string.Join(" ", tokens.GetRange(i + 2, 2))) &&
                    pair == NormalizeForPhraseCompare(string.Join(" ", tokens.GetRange(i + 4, 2))))
                {
                    tripleStarts.Add(i);
                    tripleStarts.Add(i + 2);
                }
            }

            bool modified;
            do
            {
                modified = false;
                var maxWindow = tokens.Count / 2;

                for (int window = maxWindow; window >= MinPhraseRepeatWords; window--)
                {
                    for (int start = 0; start + 2 * window <= tokens.Count; start++)
                    {
                        var firstPhrase = string.Join(" ", tokens.GetRange(start, window));
                        var firstNorm = NormalizeForPhraseCompare(firstPhrase);
                        if (firstNorm.Length < MinPhraseRepeatChars) continue;

                        var secondPhrase = string.Join(" ", tokens.GetRange(start + window, window));
                        var secondNorm = NormalizeForPhraseCompare(secondPhrase);

                        if (firstNorm == secondNorm || (window >= 5 && SameIntroExceptComma(firstNorm, secondNorm)))
                        {
                            if (window == 2 && !tripleStarts.Contains(positions[start]) &&
                                IsSentenceEnd(tokens[start + 1]) && char.IsUpper(tokens[start + 2][0]) &&
                                !IsSentenceEnd(tokens[start + 3]))
                            {
                                var continuation = new List<string>(2);
                                for (int i = start + 4; i < tokens.Count; i++)
                                {
                                    if (tokens[i].Any(char.IsLetterOrDigit)) continuation.Add(tokens[i]);
                                    if (continuation.Count == 2 || IsSentenceEnd(tokens[i])) break;
                                }
                                if (continuation.Count == 2 && NormalizeForPhraseCompare(string.Join(" ", continuation)) != firstNorm)
                                    continue;
                            }

                            tokens.RemoveRange(start + window, window);
                            positions.RemoveRange(start + window, window);
                            Log.Debug("Залипание фразы, схлопнуто {Count} слов: \"{Phrase}\"",
                                window, firstPhrase);
                            modified = true;
                            break;
                        }
                    }
                    if (modified) break;
                }
            } while (modified);

            return string.Join(" ", tokens);
        }

        private static string NormalizeForPhraseCompare(string phrase) =>
            phrase.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ' ');

        private static bool IsSentenceEnd(string token) => token[^1] is '.' or '!' or '?';

        private static bool SameIntroExceptComma(string first, string second)
        {
            foreach (var intro in new[] { "и вообще", "вообще" })
            {
                var end = intro.Length;
                if ((first.StartsWith(intro + ", ", StringComparison.Ordinal) &&
                     second.StartsWith(intro + " ", StringComparison.Ordinal) && first.AsSpan(end + 1).SequenceEqual(second.AsSpan(end))) ||
                    (second.StartsWith(intro + ", ", StringComparison.Ordinal) &&
                     first.StartsWith(intro + " ", StringComparison.Ordinal) && second.AsSpan(end + 1).SequenceEqual(first.AsSpan(end))))
                    return true;
            }
            return false;
        }

        // Word-level залипание: 2+ одинаковых слова подряд через пробелы.
        // Whisper нередко удваивает значимое слово («запомнить запомнить», «теперь теперь»).
        private static readonly System.Text.RegularExpressions.Regex RepeatedWordsRegex =
            new System.Text.RegularExpressions.Regex(
                // [\s,;:] — повторы ловятся и через пробел, и через запятую/точку-с-запятой/двоеточие
                // (Whisper-залипания вида «гриди, гриди» раньше утекали — регекс требовал только \s).
                @"\b([\p{L}\p{N}_]+)(?:[\s,;:]+\1){1,}\b",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Граница, ниже которой 2 копии могут быть валидной эмфазой («да-да», «не-не», «ой ой»).
        // Слово ≥ этой длины при 2 копиях считаем залипанием Whisper.
        private const int MinRepeatedWordLength = 4;

        /// <summary>
        /// Схлопнуть подряд идущие одинаковые слова в одно (Whisper «залипает» на коротких/нечётких сегментах).
        /// Значимые слова (≥4 символов) схлопываются с 2 копий, короткие — только с 3+ копий
        /// (защита от эмфазных междометий типа «да-да», «ну ну»).
        /// </summary>
        private static string CollapseRepeatedWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            return RepeatedWordsRegex.Replace(text, match =>
            {
                var word = match.Groups[1].Value;
                var copyCount = match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

                // Короткое слово при 2 копиях — возможно эмфаза, не трогаем.
                // При 3+ копий — однозначное залипание даже для коротких.
                if (word.Length < MinRepeatedWordLength && copyCount < 3)
                    return match.Value;

                Log.Debug("Обнаружено залипание слова ({Copies} копий), схлопнуто: \"{Original}\" → \"{Replacement}\"",
                    copyCount, match.Value, word);
                return word;
            });
        }

        /// <summary>
        /// Удалить дублирующийся префикс (Whisper галлюцинирует предыдущий текст)
        /// </summary>
        internal static string RemoveDuplicatePrefix(string newText, string previousText)
        {
            if (string.IsNullOrWhiteSpace(previousText))
            {
                return newText;
            }

            // Попробовать найти, где заканчивается previousText в newText
            // Whisper может повторить весь или часть previousText в начале newText

            // Проверка 1: newText начинается с previousText
            if (newText.StartsWith(previousText, StringComparison.Ordinal))
            {
                var unique = newText.Substring(previousText.Length).Trim();
                if (!string.IsNullOrWhiteSpace(unique))
                {
                    Log.Debug("Обнаружен и удален полный дубликат префикса ({Length} символов)", previousText.Length);
                }
                return unique;
            }

            // Проверка 2: Найти самое длинное совпадение суффикса previousText с префиксом newText
            // Например: previousText="A B C", newText="B C D" -> уникальная часть="D"
            var previousWords = previousText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var newWords = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int maxOverlap = 0;
            for (int i = 1; i <= Math.Min(previousWords.Length, newWords.Length); i++)
            {
                var previousSuffix = string.Join(" ", previousWords.Skip(previousWords.Length - i));
                var newPrefix = string.Join(" ", newWords.Take(i));

                if (previousSuffix == newPrefix)
                {
                    maxOverlap = i;
                }
            }

            if (maxOverlap > 0)
            {
                var unique = string.Join(" ", newWords.Skip(maxOverlap));
                if (!string.IsNullOrWhiteSpace(unique))
                {
                    Log.Debug("Обнаружен и удален частичный дубликат префикса ({Words} слов)", maxOverlap);
                }
                return unique;
            }

            return newText;
        }

    }
}
