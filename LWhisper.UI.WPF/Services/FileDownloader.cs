using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Скачивание файлов с проверкой целостности: качаем в .part, сверяем Content-Length,
    /// атомарно переименовываем. Плюс проверка SHA256 (fail-closed для апдейтов).
    /// </summary>
    public sealed class FileDownloader
    {
        private readonly HttpClient _http;

        public FileDownloader(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>
        /// Скачивает url в destPath через временный .part-файл.
        /// Обрыв (скачано меньше Content-Length) — IOException, .part удаляется.
        /// </summary>
        public async Task DownloadFileAsync(string url, string destPath,
            IProgress<(long read, long total)>? progress = null, CancellationToken ct = default)
        {
            var partPath = destPath + ".part";
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0L;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, useAsync: true);
                var buf = new byte[65536];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report((read, total));
                }
                if (total > 0 && read != total)
                {
                    throw new IOException($"Скачано {read} из {total} байт — файл неполный (обрыв связи?)");
                }
                await dst.FlushAsync(ct);
            }
            catch
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* best effort */ }
                throw;
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partPath, destPath);
        }

        /// <summary>
        /// Сверка SHA256 файла с ожидаемым hex-значением (БЕЗ префикса "sha256:")
        /// </summary>
        public static async Task<bool> VerifySha256Async(string filePath, string expectedHex, CancellationToken ct = default)
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            var hash = await SHA256.HashDataAsync(fs, ct);
            return string.Equals(Convert.ToHexString(hash), expectedHex.Trim().ToUpperInvariant(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Достаёт hex-хеш для файла fileName из содержимого SHA256SUMS.txt
        /// (формат строки: "&lt;hex&gt;  &lt;имя файла&gt;"). null если строка не найдена.
        /// </summary>
        public static string? ParseSha256Sums(string sumsContent, string fileName)
        {
            foreach (var rawLine in sumsContent.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                var space = line.IndexOf(' ');
                if (space <= 0) continue;

                var hash = line.Substring(0, space).Trim();
                var name = line.Substring(space).Trim().TrimStart('*'); // '*' — бинарный маркер sha256sum

                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                    && hash.Length == 64)
                {
                    return hash;
                }
            }
            return null;
        }
    }
}
