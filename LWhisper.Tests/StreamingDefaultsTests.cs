using System.Text.Json;
using LWhisper.Core.Models;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>Дефолты новой установки и сохранение пользовательских параметров при чтении JSON.</summary>
    public class StreamingDefaultsTests
    {
        /// <summary>Новая установка получает проверенную пару максимума сегмента и паузы.</summary>
        [Fact]
        public void NewInstallation_UsesLongerSegmentsAndShortPause()
        {
            var settings = new AppSettings();

            Assert.Equal(25000, settings.Streaming.MaxSegmentDurationMs);
            Assert.Equal(800, settings.Streaming.PauseThresholdMs);
        }

        /// <summary>Отсутствующие поля старого файла дополняются новыми дефолтами.</summary>
        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Streaming\":{\"Enabled\":true}}")]
        public void MissingSegmentationFields_UseDefaults(string json)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

            Assert.Equal(25000, settings.Streaming.MaxSegmentDurationMs);
            Assert.Equal(800, settings.Streaming.PauseThresholdMs);
        }

        /// <summary>Обновление не переписывает явно сохранённые параметры пользователя.</summary>
        [Theory]
        [InlineData(15000, 1500)]
        [InlineData(12000, 2200)]
        public void SavedSegmentationSettings_ArePreserved(int maximum, int pause)
        {
            var original = new AppSettings();
            original.Streaming.MaxSegmentDurationMs = maximum;
            original.Streaming.PauseThresholdMs = pause;
            original.Streaming.Enabled = true;

            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!;

            Assert.Equal(maximum, restored.Streaming.MaxSegmentDurationMs);
            Assert.Equal(pause, restored.Streaming.PauseThresholdMs);
            Assert.True(restored.Streaming.Enabled);
        }
    }
}
