namespace LWhisper.Core.Models
{
    /// <summary>
    /// Метаданные модели Whisper
    /// </summary>
    public record WhisperModelInfo(
        string Id,
        string DisplayName,
        string FileName,
        long FileSizeMB,
        long EstimatedRamMB,
        bool IsQuantized,
        string? QuantizationType,
        int SortOrder
    );
}

// Shim: required for positional records on netstandard2.1 with C# 9
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
