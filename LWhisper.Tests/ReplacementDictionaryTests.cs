using LWhisper.Core.Models;
using LWhisper.Core.Text;
using Xunit;

namespace LWhisper.Tests;

public class ReplacementDictionaryTests
{
    [Fact]
    public void LongestOriginalMatch()
    {
        var d = new ReplacementDictionary(new[] { new ReplacementRule("red fox", "blue fox"),
            new ReplacementRule("fox", "hare"), new ReplacementRule("blue fox", "wolf") });
        Assert.Equal("blue fox; hare", d.Apply("red fox; fox"));
        Assert.Equal("red  hare", d.Apply("red  fox"));
        var hyphen = new ReplacementDictionary(new[] { new ReplacementRule("test", "short"), new ReplacementRule("test-case", "long") });
        Assert.Equal("long", hyphen.Apply("test-case"));
    }

    [Theory]
    [InlineData("foobar foo_1 1foo яfoo fooé foó foo.txt ./foo C:\foo", "foobar foo_1 1foo яfoo fooé foó foo.txt ./foo C:\foo")]
    [InlineData("foo; foo. https://foo.test/a?q=foo x.foo foo.run() user@foo.test", "bar; bar. https://foo.test/a?q=foo x.foo foo.run() user@foo.test")]
    [InlineData("«C:\a b\foo.txt» foo", "«C:\a b\foo.txt» bar")]
    [InlineData("Foo. «Foo» then Foo", "Bar. «Bar» then bar")]
    [InlineData("\"foo bar.txt\" «foo bar.txt» \"foo bar\" foo.", "\"foo bar.txt\" «foo bar.txt» \"bar bar\" bar.")]
    [InlineData("run --foo foo:42; foo: 42", "run --foo foo:42; bar: 42")]
    public void LiteralBoundaries(string raw, string expected) =>
        Assert.Equal(expected, new ReplacementDictionary(new[] { new ReplacementRule("foo", "bar") }).Apply(raw));

    [Fact]
    public void CaseAndValidation()
    {
        Assert.Equal("Widget™", new ReplacementDictionary(new[] {new ReplacementRule("wrong", "Widget™")}).Apply("WRONG"));
        Assert.Equal("wrong", new ReplacementDictionary(new[] {new ReplacementRule("wrong", "right", false)}).Apply("wrong"));
        Assert.Throws<ArgumentException>(() => new ReplacementDictionary(new[] { new ReplacementRule("foo", " ") }));
        Assert.Throws<ArgumentException>(() => new ReplacementDictionary(new[] { new ReplacementRule("foo", "bar"), new ReplacementRule("Foo", "baz") }));
    }
}
