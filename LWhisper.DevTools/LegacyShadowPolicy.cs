using LWhisper.UI.WPF.Services;

namespace LWhisper.DevTools;

// November archive policy: the three cleanup stages from f636cd2. The other
// text stages did not change and are shared with the production filter.
internal static class LegacyShadowPolicy
{
    internal static string Replay(string raw, string previous, double threshold)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = CollapseRepeatedWords(CollapsePhraseRepeats(CleanWhisperText(raw)));
        return string.IsNullOrWhiteSpace(text) || SegmentTextFilter.IsKnownHallucination(text) ||
               SegmentTextFilter.GetCompressionRatio(text) >= threshold
            ? string.Empty : SegmentTextFilter.RemoveDuplicatePrefix(text, previous);
    }

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

                    if (firstNorm == secondNorm)
                    {
                        tokens.RemoveRange(start + window, window);

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

            return word;
        });
    }
}
