using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LWhisper.Core.Models;

namespace LWhisper.Core.Text
{
    /// <summary>Однопроходные замены по неизменному исходному тексту.</summary>
    public sealed class ReplacementDictionary
    {
        private readonly ReplacementRule[] _rules;
        private static readonly Regex Protected = new Regex(
            @"""[^""\r\n]*[\\/][^""\r\n]*""|«[^»\r\n]*[\\/][^»\r\n]*»|""[^""\r\n]+?\.[\p{L}\p{N}_-]+""|«[^»\r\n]+?\.[\p{L}\p{N}_-]+»|(?<![\p{L}\p{M}\p{N}_-])--[\p{L}\p{N}_][\p{L}\p{M}\p{N}_-]*|[\p{L}\p{M}\p{N}_-]+:\p{Nd}+(?![\p{L}\p{M}\p{N}_-])|[^\s""«»]*[\\/@][^\s""«»]*|[\p{L}\p{M}\p{N}_-]+(?:\.[\p{L}\p{M}\p{N}_-]+)+",
            RegexOptions.CultureInvariant);

        public ReplacementDictionary(IEnumerable<ReplacementRule> rules)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var copy = rules.ToArray();
            foreach (var r in copy)
                if (r == null || string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Target) ||
                    r.Source.Trim() != r.Source || r.Target.Trim() != r.Target || !keys.Add(r.Source))
                    throw new ArgumentException("Нужны непустые и уникальные исходные фразы без крайних пробелов.");
            _rules = copy.Where(r => r.Enabled).OrderByDescending(r => r.Source.Length).ToArray();
        }

        public string Apply(string text)
        {
            if (_rules.Length == 0 || text.Length == 0) return text;
            var spans = Protected.Matches(text);
            int span = 0, copied = 0;
            var output = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                while (span < spans.Count && spans[span].Index + spans[span].Length <= i) span++;
                if (span < spans.Count && spans[span].Index <= i)
                {
                    i = spans[span].Index + spans[span].Length - 1;
                    continue;
                }
                if (i > 0 && IsWord(text[i - 1])) continue;
                foreach (var rule in _rules)
                {
                    int end = i + rule.Source.Length;
                    if (end > text.Length || (end < text.Length && IsWord(text[end])) ||
                        (span < spans.Count && spans[span].Index < end) ||
                        string.Compare(text, i, rule.Source, 0, rule.Source.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        continue;
                    output.Append(text, copied, i - copied);
                    var target = rule.Target;
                    if (char.IsUpper(text[i]) && char.IsLower(target[0]) && !target.Any(char.IsUpper) && IsSentenceStart(text, i))
                        output.Append(char.ToUpperInvariant(target[0])).Append(target, 1, target.Length - 1);
                    else output.Append(target);
                    copied = end;
                    i = end - 1;
                    break;
                }
            }
            return copied == 0 ? text : output.Append(text, copied, text.Length - copied).ToString();
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || char.IsSurrogate(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

        private static bool IsSentenceStart(string text, int i)
        {
            int j = i - 1;
            while (j >= 0 && (char.IsWhiteSpace(text[j]) || text[j] is '«' or '»' or '"' or '“' or '”' or '(' or ')')) j--;
            return j < 0 || text[j] is '.' or '!' or '?';
        }
    }
}
