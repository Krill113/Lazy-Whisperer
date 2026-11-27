using LWhisper.Core.Models;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests;

public class LocalDictationReviewTests
{
    [Fact]
    public async Task RealAudioSmokeWhenExplicitlyEnabled()
    {
        var path = Environment.GetEnvironmentVariable("LWHISPER_REVIEW_SMOKE_WAV");
        if (string.IsNullOrEmpty(path)) return;
        using var reader = new NAudio.Wave.WaveFileReader(path);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        var data = new byte[reader.Length];
        reader.ReadExactly(data);
        var service = new LocalDictationReview();
        Assert.True(service.IsAvailable);
        var sink = new SmokeSink();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await service.ReviewAsync(new AudioData { RawData = data }, "исходный текст", CancellationToken.None);
            Assert.NotNull(result);
            Assert.True(sink.Completed, "Both workers and consensus must finish before deadline");
            System.Console.WriteLine($"Review elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        finally { Serilog.Log.Logger = previous; }
    }

    private sealed class SmokeSink : Serilog.Core.ILogEventSink
    {
        public bool Completed { get; private set; }
        public void Emit(Serilog.Events.LogEvent e) { if (e.MessageTemplate.Text == "Локальная перепроверка завершена") Completed = true; }
    }

    [Fact]
    public async Task InvalidFormatAndOversizedAudioFailOpen()
    {
        var service = new LocalDictationReview();
        Assert.Equal("baseline", await service.ReviewAsync(new AudioData { RawData = new byte[2], SampleRate = 8000 }, "baseline", CancellationToken.None));
        Assert.Equal("baseline", await service.ReviewAsync(new AudioData { RawData = new byte[120 * 32000 + 2] }, "baseline", CancellationToken.None));
    }

    [Fact]
    public async Task AlreadyCancelledReviewReturnsBaseline()
    {
        var service = new LocalDictationReview();
        Assert.Equal("baseline", await service.ReviewAsync(new AudioData { RawData = new byte[2] }, "baseline", new CancellationToken(true)));
    }

    [Fact]
    public async Task EmptyAudioReturnsBaselineWithoutLaunchingWorkers()
    {
        var result = await new LocalDictationReview().ReviewAsync(new AudioData(), "исходный текст", CancellationToken.None);
        Assert.Equal("исходный текст", result);
    }
}
