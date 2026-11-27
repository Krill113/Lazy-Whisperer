using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LWhisper.Core.Models;
using LWhisper.Core.Text;

namespace LWhisper.UI.WPF.Services;

public enum DictionaryState { Missing, Loaded, Unreadable }

/// <summary>Прочитанная версия локального словаря.</summary>
public sealed class DictionarySnapshot
{
    internal DictionarySnapshot(DictionaryState state, ReplacementRule[] rules, string? revision = null)
        => (State, Rules, Revision) = (state, Array.AsReadOnly(rules), revision);
    public DictionaryState State { get; }
    public IReadOnlyList<ReplacementRule> Rules { get; }
    internal string? Revision { get; }
}

/// <summary>Чтение и атомарная замена отдельного файла словаря.</summary>
public sealed class ReplacementDictionaryStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private static readonly object SaveLock = new();
    private sealed record Row(string Source, string Target, bool? Enabled, string? Hint);
    private sealed record Document(int Version, Row[] Rules);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DictionarySnapshot Load()
    {
        try
        {
            var bytes = File.ReadAllBytes(_path);
            var body = bytes.AsSpan();
            if (body.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) body = body[3..];
            var doc = JsonSerializer.Deserialize<Document>(body, Json);
            if (doc is not { Version: 1, Rules: not null } || doc.Rules.Any(r => r?.Enabled is null))
                return new(DictionaryState.Unreadable, []);
            var rules = doc.Rules.Select(r => new ReplacementRule(r.Source, r.Target, r.Enabled!.Value, r.Hint)).ToArray();
            _ = new ReplacementDictionary(rules);
            return new(DictionaryState.Loaded, rules, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(DictionaryState.Missing, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(DictionaryState.Unreadable, []); }
    }

    public DictionarySnapshot Save(DictionarySnapshot expected, IReadOnlyList<ReplacementRule> rules)
    {
        var copy = rules.ToArray();
        _ = new ReplacementDictionary(copy);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1,
            copy.Select(r => new Row(r.Source, r.Target, r.Enabled, r.Hint)).ToArray()), Json);
        lock (SaveLock)
        {
            var current = Load();
            if (current.State == DictionaryState.Unreadable || expected.State != current.State || expected.Revision != current.Revision)
                throw new InvalidOperationException("Словарь недоступен или изменился. Закройте окно и откройте словарь заново.");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool owned = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                if (current.State == DictionaryState.Missing) File.Move(temporary, _path);
                else File.Replace(temporary, _path, null);
                return new(DictionaryState.Loaded, copy, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            finally
            {
                if (owned) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
