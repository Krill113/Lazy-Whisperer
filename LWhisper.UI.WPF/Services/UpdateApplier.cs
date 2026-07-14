using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LWhisper.UI.WPF.Services
{
    /// <summary>
    /// Журнал состояния обновления (%APPDATA%\LWhisper\update-state.json).
    /// По нему recovery-on-launch понимает, что апдейт завис/упал, и восстанавливает установку.
    /// </summary>
    public sealed class UpdateState
    {
        public string Stage { get; set; } = "";        // starting | backup | extracting | launching
        public string InstallDir { get; set; } = "";
        public string ZipPath { get; set; } = "";
        public string BackupDir { get; set; } = "";
        public string Version { get; set; } = "";
        public DateTime StartedUtc { get; set; }
    }

    /// <summary>
    /// Updater-режим приложения: тот же exe, запущенный из копии в %LOCALAPPDATA% с аргументами
    /// --apply-update &lt;installDir&gt; &lt;zipPath&gt; &lt;pid&gt; &lt;version&gt;.
    /// Меняет файлы установки на распакованный ZIP с backup/rollback и handshake-проверкой.
    /// Работает ДО инициализации WPF и Serilog — лог пишется в updater\update.log.
    /// </summary>
    public static class UpdateApplier
    {
        public const string ApplyUpdateArg = "--apply-update";
        private const int WaitMainExitMs = 30_000;
        private const int HandshakeTimeoutMs = 60_000;

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LWhisper");
        private static readonly string LocalAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LWhisper");

        public static string StateFilePath => Path.Combine(AppDataDir, "update-state.json");
        public static string UpdaterDir => Path.Combine(LocalAppDataDir, "updater");
        public static string UpdaterLogPath => Path.Combine(UpdaterDir, "update.log");
        public static string UpdatesDir => Path.Combine(AppDataDir, "updates");

        public static string SuccessMarkerPath(string version) =>
            Path.Combine(AppDataDir, $"update-success-{version}");

        /// <summary>
        /// Контракт файлов рабочей установки — проверяется после распаковки.
        /// Согласован с шагом «Verify artifact contract» в release.yml.
        /// whisper.dll обязателен: без него распознавание тихо падает в Mock.
        /// </summary>
        private static readonly string[] RequiredFiles =
        {
            "LWhisper.UI.WPF.exe",
            "WebRtcVad.dll",
            "wpfgfx_cor3.dll",
            @"runtimes\win-x64\whisper.dll"
        };
        private const string RequiredDir = "runtimes";

        public static bool IsApplyUpdateInvocation(string[] args) =>
            args.Any(a => string.Equals(a, ApplyUpdateArg, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Точка входа updater-режима. НЕ ВОЗВРАЩАЕТСЯ — всегда завершает процесс.
        /// </summary>
        public static void RunAndExit(string[] args)
        {
            int exitCode = 0;
            string? installDirForRescue = null;
            try
            {
                Directory.CreateDirectory(UpdaterDir);
                ULog($"=== Updater start: {string.Join(" ", args)} ===");

                // Второй экземпляр апдейтера — сразу выходим
                using var mutex = new Mutex(initiallyOwned: true, @"Local\LWhisper-Updater", out var createdNew);
                if (!createdNew)
                {
                    ULog("Другой апдейтер уже работает — выход");
                    Environment.Exit(0);
                }

                var i = Array.FindIndex(args, a => string.Equals(a, ApplyUpdateArg, StringComparison.OrdinalIgnoreCase));
                if (i < 0 || args.Length < i + 5)
                {
                    ULog("ОШИБКА: недостаточно аргументов (--apply-update <installDir> <zip> <pid> <version>)");
                    Environment.Exit(2);
                }

                var installDir = args[i + 1];
                installDirForRescue = installDir;
                var zipPath = args[i + 2];
                var pid = int.Parse(args[i + 3]);
                var version = args[i + 4];

                Apply(installDir, zipPath, pid, version);
            }
            catch (Exception ex)
            {
                ULog($"ФАТАЛЬНО: {ex}");
                exitCode = 1;
                // F7: при любом сбое пользователь не должен остаться без приложения
                TryRescueRelaunch(installDirForRescue);
            }
            ULog($"Environment.Exit({exitCode})...");
            Environment.Exit(exitCode);
        }

        /// <summary>
        /// Аварийный перезапуск после неожиданного сбоя апдейтера: вернуть backup на место
        /// (если установка уехала) и запустить рабочий exe. Best effort.
        /// </summary>
        private static void TryRescueRelaunch(string? installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir)) return;

                var exe = Path.Combine(installDir, "LWhisper.UI.WPF.exe");
                if (!File.Exists(exe))
                {
                    var state = LoadState();
                    if (!string.IsNullOrEmpty(state?.BackupDir) && Directory.Exists(state.BackupDir))
                    {
                        Rollback(installDir, state.BackupDir);
                    }
                }

                if (File.Exists(exe))
                {
                    DeleteState();
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = false });
                    ULog($"Rescue: перезапущено {exe}");
                }
                else
                {
                    ULog($"Rescue: рабочий exe не найден в {installDir} — восстановите вручную из папки _backup_ рядом");
                }
            }
            catch (Exception ex)
            {
                ULog($"Rescue не удался: {ex.Message}");
            }
        }

        private static void Apply(string installDir, string zipPath, int mainPid, string version)
        {
            // 1. Дождаться выхода основного приложения (F10: по таймауту — Kill + бесконечный Wait).
            // Имя процесса проверяем: PID могли переиспользовать, чужой процесс не трогаем.
            try
            {
                var main = Process.GetProcessById(mainPid);
                if (!main.ProcessName.StartsWith("LWhisper", StringComparison.OrdinalIgnoreCase))
                {
                    ULog($"pid {mainPid} — это «{main.ProcessName}», не LWhisper (PID переиспользован) — не ждём");
                }
                else
                {
                    ULog($"Ожидание выхода pid {mainPid}...");
                    if (!main.WaitForExit(WaitMainExitMs))
                    {
                        ULog($"pid {mainPid} не вышел за {WaitMainExitMs}мс — Kill");
                        main.Kill();
                        main.WaitForExit();
                    }
                    ULog($"pid {mainPid} завершён");
                }
            }
            catch (ArgumentException)
            {
                ULog($"pid {mainPid} уже не существует");
            }

            var state = new UpdateState
            {
                Stage = "backup",
                InstallDir = installDir,
                ZipPath = zipPath,
                Version = version,
                StartedUtc = DateTime.UtcNow
            };

            // 2. Backup: переименовать текущую установку
            var backupDir = installDir.TrimEnd('\\', '/') + "_backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            state.BackupDir = backupDir;
            SaveState(state);
            ULog($"Backup: {installDir} -> {backupDir}");
            MoveDirectoryWithRetry(installDir, backupDir);

            var oldExe = Path.Combine(backupDir, "LWhisper.UI.WPF.exe");

            // 3. Распаковать новую версию
            try
            {
                state.Stage = "extracting";
                SaveState(state);
                ULog($"Распаковка {zipPath} -> {installDir}");
                Directory.CreateDirectory(installDir);
                ZipFile.ExtractToDirectory(zipPath, installDir, overwriteFiles: true);

                // 4. Контракт файлов
                var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(installDir, f))).ToList();
                if (!Directory.Exists(Path.Combine(installDir, RequiredDir)))
                {
                    missing.Add(RequiredDir + @"\");
                }
                if (missing.Count > 0)
                {
                    throw new InvalidOperationException("После распаковки не хватает: " + string.Join(", ", missing));
                }
                ULog("Контракт файлов OK");
            }
            catch (Exception ex)
            {
                ULog($"ОШИБКА распаковки/контракта: {ex.Message} — откат");
                Rollback(installDir, backupDir);
                DeleteState();
                RelaunchAndLog(oldExe, backupExisted: true, installDir);
                return;
            }

            // 5. Запустить новую версию и ждать handshake.
            // Уцелевший маркер прошлой попытки той же версии сделал бы handshake ложно-успешным
            // (и backup удалился бы до реальной проверки) — не удалился → откат.
            var newExe = Path.Combine(installDir, "LWhisper.UI.WPF.exe");
            var marker = SuccessMarkerPath(version);
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* проверяется ниже */ }
            if (File.Exists(marker))
            {
                ULog("Старый handshake-маркер не удалился — handshake недостоверен, откат");
                Rollback(installDir, backupDir);
                DeleteState();
                RelaunchAndLog(oldExe, backupExisted: true, installDir);
                return;
            }

            state.Stage = "launching";
            SaveState(state);
            ULog($"Запуск новой версии: {newExe}");

            Process newProc;
            try
            {
                newProc = Process.Start(new ProcessStartInfo(newExe) { WorkingDirectory = installDir, UseShellExecute = false })!;
            }
            catch (Exception ex)
            {
                ULog($"ОШИБКА запуска новой версии: {ex.Message} — откат");
                Rollback(installDir, backupDir);
                DeleteState();
                RelaunchAndLog(oldExe, backupExisted: true, installDir);
                return;
            }

            // Handshake: новый exe при старте видит state.stage=launching и пишет маркер update-success-<ver>.
            // Защита от сценария «SmartScreen/Defender убил свежераспакованный exe до инициализации».
            var sw = Stopwatch.StartNew();
            bool handshake = false;
            while (sw.ElapsedMilliseconds < HandshakeTimeoutMs)
            {
                if (File.Exists(marker) && !newProc.HasExited)
                {
                    handshake = true;
                    break;
                }
                if (newProc.HasExited)
                {
                    ULog($"Новый процесс вышел с кодом {newProc.ExitCode} до handshake");
                    break;
                }
                Thread.Sleep(500);
            }

            if (!handshake)
            {
                ULog("Handshake НЕ получен — откат на предыдущую версию");
                try { if (!newProc.HasExited) { newProc.Kill(); newProc.WaitForExit(); } } catch { /* уже вышел */ }
                Rollback(installDir, backupDir);
                DeleteState();
                RelaunchAndLog(oldExe, backupExisted: true, installDir);
                return;
            }

            // 6. Успех: убрать backup (F4: ретраи, при неудаче переименовать в _failed_)
            ULog($"Handshake OK (версия {version}). Удаление backup...");
            DeleteDirectoryWithRetry(backupDir);
            DeleteState();
            try { File.Delete(marker); } catch { /* приберёт recovery */ }
            try { File.Delete(zipPath); } catch { /* приберёт recovery */ }
            ULog("=== Обновление завершено успешно ===");
        }

        /// <summary>
        /// Откат: вернуть backup на место установки.
        /// F6: если installDir заблокирован — увести его в _failed_, потом вернуть backup.
        /// </summary>
        private static void Rollback(string installDir, string backupDir)
        {
            try
            {
                if (Directory.Exists(installDir))
                {
                    try
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                    catch
                    {
                        var failedDir = installDir.TrimEnd('\\', '/') + "_failed_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        ULog($"installDir заблокирован — переименование в {failedDir}");
                        MoveDirectoryWithRetry(installDir, failedDir);
                    }
                }
                MoveDirectoryWithRetry(backupDir, installDir);
                ULog("Откат выполнен");
            }
            catch (Exception ex)
            {
                ULog($"ОШИБКА отката: {ex} — установка осталась в {(Directory.Exists(backupDir) ? backupDir : "???")}");
            }
        }

        private static void RelaunchAndLog(string exePath, bool backupExisted, string installDir)
        {
            ULog("RelaunchAndLog: выбор exe...");
            // После отката старый exe снова лежит в installDir
            var target = File.Exists(Path.Combine(installDir, "LWhisper.UI.WPF.exe"))
                ? Path.Combine(installDir, "LWhisper.UI.WPF.exe")
                : exePath;
            try
            {
                ULog($"RelaunchAndLog: Process.Start({target})...");
                Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target)!, UseShellExecute = false });
                ULog($"Перезапущена предыдущая версия: {target}");
            }
            catch (Exception ex)
            {
                ULog($"Не удалось перезапустить предыдущую версию ({target}): {ex.Message}");
            }
        }

        private static void MoveDirectoryWithRetry(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(from, to);
                    return;
                }
                catch when (attempt < 5)
                {
                    ULog($"Move {from} -> {to}: попытка {attempt} не удалась, повтор через 1с");
                    Thread.Sleep(1000);
                }
            }
        }

        private static void DeleteDirectoryWithRetry(string dir)
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return;
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
            // Не удалилось (AV-лок?) — некритично, пометить и оставить recovery-очистке
            try
            {
                var failed = dir + "_stale";
                Directory.Move(dir, failed);
                ULog($"Backup не удалился, переименован: {failed}");
            }
            catch
            {
                ULog($"Backup не удалился и не переименовался: {dir} (почистит recovery)");
            }
        }

        // ---------- Журнал состояния ----------

        public static void SaveState(UpdateState state)
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static UpdateState? LoadState()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return null;
                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFilePath));
            }
            catch
            {
                return null; // битый журнал — считаем что его нет
            }
        }

        public static void DeleteState()
        {
            ULog("DeleteState...");
            try { File.Delete(StateFilePath); } catch { /* best effort */ }
        }

        // ---------- Recovery на старте основного приложения ----------

        /// <summary>
        /// Вызывается основным exe при обычном старте ДО остальной инициализации.
        /// 1) stage=launching и версия совпадает → мы новая версия, пишем handshake-маркер.
        /// 2) Устаревший журнал → починить установку из backup (если что-то пропало) и почистить.
        /// 3) Прибираем старые backup/_failed_/updates-папки.
        /// </summary>
        public static void RunStartupRecovery(string currentVersion)
        {
            try
            {
                var state = LoadState();
                if (state != null)
                {
                    var isStale = DateTime.UtcNow - state.StartedUtc > TimeSpan.FromHours(1);
                    if (state.Stage == "launching" && VersionsEqual(state.Version, currentVersion))
                    {
                        // Мы — свежеустановленная версия: подтверждаем апдейтеру, что живы
                        Directory.CreateDirectory(AppDataDir);
                        File.WriteAllText(SuccessMarkerPath(state.Version), DateTime.UtcNow.ToString("O"));
                        Serilog.Log.Information("Обновление до {Version} применено — handshake-маркер записан", currentVersion);
                        if (isStale)
                        {
                            // Апдейтер умер после запуска новой версии, не убрав журнал — иначе он вечный
                            Serilog.Log.Warning("Журнал обновления завис в stage=launching — очистка");
                            DeleteState();
                        }
                        // Иначе state.json удаляет апдейтер после удаления backup
                    }
                    else if (isStale)
                    {
                        // Апдейтер умер на полпути. Мы работаем — exe жив, но установка могла
                        // остаться неполной (полураспакованный ZIP) → дочинить из backup.
                        Serilog.Log.Warning("Найден устаревший update-state.json (stage={Stage}, {Age} назад) — восстановление и очистка",
                            state.Stage, DateTime.UtcNow - state.StartedUtc);
                        RepairInstallFromBackup(state);
                        DeleteState();
                    }
                }

                CleanupOldArtifacts();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Recovery обновления: ошибка (некритично)");
            }
        }

        /// <summary>
        /// Сравнение версий с нормализацией до 3 компонент — строки "1.1" и "1.1.0" равны
        /// </summary>
        private static bool VersionsEqual(string a, string b)
        {
            if (!Version.TryParse(a, out var va) || !Version.TryParse(b, out var vb))
            {
                return string.Equals(a, b, StringComparison.Ordinal);
            }
            static Version Norm(Version v) => new(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
            return Norm(va).Equals(Norm(vb));
        }

        /// <summary>
        /// Дочинить собственную установку из backup: скопировать недостающие файлы контракта.
        /// Работающий exe заменить нельзя (и не нужно — он жив), но потерянные при
        /// оборванной распаковке нативки/runtimes копируются без конфликтов.
        /// </summary>
        private static void RepairInstallFromBackup(UpdateState state)
        {
            if (string.IsNullOrEmpty(state.BackupDir) || !Directory.Exists(state.BackupDir)) return;

            var processDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (processDir == null) return;
            // Чиним только собственную папку установки — чужие не трогаем
            if (!string.Equals(Path.GetFullPath(processDir).TrimEnd('\\', '/'),
                    Path.GetFullPath(state.InstallDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var f in RequiredFiles)
            {
                var dst = Path.Combine(processDir, f);
                var src = Path.Combine(state.BackupDir, f);
                if (!File.Exists(dst) && File.Exists(src))
                {
                    try
                    {
                        File.Copy(src, dst);
                        Serilog.Log.Warning("Recovery: восстановлен из backup недостающий файл {File}", f);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Recovery: не удалось восстановить {File}", f);
                    }
                }
            }

            var dstRuntimes = Path.Combine(processDir, RequiredDir);
            var srcRuntimes = Path.Combine(state.BackupDir, RequiredDir);
            if (!Directory.Exists(dstRuntimes) && Directory.Exists(srcRuntimes))
            {
                try
                {
                    CopyDirectory(srcRuntimes, dstRuntimes);
                    Serilog.Log.Warning("Recovery: папка {Dir} восстановлена из backup", RequiredDir);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Recovery: не удалось восстановить папку {Dir}", RequiredDir);
                }
            }
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }

        private static void CleanupOldArtifacts()
        {
            // Старые backup/_failed_ папки рядом с установкой (старше 7 дней).
            // Backup из ЖИВОГО журнала не трогаем ни при каком возрасте — он может быть
            // нужен апдейтеру для отката прямо сейчас (rename не обновляет mtime папки).
            var liveBackupDir = LoadState()?.BackupDir;
            var processPath = Environment.ProcessPath;
            if (processPath != null)
            {
                var installDir = Path.GetDirectoryName(processPath)!;
                var parent = Path.GetDirectoryName(installDir);
                var name = Path.GetFileName(installDir.TrimEnd('\\', '/'));
                if (parent != null)
                {
                    foreach (var dir in Directory.EnumerateDirectories(parent, name + "_*"))
                    {
                        var dirName = Path.GetFileName(dir);
                        bool isOurArtifact = dirName.Contains("_backup_") || dirName.Contains("_failed_") || dirName.EndsWith("_stale");
                        bool isLiveBackup = !string.IsNullOrEmpty(liveBackupDir)
                            && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(liveBackupDir), StringComparison.OrdinalIgnoreCase);
                        if (isOurArtifact && !isLiveBackup && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-7))
                        {
                            try
                            {
                                Directory.Delete(dir, recursive: true);
                                Serilog.Log.Information("Удалён старый артефакт обновления: {Dir}", dir);
                            }
                            catch { /* залочен — попробуем в следующий раз */ }
                        }
                    }
                }
            }

            // Скачанные ZIP старше 7 дней
            if (Directory.Exists(UpdatesDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(UpdatesDir))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-7))
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { /* залочен */ }
                    }
                }
            }

            // Просроченные handshake-маркеры
            if (Directory.Exists(AppDataDir))
            {
                foreach (var f in Directory.EnumerateFiles(AppDataDir, "update-success-*"))
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))
                    {
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
                }
            }
        }

        /// <summary>Лог updater-режима (Serilog ещё/уже недоступен)</summary>
        private static void ULog(string message)
        {
            try
            {
                File.AppendAllText(UpdaterLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // логировать некуда — молча продолжаем, обновление важнее лога
            }
        }
    }
}
