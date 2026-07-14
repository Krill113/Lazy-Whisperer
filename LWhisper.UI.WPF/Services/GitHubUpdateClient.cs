using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Релиз GitHub (подмножество полей API releases/latest)
    /// </summary>
    public sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")]   string TagName,
        [property: JsonPropertyName("name")]       string? Name,
        [property: JsonPropertyName("body")]       string? Body,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")]      bool Draft,
        [property: JsonPropertyName("html_url")]   string HtmlUrl,
        [property: JsonPropertyName("assets")]     GitHubAsset[] Assets);

    /// <summary>
    /// Файл-ассет релиза GitHub
    /// </summary>
    public sealed record GitHubAsset(
        [property: JsonPropertyName("name")]                 string Name,
        [property: JsonPropertyName("size")]                 long Size,
        [property: JsonPropertyName("state")]                string State,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")]               string? Digest);

    /// <summary>
    /// Выбор нужных ассетов из релиза. Имена согласованы с CI (release.yml):
    /// ZIP = LWhisper-win-x64-vX.Y.Z.zip, хеши = SHA256SUMS.txt
    /// </summary>
    public static class ReleaseAssetSelector
    {
        public static GitHubAsset? FindZip(GitHubRelease r) =>
            r.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("LWhisper-win-x64", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && a.State == "uploaded");

        public static GitHubAsset? FindSha256Sums(GitHubRelease r) =>
            r.Assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                && a.State == "uploaded");

        /// <summary>
        /// Версия из тега релиза: "v1.2.3" → 1.2.3. null если тег не парсится.
        /// Всегда нормализуется до РОВНО трёх компонент — тот же формат, что
        /// UpdateService.CurrentVersion, иначе handshake-сравнение версий ломается
        /// на тегах вида v1.1 или v1.2.3.4.
        /// </summary>
        public static Version? ParseVersion(string tagName)
        {
            var s = tagName.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(1);
            }
            if (!Version.TryParse(s, out var v))
            {
                return null;
            }
            return new Version(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
        }
    }

    /// <summary>
    /// Проверка обновлений не удалась по причине, которую НЕЛЬЗЯ трактовать как «обновлений нет»
    /// (rate-limit GitHub, сетевая ошибка, неожиданный ответ)
    /// </summary>
    public sealed class UpdateCheckException : Exception
    {
        public bool RateLimited { get; }

        public UpdateCheckException(string message, bool rateLimited = false, Exception? inner = null)
            : base(message, inner)
        {
            RateLimited = rateLimited;
        }
    }

    /// <summary>
    /// Клиент GitHub Releases API для проверки обновлений
    /// </summary>
    public sealed class GitHubUpdateClient
    {
        public const string RepoOwner = "Krill113";
        public const string RepoName = "Lazy-Whisperer";
        private const string LatestReleaseUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

        private readonly HttpClient _http;

        public GitHubUpdateClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>
        /// Настроить обязательные для GitHub API заголовки на HttpClient
        /// </summary>
        public static void ConfigureHttpClient(HttpClient http)
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LWhisper-Updater/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            http.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Последний опубликованный релиз (не draft, не prerelease — семантика releases/latest).
        /// null — релизов ещё нет (404). Rate-limit и сетевые ошибки — UpdateCheckException.
        /// </summary>
        public async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken ct = default)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(LatestReleaseUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // отмена вызывающим — не «сетевая ошибка»
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new UpdateCheckException($"Не удалось обратиться к GitHub: {ex.Message}", inner: ex);
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    return null; // релизов ещё нет — это не ошибка
                }

                // Rate-limit (60 запросов/час на IP анонимно) отличаем от «нет обновлений»
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    throw new UpdateCheckException(
                        "GitHub ограничил частоту запросов — повторите проверку позже.", rateLimited: true);
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new UpdateCheckException($"GitHub вернул {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }

                var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
                if (release == null)
                {
                    throw new UpdateCheckException("Пустой ответ GitHub при запросе релиза");
                }
                return release;
            }
        }
    }
}
