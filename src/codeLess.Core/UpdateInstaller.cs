using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public enum UpdateOs { Windows, Mac, Linux }

    /// <summary>How the running copy was installed (decides the asset and how it is replaced).</summary>
    public enum UpdateInstallKind
    {
        /// <summary>Development run (dotnet codeLess.dll) or unrecognised layout: download only, install by hand.</summary>
        Unknown,
        /// <summary>macOS codeLess.app (from the .dmg / .zip).</summary>
        MacAppBundle,
        /// <summary>Windows Inno Setup install (unins000.exe next to codeLess.exe).</summary>
        WindowsInstaller,
        /// <summary>Windows portable codeLess.exe.</summary>
        WindowsPortable,
        /// <summary>Windows per-user MSI install (%LOCALAPPDATA%\codeLess, WiX MajorUpgrade): upgraded with msiexec /qn.</summary>
        WindowsMsi,
        /// <summary>Linux portable .tar.gz extracted into a folder.</summary>
        LinuxTarball,
        /// <summary>Linux .deb (/usr/lib/codeless): needs `sudo apt install`, never run by codeLess.</summary>
        LinuxDeb
    }

    /// <summary>The running copy of codeLess: OS, CPU, install kind and where it lives. C# 5.</summary>
    public class UpdateEnvironment
    {
        public UpdateOs Os;
        public bool Arm64;
        public UpdateInstallKind Kind;
        /// <summary>macOS: the .app folder; Windows: codeLess.exe; Linux .tar.gz: the install folder; .deb: /usr/lib/codeless.</summary>
        public string InstallPath = "";
        /// <summary>The process executable (relaunched after the update).</summary>
        public string ExecutablePath = "";
        public int ProcessId;

        public static UpdateOs CurrentOs
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return UpdateOs.Windows;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return UpdateOs.Mac;
                return UpdateOs.Linux;
            }
        }

        /// <summary>The running process (<paramref name="processPath"/>: Environment.ProcessPath / Application.ExecutablePath).</summary>
        public static UpdateEnvironment DetectCurrent(string processPath, int processId)
        {
            UpdateEnvironment e = Detect(CurrentOs, RuntimeInformation.OSArchitecture == Architecture.Arm64, processPath, File.Exists);
            e.ProcessId = processId;
            return e;
        }

        /// <summary>Pure detection (unit-tested): <paramref name="fileExists"/> probes the install folder.</summary>
        public static UpdateEnvironment Detect(UpdateOs os, bool arm64, string processPath, Func<string, bool> fileExists)
        {
            UpdateEnvironment e = new UpdateEnvironment();
            e.Os = os;
            e.Arm64 = arm64;
            e.Kind = UpdateInstallKind.Unknown;
            e.ExecutablePath = processPath ?? "";
            string p = processPath ?? "";
            if (p.Length == 0) return e;
            if (os == UpdateOs.Mac)
            {
                int idx = p.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
                if (idx > 0)
                {
                    e.Kind = UpdateInstallKind.MacAppBundle;
                    e.InstallPath = p.Substring(0, idx + 4);
                }
                return e;
            }
            string name = FileNameOf(p, os);
            string dir = DirectoryOf(p, os);
            if (os == UpdateOs.Windows)
            {
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return e;
                e.InstallPath = p;
                if (fileExists != null && fileExists(dir + "\\unins000.exe")) e.Kind = UpdateInstallKind.WindowsInstaller;
                else if (IsMsiInstallFolder(dir)) e.Kind = UpdateInstallKind.WindowsMsi;
                else e.Kind = UpdateInstallKind.WindowsPortable;
                return e;
            }
            if (!name.Equals("codeLess", StringComparison.Ordinal)) return e; // "dotnet" (development run)
            e.InstallPath = dir;
            // the .deb installs into /usr/lib/codeless (owned by root, managed by dpkg)
            e.Kind = dir.StartsWith("/usr/", StringComparison.Ordinal) ? UpdateInstallKind.LinuxDeb : UpdateInstallKind.LinuxTarball;
            return e;
        }

        /// <summary>
        /// The MSI (Installer.wxs) installs per user into %LOCALAPPDATA%\codeLess; Inno Setup uses
        /// %LOCALAPPDATA%\Programs\codeLess (and leaves unins000.exe), so the folder tells them apart.
        /// </summary>
        public static bool IsMsiInstallFolder(string dir)
        {
            string d = (dir ?? "").Replace('/', '\\').TrimEnd('\\');
            return d.EndsWith("\\AppData\\Local\\codeLess", StringComparison.OrdinalIgnoreCase);
        }

        private static string FileNameOf(string p, UpdateOs os)
        {
            int i = os == UpdateOs.Windows ? Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/')) : p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        private static string DirectoryOf(string p, UpdateOs os)
        {
            int i = os == UpdateOs.Windows ? Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/')) : p.LastIndexOf('/');
            return i > 0 ? p.Substring(0, i) : "";
        }

        /// <summary>True when codeLess can replace itself and restart (else: verified download + instructions).</summary>
        public bool SupportsAutomaticInstall
        {
            get
            {
                return Kind == UpdateInstallKind.MacAppBundle || Kind == UpdateInstallKind.WindowsInstaller || Kind == UpdateInstallKind.WindowsMsi ||
                       Kind == UpdateInstallKind.WindowsPortable || Kind == UpdateInstallKind.LinuxTarball;
            }
        }
    }

    /// <summary>Release asset choice and SHA256SUMS.txt verification. C# 5.</summary>
    public static class UpdateAssets
    {
        public const string ChecksumsAssetName = "SHA256SUMS.txt";

        /// <summary>The release asset that updates this install (names produced by CI, see build-and-sign.yml).</summary>
        public static string AssetNameFor(UpdateOs os, bool arm64, UpdateInstallKind kind)
        {
            string arch = arm64 ? "arm64" : "x64";
            switch (os)
            {
                case UpdateOs.Mac:
                    return "codeLess-mac-" + arch + ".zip";
                case UpdateOs.Windows:
                    if (kind == UpdateInstallKind.WindowsPortable) return "codeLess.exe";
                    return kind == UpdateInstallKind.WindowsMsi ? "codeLess-Setup.msi" : "codeLess-Setup.exe";
                default:
                    return kind == UpdateInstallKind.LinuxDeb ? "codeLess-linux-" + arch + ".deb" : "codeLess-linux-" + arch + ".tar.gz";
            }
        }

        public static string AssetNameFor(UpdateEnvironment env)
        {
            return AssetNameFor(env.Os, env.Arm64, env.Kind);
        }

        private static readonly Regex SumLine = new Regex(@"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$");

        /// <summary>Parses `sha256sum` output ("&lt;hex&gt;  name" / "&lt;hex&gt; *name"): file name → lower-case hash.</summary>
        public static Dictionary<string, string> ParseSha256Sums(string text)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                Match m = SumLine.Match(line);
                if (!m.Success) continue;
                string name = m.Groups[2].Value.Trim();
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.Length == 0) continue;
                map[name] = m.Groups[1].Value.ToLowerInvariant();
            }
            return map;
        }

        /// <summary>Lower-case hex SHA-256 of a file (streamed).</summary>
        public static string ComputeSha256(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>True only when <paramref name="sums"/> lists <paramref name="assetName"/> with exactly this file's hash.</summary>
        public static bool Verify(string filePath, string assetName, Dictionary<string, string> sums, out string error)
        {
            error = "";
            string expected;
            if (sums == null || !sums.TryGetValue(assetName ?? "", out expected))
            {
                error = ChecksumsAssetName + " has no entry for " + assetName + "; the download was not installed.";
                return false;
            }
            string actual = ComputeSha256(filePath);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                error = "Checksum mismatch for " + assetName + " (expected " + expected + ", got " + actual + "); the download was deleted.";
                return false;
            }
            return true;
        }
    }

    /// <summary>Outcome of <see cref="UpdateDownloader.Download"/>.</summary>
    public class UpdateDownloadResult
    {
        public bool Success;
        public bool Blocked;
        public bool Cancelled;
        public string Error = "";
        public string FilePath = "";
        public string AssetName = "";
        public string Sha256 = "";
    }

    /// <summary>
    /// Downloads the release asset for this install through NetworkLedger (Offline Mode refuses it; shown in Network
    /// Activity), with progress and cancellation, and verifies it against the release's SHA256SUMS.txt: a missing
    /// checksum file, a missing entry or a mismatch refuses the update. C# 5.
    /// </summary>
    public static class UpdateDownloader
    {
        public const string Feature = "Update download";

        public static UpdateDownloadResult Download(UpdateCheckResult release, string assetName, string targetDir,
            Action<long, long> progress, Func<bool> cancelled)
        {
            UpdateDownloadResult r = new UpdateDownloadResult();
            r.AssetName = assetName ?? "";
            try
            {
                UpdateReleaseAsset asset = release != null ? release.FindAsset(assetName) : null;
                if (asset == null) { r.Error = "This release has no " + assetName + " download."; return r; }
                UpdateReleaseAsset sumsAsset = release.FindAsset(UpdateAssets.ChecksumsAssetName);
                if (sumsAsset == null) { r.Error = "This release has no " + UpdateAssets.ChecksumsAssetName + "; refusing to install an unverified download."; return r; }

                LedgerHttpRequest req = new LedgerHttpRequest();
                req.Feature = Feature;
                req.Url = sumsAsset.Url;
                req.UserAgent = "codeLess-update";
                req.TimeoutMs = 20000;
                req.MaxResponseBytes = 1024 * 1024;
                LedgerHttpResponse sumsRes = LedgerHttp.Send(req);
                if (!sumsRes.IsSuccess) { r.Error = "Could not download " + UpdateAssets.ChecksumsAssetName + ": " + (sumsRes.Status > 0 ? "HTTP " + sumsRes.Status : sumsRes.Error); return r; }
                Dictionary<string, string> sums = UpdateAssets.ParseSha256Sums(sumsRes.Body);
                string expected;
                if (!sums.TryGetValue(assetName, out expected))
                {
                    r.Error = UpdateAssets.ChecksumsAssetName + " has no entry for " + assetName + "; refusing to install an unverified download.";
                    return r;
                }

                Directory.CreateDirectory(targetDir);
                string final = Path.Combine(targetDir, assetName);
                string error;
                if (File.Exists(final))
                {
                    if (UpdateAssets.Verify(final, assetName, sums, out error))
                    {
                        if (progress != null) progress(asset.Size, asset.Size);
                        r.Success = true; r.FilePath = final; r.Sha256 = expected;
                        return r;
                    }
                    TryDelete(final);
                }

                string partial = final + ".partial";
                TryDelete(partial);
                LedgerHttpResponse res = LedgerHttp.DownloadToFile(Feature, asset.Url, partial, "codeLess-update", 30000, progress, cancelled);
                if (cancelled != null && cancelled()) { r.Cancelled = true; r.Error = "Cancelled"; TryDelete(partial); return r; }
                if (!res.IsSuccess || !string.IsNullOrEmpty(res.Error))
                {
                    r.Error = "Download failed: " + (string.IsNullOrEmpty(res.Error) ? "HTTP " + res.Status : res.Error);
                    TryDelete(partial);
                    return r;
                }
                if (!UpdateAssets.Verify(partial, assetName, sums, out error))
                {
                    r.Error = error;
                    TryDelete(partial);
                    return r;
                }
                File.Move(partial, final);
                r.Success = true;
                r.FilePath = final;
                r.Sha256 = expected;
            }
            catch (NetworkBlockedException bex)
            {
                r.Blocked = true;
                r.Error = bex.Message;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>What happens on "Restart to Update" (or the manual instructions when it can't be automatic).</summary>
    public class UpdateInstallPlan
    {
        /// <summary>True: run <see cref="ScriptPath"/> and quit; it replaces the app and relaunches it.</summary>
        public bool CanInstall;
        public string ScriptPath = "";
        public string ScriptBody = "";
        /// <summary>Instructions shown when <see cref="CanInstall"/> is false (or why the install was refused).</summary>
        public string ManualMessage = "";
        /// <summary>A command the user can copy (e.g. sudo apt install ./codeLess-linux-x64.deb).</summary>
        public string ManualCommand = "";
        /// <summary>File / folder to reveal in Finder / Explorer / the file manager for a manual install.</summary>
        public string RevealPath = "";
        public string LogPath = "";
        public string Error = "";
    }

    /// <summary>Paths and commands for the generated helper scripts (tests override the relaunch / confirm commands).</summary>
    public class UpdateScriptArgs
    {
        public int ProcessId;
        /// <summary>Installed copy: .app folder (macOS), install folder (Linux), codeLess.exe (Windows).</summary>
        public string TargetPath = "";
        /// <summary>The verified new copy: extracted .app / folder / exe, or the Inno Setup installer.</summary>
        public string NewPath = "";
        public string BackupPath = "";
        public string LogPath = "";
        /// <summary>Linux: executable name inside the folder.</summary>
        public string ExecutableName = "codeLess";
        /// <summary>Windows installer: the exe started after setup.</summary>
        public string RelaunchExe = "";
        /// <summary>Seconds to wait for codeLess to quit.</summary>
        public int WaitSeconds = 120;
        /// <summary>Tests: replaces the relaunch command (sh syntax; $APP / $DIR are set).</summary>
        public string RelaunchCommandOverride;
        /// <summary>Tests: replaces the "new copy is running" check (sh syntax).</summary>
        public string ConfirmCommandOverride;
    }

    /// <summary>
    /// Replaces the running codeLess with a verified download and relaunches it, VS Code style: after the user agrees to
    /// restart (unsaved files go through the normal prompt), a small generated helper waits for this process to exit,
    /// moves the old copy aside, moves the new one into the same place, relaunches and removes the backup once the new
    /// copy runs (restoring the old copy when the swap or relaunch fails). When the location isn't writable (or for .deb
    /// installs) nothing is replaced: the verified download is revealed with instructions. C# 5.
    /// </summary>
    public static class UpdateInstaller
    {
        /// <summary>POSIX sh single-quoting: 'it'"'"'s'.</summary>
        public static string ShQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\"'\"'") + "'";
        }

        /// <summary>cmd.exe batch quoting: "path" with % doubled (paths cannot contain double quotes on Windows).</summary>
        public static string CmdQuote(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "").Replace("%", "%%") + "\"";
        }

        public static bool IsDirectoryWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            string probe = Path.Combine(dir, ".codeless-write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
            finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }

        /// <summary>Backup location next to the installed copy (same volume, so the swap is a rename).</summary>
        public static string BackupPathFor(UpdateEnvironment env)
        {
            string path = (env.InstallPath ?? "").TrimEnd('/', '\\');
            if (env.Os == UpdateOs.Mac)
            {
                string parent = Path.GetDirectoryName(path) ?? "";
                return Path.Combine(parent, "." + Path.GetFileName(path) + ".update-backup");
            }
            return path + ".update-backup";
        }

        /// <summary>Removes a backup left by an update whose relaunch could not be confirmed (call from the new copy).</summary>
        public static void CleanupStaleBackup(UpdateEnvironment env)
        {
            try
            {
                if (env == null || !env.SupportsAutomaticInstall || env.Kind == UpdateInstallKind.WindowsInstaller || env.Kind == UpdateInstallKind.WindowsMsi) return;
                string backup = BackupPathFor(env);
                if (env.Kind == UpdateInstallKind.WindowsPortable)
                {
                    UpdateDownloader.TryDelete(env.InstallPath + ".old");
                    return;
                }
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ preparation

        /// <summary>
        /// Validates the verified download and generates the helper (or the manual instructions). Blocking (extracts the
        /// archive): call from a worker thread. <paramref name="workDir"/> is a private folder for this version.
        /// </summary>
        public static UpdateInstallPlan Prepare(UpdateEnvironment env, string downloadedFile, string expectedVersion, string workDir)
        {
            UpdateInstallPlan plan = new UpdateInstallPlan();
            plan.RevealPath = downloadedFile ?? "";
            try
            {
                Directory.CreateDirectory(workDir);
                plan.LogPath = Path.Combine(workDir, "update.log");
                string fileName = Path.GetFileName(downloadedFile ?? "");
                switch (env.Kind)
                {
                    case UpdateInstallKind.MacAppBundle:
                        return PrepareMac(env, downloadedFile, expectedVersion, workDir, plan);
                    case UpdateInstallKind.WindowsInstaller:
                    case UpdateInstallKind.WindowsMsi:
                    case UpdateInstallKind.WindowsPortable:
                        return PrepareWindows(env, downloadedFile, workDir, plan);
                    case UpdateInstallKind.LinuxTarball:
                        return PrepareLinuxTarball(env, downloadedFile, workDir, plan);
                    case UpdateInstallKind.LinuxDeb:
                        plan.ManualCommand = "sudo apt install " + ShQuote(downloadedFile);
                        plan.ManualMessage = "codeLess was installed from a .deb package, so the update is installed with apt (codeLess never runs sudo itself). The verified package was downloaded to:\n" +
                                             downloadedFile + "\n\nQuit codeLess and run:\n" + plan.ManualCommand;
                        return plan;
                    default:
                        plan.ManualMessage = "This copy of codeLess isn't running from an installed app (" + (env.ExecutablePath ?? "") +
                                             "), so it can't replace itself. The verified download (" + fileName + ") is at:\n" + downloadedFile;
                        return plan;
                }
            }
            catch (Exception ex)
            {
                plan.CanInstall = false;
                plan.Error = ex.Message;
                plan.ManualMessage = "The update could not be prepared (" + ex.Message + "). The verified download is at:\n" + downloadedFile;
                return plan;
            }
        }

        private static UpdateInstallPlan PrepareMac(UpdateEnvironment env, string zip, string expectedVersion, string workDir, UpdateInstallPlan plan)
        {
            string extract = Path.Combine(workDir, "extracted");
            if (Directory.Exists(extract)) Directory.Delete(extract, true);
            Directory.CreateDirectory(extract);
            string output;
            int code = RunProcess("/usr/bin/ditto", new string[] { "-x", "-k", zip, extract }, 180000, out output);
            if (code != 0) return Manual(plan, "The update archive could not be extracted (ditto exit " + code + "). " + output.Trim(), zip);
            string newApp = null;
            foreach (string d in Directory.GetDirectories(extract, "*.app")) { newApp = d; break; }
            if (newApp == null) return Manual(plan, "The update archive contains no .app bundle.", zip);
            string error;
            if (!ValidateMacBundle(newApp, expectedVersion, out error)) return Manual(plan, error, zip);

            string appPath = env.InstallPath.TrimEnd('/');
            string parent = Path.GetDirectoryName(appPath) ?? "";
            plan.RevealPath = newApp;
            if (appPath.IndexOf("/AppTranslocation/", StringComparison.Ordinal) >= 0 || !IsDirectoryWritable(parent) || !IsDirectoryWritable(appPath))
            {
                plan.ManualMessage = "codeLess can't replace itself in " + parent + " (the folder isn't writable for your user" +
                                     (appPath.IndexOf("/AppTranslocation/", StringComparison.Ordinal) >= 0 ? ", or macOS is running the app from a quarantined copy" : "") +
                                     ").\n\nThe verified codeLess " + expectedVersion + " is shown in Finder: quit codeLess, then drag the new codeLess.app into " +
                                     parent + " and choose Replace.";
                return plan;
            }
            UpdateScriptArgs a = new UpdateScriptArgs();
            a.ProcessId = env.ProcessId;
            a.TargetPath = appPath;
            a.NewPath = newApp;
            a.BackupPath = BackupPathFor(env);
            a.LogPath = plan.LogPath;
            return Finish(plan, Path.Combine(workDir, "install-update.sh"), BuildMacScript(a));
        }

        private static UpdateInstallPlan PrepareWindows(UpdateEnvironment env, string file, string workDir, UpdateInstallPlan plan)
        {
            string exe = env.InstallPath;
            string dir = Path.GetDirectoryName(exe) ?? "";
            UpdateScriptArgs a = new UpdateScriptArgs();
            a.ProcessId = env.ProcessId;
            a.TargetPath = exe;
            a.NewPath = file;
            a.LogPath = plan.LogPath;
            a.RelaunchExe = exe;
            if (env.Kind == UpdateInstallKind.WindowsInstaller)
                return Finish(plan, Path.Combine(workDir, "install-update.cmd"), BuildWindowsInstallerScript(a));
            if (env.Kind == UpdateInstallKind.WindowsMsi)
                return Finish(plan, Path.Combine(workDir, "install-update.cmd"), BuildWindowsMsiScript(a));
            if (!IsDirectoryWritable(dir))
                return Manual(plan, "codeLess can't replace " + exe + " (the folder isn't writable). Quit codeLess and replace it with the verified download.", file);
            a.BackupPath = exe + ".old";
            return Finish(plan, Path.Combine(workDir, "install-update.cmd"), BuildWindowsPortableScript(a));
        }

        private static UpdateInstallPlan PrepareLinuxTarball(UpdateEnvironment env, string tarGz, string workDir, UpdateInstallPlan plan)
        {
            string staged = Path.Combine(workDir, "staged");
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            string output;
            int code = RunProcess("tar", new string[] { "-xzf", tarGz, "-C", staged }, 180000, out output);
            if (code != 0) return Manual(plan, "The update archive could not be extracted (tar exit " + code + "). " + output.Trim(), tarGz);
            if (!File.Exists(Path.Combine(staged, "codeLess"))) return Manual(plan, "The update archive has no codeLess executable.", tarGz);
            string dir = env.InstallPath.TrimEnd('/');
            string parent = Path.GetDirectoryName(dir) ?? "";
            if (!IsDirectoryWritable(parent) || !IsDirectoryWritable(dir))
                return Manual(plan, "codeLess can't replace itself in " + dir + " (not writable). Quit codeLess and extract the verified download there.", tarGz);
            UpdateScriptArgs a = new UpdateScriptArgs();
            a.ProcessId = env.ProcessId;
            a.TargetPath = dir;
            a.NewPath = staged;
            a.BackupPath = BackupPathFor(env);
            a.LogPath = plan.LogPath;
            return Finish(plan, Path.Combine(workDir, "install-update.sh"), BuildLinuxScript(a));
        }

        private static UpdateInstallPlan Manual(UpdateInstallPlan plan, string message, string reveal)
        {
            plan.CanInstall = false;
            plan.Error = message;
            plan.ManualMessage = message + "\n\nThe verified download is at:\n" + reveal;
            plan.RevealPath = reveal;
            return plan;
        }

        private static UpdateInstallPlan Finish(UpdateInstallPlan plan, string scriptPath, string body)
        {
            File.WriteAllText(scriptPath, body, new UTF8Encoding(false));
            plan.ScriptPath = scriptPath;
            plan.ScriptBody = body;
            plan.CanInstall = true;
            return plan;
        }

        /// <summary>Reads a &lt;key&gt;K&lt;/key&gt;&lt;string&gt;V&lt;/string&gt; pair from an XML Info.plist (null when absent).</summary>
        public static string ReadPlistString(string plistPath, string key)
        {
            try
            {
                string xml = File.ReadAllText(plistPath);
                Match m = Regex.Match(xml, "<key>\\s*" + Regex.Escape(key) + "\\s*</key>\\s*<string>([^<]*)</string>");
                return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
            }
            catch { return null; }
        }

        /// <summary>The bundle is the expected version and has its executable.</summary>
        public static bool ValidateMacBundle(string appPath, string expectedVersion, out string error)
        {
            error = "";
            string plist = Path.Combine(Path.Combine(appPath, "Contents"), "Info.plist");
            if (!File.Exists(plist)) { error = "The downloaded app has no Info.plist."; return false; }
            string version = ReadPlistString(plist, "CFBundleShortVersionString");
            Version have, want;
            if (!UpdateChecker.TryParseVersion(version, out have) || !UpdateChecker.TryParseVersion(expectedVersion, out want) || have != want)
            {
                error = "The downloaded app is version " + (version ?? "?") + ", expected " + expectedVersion + ".";
                return false;
            }
            string exe = ReadPlistString(plist, "CFBundleExecutable");
            if (string.IsNullOrEmpty(exe) || !File.Exists(Path.Combine(Path.Combine(Path.Combine(appPath, "Contents"), "MacOS"), exe)))
            {
                error = "The downloaded app has no executable.";
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ helper scripts

        private static void AppendShWait(StringBuilder sb, UpdateScriptArgs a)
        {
            sb.Append("exec >>\"$LOG\" 2>&1\n");
            sb.Append("echo \"[$(date)] codeLess update: waiting for process $PID to quit\"\n");
            sb.Append("n=0\n");
            sb.Append("while kill -0 \"$PID\" 2>/dev/null; do\n");
            sb.Append("  n=$((n+1))\n");
            sb.Append("  if [ \"$n\" -gt ").Append(Math.Max(1, a.WaitSeconds) * 5).Append(" ]; then echo \"codeLess did not quit; update cancelled\"; exit 1; fi\n");
            sb.Append("  sleep 0.2\n");
            sb.Append("done\n");
        }

        /// <summary>macOS helper: wait, move the old .app aside, move the new one in, `open` it, drop the backup once it runs.</summary>
        public static string BuildMacScript(UpdateScriptArgs a)
        {
            string relaunch = a.RelaunchCommandOverride ?? "open \"$APP\"";
            string confirm = a.ConfirmCommandOverride ??
                             "ps -axo command= | grep -F -- \"$APP/Contents/MacOS/\" | grep -v -F -- \"grep\" >/dev/null";
            StringBuilder sb = new StringBuilder();
            sb.Append("#!/bin/sh\n");
            sb.Append("# codeLess update helper (generated; runs once after codeLess quits)\n");
            sb.Append("PID=").Append(a.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("APP=").Append(ShQuote(a.TargetPath)).Append('\n');
            sb.Append("NEW=").Append(ShQuote(a.NewPath)).Append('\n');
            sb.Append("BACKUP=").Append(ShQuote(a.BackupPath)).Append('\n');
            sb.Append("LOG=").Append(ShQuote(a.LogPath)).Append('\n');
            AppendShWait(sb, a);
            sb.Append("rm -rf \"$BACKUP\"\n");
            sb.Append("if ! mv \"$APP\" \"$BACKUP\"; then echo \"could not move the old app aside\"; ").Append(relaunch).Append("; exit 1; fi\n");
            sb.Append("if ! mv \"$NEW\" \"$APP\"; then\n");
            sb.Append("  echo \"could not move the new app into place; restoring the previous version\"\n");
            sb.Append("  rm -rf \"$APP\"; mv \"$BACKUP\" \"$APP\"; ").Append(relaunch).Append("; exit 1\n");
            sb.Append("fi\n");
            sb.Append("if ! ").Append(relaunch).Append("; then\n");
            sb.Append("  echo \"relaunch failed; restoring the previous version\"\n");
            sb.Append("  rm -rf \"$APP\"; mv \"$BACKUP\" \"$APP\"; ").Append(relaunch).Append("; exit 1\n");
            sb.Append("fi\n");
            sb.Append("n=0\n");
            sb.Append("while [ \"$n\" -lt 60 ]; do\n");
            sb.Append("  if ").Append(confirm).Append("; then rm -rf \"$BACKUP\"; echo \"updated\"; exit 0; fi\n");
            sb.Append("  n=$((n+1)); sleep 0.5\n");
            sb.Append("done\n");
            sb.Append("echo \"relaunch not confirmed; the previous version stays at $BACKUP until the next start\"\n");
            sb.Append("exit 0\n");
            return sb.ToString();
        }

        /// <summary>Linux .tar.gz helper: same swap pattern for the install folder, relaunching the executable detached.</summary>
        public static string BuildLinuxScript(UpdateScriptArgs a)
        {
            string relaunch = a.RelaunchCommandOverride ?? "nohup \"$DIR/$EXE\" >/dev/null 2>&1 &";
            string confirm = a.ConfirmCommandOverride ?? "kill -0 $! 2>/dev/null";
            StringBuilder sb = new StringBuilder();
            sb.Append("#!/bin/sh\n");
            sb.Append("# codeLess update helper (generated; runs once after codeLess quits)\n");
            sb.Append("PID=").Append(a.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("DIR=").Append(ShQuote(a.TargetPath)).Append('\n');
            sb.Append("NEW=").Append(ShQuote(a.NewPath)).Append('\n');
            sb.Append("BACKUP=").Append(ShQuote(a.BackupPath)).Append('\n');
            sb.Append("EXE=").Append(ShQuote(a.ExecutableName)).Append('\n');
            sb.Append("LOG=").Append(ShQuote(a.LogPath)).Append('\n');
            AppendShWait(sb, a);
            sb.Append("rm -rf \"$BACKUP\"\n");
            sb.Append("if ! mv \"$DIR\" \"$BACKUP\"; then\n");
            sb.Append("  echo \"could not move the old version aside\"\n");
            sb.Append("  ").Append(relaunch).Append('\n');
            sb.Append("  exit 1\n");
            sb.Append("fi\n");
            sb.Append("if ! mv \"$NEW\" \"$DIR\"; then\n");
            sb.Append("  echo \"could not move the new version into place; restoring\"\n");
            sb.Append("  rm -rf \"$DIR\"; mv \"$BACKUP\" \"$DIR\"\n");
            sb.Append("  ").Append(relaunch).Append('\n');
            sb.Append("  exit 1\n");
            sb.Append("fi\n");
            sb.Append("chmod +x \"$DIR/$EXE\" 2>/dev/null\n");
            sb.Append(relaunch).Append('\n');
            sb.Append("sleep 3\n");
            sb.Append("if ").Append(confirm).Append("; then rm -rf \"$BACKUP\"; echo \"updated\"; exit 0; fi\n");
            sb.Append("echo \"relaunch not confirmed; the previous version stays at $BACKUP until the next start\"\n");
            sb.Append("exit 0\n");
            return sb.ToString();
        }

        private static void AppendCmdWait(StringBuilder sb, UpdateScriptArgs a)
        {
            string pid = a.ProcessId.ToString(CultureInfo.InvariantCulture);
            sb.Append("set N=0\r\n");
            sb.Append(":wait\r\n");
            sb.Append("tasklist /FI \"PID eq ").Append(pid).Append("\" /NH 2>NUL | find \" ").Append(pid).Append(" \" >NUL\r\n");
            sb.Append("if errorlevel 1 goto gone\r\n");
            sb.Append("set /a N+=1\r\n");
            sb.Append("if %N% GTR ").Append(Math.Max(1, a.WaitSeconds)).Append(" goto timeout\r\n");
            sb.Append("ping -n 2 127.0.0.1 >NUL\r\n");
            sb.Append("goto wait\r\n");
            sb.Append(":timeout\r\n");
            sb.Append("echo codeLess did not quit; update cancelled>>").Append(CmdQuote(a.LogPath)).Append("\r\n");
            sb.Append("exit /b 1\r\n");
            sb.Append(":gone\r\n");
        }

        /// <summary>Windows Inno Setup helper: wait, run the installer silently, relaunch.</summary>
        public static string BuildWindowsInstallerScript(UpdateScriptArgs a)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("rem codeLess update helper (generated; runs once after codeLess quits)\r\n");
            AppendCmdWait(sb, a);
            sb.Append(CmdQuote(a.NewPath)).Append(" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=").Append(CmdQuote(a.LogPath)).Append("\r\n");
            sb.Append("start \"\" ").Append(CmdQuote(a.RelaunchExe)).Append("\r\n");
            sb.Append("exit /b 0\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Windows MSI helper: wait, per-user silent major upgrade with msiexec (no elevation: the package is perUser),
        /// relaunch. msiexec's exit code 3010 means "succeeded, reboot suggested" and is treated as success.
        /// </summary>
        public static string BuildWindowsMsiScript(UpdateScriptArgs a)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("rem codeLess update helper (generated; runs once after codeLess quits)\r\n");
            AppendCmdWait(sb, a);
            sb.Append("msiexec.exe /i ").Append(CmdQuote(a.NewPath)).Append(" /qn /norestart /l*v ").Append(CmdQuote(a.LogPath)).Append("\r\n");
            sb.Append("if errorlevel 3011 goto failed\r\n");
            sb.Append("if errorlevel 3010 goto ok\r\n");
            sb.Append("if errorlevel 1 goto failed\r\n");
            sb.Append(":ok\r\n");
            sb.Append("start \"\" ").Append(CmdQuote(a.RelaunchExe)).Append("\r\n");
            sb.Append("exit /b 0\r\n");
            sb.Append(":failed\r\n");
            sb.Append("echo msiexec failed; starting the installed copy>>").Append(CmdQuote(a.LogPath)).Append("\r\n");
            sb.Append("start \"\" ").Append(CmdQuote(a.RelaunchExe)).Append("\r\n");
            sb.Append("exit /b 1\r\n");
            return sb.ToString();
        }

        /// <summary>Windows portable helper: wait, swap codeLess.exe (old copy kept until the new one is in place), relaunch.</summary>
        public static string BuildWindowsPortableScript(UpdateScriptArgs a)
        {
            string exe = CmdQuote(a.TargetPath), old = CmdQuote(a.BackupPath), fresh = CmdQuote(a.NewPath), log = CmdQuote(a.LogPath);
            string dir = Path.GetDirectoryName(a.TargetPath) ?? "";
            string coreDll = CmdQuote(dir + "\\codeLess.Core.dll"), coreOld = CmdQuote(dir + "\\codeLess.Core.dll.old");
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("rem codeLess update helper (generated; runs once after codeLess quits)\r\n");
            AppendCmdWait(sb, a);
            sb.Append("del /F /Q ").Append(old).Append(" >NUL 2>&1\r\n");
            sb.Append("move /Y ").Append(exe).Append(' ').Append(old).Append(" >>").Append(log).Append(" 2>&1\r\n");
            sb.Append("if errorlevel 1 goto relaunch\r\n");
            sb.Append("copy /Y ").Append(fresh).Append(' ').Append(exe).Append(" >>").Append(log).Append(" 2>&1\r\n");
            sb.Append("if errorlevel 1 goto restore\r\n");
            // the new exe embeds codeLess.Core.dll; a stale copy next to it would be loaded instead
            sb.Append("if exist ").Append(coreDll).Append(" move /Y ").Append(coreDll).Append(' ').Append(coreOld).Append(" >NUL 2>&1\r\n");
            sb.Append("start \"\" ").Append(exe).Append("\r\n");
            sb.Append("del /F /Q ").Append(old).Append(" >NUL 2>&1\r\n");
            sb.Append("del /F /Q ").Append(coreOld).Append(" >NUL 2>&1\r\n");
            sb.Append("exit /b 0\r\n");
            sb.Append(":restore\r\n");
            sb.Append("echo could not copy the new version; restoring>>").Append(log).Append("\r\n");
            sb.Append("move /Y ").Append(old).Append(' ').Append(exe).Append(" >NUL 2>&1\r\n");
            sb.Append(":relaunch\r\n");
            sb.Append("start \"\" ").Append(exe).Append("\r\n");
            sb.Append("exit /b 1\r\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ launch

        /// <summary>Starts the helper detached (it outlives this process). Call right before quitting.</summary>
        public static bool Launch(UpdateInstallPlan plan, out string error)
        {
            error = "";
            if (plan == null || !plan.CanInstall || !File.Exists(plan.ScriptPath)) { error = "No update is ready to install."; return false; }
            try
            {
                ProcessStartInfo psi;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    psi = new ProcessStartInfo("cmd.exe", "/d /c \"\"" + plan.ScriptPath + "\"\"");
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                }
                else
                {
                    // double fork through sh so the helper is reparented to launchd / init and survives our exit
                    psi = new ProcessStartInfo("/bin/sh", JoinArgs(new string[] { "-c", "nohup /bin/sh " + ShQuote(plan.ScriptPath) + " >/dev/null 2>&1 &" }));
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p != null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) p.WaitForExit(5000);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Shows a file / folder in Finder, Explorer or the file manager.</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start("explorer.exe", "/select,\"" + path.Replace("\"", "") + "\"");
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    RunDetached("/usr/bin/open", new string[] { "-R", path });
                else
                    RunDetached("xdg-open", new string[] { Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path) });
            }
            catch { }
        }

        private static void RunDetached(string file, string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, JoinArgs(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process.Start(psi)) { }
        }

        /// <summary>Runs a tool and waits; returns the exit code (-1 on timeout / start failure).</summary>
        public static int RunProcess(string file, string[] args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, JoinArgs(args));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "timed out";
                        return -1;
                    }
                    p.WaitForExit();
                    output = (so.Wait(2000) ? so.Result : "") + (se.Wait(2000) ? se.Result : "");
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        /// <summary>Windows-style argument quoting (also parsed this way by .NET on macOS / Linux).</summary>
        public static string JoinArgs(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                string s = a ?? "";
                if (s.Length > 0 && s.IndexOfAny(new char[] { ' ', '\t', '"', '\'', '\\' }) < 0) { sb.Append(s); continue; }
                sb.Append('"');
                int backslashes = 0;
                foreach (char c in s)
                {
                    if (c == '\\') { backslashes++; continue; }
                    if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                    sb.Append('\\', backslashes); backslashes = 0;
                    sb.Append(c);
                }
                sb.Append('\\', backslashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }
}
