using System.IO;
using System.Linq;
using LWhisper.DevTools;
using Xunit;

namespace LWhisper.Tests
{
    public class CliOptionsTests
    {
        [Fact]
        public void Parse_TranscribeMinimal_AppliesDocumentedDefaults()
        {
            var o = CliOptions.Parse(new[] { "transcribe", "--input", @"C:\corpus" });

            Assert.Equal("transcribe", o.Command);
            Assert.Equal(new[] { @"C:\corpus" }, o.Inputs);
            Assert.Null(o.Model);
            Assert.Equal("ru", o.Language);
            Assert.Equal(448, o.CtxFloor);
            Assert.Null(o.Threads);
            Assert.Equal("legacy", o.ThreadMode);
            Assert.False(o.Beam);
            Assert.Equal(1, o.Parallel);
            Assert.Equal(1, o.Repeat);
            Assert.Null(o.OutDir);
            Assert.Null(o.Tag);
            Assert.Equal("both", o.Format);
            Assert.False(o.Quiet);
            Assert.Equal(200, o.MaxRuns);
            Assert.Equal(30, o.MaxDurationSeconds);
            Assert.Null(o.GridCtx);
            Assert.Null(o.GridThreads);
            Assert.Null(o.GridBeam);
            Assert.Null(o.BaselineReport);
        }

        [Fact]
        public void Parse_TranscribeFullOptions_ReadsEveryValue()
        {
            var o = CliOptions.Parse(new[]
            {
                "transcribe",
                "--input", @"C:\a.wav", "--input", @"C:\b.wav",
                "--model", "large-v3-turbo",
                "--language", "EN",
                "--ctx-floor", "0",
                "--threads", "6",
                "--thread-mode", "Divided",
                "--beam",
                "--parallel", "2",
                "--repeat", "3",
                "--out", @"C:\rep",
                "--tag", "ctx-floor-ab",
                "--format", "MD",
                "--quiet",
                "--max-duration", "45"
            });

            Assert.Equal(2, o.Inputs.Count);
            Assert.Equal("large-v3-turbo", o.Model);
            Assert.Equal("en", o.Language);
            Assert.Equal(0, o.CtxFloor);
            Assert.Equal(6, o.Threads);
            Assert.Equal("divided", o.ThreadMode);
            Assert.True(o.Beam);
            Assert.Equal(2, o.Parallel);
            Assert.Equal(3, o.Repeat);
            Assert.Equal(@"C:\rep", o.OutDir);
            Assert.Equal("ctx-floor-ab", o.Tag);
            Assert.Equal("md", o.Format);
            Assert.True(o.Quiet);
            Assert.Equal(45, o.MaxDurationSeconds);
        }

        [Fact]
        public void Parse_SweepGrid_ParsesCsvLists()
        {
            var o = CliOptions.Parse(new[]
            {
                "sweep",
                "--input", @"C:\corpus",
                "--grid-ctx", "0,256, 448",
                "--grid-threads", "4,8",
                "--grid-beam", "false,true",
                "--baseline", @"C:\old\report.json",
                "--max-runs", "500"
            });

            Assert.Equal("sweep", o.Command);
            Assert.Equal(new[] { 0, 256, 448 }, o.GridCtx);
            Assert.Equal(new[] { 4, 8 }, o.GridThreads);
            Assert.Equal(new[] { false, true }, o.GridBeam);
            Assert.Equal(@"C:\old\report.json", o.BaselineReport);
            Assert.Equal(500, o.MaxRuns);
        }

        [Fact]
        public void Parse_EngineInfo_NeedsNoInput()
        {
            var o = CliOptions.Parse(new[] { "engine-info" });
            Assert.Equal("engine-info", o.Command);
            Assert.Empty(o.Inputs);
        }

        [Theory]
        [InlineData("mcp")]
        [InlineData("--mcp")]
        public void Parse_McpEntryPoints_YieldMcpCommand(string arg)
        {
            Assert.Equal("mcp", CliOptions.Parse(new[] { arg }).Command);
        }

        [Fact]
        public void Parse_UnknownOption_Throws()
        {
            var ex = Assert.Throws<CliParseException>(
                () => CliOptions.Parse(new[] { "transcribe", "--input", "x.wav", "--turbo" }));
            Assert.Contains("--turbo", ex.Message);
        }

        [Fact]
        public void Parse_UnknownCommand_Throws()
        {
            Assert.Throws<CliParseException>(() => CliOptions.Parse(new[] { "benchmark" }));
        }

        [Fact]
        public void Parse_NoArguments_Throws()
        {
            Assert.Throws<CliParseException>(() => CliOptions.Parse(Array.Empty<string>()));
        }

        [Fact]
        public void Parse_OptionWithoutValue_Throws()
        {
            Assert.Throws<CliParseException>(() => CliOptions.Parse(new[] { "transcribe", "--input" }));
        }

        [Theory]
        [InlineData("--language", "de")]
        [InlineData("--thread-mode", "turbo")]
        [InlineData("--format", "html")]
        [InlineData("--ctx-floor", "-1")]
        [InlineData("--threads", "0")]
        [InlineData("--parallel", "abc")]
        [InlineData("--max-duration", "0")]
        public void Parse_InvalidValue_Throws(string option, string value)
        {
            Assert.Throws<CliParseException>(
                () => CliOptions.Parse(new[] { "transcribe", "--input", "x.wav", option, value }));
        }

        [Fact]
        public void Parse_TranscribeWithoutInput_Throws()
        {
            Assert.Throws<CliParseException>(() => CliOptions.Parse(new[] { "transcribe" }));
        }

        [Fact]
        public void Parse_GridOptionsOutsideSweep_Throws()
        {
            Assert.Throws<CliParseException>(
                () => CliOptions.Parse(new[] { "transcribe", "--input", "x.wav", "--grid-ctx", "0,448" }));
        }

        [Fact]
        public void Usage_ListsEveryCommand()
        {
            var usage = CliOptions.Usage;
            Assert.Contains("transcribe", usage);
            Assert.Contains("sweep", usage);
            Assert.Contains("engine-info", usage);
            Assert.Contains("--grid-ctx", usage);
        }
    }

    public class WavFileTests
    {
        /// <summary>Канонический 44-байтный WAV — тест не зависит от WavWriter из CP1.</summary>
        private static byte[] BuildWav(int sampleRate, short channels, short bits, byte[] pcm)
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                var byteRate = sampleRate * channels * bits / 8;
                var blockAlign = (short)(channels * bits / 8);
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcm.Length);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write(channels);
                w.Write(sampleRate);
                w.Write(byteRate);
                w.Write(blockAlign);
                w.Write(bits);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(pcm.Length);
                w.Write(pcm);
            }
            return ms.ToArray();
        }

        [Fact]
        public void Parse_Pcm16kMono_ReturnsPcmAndDuration()
        {
            var pcm = new byte[32000]; // 16000 сэмплов = ровно 1 секунда
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);

            var audio = WavFile.Parse(BuildWav(16000, 1, 16, pcm), "unit.wav");

            Assert.Equal(16000, audio.SampleRate);
            Assert.Equal(1, audio.Channels);
            Assert.Equal(16, audio.BitsPerSample);
            Assert.Equal(pcm, audio.RawData);
            Assert.Equal(1000.0, audio.Duration.TotalMilliseconds, 3);
        }

        [Fact]
        public void Parse_SkipsUnknownChunks()
        {
            // SAPI и другие писатели вставляют между fmt и data служебные чанки (LIST/fact)
            var pcm = new byte[3200];
            var basic = BuildWav(16000, 1, 16, pcm);
            var listChunk = new List<byte>();
            listChunk.AddRange(System.Text.Encoding.ASCII.GetBytes("LIST"));
            listChunk.AddRange(BitConverter.GetBytes(4));
            listChunk.AddRange(new byte[] { 1, 2, 3, 4 });

            var withList = new List<byte>();
            withList.AddRange(basic.Take(12));                 // RIFF/size/WAVE
            withList.AddRange(listChunk);
            withList.AddRange(basic.Skip(12));                 // fmt + data

            var audio = WavFile.Parse(withList.ToArray(), "unit.wav");

            Assert.Equal(3200, audio.RawData.Length);
        }

        [Theory]
        [InlineData(44100, 1, 16)]
        [InlineData(16000, 2, 16)]
        [InlineData(16000, 1, 8)]
        public void Parse_WrongFormat_Throws(int rate, int channels, int bits)
        {
            var pcm = new byte[3200];
            var ex = Assert.Throws<InvalidDataException>(
                () => WavFile.Parse(BuildWav(rate, (short)channels, (short)bits, pcm), "bad.wav"));
            Assert.Contains("bad.wav", ex.Message);
        }

        [Fact]
        public void Parse_NotRiff_Throws()
        {
            var junk = new byte[64];
            Assert.Throws<InvalidDataException>(() => WavFile.Parse(junk, "junk.wav"));
        }
    }

    public class StatisticsTests
    {
        private static RunRecord Run(string arm, double durationMs, double elapsedMs, string text = "t", string? error = null)
            => new()
            {
                File = $@"C:\corpus\{text}-{durationMs}-{elapsedMs}.wav",
                Arm = arm,
                DurationMs = durationMs,
                ElapsedMs = elapsedMs,
                Rtf = durationMs > 0 ? elapsedMs / durationMs : 0,
                Text = text,
                TextSha256 = text,
                Error = error
            };

        [Theory]
        [InlineData(0, 1)]
        [InlineData(10, 1)]
        [InlineData(25, 3)]
        [InlineData(50, 5)]
        [InlineData(90, 9)]
        [InlineData(100, 10)]
        public void Percentile_NearestRank_OnTable(int p, double expected)
        {
            var data = new List<double> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.Equal(expected, Statistics.Percentile(data, p));
        }

        [Fact]
        public void Percentile_UnsortedInput_SortsInternally()
        {
            var data = new List<double> { 10, 1, 7, 3, 5, 9, 2, 8, 4, 6 };
            Assert.Equal(5, Statistics.Percentile(data, 50));
        }

        [Fact]
        public void Percentile_EmptyOrSingle()
        {
            Assert.Equal(0, Statistics.Percentile(Array.Empty<double>(), 50));
            Assert.Equal(42, Statistics.Percentile(new double[] { 42 }, 10));
        }

        [Fact]
        public void TailRate_ShareOfRtfAboveThreshold()
        {
            // 4 прогона, rtf: 0.5, 1.4, 1.6, 3.0 -> хвост 2/4
            var runs = new[]
            {
                Run("a", 1000, 500), Run("a", 1000, 1400),
                Run("a", 1000, 1600), Run("a", 1000, 3000)
            };
            Assert.Equal(0.5, Statistics.TailRate(runs), 6);
        }

        [Fact]
        public void TailRate_ExcludesErroredRuns()
        {
            var runs = new[]
            {
                Run("a", 1000, 500),
                Run("a", 1000, 9000, error: "native -5")
            };
            Assert.Equal(0.0, Statistics.TailRate(runs), 6);
        }

        [Fact]
        public void TailRate_EmptyIsZero()
        {
            Assert.Equal(0.0, Statistics.TailRate(Array.Empty<RunRecord>()), 6);
        }

        [Fact]
        public void SummarizeArm_SplitsShortGroupAndCountsDistinctTexts()
        {
            var runs = new[]
            {
                Run("ctx=448", 3000, 2000, "один"),   // короткий (<5120)
                Run("ctx=448", 4000, 2400, "один"),   // короткий
                Run("ctx=448", 9000, 3000, "два"),    // длинный
                Run("ctx=448", 12000, 4000, "три")    // длинный
            };

            var s = Statistics.SummarizeArm("ctx=448", runs);

            Assert.Equal("ctx=448", s.Arm);
            Assert.Equal(4, s.N);
            Assert.Equal(2, s.ShortN);
            Assert.Equal(3, s.DistinctTexts);
            Assert.Equal(2000, s.ShortP10Ms);
            // nearest-rank: медиана чётного набора = нижний элемент (rank = ceil(50/100 × 2) = 1),
            // согласовано с Percentile_NearestRank_OnTable и определением метрик спеки §5.
            Assert.Equal(2000, s.ShortMedianMs);
        }

        [Fact]
        public void Summarize_GroupsByArm()
        {
            var runs = new[]
            {
                Run("ctx=0", 3000, 1000), Run("ctx=0", 3000, 1100),
                Run("ctx=448", 3000, 2000), Run("ctx=448", 3000, 2100)
            };

            var summary = Statistics.Summarize(runs);

            Assert.Equal(4, summary.N);
            Assert.Equal(2, summary.ByArm.Count);
            Assert.Equal("ctx=0", summary.ByArm[0].Arm);
            Assert.Equal("ctx=448", summary.ByArm[1].Arm);
        }

        [Fact]
        public void CompareToBaseline_FindsTextMismatchAndDeltas()
        {
            var baseline = new RunReport { Runs = { Run("a", 3000, 1000, "исходный текст") } };
            baseline.Runs[0].File = @"C:\old\short-01.wav";
            baseline.Summary = Statistics.Summarize(baseline.Runs);

            var current = new RunReport { Runs = { Run("a", 3000, 2000, "другой текст") } };
            current.Runs[0].File = @"D:\new\short-01.wav";   // тот же файл, другой каталог
            current.Summary = Statistics.Summarize(current.Runs);

            var diff = Statistics.CompareToBaseline(current, baseline, @"C:\old\report.json");

            Assert.Equal(1, diff.TextMismatches);
            Assert.Equal(new[] { "short-01.wav" }, diff.MismatchFiles);
            Assert.Single(diff.Mismatches);
            Assert.Equal("исходный текст", diff.Mismatches[0].BaselineText);
            Assert.Equal("другой текст", diff.Mismatches[0].CurrentText);
            Assert.Equal(1000, diff.P10DeltaMs);
        }
    }

    public class ReportWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lwhisper-devtools-" + Guid.NewGuid().ToString("N"));

        public ReportWriterTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static RunReport SampleReport()
        {
            var report = new RunReport
            {
                Tag = "ctx-floor-ab",
                StartedUtc = "2026-08-11T09:00:00.0000000Z",
                FinishedUtc = "2026-08-11T09:07:31.0000000Z",
                Engine = new EngineInfo
                {
                    ModelFile = "ggml-large-v3-turbo.bin",
                    ModelPath = @"C:\Models\ggml-large-v3-turbo.bin",
                    ModelExists = true,
                    Language = "ru",
                    ProcessorCount = 8,
                    DefaultThreads = 8,
                    CtxFloorDefault = 448,
                    ThreadMode = "legacy",
                    WhisperNet = "1.9.0",
                    RuntimeInfo = "WHISPER : CPU AVX2=1"
                }
            };

            report.Runs.Add(new RunRecord
            {
                File = @"C:\corpus\short-01.wav",
                FileSha256 = "aa",
                DurationMs = 3200,
                Arm = "ctx=448,threads=auto,beam=false",
                CtxFloor = 448,
                AudioContextSize = 448,
                Threads = null,
                Beam = false,
                Parallel = 1,
                RepeatIndex = 0,
                ElapsedMs = 1975,
                Rtf = 1975.0 / 3200.0,
                Text = "Отметка низа трубы двенадцать пятьдесят | с трубой",
                TextSha256 = "bb"
            });

            report.Summary = Statistics.Summarize(report.Runs);
            return report;
        }

        [Fact]
        public void ToJson_UsesCamelCaseAndKeepsSchemaVersion()
        {
            var json = ReportWriter.ToJson(SampleReport());

            Assert.Contains("\"schemaVersion\": 1", json);
            Assert.Contains("\"tool\": \"LWhisper.DevTools\"", json);
            Assert.Contains("\"audioContextSize\": 448", json);
            Assert.Contains("\"byArm\"", json);
            Assert.Contains("\"shortP10Ms\"", json);
            // Кириллица не должна превращаться в \uXXXX — отчёт читает человек
            Assert.Contains("Отметка низа трубы", json);
        }

        [Fact]
        public void FromJson_RoundTripsReport()
        {
            var original = SampleReport();
            var restored = ReportWriter.FromJson(ReportWriter.ToJson(original));

            Assert.NotNull(restored);
            Assert.Equal(1, restored!.SchemaVersion);
            Assert.Equal("ctx-floor-ab", restored.Tag);
            Assert.Single(restored.Runs);
            Assert.Equal(448, restored.Runs[0].AudioContextSize);
            Assert.Null(restored.Runs[0].Threads);
            Assert.Equal("ggml-large-v3-turbo.bin", restored.Engine.ModelFile);
            Assert.Single(restored.Summary.ByArm);
        }

        [Fact]
        public void ToMarkdown_HasHeaderArmTableAndEscapesPipes()
        {
            var md = ReportWriter.ToMarkdown(SampleReport());

            Assert.Contains("# LWhisper DevTools", md);
            Assert.Contains("ggml-large-v3-turbo.bin", md);
            Assert.Contains("ctx-floor-ab", md);
            Assert.Contains("## Итог", md);
            Assert.Contains("## По плечам", md);
            Assert.Contains("## Текст по файлам", md);
            Assert.Contains("ctx=448,threads=auto,beam=false", md);
            // Символ | внутри текста обязан быть экранирован, иначе таблица разъезжается
            Assert.Contains(@"\|", md);
        }

        [Fact]
        public void ToMarkdown_WithBaseline_RendersBothVersions()
        {
            var report = SampleReport();
            report.Summary.BaselineDiff = new BaselineDiff
            {
                BaselineFile = @"C:\old\report.json",
                TextMismatches = 1,
                MismatchFiles = { "short-01.wav" },
                Mismatches = { new BaselineTextMismatch { File = "short-01.wav", BaselineText = "было", CurrentText = "стало" } },
                TailRateDelta = -0.55,
                P10DeltaMs = 355
            };

            var md = ReportWriter.ToMarkdown(report);

            Assert.Contains("## Расхождения текста", md);
            Assert.Contains("было", md);
            Assert.Contains("стало", md);
        }

        [Theory]
        [InlineData("both", true, true)]
        [InlineData("json", true, false)]
        [InlineData("md", false, true)]
        public void Write_HonoursFormat(string format, bool expectJson, bool expectMd)
        {
            var (jsonPath, mdPath) = ReportWriter.Write(SampleReport(), _dir, format);

            Assert.Equal(expectJson, jsonPath.Length > 0 && File.Exists(jsonPath));
            Assert.Equal(expectMd, mdPath.Length > 0 && File.Exists(mdPath));
        }
    }

    public class TranscribeRunnerFormulaTests
    {
        // Зеркало формулы §5.2 скелета: max(floor, align64(ceil(dur/30*1500))), 0 = kill-switch
        [Theory]
        [InlineData(0.0, 448, 448)]
        [InlineData(3.0, 448, 448)]    // raw=150 -> align 192 -> floor побеждает
        [InlineData(5.0, 448, 448)]    // raw=250 -> align 256 -> floor побеждает
        [InlineData(10.0, 448, 512)]   // raw=500 -> align 512 -> формула побеждает
        [InlineData(15.0, 448, 768)]   // raw=750 -> align 768
        [InlineData(3.0, 0, 0)]        // kill-switch
        [InlineData(15.0, 0, 0)]       // kill-switch не зависит от длительности
        [InlineData(3.0, 256, 256)]
        [InlineData(9.0, 256, 512)]    // raw=450 -> align 512
        public void ExpectedAudioContextSize_MatchesLawFormula(double seconds, int floor, int expected)
        {
            Assert.Equal(expected, TranscribeRunner.ExpectedAudioContextSize(seconds, floor));
        }

        [Fact]
        public void ExpectedAudioContextSize_NegativeFloorIsKillSwitch()
        {
            Assert.Equal(0, TranscribeRunner.ExpectedAudioContextSize(5.0, -1));
        }

        // Полная запись сессии (CP1) в корпус свипа не берётся: 600 с -> ctx 30016 -> fallback + часы прогона
        [Theory]
        [InlineData(@"C:\dump\20260811-101500\session.wav", false)]
        [InlineData(@"C:\dump\20260811-101500\SESSION.WAV", false)]
        [InlineData(@"C:\dump\20260811-101500\seg-0001.wav", true)]
        [InlineData(@"C:\corpus\my-session.wav", true)]
        public void IsCorpusCandidate_ExcludesOnlySessionWav(string path, bool expected)
        {
            Assert.Equal(expected, TranscribeRunner.IsCorpusCandidate(path));
        }
    }
}
