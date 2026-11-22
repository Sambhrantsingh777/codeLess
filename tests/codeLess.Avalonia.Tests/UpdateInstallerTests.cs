using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Asset choice, install-kind detection, SHA256SUMS verification, helper scripts and update settings.</summary>
    public class UpdateInstallerTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "codeLess update test 'quoted' " + Guid.NewGuid().ToString("N").Substring(0, 6));

        public UpdateInstallerTests() { Directory.CreateDirectory(root); }

        public void Dispose()
        {
            try { MakeWritable(root); Directory.Delete(root, true); } catch { }
        }

        private static void MakeWritable(string dir)
        {
            if (OperatingSystem.IsWindows() || !Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Prepend(dir))
                try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        }

        // ------------------------------------------------------------------ asset choice

        [Theory]
        [InlineData(UpdateOs.Mac, true, UpdateInstallKind.MacAppBundle, "codeLess-mac-arm64.zip")]
        [InlineData(UpdateOs.Mac, false, UpdateInstallKind.MacAppBundle, "codeLess-mac-x64.zip")]
        [InlineData(UpdateOs.Mac, true, UpdateInstallKind.Unknown, "codeLess-mac-arm64.zip")]
        [InlineData(UpdateOs.Windows, false, UpdateInstallKind.WindowsInstaller, "codeLess-Setup.exe")]
        [InlineData(UpdateOs.Windows, true, UpdateInstallKind.WindowsInstaller, "codeLess-Setup.exe")]
        [InlineData(UpdateOs.Windows, false, UpdateInstallKind.WindowsPortable, "codeLess.exe")]
        [InlineData(UpdateOs.Linux, false, UpdateInstallKind.LinuxTarball, "codeLess-linux-x64.tar.gz")]
        [InlineData(UpdateOs.Linux, true, UpdateInstallKind.LinuxTarball, "codeLess-linux-arm64.tar.gz")]
        [InlineData(UpdateOs.Linux, false, UpdateInstallKind.LinuxDeb, "codeLess-linux-x64.deb")]
        [InlineData(UpdateOs.Linux, true, UpdateInstallKind.LinuxDeb, "codeLess-linux-arm64.deb")]
        public void AssetName_PerOsArchAndInstallKind(UpdateOs os, bool arm, UpdateInstallKind kind, string expected)
        {
            Assert.Equal(expected, UpdateAssets.AssetNameFor(os, arm, kind));
        }

        [Fact]
        public void InstallKind_IsDetectedFromTheProcessPath()
        {
            var mac = UpdateEnvironment.Detect(UpdateOs.Mac, true, "/Applications/My Apps/codeLess.app/Contents/MacOS/codeLess", null);
            Assert.Equal(UpdateInstallKind.MacAppBundle, mac.Kind);
            Assert.Equal("/Applications/My Apps/codeLess.app", mac.InstallPath);
            Assert.True(mac.SupportsAutomaticInstall);
            Assert.Equal(UpdateInstallKind.Unknown, UpdateEnvironment.Detect(UpdateOs.Mac, true, "/usr/local/share/dotnet/dotnet", null).Kind);

            var inno = UpdateEnvironment.Detect(UpdateOs.Windows, false, @"C:\Users\O'Brien\AppData\Local\Programs\codeLess\codeLess.exe",
                p => p == @"C:\Users\O'Brien\AppData\Local\Programs\codeLess\unins000.exe");
            Assert.Equal(UpdateInstallKind.WindowsInstaller, inno.Kind);
            Assert.Equal(UpdateInstallKind.WindowsPortable, UpdateEnvironment.Detect(UpdateOs.Windows, false, @"D:\tools\codeLess.exe", p => false).Kind);
            Assert.Equal(UpdateInstallKind.Unknown, UpdateEnvironment.Detect(UpdateOs.Windows, false, @"C:\Program Files\dotnet\dotnet.exe", p => false).Kind);

            var deb = UpdateEnvironment.Detect(UpdateOs.Linux, false, "/usr/lib/codeless/codeLess", null);
            Assert.Equal(UpdateInstallKind.LinuxDeb, deb.Kind);
            Assert.False(deb.SupportsAutomaticInstall);
            var tar = UpdateEnvironment.Detect(UpdateOs.Linux, true, "/home/me/apps/code less/codeLess", null);
            Assert.Equal(UpdateInstallKind.LinuxTarball, tar.Kind);
            Assert.Equal("/home/me/apps/code less", tar.InstallPath);
            Assert.Equal(UpdateInstallKind.Unknown, UpdateEnvironment.Detect(UpdateOs.Linux, false, "/usr/share/dotnet/dotnet", null).Kind);
        }

        // ------------------------------------------------------------------ versions

        [Theory]
        [InlineData("v3.3.4", "3.3.3", true)]
        [InlineData("3.3.10", "3.3.9", true)]
        [InlineData("3.4", "3.3.99", true)]
        [InlineData("v3.3.3", "3.3.3.0", false)]
        [InlineData("3.3.3", "3.3.4", false)]
        [InlineData("V4.0.0-rc.1", "3.9.9", true)]
        [InlineData("3.3.4-beta", "3.3.4", false)]
        [InlineData("", "3.3.3", false)]
        [InlineData("latest", "3.3.3", false)]
        [InlineData("3.3.4", "", false)]
        [InlineData(" 3.3.4 ", "3.3.3", true)]
        public void VersionCompare_EdgeCases(string latest, string current, bool newer)
        {
            Assert.Equal(newer, UpdateChecker.IsNewerVersion(latest, current));
        }

        // ------------------------------------------------------------------ checksums

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        [Fact]
        public void Sha256Sums_ParsesSha256sumOutput()
        {
            string a = new string('a', 64), b = new string('B', 64), c = new string('c', 64);
            var map = UpdateAssets.ParseSha256Sums(
                "# checksums\r\n" + a + "  codeLess-mac-arm64.zip\r\n" + b + " *codeLess-Setup.exe\n" + c + "  ./dist/codeLess-linux-x64.tar.gz\n" +
                "not a checksum line\n" + "1234  short.zip\n\n");
            Assert.Equal(3, map.Count);
            Assert.Equal(a, map["codeLess-mac-arm64.zip"]);
            Assert.Equal(b.ToLowerInvariant(), map["codeLess-Setup.exe"]);
            Assert.Equal(c, map["codeLess-linux-x64.tar.gz"]);
            Assert.False(map.ContainsKey("short.zip"));
        }

        [Fact]
        public void Sha256Verify_RefusesMismatchAndMissingEntry()
        {
            byte[] data = Encoding.UTF8.GetBytes("codeLess update payload");
            string file = Path.Combine(root, "codeLess-mac-arm64.zip");
            File.WriteAllBytes(file, data);
            Assert.Equal(Sha(data), UpdateAssets.ComputeSha256(file));

            var ok = UpdateAssets.ParseSha256Sums(Sha(data) + "  codeLess-mac-arm64.zip\n");
            Assert.True(UpdateAssets.Verify(file, "codeLess-mac-arm64.zip", ok, out string err), err);

            var bad = UpdateAssets.ParseSha256Sums(new string('0', 64) + "  codeLess-mac-arm64.zip\n");
            Assert.False(UpdateAssets.Verify(file, "codeLess-mac-arm64.zip", bad, out err));
            Assert.Contains("mismatch", err, StringComparison.OrdinalIgnoreCase);

            var other = UpdateAssets.ParseSha256Sums(Sha(data) + "  codeLess-mac-x64.zip\n");
            Assert.False(UpdateAssets.Verify(file, "codeLess-mac-arm64.zip", other, out err));
            Assert.Contains("no entry", err);
        }

        // ------------------------------------------------------------------ quoting + helper scripts

        [Fact]
        public void Quoting_HandlesSpacesApostrophesAndPercent()
        {
            Assert.Equal("'/Applications/My App'", UpdateInstaller.ShQuote("/Applications/My App"));
            Assert.Equal("'/Users/o'\"'\"'brien/codeLess.app'", UpdateInstaller.ShQuote("/Users/o'brien/codeLess.app"));
            Assert.Equal("\"C:\\Users\\O'Brien\\100%% done\\codeLess.exe\"", UpdateInstaller.CmdQuote("C:\\Users\\O'Brien\\100% done\\codeLess.exe"));
        }

        private static int DeadPid()
        {
            using var p = Process.Start(new ProcessStartInfo("/bin/sh", "-c true") { UseShellExecute = false });
            p.WaitForExit();
            return p.Id;
        }

        private static (int Code, string Output) RunSh(string script)
        {
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30000), "helper script did not finish");
            return (p.ExitCode, o);
        }

        private string FakeBundle(string parent, string name, string version, bool withExe = true)
        {
            string app = Path.Combine(parent, name);
            Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"),
                "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>\n<key>CFBundleExecutable</key>\n<string>codeLess</string>\n" +
                "<key>CFBundleShortVersionString</key>\n<string>" + version + "</string>\n</dict></plist>");
            if (withExe) File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "codeLess"), "#!/bin/sh\necho " + version + "\n");
            return app;
        }

        [Fact]
        public void MacHelperScript_SwapsBundle_InPathsWithSpacesAndApostrophes_AndRemovesBackup()
        {
            if (OperatingSystem.IsWindows()) return;
            string apps = Path.Combine(root, "My Apps");
            Directory.CreateDirectory(apps);
            string app = FakeBundle(apps, "codeLess.app", "3.3.3");
            string staged = FakeBundle(Path.Combine(root, "staged it's"), "codeLess.app", "3.3.4");
            var env = new UpdateEnvironment { Os = UpdateOs.Mac, Kind = UpdateInstallKind.MacAppBundle, InstallPath = app };
            string backup = UpdateInstaller.BackupPathFor(env);
            Assert.Equal(Path.Combine(apps, ".codeLess.app.update-backup"), backup);
            var a = new UpdateScriptArgs
            {
                ProcessId = DeadPid(), TargetPath = app, NewPath = staged, BackupPath = backup, LogPath = Path.Combine(root, "update.log"),
                RelaunchCommandOverride = "touch \"$APP.relaunched\"", ConfirmCommandOverride = "true"
            };
            string script = Path.Combine(root, "install-update.sh");
            File.WriteAllText(script, UpdateInstaller.BuildMacScript(a));
            var (code, _) = RunSh(script);
            Assert.Equal(0, code);
            Assert.Contains("3.3.4", File.ReadAllText(Path.Combine(app, "Contents", "Info.plist")));
            Assert.False(Directory.Exists(staged));
            Assert.False(Directory.Exists(backup));
            Assert.True(File.Exists(app + ".relaunched"));
            Assert.Contains("updated", File.ReadAllText(a.LogPath));
        }

        [Fact]
        public void MacHelperScript_RestoresThePreviousApp_WhenTheNewOneCannotBeMovedIn()
        {
            if (OperatingSystem.IsWindows()) return;
            string app = FakeBundle(root, "codeLess.app", "3.3.3");
            var a = new UpdateScriptArgs
            {
                ProcessId = DeadPid(), TargetPath = app, NewPath = Path.Combine(root, "missing", "codeLess.app"),
                BackupPath = Path.Combine(root, ".codeLess.app.update-backup"), LogPath = Path.Combine(root, "update.log"),
                RelaunchCommandOverride = "touch \"$APP.relaunched\"", ConfirmCommandOverride = "true"
            };
            string script = Path.Combine(root, "install-update.sh");
            File.WriteAllText(script, UpdateInstaller.BuildMacScript(a));
            var (code, _) = RunSh(script);
            Assert.Equal(1, code);
            Assert.Contains("3.3.3", File.ReadAllText(Path.Combine(app, "Contents", "Info.plist")));
            Assert.False(Directory.Exists(a.BackupPath));
            Assert.True(File.Exists(app + ".relaunched"));
        }

        [Fact]
        public void LinuxHelperScript_SwapsInstallFolder()
        {
            if (OperatingSystem.IsWindows()) return;
            string dir = Path.Combine(root, "code less");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "codeLess"), "old");
            string staged = Path.Combine(root, "staged");
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "codeLess"), "new");
            var a = new UpdateScriptArgs
            {
                ProcessId = DeadPid(), TargetPath = dir, NewPath = staged, BackupPath = dir + ".update-backup", LogPath = Path.Combine(root, "update.log"),
                RelaunchCommandOverride = "touch \"$DIR.relaunched\"", ConfirmCommandOverride = "true"
            };
            string script = Path.Combine(root, "install-update.sh");
            File.WriteAllText(script, UpdateInstaller.BuildLinuxScript(a));
            var (code, output) = RunSh(script);
            Assert.True(code == 0, output + File.ReadAllText(a.LogPath));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "codeLess")));
            Assert.False(Directory.Exists(a.BackupPath));
            Assert.True(File.Exists(dir + ".relaunched"));
        }

        [Fact]
        public void WindowsHelperScripts_QuoteEveryPath_AndRunInnoSetupSilently()
        {
            var a = new UpdateScriptArgs
            {
                ProcessId = 4242,
                TargetPath = @"C:\Users\O'Brien\100% tools\codeLess.exe",
                NewPath = @"C:\Users\O'Brien\AppData\Local\Temp\codeLess-update\3.3.4\codeLess-Setup.exe",
                LogPath = @"C:\Users\O'Brien\AppData\Local\Temp\codeLess-update\3.3.4\install\update.log",
                RelaunchExe = @"C:\Users\O'Brien\100% tools\codeLess.exe",
                BackupPath = @"C:\Users\O'Brien\100% tools\codeLess.exe.old"
            };
            string inno = UpdateInstaller.BuildWindowsInstallerScript(a);
            Assert.Contains("\"C:\\Users\\O'Brien\\AppData\\Local\\Temp\\codeLess-update\\3.3.4\\codeLess-Setup.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", inno);
            Assert.Contains("start \"\" \"C:\\Users\\O'Brien\\100%% tools\\codeLess.exe\"", inno);
            Assert.Contains("tasklist /FI \"PID eq 4242\"", inno);
            Assert.Contains("\r\n", inno);

            a.NewPath = @"C:\Users\O'Brien\AppData\Local\Temp\codeLess-update\3.3.4\codeLess.exe";
            string portable = UpdateInstaller.BuildWindowsPortableScript(a);
            Assert.Contains("move /Y \"C:\\Users\\O'Brien\\100%% tools\\codeLess.exe\" \"C:\\Users\\O'Brien\\100%% tools\\codeLess.exe.old\"", portable);
            Assert.Contains("copy /Y \"C:\\Users\\O'Brien\\AppData\\Local\\Temp\\codeLess-update\\3.3.4\\codeLess.exe\" \"C:\\Users\\O'Brien\\100%% tools\\codeLess.exe\"", portable);
            Assert.Contains(":restore", portable);
            // no path outside quotes: every occurrence of a path segment is inside "..."
            foreach (string line in portable.Split("\r\n"))
                if (line.Contains("O'Brien")) Assert.Matches("\"[^\"]*O'Brien[^\"]*\"", line);
        }

        // ------------------------------------------------------------------ preparation

        [Fact]
        public void PrepareMac_ExtractsZip_ValidatesBundle_AndWritesHelper_OrFallsBackWhenNotWritable()
        {
            if (!OperatingSystem.IsMacOS()) return;
            string src = Path.Combine(root, "build");
            FakeBundle(src, "codeLess.app", "3.3.4");
            string zip = Path.Combine(root, "codeLess-mac-arm64.zip");
            Assert.Equal(0, UpdateInstaller.RunProcess("/usr/bin/ditto", new[] { "-c", "-k", "--keepParent", Path.Combine(src, "codeLess.app"), zip }, 60000, out _));
            string apps = Path.Combine(root, "Apps it's");
            Directory.CreateDirectory(apps);
            string installed = FakeBundle(apps, "codeLess.app", "3.3.3");
            var env = new UpdateEnvironment { Os = UpdateOs.Mac, Arm64 = true, Kind = UpdateInstallKind.MacAppBundle, InstallPath = installed, ProcessId = 12345 };

            var plan = UpdateInstaller.Prepare(env, zip, "3.3.4", Path.Combine(root, "work"));
            Assert.True(plan.CanInstall, plan.ManualMessage);
            Assert.True(File.Exists(plan.ScriptPath));
            Assert.Contains("APP=" + UpdateInstaller.ShQuote(installed), plan.ScriptBody);
            Assert.Contains("PID=12345", plan.ScriptBody);
            Assert.Contains("open \"$APP\"", plan.ScriptBody);

            // wrong version inside the archive: refused
            var wrong = UpdateInstaller.Prepare(env, zip, "3.3.5", Path.Combine(root, "work2"));
            Assert.False(wrong.CanInstall);
            Assert.Contains("expected 3.3.5", wrong.ManualMessage);

            // the app's folder is not writable: reveal in Finder with instructions instead
            File.SetUnixFileMode(apps, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var manual = UpdateInstaller.Prepare(env, zip, "3.3.4", Path.Combine(root, "work3"));
                Assert.False(manual.CanInstall);
                Assert.Contains("drag the new codeLess.app", manual.ManualMessage);
                Assert.EndsWith("codeLess.app", manual.RevealPath);
            }
            finally { MakeWritable(apps); }
        }

        [Fact]
        public void BundleValidation_RequiresVersionAndExecutable()
        {
            string ok = FakeBundle(Path.Combine(root, "v"), "codeLess.app", "3.3.4");
            Assert.True(UpdateInstaller.ValidateMacBundle(ok, "v3.3.4", out _));
            Assert.False(UpdateInstaller.ValidateMacBundle(ok, "3.3.5", out _));
            string noExe = FakeBundle(Path.Combine(root, "n"), "codeLess.app", "3.3.4", withExe: false);
            Assert.False(UpdateInstaller.ValidateMacBundle(noExe, "3.3.4", out string err));
            Assert.Contains("executable", err);
        }

        [Fact]
        public void PrepareDeb_NeverRunsSudo_ButShowsTheAptCommand()
        {
            string deb = Path.Combine(root, "codeLess-linux-x64.deb");
            File.WriteAllText(deb, "x");
            var env = new UpdateEnvironment { Os = UpdateOs.Linux, Kind = UpdateInstallKind.LinuxDeb, InstallPath = "/usr/lib/codeless" };
            var plan = UpdateInstaller.Prepare(env, deb, "3.3.4", Path.Combine(root, "work"));
            Assert.False(plan.CanInstall);
            Assert.Equal("sudo apt install " + UpdateInstaller.ShQuote(deb), plan.ManualCommand);
            Assert.Equal(deb, plan.RevealPath);
            Assert.True(string.IsNullOrEmpty(plan.ScriptPath));
        }

        // ------------------------------------------------------------------ settings + state

        [Fact]
        public void UpdateMode_MigratesFromAutoCheckAndKeepsItInSync()
        {
            var s = new IdePersistentSettings();
            s.UpdateMode = null;
            s.AutoCheckForUpdates = true;
            Assert.Equal(UpdateModes.Default, s.GetEffectiveUpdateMode());
            s.AutoCheckForUpdates = false;
            Assert.Equal(UpdateModes.Manual, s.GetEffectiveUpdateMode());
            s.SetUpdateMode("start");
            Assert.True(s.AutoCheckForUpdates);
            Assert.Equal(UpdateModes.Start, s.GetEffectiveUpdateMode());
            s.SetUpdateMode("none");
            Assert.False(s.AutoCheckForUpdates);
            s.UpdateMode = "bogus";
            Assert.Equal(UpdateModes.Default, s.GetEffectiveUpdateMode());

            // the mode survives a JSON round trip (settings file)
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            s.SetUpdateMode("manual");
            var back = ser.Deserialize<IdePersistentSettings>(ser.Serialize(s));
            Assert.Equal(UpdateModes.Manual, back.GetEffectiveUpdateMode());
        }

        [Fact]
        public void AutoCheck_RespectsModeOfflineAndInterval()
        {
            var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            string recent = now.AddMinutes(-20).ToString("o");
            string old = now.AddHours(-30).ToString("o");
            Assert.True(UpdateService.ShouldAutoCheck("default", old, false, 1, now));
            Assert.True(UpdateService.ShouldAutoCheck("start", null, false, 1, now));
            Assert.False(UpdateService.ShouldAutoCheck("default", recent, false, 1, now));
            Assert.False(UpdateService.ShouldAutoCheck("default", old, true, 1, now));
            Assert.False(UpdateService.ShouldAutoCheck("manual", old, false, 1, now));
            Assert.False(UpdateService.ShouldAutoCheck("none", old, false, 1, now));
        }

        [Fact]
        public void MenuLabel_FollowsTheUpdateState()
        {
            var svc = new UpdateService("3.3.3", new UpdateEnvironment { Os = UpdateOs.Mac, Kind = UpdateInstallKind.MacAppBundle }, root);
            Assert.Equal("Check for Updates...", svc.MenuLabel);
            Assert.True(svc.MenuEnabled);
            Assert.Equal("codeLess-mac-x64.zip", svc.AssetName);
        }

        [Fact]
        public void MenuModel_HasCheckForUpdates_InHelpOnMac_AndInTheAppMenu()
        {
            var menus = WorkbenchMenuModel.Build();
            var help = menus.Single(m => m.Title == "Help");
            Assert.Contains(help.Items, e => e.Id == "app.checkUpdates" && e.VisibleOn(true));
            Assert.Contains(help.Items, e => e.Id == "app.checkUpdates" && e.VisibleOn(false));
            var app = menus.Single(m => m.Title == "codeLess");
            Assert.True(app.VisibleOn(true));
            foreach (string id in new[] { "app.about", "app.checkUpdates", "app.quit" })
                Assert.Contains(app.Items, e => e.Id == id);
            Assert.Contains(app.Items, e => e.IsSubmenu && e.Children.Any(c => c.Id == "pref.settings"));
        }
    }
}
