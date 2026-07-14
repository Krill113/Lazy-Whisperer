using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LWhisper.UI.WPF.Services;
using Xunit;

namespace LWhisper.Tests
{
    /// <summary>
    /// Стаб HttpMessageHandler: отдаёт заданный контент, опционально врёт про Content-Length
    /// </summary>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly long? _contentLengthOverride;

        public StubHandler(byte[] content, long? contentLengthOverride = null)
        {
            _content = content;
            _contentLengthOverride = contentLengthOverride;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content)
            };
            if (_contentLengthOverride.HasValue)
            {
                resp.Content.Headers.ContentLength = _contentLengthOverride.Value;
            }
            return Task.FromResult(resp);
        }
    }

    public class FileDownloaderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lwhisper-tests-" + Guid.NewGuid().ToString("N"));

        public FileDownloaderTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task DownloadFileAsync_Success_WritesFileAndRemovesPart()
        {
            var payload = Encoding.UTF8.GetBytes("hello update");
            var downloader = new FileDownloader(new HttpClient(new StubHandler(payload)));
            var dest = Path.Combine(_dir, "file.zip");

            await downloader.DownloadFileAsync("http://localhost/fake.zip", dest);

            Assert.True(File.Exists(dest));
            Assert.False(File.Exists(dest + ".part"));
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        }

        [Fact]
        public async Task DownloadFileAsync_ContentLengthMismatch_ThrowsAndCleansPart()
        {
            var payload = Encoding.UTF8.GetBytes("short");
            // Сервер обещает 999 байт, а отдаёт 5 — обрыв связи
            var downloader = new FileDownloader(new HttpClient(new StubHandler(payload, contentLengthOverride: 999)));
            var dest = Path.Combine(_dir, "file.zip");

            await Assert.ThrowsAsync<IOException>(() => downloader.DownloadFileAsync("http://localhost/fake.zip", dest));

            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".part"));
        }

        [Fact]
        public async Task VerifySha256Async_CorrectHash_ReturnsTrue()
        {
            var file = Path.Combine(_dir, "data.bin");
            var payload = Encoding.UTF8.GetBytes("integrity matters");
            await File.WriteAllBytesAsync(file, payload);
            var expected = Convert.ToHexString(SHA256.HashData(payload));

            Assert.True(await FileDownloader.VerifySha256Async(file, expected));
            Assert.True(await FileDownloader.VerifySha256Async(file, expected.ToLowerInvariant()));
        }

        [Fact]
        public async Task VerifySha256Async_WrongHash_ReturnsFalse()
        {
            var file = Path.Combine(_dir, "data.bin");
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("integrity matters"));

            Assert.False(await FileDownloader.VerifySha256Async(file, new string('a', 64)));
        }

        [Theory]
        [InlineData("abc123  LWhisper-win-x64-v1.0.0.zip", "LWhisper-win-x64-v1.0.0.zip", null)] // хеш не 64 символа — игнор
        [InlineData("", "any.zip", null)]
        public void ParseSha256Sums_EdgeCases(string content, string fileName, string? expected)
        {
            Assert.Equal(expected, FileDownloader.ParseSha256Sums(content, fileName));
        }

        [Fact]
        public void ParseSha256Sums_FindsHashByFileName()
        {
            var hash = new string('f', 64);
            var content = $"{new string('0', 64)}  other.zip\n{hash}  LWhisper-win-x64-v1.2.3.zip\n";

            Assert.Equal(hash, FileDownloader.ParseSha256Sums(content, "LWhisper-win-x64-v1.2.3.zip"));
            Assert.Equal(hash, FileDownloader.ParseSha256Sums(content, "lwhisper-WIN-x64-V1.2.3.ZIP")); // регистронезависимо
            Assert.Null(FileDownloader.ParseSha256Sums(content, "missing.zip"));
        }

        [Fact]
        public void ParseSha256Sums_BinaryMarkerAsterisk_Stripped()
        {
            var hash = new string('e', 64);
            Assert.Equal(hash, FileDownloader.ParseSha256Sums($"{hash} *LWhisper-win-x64-v1.0.0.zip", "LWhisper-win-x64-v1.0.0.zip"));
        }
    }

    public class ReleaseModelTests
    {
        [Theory]
        [InlineData("v1.2.3", "1.2.3")]
        [InlineData("V2.0.0", "2.0.0")]
        [InlineData("1.0.0", "1.0.0")]
        [InlineData(" v1.0.1 ", "1.0.1")]
        [InlineData("v1.1", "1.1.0")]     // нормализация до 3 компонент — иначе ломается handshake
        [InlineData("v1.2.3.4", "1.2.3")] // 4-я компонента отбрасывается
        public void ParseVersion_ValidTags(string tag, string expected)
        {
            Assert.Equal(Version.Parse(expected), ReleaseAssetSelector.ParseVersion(tag));
        }

        [Theory]
        [InlineData("latest")]
        [InlineData("v1")]
        [InlineData("")]
        [InlineData("release-1.0")]
        public void ParseVersion_InvalidTags_ReturnsNull(string tag)
        {
            Assert.Null(ReleaseAssetSelector.ParseVersion(tag));
        }

        [Fact]
        public void GitHubRelease_DeserializesFromApiSample()
        {
            const string json = """
            {
              "tag_name": "v1.0.0",
              "name": "LWhisper v1.0.0",
              "body": "Release notes",
              "prerelease": false,
              "draft": false,
              "html_url": "https://github.com/Krill113/Lazy-Whisperer/releases/tag/v1.0.0",
              "assets": [
                {
                  "name": "LWhisper-win-x64-v1.0.0.zip",
                  "size": 12345,
                  "state": "uploaded",
                  "browser_download_url": "https://github.com/Krill113/Lazy-Whisperer/releases/download/v1.0.0/LWhisper-win-x64-v1.0.0.zip",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "size": 100,
                  "state": "uploaded",
                  "browser_download_url": "https://github.com/Krill113/Lazy-Whisperer/releases/download/v1.0.0/SHA256SUMS.txt",
                  "digest": null
                }
              ]
            }
            """;

            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            Assert.NotNull(release);
            Assert.Equal("v1.0.0", release.TagName);
            Assert.False(release.Prerelease);
            Assert.Equal(2, release.Assets.Length);

            var zip = ReleaseAssetSelector.FindZip(release);
            Assert.NotNull(zip);
            Assert.Equal("LWhisper-win-x64-v1.0.0.zip", zip.Name);
            Assert.StartsWith("sha256:", zip.Digest);

            var sums = ReleaseAssetSelector.FindSha256Sums(release);
            Assert.NotNull(sums);
            Assert.Equal("SHA256SUMS.txt", sums.Name);
        }

        [Fact]
        public void FindZip_IgnoresNonUploadedAndForeignAssets()
        {
            var release = new GitHubRelease("v1.0.0", null, null, false, false, "url", new[]
            {
                new GitHubAsset("LWhisper-win-x64-v1.0.0.zip", 1, "starter", "url", null), // не докачан на GitHub
                new GitHubAsset("SHA256SUMS.txt", 1, "uploaded", "url", null),
                new GitHubAsset("LWhisper-linux-arm64-v1.0.0.tar.gz", 1, "uploaded", "url", null)
            });

            Assert.Null(ReleaseAssetSelector.FindZip(release));
        }
    }

    public class UpdateStateTests
    {
        [Fact]
        public void UpdateState_RoundTripsThroughJson()
        {
            var state = new UpdateState
            {
                Stage = "backup",
                InstallDir = @"C:\Apps\LWhisper",
                ZipPath = @"C:\Users\x\AppData\Roaming\LWhisper\updates\1.0.0\LWhisper-win-x64-v1.0.0.zip",
                BackupDir = @"C:\Apps\LWhisper_backup_20260714-120000",
                Version = "1.0.0",
                StartedUtc = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)
            };

            var restored = JsonSerializer.Deserialize<UpdateState>(JsonSerializer.Serialize(state));

            Assert.NotNull(restored);
            Assert.Equal(state.Stage, restored.Stage);
            Assert.Equal(state.InstallDir, restored.InstallDir);
            Assert.Equal(state.Version, restored.Version);
            Assert.Equal(state.StartedUtc, restored.StartedUtc);
        }
    }
}
