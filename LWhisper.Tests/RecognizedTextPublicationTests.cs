using LWhisper.Core.Models;
using LWhisper.Core.Text;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests;

public sealed class RecognizedTextPublicationTests
{
    [Fact]
    public void PublishesOnlyRawSnapshots()
    {
        var rules = new[] { new ReplacementRule("red fox", "blue fox"), new ReplacementRule("blue fox", "wolf") };
        var publication = new RecognizedTextPublication(new ReplacementDictionary(rules));
        Assert.Equal("red", publication.Publish("red"));
        rules[0] = new ReplacementRule("red fox", "new");
        Assert.Equal("blue fox", publication.Publish("red fox"));
        Assert.Equal("blue fox", publication.Publish("red fox"));
        Assert.Equal("new", new RecognizedTextPublication(new ReplacementDictionary(rules)).Publish("red fox"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DismissalSuppressesThisRecordingButKeepsFinalText(bool streaming)
    {
        var matcher = new ReplacementDictionary(new[] { new ReplacementRule("old", "new") });
        var recording = new RecognizedTextPublication(matcher);
        if (streaming) Assert.Equal("new partial", recording.Publish("old partial"));
        recording.DismissPreview();
        Assert.False(recording.CanPresent);
        int previewRequests = 0;
        int autoInsertRequests = 0;
        if (streaming)
        {
            Assert.Equal("new progress", recording.Publish("old progress"));
            recording.PresentIfAllowed(() => previewRequests++);
        }
        Assert.Equal("new final", recording.Publish("old final"));
        recording.PresentIfAllowed(() => { previewRequests++; autoInsertRequests++; });
        Assert.Equal(0, previewRequests);
        Assert.Equal(0, autoInsertRequests);
        new RecognizedTextPublication(matcher).PresentIfAllowed(() => previewRequests++);
        Assert.Equal(1, previewRequests);
    }
}
