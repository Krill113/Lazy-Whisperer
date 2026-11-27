using System.Diagnostics;
using System.IO;
using System.Text;
using LWhisper.Core.Models;
using LWhisper.Core.Text;
using LWhisper.SpeechEngine;
using Serilog;

namespace LWhisper.UI.WPF.Services;

/// <summary>Необязательная локальная перепроверка без сохранения записи.</summary>
public sealed class LocalDictationReview
{
    private const int MaxBytes = 120 * 32000;
    private const string Marker = "LWHISPER_REVIEW_RESULT:";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _turbo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LWhisper", "Models", "ggml-large-v3-turbo.bin");
    private readonly string _model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "orca", "speech-models", "parakeet-tdt-0.6b-v3-int8", "parakeet-tdt-0.6b-v3-Q8_0.gguf");
    private readonly string _package = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "orca", "resources", "node_modules", "transcribe-cpp", "dist", "index.js");
    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "local-dictation-parakeet.mjs");
    private readonly string _node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
    private readonly string _executable = Path.Combine(AppContext.BaseDirectory, "LWhisper.UI.WPF.exe");

    /// <summary>Только быстрая проверка файлов; успешность запуска и GPU не гарантируются.</summary>
    public bool IsAvailable => File.Exists(_turbo) && File.Exists(_model) && File.Exists(_package)
        && File.Exists(_script) && File.Exists(_node) && File.Exists(_executable);

    /// <summary>При отсутствии результата любого декодера возвращает исходный текст.</summary>
    public async Task<string> ReviewAsync(AudioData audio, string baseline, CancellationToken cancellationToken)
    {
        if (audio == null || audio.SampleRate != 16000 || audio.Channels != 1 || audio.BitsPerSample != 16 ||
            audio.RawData == null || audio.RawData.Length == 0 || audio.RawData.Length > MaxBytes || (audio.RawData.Length & 1) != 0 ||
            string.IsNullOrWhiteSpace(baseline) || !IsAvailable || cancellationToken.IsCancellationRequested || !Gate.Wait(0))
            return baseline;
        var stage = "Turbo";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var turbo = await RunAsync(_executable, new[] { "--review-turbo", _turbo }, audio.RawData, deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(turbo)) { Log.Warning("Локальная перепроверка: {Stage} не вернул результат", stage); return baseline; }
            stage = "Parakeet";
            var parakeet = await RunAsync(_node, new[] { _script, _package, _model }, audio.RawData, deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(parakeet)) { Log.Warning("Локальная перепроверка: {Stage} не вернул результат", stage); return baseline; }
            stage = "consensus";
            var reviewed = DictationConsensus.Apply(baseline, turbo, parakeet);
            Log.Information("Локальная перепроверка завершена");
            return reviewed;
        }
        catch (OperationCanceledException) { Log.Warning("Локальная перепроверка отменена или истекло время: {Stage}", stage); return baseline; }
        catch (Exception ex) { Log.Warning("Локальная перепроверка: {Stage} завершился с ошибкой {ErrorType}", stage, ex.GetType().Name); return baseline; }
        finally { Gate.Release(); }
    }

    private static async Task<string?> RunAsync(string executable, string[] args, byte[] pcm, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Remove("LWHISPER_DEBUG_AUDIO");
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        using var registration = token.Register(() => { try { process.Kill(true); } catch { } });
        try
        {
            var output = ReadResultAsync(process.StandardOutput, token);
            var errors = DrainAsync(process.StandardError, token);
            await process.StandardInput.BaseStream.WriteAsync(pcm, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return process.ExitCode == 0 ? result : null;
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }

    private static async Task<string?> ReadResultAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException();
            output.Append(buffer, 0, count);
        }
        var line = output.ToString().Split('\n').LastOrDefault(x => x.StartsWith(Marker, StringComparison.Ordinal));
        return line == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(line[Marker.Length..].Trim()));
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0) { }
    }

    internal static async Task<int> RunTurboWorkerAsync(string model)
    {
        try
        {
            using var input = new MemoryStream();
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[32768];
            int read;
            while ((read = await stdin.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                if (input.Length + read > MaxBytes) return 1;
                input.Write(buffer, 0, read);
            }
            if (input.Length == 0 || (input.Length & 1) != 0) return 1;
            var bytes = input.ToArray();
            var pcm = new float[bytes.Length / 2];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            using var recognizer = new WhisperSpeechRecognizer(model, "ru", false, new StreamingSettings { UseBeamSearch = true }, null, 3);
            await recognizer.InitializeAsync().ConfigureAwait(false);
            if (!recognizer.IsUsingGpu || !(recognizer.GpuDetection?.ModelBackend?.StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase) ?? false)) return 1;
            using var processor = recognizer.CreateFullSessionReviewProcessor();
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(pcm))
                if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(segment.Text.Trim());
            Console.Out.WriteLine(Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(" ", parts))));
            return 0;
        }
        catch { return 1; }
    }
}
