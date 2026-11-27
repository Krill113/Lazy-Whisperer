namespace LWhisper.Core.Models
{
    /// <summary>Одно неизменяемое локальное правило автозамены.</summary>
    public sealed class ReplacementRule
    {
        public ReplacementRule(string source, string target, bool enabled = true, string? hint = null)
            => (Source, Target, Enabled, Hint) = (source, target, enabled, hint);

        public string Source { get; }
        public string Target { get; }
        public bool Enabled { get; }
        public string? Hint { get; }
    }
}
