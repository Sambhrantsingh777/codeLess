using System;
using System.Diagnostics;
using System.IO;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Real end-to-end update on macOS against the published GitHub release, run only on demand:
    ///   CODELESS_LIVE_UPDATE_TEST=1 CODELESS_LIVE_UPDATE_FROM=/Applications/codeLess.app dotnet test --filter LiveUpdate
    /// Copies the installed app into a temp folder (the real install is never touched), pretends to be the previous
    /// version, then runs the real check → download → SHA-256 verify → extract/validate → generated helper script
    /// (relaunch replaced by a no-op so no window opens) and asserts the copy was swapped to the new version.
    /// </summary>
    public class LiveUpdateEndToEndTests
    {
        [Fact]
        public void LiveUpdate_Mac_Downloads_Verifies_And_Swaps_A_Copy_Of_The_App()
        {
            if (Environment.GetEnvironmentVariable("CODELESS_LIVE_UPDATE_TEST") != "1" || !OperatingSystem.IsMacOS()) return;
            string from = Environment.GetEnvironmentVariable("CODELESS_LIVE_UPDATE_FROM") ?? "/Applications/codeLess.app";
            Assert.True(Directory.Exists(from), "no app at " + from);
            string oldVersion = UpdateInstaller.ReadPlistString(Path.Combine(from, "Contents", "Info.plist"), "CFBundleShortVersionString");

            string root = Path.Combine(Path.GetTempPath(), "cl_live_update_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string apps = Path.Combine(root, "My Apps");
            Directory.CreateDirectory(apps);
            string app = Path.Combine(apps, "codeLess.app");
            try
            {
                Assert.Equal(0, UpdateInstaller.RunProcess("/bin/cp", new[] { "-R", from, app }, 120000, out _));

                var release = UpdateChecker.Check(oldVersion, 20000);
                Assert.True(release.Success, release.Error);
                Assert.True(release.IsNewer, "latest " + release.LatestVersion + " is not newer than " + oldVersion);

                // a process that has already exited stands in for the running codeLess the helper waits for
                var p = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false });
                p.WaitForExit();
                var env = UpdateEnvironment.Detect(UpdateOs.Mac, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64,
                    Path.Combine(app, "Contents", "MacOS", "codeLess"), File.Exists);
                env.ProcessId = p.Id;
                Assert.Equal(UpdateInstallKind.MacAppBundle, env.Kind);

                string dl = Path.Combine(root, "download");
                var d = UpdateDownloader.Download(release, UpdateAssets.AssetNameFor(env), dl, null, () => false);
                Assert.True(d.Success, d.Error);

                var plan = UpdateInstaller.Prepare(env, d.FilePath, release.LatestVersion, Path.Combine(root, "work"));
                Assert.True(plan.CanInstall, plan.Error + " " + plan.ManualMessage);
                string body = plan.ScriptBody.Replace("open \"$APP\"", "true");
                body = body.Replace("ps -axo command= | grep -F -- \"$APP/Contents/MacOS/\" | grep -v -F -- \"grep\" >/dev/null", "true");
                File.WriteAllText(plan.ScriptPath, body);
                int code = UpdateInstaller.RunProcess("/bin/sh", new[] { plan.ScriptPath }, 120000, out string log);
                Assert.True(code == 0, log);

                string newVersion = UpdateInstaller.ReadPlistString(Path.Combine(app, "Contents", "Info.plist"), "CFBundleShortVersionString");
                Assert.Equal(release.LatestVersion, newVersion);
                Assert.True(File.Exists(Path.Combine(app, "Contents", "MacOS", "codeLess")));
                Assert.False(Directory.Exists(UpdateInstaller.BackupPathFor(env)), "backup left behind");
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "cl_live_update_result.txt"),
                    DateTime.Now + ": " + oldVersion + " -> " + newVersion + " OK (" + d.FilePath + ")\n");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
