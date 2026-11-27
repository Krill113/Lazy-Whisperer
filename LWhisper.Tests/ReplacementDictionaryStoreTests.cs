using System.IO;
using LWhisper.Core.Models;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests;

public sealed class ReplacementDictionaryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private string FileName => Path.Combine(_root, "rules.json");

    [Fact]
    public void EmptyStaysEmptyAndStaleSaveRefuses()
    {
        var store = new ReplacementDictionaryStore(FileName);
        var missing = store.Load();
        Assert.Equal(DictionaryState.Missing, missing.State);
        var saved = store.Save(missing, new[] { new ReplacementRule("alpha", "beta", false, "hint") });
        var row = Assert.Single(new ReplacementDictionaryStore(FileName).Load().Rules);
        Assert.False(row.Enabled);
        Assert.Equal("hint", row.Hint);
        store.Save(saved, Array.Empty<ReplacementRule>());
        Assert.Throws<InvalidOperationException>(() => store.Save(saved, saved.Rules));
        var empty = new ReplacementDictionaryStore(FileName).Load();
        Assert.Equal(DictionaryState.Loaded, empty.State);
        Assert.Empty(empty.Rules);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"version\":2,\"rules\":[]}")]
    [InlineData("{\"version\":1,\"rules\":[{\"source\":\"a\",\"target\":\"b\"}]}")]
    public void UnreadableIsNotOverwritten(string contents)
    {
        File.WriteAllText(FileName, contents);
        var store = new ReplacementDictionaryStore(FileName);
        var state = store.Load();
        Assert.Equal(DictionaryState.Unreadable, state.State);
        Assert.Throws<InvalidOperationException>(() => store.Save(state, Array.Empty<ReplacementRule>()));
        Assert.Equal(contents, File.ReadAllText(FileName));
    }

    [Fact]
    public void LockedDestinationKeepsOriginal()
    {
        var store = new ReplacementDictionaryStore(FileName);
        var saved = store.Save(store.Load(), new[] { new ReplacementRule("alpha", "beta") });
        var before = File.ReadAllBytes(FileName);
        using (File.Open(FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => store.Save(saved, Array.Empty<ReplacementRule>()));
        Assert.Equal(before, File.ReadAllBytes(FileName));
        Assert.Single(Directory.GetFiles(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
