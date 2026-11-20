using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LWhisper.UI.WPF.Services;

namespace LWhisper.DevTools;

internal static class ShadowRepeat
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal sealed record Manifest(int Version, Session[] Sessions);
    internal sealed record Session(string Id, string History, double CompressionThreshold, Segment[] Segments);
    internal sealed record Segment(int Id, int Order, string Wav, string WavSha256, string ArchivedRaw,
        string ArchivedPost, Arm? Prompted, Arm? Neutral, Witness? Witness);
    internal sealed record Arm(string WavSha256, string Text, bool Success, bool? Fallback, Config Config, string? PromptSha256);
    internal sealed record Config(string Engine, string ModelSha256, string Backend, string Runtime, string Language,
        bool Beam, int? AudioContext, int? Threads, int? Parallelism);
    internal sealed record Witness(string WavSha256, string Text, bool Success, string Engine, string Runner, string ModelSha256);
    private sealed record Row(int Id, int Order, string WavSha256, string Reason, bool Flagged, bool Covered,
        string BaselinePost, string SameHistoryPromptedPost, string? NeutralPost, string ShadowPost);
    private sealed record Result(string Id, string History, string Baseline, string Shadow, List<Row> Segments);

    internal static int Run(string[] args)
    {
        if (args is not [_, var input, var output])
        {
            Console.Error.WriteLine("Usage: shadow-repeat <absolute-manifest.json> <absolute-new-output.json>");
            return 1;
        }
        try
        {
            var manifestPath = LocalPath(input);
            var root = Path.GetDirectoryName(manifestPath)!;
            var destination = PrivateOutput(output);
            Require(!Inside(root, destination));
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            CheckDuplicateProperties(document.RootElement);
            var data = document.RootElement.Deserialize<Manifest>(Json);
            Require(data is { Version: 1, Sessions.Length: > 0 });
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<Result>();
            foreach (var session in data!.Sessions)
            {
                Require(!string.IsNullOrWhiteSpace(session.Id) && ids.Add(session.Id));
                results.Add(Replay(session, root));
            }
            var rows = results.SelectMany(r => r.Segments).ToArray();
            using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new
            {
                version = 1, totalSessions = results.Count, totalSegments = rows.Length,
                flagged = rows.Count(r => r.Flagged), covered = rows.Count(r => r.Covered),
                selected = rows.Count(r => r.Reason == "selected"),
                changedFinals = results.Count(r => r.Baseline != r.Shadow), sessions = results
            }, Json);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or NotSupportedException or InvalidOperationException or NullReferenceException)
        {
            Console.Error.WriteLine("Shadow input or output rejected; check schema, paths and provenance locally.");
            return 1;
        }
    }

    private static Result Replay(Session session, string root)
    {
        Require(session.History is "validated-replay" or "single-segment" or "unknown");
        Require(session.CompressionThreshold > 0 && double.IsFinite(session.CompressionThreshold));
        var ordered = (session.Segments ?? throw new InvalidDataException()).OrderBy(s => s.Order).ToArray();
        Require(ordered.Select(s => s.Order).Distinct().Count() == ordered.Length && ordered.All(s => s.Order > 0));
        Require(ordered.Select(s => s.Id).Distinct().Count() == ordered.Length && ordered.All(s => s.Id > 0));
        var baseline = new SortedDictionary<int, string>();
        var archive = new SortedDictionary<int, string>();
        bool tainted = false;
        var shadow = new SortedDictionary<int, string>();
        var rows = new List<Row>();
        foreach (var s in ordered)
        {
            var raw = s.ArchivedRaw ?? throw new InvalidDataException();
            Require(s.ArchivedPost is not null);
            Require(!Path.IsPathRooted(s.Wav));
            var path = LocalPath(Path.Combine(root, s.Wav));
            Require(Inside(root, path) && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase));
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            bool validWav;
            try { validWav = EqualHash(hash, s.WavSha256) && WavFile.Parse(bytes, "input").RawData.Length > 0; }
            catch (InvalidDataException) { validWav = false; }
            string Filter(string raw, SortedDictionary<int, string> history) =>
                SegmentTextFilter.Replay(raw, Join(history), session.CompressionThreshold);
            var normal = Filter(raw, baseline);
            var here = Filter(raw, shadow);
            bool flagged = Tandem(raw);
            string? alternative = null;
            bool covered = false;
            bool knownPolicy = session.History == "validated-replay" ||
                (session.History == "single-segment" && ordered.Length == 1);
            bool archiveMatch = knownPolicy && (session.History == "validated-replay"
                ? LegacyShadowPolicy.Replay(raw, Join(archive), session.CompressionThreshold)
                : normal) == s.ArchivedPost;
            string reason = tainted ? "invalid-predecessor" : !validWav ? "invalid-wav"
                : !knownPolicy ? "unknown-history" : !archiveMatch ? "archive-mismatch"
                : !flagged ? "unflagged" : "missing-or-invalid-arms";
            tainted |= !validWav || (knownPolicy && !archiveMatch);
            if (archiveMatch && !tainted && !string.IsNullOrWhiteSpace(s.ArchivedPost))
                archive.Add(s.Id, s.ArchivedPost.Trim());
            if (reason == "missing-or-invalid-arms" && ArmPair(s, hash))
            {
                alternative = Filter(s.Neutral!.Text, shadow);
                var witness = s.Witness;
                if (witness is not { Success: true, Engine: "parakeet" } || !EqualHash(hash, witness.WavSha256) ||
                    !IsHash(witness.ModelSha256) || string.IsNullOrWhiteSpace(witness.Runner))
                    reason = "missing-or-invalid-witness";
                else
                {
                    var left = Normalize(alternative);
                    var right = Normalize(witness.Text);
                    covered = left.Length > 0 && right.Length > 0;
                    reason = !covered ? "empty-evidence" : SameWords(left, Normalize(here)) ? "no-word-change"
                        : left != right ? "witness-disagreement" : "selected";
                }
            }
            var accepted = reason == "selected" ? alternative! : here;
            if (validWav && !string.IsNullOrWhiteSpace(normal)) baseline.Add(s.Id, normal.Trim());
            if (validWav && !string.IsNullOrWhiteSpace(accepted)) shadow.Add(s.Id, accepted.Trim());
            rows.Add(new(s.Id, s.Order, hash.ToLowerInvariant(), reason, flagged, covered, normal, here, alternative, accepted));
        }
        return new(session.Id, session.History, Join(baseline), Join(shadow), rows);
    }

    private static bool ArmPair(Segment s, string hash) =>
        s.Prompted is { Success: true, Fallback: false, Config: { Beam: true } c } p &&
        s.Neutral is { Success: true, Fallback: false, PromptSha256: null } n && n.Config == c &&
        EqualHash(hash, p.WavSha256) && EqualHash(hash, n.WavSha256) && IsHash(p.PromptSha256) &&
        IsHash(c.ModelSha256) && c.AudioContext is >= 0 && c.Threads is > 0 && c.Parallelism is > 0 &&
        !string.IsNullOrWhiteSpace(c.Engine) && !string.IsNullOrWhiteSpace(c.Backend) &&
        !string.IsNullOrWhiteSpace(c.Runtime) && !string.IsNullOrWhiteSpace(c.Language) &&
        p.Text == s.ArchivedRaw && !string.IsNullOrWhiteSpace(n.Text);

    private static string Join(SortedDictionary<int, string> items) => string.Join(" ", items.Values);

    private static bool Tandem(string raw)
    {
        var words = Regex.Matches(raw.ToLowerInvariant(), @"\w+").Select(m => m.Value).ToArray();
        for (int width = 1; width <= 10; width++)
            for (int i = 0; i + 2 * width <= words.Length; i++)
                if (words.AsSpan(i, width).SequenceEqual(words.AsSpan(i + width, width))) return true;
        return false;
    }

    // This is an additional veto, never a way to make two witness texts agree.
    private static bool SameWords(string left, string right) =>
        Regex.Matches(left, @"[\p{L}\p{N}_]+").Select(m => m.Value)
            .SequenceEqual(Regex.Matches(right, @"[\p{L}\p{N}_]+").Select(m => m.Value));

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Do not erase signs, decimal punctuation or identifier separators. A terminal dot after a digit is ambiguous.
        var s = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return s.Length > 1 && char.IsLetter(s[^2]) && s[^1] is '.' or '!' or '?' ? s[..^1] : s;
    }

    private static void CheckDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckDuplicateProperties(item);
        if (node.ValueKind != JsonValueKind.Object) return;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in node.EnumerateObject())
        {
            Require(keys.Add(p.Name));
            CheckDuplicateProperties(p.Value);
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool EqualHash(string a, string? b) => IsHash(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string parent, string child) =>
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static string PrivateOutput(string path)
    {
        var destination = LocalPath(path, newFile: true);
        var bench = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LWhisper", "bench");
        Require(Path.IsPathFullyQualified(bench) && Inside(bench, destination));
        Require(!File.Exists(destination) && !Directory.Exists(destination));
        for (var parent = Path.GetDirectoryName(destination); parent is not null; parent = Path.GetDirectoryName(parent))
            foreach (var marker in new[] { ".git", ".sb-initialized", ".obsidian" })
                Require(!Path.Exists(Path.Combine(parent, marker)));
        return destination;
    }

    private static string LocalPath(string path, bool newFile = false)
    {
        Require(Path.IsPathFullyQualified(path) && !path.StartsWith("//") && !path.StartsWith(@"\\"));
        var full = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(full)!;
        var relative = full[pathRoot.Length..];
        Require(!relative.Contains(':') && !relative.Contains('~') &&
            relative.Split(Path.DirectorySeparatorChar).All(part => part.Length > 0 && part == part.TrimEnd(' ', '.')));
        string? check = newFile ? Path.GetDirectoryName(full) : full;
        while (check is not null)
        {
            Require((File.GetAttributes(check) & FileAttributes.ReparsePoint) == 0);
            check = Path.GetDirectoryName(check);
        }
        return full;
    }

    private static void Require(bool value)
    {
        if (!value) throw new InvalidDataException();
    }
}
