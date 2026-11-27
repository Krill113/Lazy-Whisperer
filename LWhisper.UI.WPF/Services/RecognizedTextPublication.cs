using LWhisper.Core.Text;

namespace LWhisper.UI.WPF.Services;

/// <summary>Проекция исходного текста со снимком правил на одну запись.</summary>
internal sealed class RecognizedTextPublication(ReplacementDictionary snapshot)
{
    private string? _original;
    private string _presented = "";

    public bool CanPresent { get; private set; } = true;

    public void DismissPreview() => CanPresent = false;

    public void PresentIfAllowed(Action present)
    {
        if (CanPresent) present();
    }

    public string Publish(string assembled)
    {
        if (assembled != _original)
        {
            _presented = snapshot.Apply(assembled);
            _original = assembled;
        }
        return _presented;
    }
}
