using LWhisper.Core.Text;
using Xunit;
namespace LWhisper.Tests;
public class DictationConsensusTests
{
    [Theory]
    [InlineData("Пятая фраза записалась.", "Эта фраза записалась.", "эта фраза записалась", "Эта фраза записалась.")]
    [InlineData("Сначала проверим описку, потом отписку.", "Сначала проверим отписку, потом отписку.", "Сначала проверим отписку, потом отписку.", "Сначала проверим описку, потом отписку.")]
    [InlineData("Подожди. Пока ничего не меняй.", "Подожди, подожди! Пока ничего не меняй!", "подожди, подожди, пока ничего не меняй", "Подожди, подожди. Пока ничего не меняй.")]
    [InlineData("Это не новый файл.", "Это новый файл.", "Это новый файл.", "Это не новый файл.")]
    [InlineData("Импорт из Civil 16 ГБ.", "Импорт из цивил 60 ГБ.", "Импорт из цивил 60 ГБ.", "Импорт из Civil 16 ГБ.")]
    [InlineData("Открой окно. Открой окно.", "Открой окно.", "Открой окно. Открой окно.", "Открой окно. Открой окно.")]
    [InlineData("Пятая фраза записалась.", "Эта фраза записалась.", "Пятая фраза записалась.", "Пятая фраза записалась.")]
    [InlineData("Проверяй \"новый проект.txt\" сегодня.", "Проверяй \"старый проект.txt\" сегодня.", "Проверяй старый проект сегодня.", "Проверяй \"новый проект.txt\" сегодня.")]
    public void KeepsUnrelatedContent(string original, string turbo, string witness, string expected)
        => Assert.Equal(expected, DictationConsensus.Apply(original, turbo, witness));

    [Fact]
    public void RemovesOnlyDuplicateSupportedByBothReads()
    {
        const string block = "цвет панели зависит от выбранного значения в настройках проекта";
        var intended = $"Сначала {block}, затем проверяем результат работы.";
        var duplicated = $"Сначала {block}, {block}, затем проверяем результат работы.";
        Assert.Equal(intended, DictationConsensus.Apply(duplicated, intended, intended));
        Assert.Equal(duplicated, DictationConsensus.Apply(duplicated, duplicated, intended));
    }
}
