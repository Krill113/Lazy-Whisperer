using System;
using System.Linq;
using LWhisper.DevTools.Mcp;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// CP3: валидация и биндинг входа MCP-инструментов (McpArgs).
    /// Транспорт и native-распознавание не тестируются — только чистая логика.
    /// </summary>
    public class McpToolArgsTests
    {
        // --- NormalizeLanguage ---

        [Fact]
        public void NormalizeLanguage_Null_ДаётДефолтRu()
        {
            Assert.Equal("ru", McpArgs.NormalizeLanguage(null));
        }

        [Theory]
        [InlineData("  RU  ", "ru")]
        [InlineData("En", "en")]
        [InlineData("AUTO", "auto")]
        public void NormalizeLanguage_Нормализует(string input, string expected)
        {
            Assert.Equal(expected, McpArgs.NormalizeLanguage(input));
        }

        [Fact]
        public void NormalizeLanguage_НеизвестныйЯзык_Бросает()
        {
            Assert.Throws<ArgumentException>(() => McpArgs.NormalizeLanguage("de"));
        }

        // --- NormalizeThreadMode ---

        [Fact]
        public void NormalizeThreadMode_Пусто_ДаётNull()
        {
            Assert.Null(McpArgs.NormalizeThreadMode(null));
            Assert.Null(McpArgs.NormalizeThreadMode("   "));
        }

        [Theory]
        [InlineData("Legacy", "legacy")]
        [InlineData(" DIVIDED ", "divided")]
        public void NormalizeThreadMode_Нормализует(string input, string expected)
        {
            Assert.Equal(expected, McpArgs.NormalizeThreadMode(input));
        }

        [Fact]
        public void NormalizeThreadMode_НеизвестныйРежим_Бросает()
        {
            Assert.Throws<ArgumentException>(() => McpArgs.NormalizeThreadMode("turbo"));
        }

        // --- EffectiveMaxRuns ---

        [Fact]
        public void EffectiveMaxRuns_Null_Даёт200()
        {
            Assert.Equal(200, McpArgs.EffectiveMaxRuns(null));
            Assert.Equal(200, McpArgs.DefaultMaxRuns);
        }

        [Fact]
        public void EffectiveMaxRuns_ЯвноеЗначение_Уважается()
        {
            Assert.Equal(12, McpArgs.EffectiveMaxRuns(12));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void EffectiveMaxRuns_НеположительноеЗначение_Бросает(int maxRuns)
        {
            Assert.Throws<ArgumentException>(() => McpArgs.EffectiveMaxRuns(maxRuns));
        }

        // --- CountRuns ---

        [Fact]
        public void CountRuns_ВсеИзмеренияЗаданы_Перемножает()
        {
            var runs = McpArgs.CountRuns(
                fileCount: 3,
                ctxFloors: new[] { 0, 448 },
                threads: new[] { 4, 6, 8 },
                beam: new[] { false, true },
                repeat: 2);

            Assert.Equal(3 * 2 * 3 * 2 * 2, runs);
        }

        [Fact]
        public void CountRuns_ПустыеИзмерения_СчитаютсяЗаОдно()
        {
            Assert.Equal(5, McpArgs.CountRuns(5, null, null, null, null));
            Assert.Equal(5, McpArgs.CountRuns(5, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<bool>(), 1));
        }

        [Fact]
        public void CountRuns_НетФайлов_Бросает()
        {
            Assert.Throws<ArgumentException>(() => McpArgs.CountRuns(0, null, null, null, null));
        }

        [Fact]
        public void CountRuns_НеположительныйRepeat_Бросает()
        {
            Assert.Throws<ArgumentException>(() => McpArgs.CountRuns(1, null, null, null, 0));
        }

        // --- Csv ---

        [Fact]
        public void Csv_Целые_БезПробеловИнвариантно()
        {
            Assert.Equal("0,256,448", McpArgs.Csv(new[] { 0, 256, 448 }));
        }

        [Fact]
        public void Csv_Булевы_ВНижнемРегистре()
        {
            Assert.Equal("false,true", McpArgs.Csv(new[] { false, true }));
        }

        // --- BuildTranscribeArgs ---

        [Fact]
        public void BuildTranscribeArgs_Минимум_СодержитОбязательное()
        {
            var args = McpArgs.BuildTranscribeArgs(
                @"C:\corpus\a.wav", "ru", null, null, null, null, null, @"C:\out");

            Assert.Equal("transcribe", args[0]);
            AssertPair(args, "--input", @"C:\corpus\a.wav");
            AssertPair(args, "--language", "ru");
            AssertPair(args, "--out", @"C:\out");
            AssertPair(args, "--format", "json");
            Assert.Contains("--quiet", args);
            Assert.DoesNotContain("--ctx-floor", args);
            Assert.DoesNotContain("--threads", args);
            Assert.DoesNotContain("--thread-mode", args);
            Assert.DoesNotContain("--beam", args);
            Assert.DoesNotContain("--model", args);
        }

        [Fact]
        public void BuildTranscribeArgs_CtxFloorНоль_ПередаётсяЯвно()
        {
            // 0 — это kill-switch, а не «значение не задано»: он обязан доехать до CLI
            var args = McpArgs.BuildTranscribeArgs(
                @"C:\corpus\a.wav", "ru", 0, null, null, null, null, @"C:\out");

            AssertPair(args, "--ctx-floor", "0");
        }

        [Fact]
        public void BuildTranscribeArgs_ВсеПараметры_Пробрасываются()
        {
            var args = McpArgs.BuildTranscribeArgs(
                @"C:\corpus\a.wav", "auto", 448, 6, "Divided", true, "large-v3-turbo", @"C:\out");

            AssertPair(args, "--language", "auto");
            AssertPair(args, "--ctx-floor", "448");
            AssertPair(args, "--threads", "6");
            AssertPair(args, "--thread-mode", "divided");
            AssertPair(args, "--model", "large-v3-turbo");
            Assert.Contains("--beam", args);
        }

        [Fact]
        public void BuildTranscribeArgs_BeamFalse_ФлагНеДобавляется()
        {
            var args = McpArgs.BuildTranscribeArgs(
                @"C:\corpus\a.wav", "ru", null, null, null, false, null, @"C:\out");

            Assert.DoesNotContain("--beam", args);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BuildTranscribeArgs_ПустойPath_Бросает(string path)
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildTranscribeArgs(path, "ru", null, null, null, null, null, @"C:\out"));
        }

        [Fact]
        public void BuildTranscribeArgs_ОтрицательныйCtxFloor_Бросает()
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildTranscribeArgs(@"C:\a.wav", "ru", -1, null, null, null, null, @"C:\out"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        public void BuildTranscribeArgs_НеположительныеThreads_Бросает(int threads)
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildTranscribeArgs(@"C:\a.wav", "ru", null, threads, null, null, null, @"C:\out"));
        }

        [Fact]
        public void BuildTranscribeArgs_ПустойOutDir_Бросает()
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildTranscribeArgs(@"C:\a.wav", "ru", null, null, null, null, null, "  "));
        }

        // --- BuildSweepArgs ---

        [Fact]
        public void BuildSweepArgs_НесколькоФайлов_ПовторяетInput()
        {
            var args = McpArgs.BuildSweepArgs(
                new[] { @"C:\c\a.wav", @"C:\c\b.wav" }, null, null, null, null, null, 200, @"C:\out");

            Assert.Equal("sweep", args[0]);
            Assert.Equal(2, args.Count(a => a == "--input"));
            Assert.Contains(@"C:\c\a.wav", args);
            Assert.Contains(@"C:\c\b.wav", args);
            AssertPair(args, "--max-runs", "200");
            AssertPair(args, "--format", "both");
            Assert.DoesNotContain("--grid-ctx", args);
            Assert.DoesNotContain("--grid-threads", args);
            Assert.DoesNotContain("--grid-beam", args);
        }

        [Fact]
        public void BuildSweepArgs_Сетка_ПревращаетсяВCsv()
        {
            var args = McpArgs.BuildSweepArgs(
                new[] { @"C:\c\a.wav" },
                ctxFloors: new[] { 0, 256, 448 },
                threads: new[] { 4, 8 },
                beam: new[] { false, true },
                repeat: 3,
                parallel: 1,
                maxRuns: 50,
                outDir: @"C:\out");

            AssertPair(args, "--grid-ctx", "0,256,448");
            AssertPair(args, "--grid-threads", "4,8");
            AssertPair(args, "--grid-beam", "false,true");
            AssertPair(args, "--repeat", "3");
            AssertPair(args, "--parallel", "1");
            AssertPair(args, "--max-runs", "50");
        }

        [Fact]
        public void BuildSweepArgs_ПустыеПути_Бросает()
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildSweepArgs(Array.Empty<string>(), null, null, null, null, null, 200, @"C:\out"));
        }

        [Fact]
        public void BuildSweepArgs_ОтрицательныйCtxВСетке_Бросает()
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildSweepArgs(new[] { @"C:\a.wav" }, new[] { -1 }, null, null, null, null, 200, @"C:\out"));
        }

        [Fact]
        public void BuildSweepArgs_НеположительныйParallel_Бросает()
        {
            Assert.Throws<ArgumentException>(() =>
                McpArgs.BuildSweepArgs(new[] { @"C:\a.wav" }, null, null, null, null, 0, 200, @"C:\out"));
        }

        // --- McpMode.IsRequested ---

        [Fact]
        public void IsRequested_ПодкомандаMcp_ДаётTrue()
        {
            Assert.True(McpMode.IsRequested(new[] { "mcp" }));
            Assert.True(McpMode.IsRequested(new[] { "MCP", "--trace" }));
        }

        [Fact]
        public void IsRequested_ФлагMcp_ДаётTrue()
        {
            Assert.True(McpMode.IsRequested(new[] { "transcribe", "--mcp" }));
        }

        [Fact]
        public void IsRequested_ОбычныеКоманды_ДаютFalse()
        {
            Assert.False(McpMode.IsRequested(null));
            Assert.False(McpMode.IsRequested(Array.Empty<string>()));
            Assert.False(McpMode.IsRequested(new[] { "transcribe", "--input", "a.wav" }));
            Assert.False(McpMode.IsRequested(new[] { "engine-info" }));
        }

        // --- Детектор аварийного fallback в MCP-режиме ---

        /// <summary>
        /// Закон §5.4 + спека §5 правило 4: в MCP-режиме поле usedFallback обязано быть настоящим.
        /// Детектор ставится вместе с файловым логгером; если его нет, McpEngine отдаёт прогонщику
        /// fallbackWatch = null и usedFallback становится константой false — стоп-условие волны
        /// через MCP-стенд перестаёт работать.
        /// Тест трогает глобальный Log.Logger и создаёт каталог {DebugRoot}\mcp — состояние
        /// восстанавливается в finally.
        /// </summary>
        [Fact]
        public void ConfigureFileOnlyLogging_УстанавливаетДетекторFallback()
        {
            var previousLogger = Serilog.Log.Logger;
            var previousWatch = McpEngine.FallbackWatch;
            try
            {
                McpEngine.FallbackWatch = null;

                McpMode.ConfigureFileOnlyLogging();

                Assert.NotNull(McpEngine.FallbackWatch);
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
                Serilog.Log.Logger = previousLogger;
                McpEngine.FallbackWatch = previousWatch;
            }
        }

        private static void AssertPair(string[] args, string option, string value)
        {
            var index = Array.IndexOf(args, option);
            Assert.True(index >= 0, $"Опция {option} отсутствует в argv: {string.Join(" ", args)}");
            Assert.True(index + 1 < args.Length, $"У опции {option} нет значения");
            Assert.Equal(value, args[index + 1]);
        }
    }
}
