using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Serilog;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Найденное обновление: релиз новее текущей версии + его ZIP-ассет
    /// </summary>
    public sealed record UpdateInfo(Version Version, GitHubRelease Release, GitHubAsset ZipAsset);

    /// <summary>
    /// Оркестрация обновления: проверка → скачивание → верификация SHA256 (fail-closed) → запуск апдейтера.
    /// Живёт только в UI.WPF (Windows-дистрибуция) — Core/SpeechEngine не затрагивает.
    /// </summary>
    public sealed class UpdateService
    {
        private readonly HttpClient _http;
        private readonly HttpClient _downloadHttp;
        private readonly GitHubUpdateClient _client;
        private readonly FileDownloader _downloader;

        public UpdateService()
        {
            // API-клиент: короткий таймаут (30с) — метаданные маленькие
            _http = new HttpClient();
            GitHubUpdateClient.ConfigureHttpClient(_http);
            _client = new GitHubUpdateClient(_http);

            // Клиент скачивания: HttpClient.Timeout покрывает ЧТЕНИЕ ТЕЛА даже при
            // ResponseHeadersRead — 30с убили бы ~100МБ ZIP на медленном канале.
            // 30 минут хватает даже на ~1 Мбит/с.
            _downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _downloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("LWhisper-Updater/1.0");
            _downloader = new FileDownloader(_downloadHttp);
        }

        /// <summary>Текущая версия приложения (3 компоненты: 1.2.3)</summary>
        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
            }
        }

        /// <summary>
        /// Dev-сборка (0.0.0 из bin/Release): авто-проверка и установка обновлений отключаются,
        /// чтобы рабочая копия разработчика не предлагала «обновиться» до релиза
        /// </summary>
        public static bool IsDevBuild => CurrentVersion is { Major: 0, Minor: 0, Build: 0 };

        /// <summary>
        /// Проверить наличие новой версии. null — обновлений нет (или релизов ещё нет).
        /// Rate-limit/сеть — UpdateCheckException (НЕ означает «обновлений нет»).
        /// </summary>
        public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            var release = await _client.GetLatestReleaseAsync(ct);
            if (release == null || release.Draft || release.Prerelease)
            {
                return null;
            }

            var remote = ReleaseAssetSelector.ParseVersion(release.TagName);
            if (remote == null)
            {
                Log.Warning("Не удалось распарсить версию из тега релиза: {Tag}", release.TagName);
                return null;
            }

            if (remote <= CurrentVersion)
            {
                Log.Debug("Обновлений нет: текущая {Current}, последняя {Remote}", CurrentVersion, remote);
                return null;
            }

            var zip = ReleaseAssetSelector.FindZip(release);
            if (zip == null)
            {
                Log.Warning("Релиз {Tag} без ZIP-ассета — пропуск", release.TagName);
                return null;
            }

            Log.Information("Доступно обновление: {Current} → {Remote}", CurrentVersion, remote);
            return new UpdateInfo(remote, release, zip);
        }

        /// <summary>
        /// Скачать ZIP обновления в %APPDATA%\LWhisper\updates\&lt;ver&gt;\ и проверить SHA256.
        /// Fail-closed: нет ни digest у ассета, ни SHA256SUMS.txt → отказ.
        /// Возвращает путь к проверенному ZIP.
        /// </summary>
        public async Task<string> DownloadAsync(UpdateInfo update,
            IProgress<(long read, long total)>? progress = null, CancellationToken ct = default)
        {
            var dir = Path.Combine(UpdateApplier.UpdatesDir, update.Version.ToString());
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, update.ZipAsset.Name);

            Log.Information("Скачивание обновления {Version}: {Url}", update.Version, update.ZipAsset.BrowserDownloadUrl);
            await _downloader.DownloadFileAsync(update.ZipAsset.BrowserDownloadUrl, zipPath, progress, ct);

            // Ожидаемый SHA256: сначала digest ассета, затем SHA256SUMS.txt из релиза
            string? expectedHex = null;
            if (!string.IsNullOrEmpty(update.ZipAsset.Digest) &&
                update.ZipAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                expectedHex = update.ZipAsset.Digest.Substring("sha256:".Length);
            }
            else
            {
                var sums = ReleaseAssetSelector.FindSha256Sums(update.Release);
                if (sums != null)
                {
                    try
                    {
                        var sumsContent = await _downloadHttp.GetStringAsync(sums.BrowserDownloadUrl, ct);
                        expectedHex = FileDownloader.ParseSha256Sums(sumsContent, update.ZipAsset.Name);
                    }
                    catch
                    {
                        // Хеши недоступны → непроверяемый ZIP не оставляем на диске
                        TryDelete(zipPath);
                        throw;
                    }
                }
            }

            if (string.IsNullOrEmpty(expectedHex))
            {
                TryDelete(zipPath);
                throw new InvalidOperationException(
                    "У релиза нет SHA256 (ни digest, ни SHA256SUMS.txt) — установка отклонена (fail-closed).");
            }

            if (!await FileDownloader.VerifySha256Async(zipPath, expectedHex, ct))
            {
                TryDelete(zipPath);
                throw new InvalidOperationException(
                    "SHA256 скачанного файла не совпал — файл повреждён или подменён. Установка отклонена.");
            }

            Log.Information("SHA256 обновления {Version} проверен успешно", update.Version);
            return zipPath;
        }

        /// <summary>
        /// Запустить применение обновления: копия текущего exe стартует в updater-режиме,
        /// приложение завершается. При любой проблеме ДО Shutdown — исключение, приложение живёт дальше.
        /// </summary>
        public void LaunchUpdater(string zipPath, Version targetVersion)
        {
            if (IsDevBuild)
            {
                throw new InvalidOperationException(
                    "Dev-сборка (0.0.0) обновляться не может — только релизные single-file сборки.");
            }

            // BLOCKER-фикс спеки: только ProcessPath. AppContext.BaseDirectory у single-file
            // указывает на %TEMP%-распаковку — затёрли бы не ту папку.
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не удалось определить путь текущего exe");
            var installDir = Path.GetDirectoryName(processPath)
                ?? throw new InvalidOperationException("Не удалось определить папку установки");

            // F7: проверка записываемости ДО выхода из приложения
            var probeFile = Path.Combine(installDir, ".write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probeFile, "");
                File.Delete(probeFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Нет прав на запись в папку установки ({installDir}). " +
                    "Переместите LWhisper в папку пользователя (например, Documents) и повторите.", ex);
            }

            // Копия exe в %LOCALAPPDATA% — апдейтер не может работать из папки, которую сам заменяет
            Directory.CreateDirectory(UpdateApplier.UpdaterDir);
            var updaterExe = Path.Combine(UpdateApplier.UpdaterDir, "LWhisper.UI.WPF.exe");
            File.Copy(processPath, updaterExe, overwrite: true);

            UpdateApplier.SaveState(new UpdateState
            {
                Stage = "starting",
                InstallDir = installDir,
                ZipPath = zipPath,
                Version = targetVersion.ToString(),
                StartedUtc = DateTime.UtcNow
            });

            var pid = Environment.ProcessId;
            Log.Information("Запуск апдейтера: {Updater} → установка {Version} в {Dir}", updaterExe, targetVersion, installDir);
            Log.CloseAndFlush();

            Process.Start(new ProcessStartInfo(updaterExe)
            {
                ArgumentList = { UpdateApplier.ApplyUpdateArg, installDir, zipPath, pid.ToString(), targetVersion.ToString() },
                WorkingDirectory = UpdateApplier.UpdaterDir,
                UseShellExecute = false
            });

            Application.Current.Shutdown();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
