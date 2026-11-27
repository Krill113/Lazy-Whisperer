using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests;

public class ReviewAudioBufferTests
{
    [Fact]
    public void CapturesEveryFrameBeforeVadAndDetachesAtStop()
    {
        var buffer = new ReviewAudioBuffer(4);
        byte[] frame = { 1, 2, 99 };
        buffer.Append(frame, 2);
        frame[0] = 3; frame[1] = 4;
        buffer.Append(frame, 2);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.Take());
        buffer.Append(frame, 2);
        Assert.Empty(buffer.Take());
    }

    [Fact]
    public void NeverOffersTruncatedAudioAsAWholeRecording()
    {
        var buffer = new ReviewAudioBuffer(2);
        buffer.Append(new byte[2], 2);
        buffer.Append(new byte[2], 2);
        Assert.Empty(buffer.Take());
    }
}
