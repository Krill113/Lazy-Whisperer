using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// CP4 (C6): маркеры галлюцинаций Whisper — два тира фильтра
    /// SegmentRecognitionManager.IsKnownHallucination.
    /// Substring-тир срабатывает в любом месте сегмента (уверенные бренд-маркеры),
    /// full-match-тир — только если сегмент состоит ровно из фразы (защита реальной речи).
    /// </summary>
    public class HallucinationFilterTests
    {
        // ---------- НОВЫЕ substring-маркеры (C6, источник whisper-type) ----------

        [Theory]
        [InlineData("Субтитры делал")]
        [InlineData("Редактор субтитров")]
        [InlineData("Amara.org")]
        [InlineData("Subtitles by")]
        [InlineData("Переведено сообществом")]
        [InlineData("Thank you for watching")]
        public void NewSubstringMarker_IsDetectedAnywhereInSegment(string marker)
        {
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker),
                $"маркер \"{marker}\" не пойман как самостоятельный сегмент");
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("Начало текста " + marker + " и хвост."),
                $"маркер \"{marker}\" не пойман внутри текста");
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker.ToLowerInvariant()),
                $"маркер \"{marker}\" не пойман в нижнем регистре");
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker.ToUpperInvariant()),
                $"маркер \"{marker}\" не пойман в верхнем регистре");
        }

        [Fact]
        public void NewSubstringMarker_RealWorldSamples_AreDetected()
        {
            // Формы, в которых боилерплейт реально приходит из YouTube-corpus.
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("Редактор субтитров А.Семкин"));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("Субтитры делал Иван Петров"));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("Subtitles by the Amara.org community"));
        }

        // ---------- НОВЫЕ full-match-маркеры (C6) ----------

        [Theory]
        [InlineData("До новых встреч")]
        [InlineData("Ставьте лайк")]
        public void NewFullMatchMarker_IsDetectedAsWholeSegment(string marker)
        {
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker + "."));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("  " + marker + "!  "));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker.ToLowerInvariant()));
        }

        [Theory]
        [InlineData("До новых встреч на объекте, я записал отметки")]
        [InlineData("Ставьте лайк на чертеже в правом верхнем углу")]
        public void NewFullMatchMarker_InsideRealSpeech_Survives(string text)
        {
            Assert.False(SegmentRecognitionManager.IsKnownHallucination(text),
                "full-match-маркер не имеет права дропать сегмент, в котором есть другая речь");
        }

        // ---------- Контрольная фраза (стоп-условие CP4 из спеки §3.6) ----------

        [Fact]
        public void ControlPhrase_SurvivesCompletely()
        {
            // Обе половины фразы совпадают с full-match-маркерами по отдельности,
            // но сегмент содержит реальную речь — дропать его нельзя.
            Assert.False(SegmentRecognitionManager.IsKnownHallucination(
                "спасибо за просмотр чертежа, продолжение следует в следующем листе"));
        }

        // ---------- Регрессия: существующие маркеры не ослаблены ----------

        [Theory]
        [InlineData("DimaTorzok")]
        [InlineData("Субтитры создавал")]
        [InlineData("Субтитры подготовил")]
        [InlineData("Субтитры сделал")]
        [InlineData("Субтитры выполнил")]
        [InlineData("Корректор субтитров")]
        [InlineData("Подписывайтесь на канал")]
        [InlineData("Like and subscribe")]
        public void ExistingSubstringMarker_StillDetectedAnywhere(string marker)
        {
            Assert.True(SegmentRecognitionManager.IsKnownHallucination("Надо доделать чертёж. " + marker + " и всё."),
                $"существующий substring-маркер \"{marker}\" перестал ловиться внутри текста — это ослабление, запрещённое спекой");
        }

        [Theory]
        [InlineData("Спасибо за просмотр")]
        [InlineData("Продолжение следует")]
        [InlineData("Спасибо за внимание")]
        [InlineData("Thanks for watching")]
        public void ExistingFullMatchMarker_StillDetectedAsWholeSegment(string marker)
        {
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker));
            Assert.True(SegmentRecognitionManager.IsKnownHallucination(marker + "."));
        }

        [Theory]
        [InlineData("Спасибо за просмотр кода, тут всё понятно")]
        [InlineData("Продолжение следует по разрезу два-два")]
        [InlineData("Спасибо за внимание к отметке низа трубы")]
        public void ExistingFullMatchMarker_InsideRealSpeech_Survives(string text)
        {
            Assert.False(SegmentRecognitionManager.IsKnownHallucination(text));
        }

        // ---------- Реальная инженерная диктовка и пустой ввод ----------

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Отметка низа трубы двенадцать пятьдесят")]
        [InlineData("Пикет двенадцать плюс сорок, колодец КК-3")]
        [InlineData("Редактор проекта согласовал отметки")]
        public void RealSpeechAndEmptyInput_AreNotDetected(string text)
        {
            Assert.False(SegmentRecognitionManager.IsKnownHallucination(text));
        }
    }
}
