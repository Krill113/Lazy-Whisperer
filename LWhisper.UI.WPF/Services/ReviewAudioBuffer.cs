using System.IO;

namespace LWhisper.UI.WPF.Services;

/// <summary>Одноразовый снимок всей диктовки до VAD. При переполнении не возвращает обрезанную запись.</summary>
internal sealed class ReviewAudioBuffer(int maxBytes)
{
    private readonly object _sync = new();
    private MemoryStream? _pcm = new();

    internal void Append(byte[] data, int count)
    {
        lock (_sync)
        {
            if (_pcm == null) return;
            if (count <= maxBytes - _pcm.Length) _pcm.Write(data, 0, count);
            else { _pcm.Dispose(); _pcm = null; }
        }
    }

    internal byte[] Take()
    {
        lock (_sync)
        {
            var data = _pcm?.ToArray() ?? Array.Empty<byte>();
            _pcm?.Dispose();
            _pcm = null;
            return data;
        }
    }
}
