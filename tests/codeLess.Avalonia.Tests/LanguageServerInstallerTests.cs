using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Fakes for the language server installer (no network, no real npm).</summary>
    public static class LanguageServerInstallKit
    {
        public sealed class FakeProbe : ILanguageServerToolProbe
        {
            public readonly Dictionary<string, string> Tools = new Dictionary<string, string>(StringComparer.Ordinal);
            public string Find(string tool) => Tools.TryGetValue(tool, out var p) ? p : null;
        }

        /// <summary>Records steps; <see cref="OnRun"/> decides the exit code (and may create files / block).</summary>
        public sealed class FakeRunner : ILanguageServerProcessRunner
        {
            public readonly List<LanguageServerInstallStep> Steps = new List<LanguageServerInstallStep>();
            public Func<LanguageServerInstallStep, Action<string>, CancellationToken, int> OnRun = (s, o, c) => 0;
            public int Calls { get { lock (Steps) return Steps.Count; } }

            public int Run(LanguageServerInstallStep step, Action<string> output, CancellationToken ct, int timeoutMs)
            {
                lock (Steps) Steps.Add(step);
                return OnRun(step, output, ct);
            }
        }

        public sealed class FakeDownloader : ILanguageServerDownloader
        {
            public string Json;
            public string ArchivePath;
            public readonly List<string> Urls = new List<string>();

            public string GetText(string url, int timeoutMs, out string error)
            {
                Urls.Add(url);
                error = Json == null ? "HTTP 404" : null;
                return Json;
            }

            public bool DownloadFile(string url, string path, long maxBytes, CancellationToken ct, int timeoutMs, out string error)
            {
                Urls.Add(url);
                error = null;
                File.Copy(ArchivePath, path, true);
                return true;
            }
        }

        public static LanguageServerInstaller NewInstaller(string root, FakeProbe probe, ILanguageServerProcessRunner runner, string platform = null, ILanguageServerDownloader downloader = null)
        {
            var i = new LanguageServerInstaller(root, probe, runner, downloader ?? new FakeDownloader());
            if (platform != null) i.Platform = platform;
            i.IsOffline = () => false; // never the process-wide Offline Mode flag
            return i;
        }

        /// <summary>A real "npm" launcher (shell script / .cmd) that runs Fixtures/fake_npm.py. Null when no Python exists.</summary>
        public static string CreateFakeNpm(string toolsDir)
        {
            string py = LanguageServerRegistry.FindExecutable("python3", null) ?? LanguageServerRegistry.FindExecutable("python", null);
            string script = Path.Combine(AppContext.BaseDirectory, "fake_npm.py");
            Assert.True(File.Exists(script), "fixture missing: " + script);
            if (py == null) return null;
            Directory.CreateDirectory(toolsDir);
            if (OperatingSystem.IsWindows())
            {
                string cmd = Path.Combine(toolsDir, "npm.cmd");
                File.WriteAllText(cmd, "@\"" + py + "\" \"" + script + "\" %*\r\n");
                return cmd;
            }
            string sh = Path.Combine(toolsDir, "npm");
            File.WriteAllText(sh, "#!/bin/sh\nexec " + LanguageServerInstaller.PosixQuote(py) + " " + LanguageServerInstaller.PosixQuote(script) + " \"$@\"\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return sh;
        }

        public static void WaitUntil(Func<bool> cond, int ms = 20000, string what = "condition")
        {
            ms = PerfBudget.Wait(ms);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return;
                Thread.Sleep(20);
            }
            Assert.True(cond(), "Timed out waiting for " + what);
        }

        public static LanguageServerInstallResult Wait(LanguageServerInstallJob job)
        {
            Assert.True(job.Task.Wait(PerfBudget.Wait(30000)), "install did not finish");
            return job.Task.Result;
        }
    }

    public class LanguageServerInstallerTests : IDisposable
    {
        private readonly string root;

        public LanguageServerInstallerTests()
        {
            // spaces and an apostrophe on purpose: every command must survive them
            root = Path.Combine(Path.GetTempPath(), "codeLess LSP O'Inst " + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private static LanguageServerDefinition Def(string id) => LanguageServerRegistry.DefinitionById(id);

        private static string Fake(string name) => OperatingSystem.IsWindows() ? @"C:\tools\" + name + ".exe" : "/fake/bin/" + name;

        [Fact]
        public void Recipe_Selection_Follows_Toolchain_Availability_And_Platform()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, new LanguageServerInstallKit.FakeRunner(), "macos-arm64");

            // Python: npm first, the private venv only when npm is missing, a reason when neither exists.
            probe.Tools["npm"] = Fake("npm");
            probe.Tools["python"] = Fake("python3");
            var p = inst.Plan(Def("python"));
            Assert.Equal(LanguageServerInstallKind.Npm, p.Recipe.Kind);
            Assert.Contains("pyright", p.Steps[0].Args);
            probe.Tools.Remove("npm");
            p = inst.Plan(Def("python"));
            Assert.Equal(LanguageServerInstallKind.PythonVenv, p.Recipe.Kind);
            Assert.Equal(2, p.Steps.Count);
            Assert.Equal(new[] { "-m", "venv", Path.Combine(root, "python", "venv") }, p.Steps[0].Args);
            Assert.Contains("basedpyright", p.Steps[1].Args);
            Assert.StartsWith(Path.Combine(root, "python", "venv"), p.Steps[1].Exe);
            probe.Tools.Clear();
            p = inst.Plan(Def("python"));
            Assert.False(p.CanInstall);
            Assert.Contains("Node.js (npm)", p.Reason);
            Assert.Contains("Python 3", p.Reason);

            // TypeScript needs npm; html / css / json share one package folder.
            Assert.False(inst.Plan(Def("typescript")).CanInstall);
            probe.Tools["npm"] = Fake("npm");
            p = inst.Plan(Def("typescript"));
            Assert.Equal(new[] { "typescript-language-server", "typescript" }, p.Steps[0].Args.Skip(p.Steps[0].Args.Length - 2).ToArray());
            Assert.Equal(Path.Combine(root, "vscode-langservers-extracted"), inst.Plan(Def("css")).TargetDir);
            Assert.Equal(inst.Plan(Def("html")).TargetDir, inst.Plan(Def("json")).TargetDir);

            // Go / Rust / C#
            Assert.False(inst.Plan(Def("go")).CanInstall);
            probe.Tools["go"] = Fake("go");
            p = inst.Plan(Def("go"));
            Assert.Equal(new[] { "install", "golang.org/x/tools/gopls@latest" }, p.Steps[0].Args);
            Assert.Equal(Path.Combine(root, "go", "bin"), p.Steps[0].Env["GOBIN"]);
            probe.Tools["rustup"] = Fake("rustup");
            Assert.Equal(new[] { "component", "add", "rust-analyzer" }, inst.Plan(Def("rust")).Steps[0].Args);
            probe.Tools["dotnet"] = Fake("dotnet");
            Assert.Equal(new[] { "tool", "install", "csharp-ls", "--tool-path", Path.Combine(root, "csharp", "bin") }, inst.Plan(Def("csharp")).Steps[0].Args);

            // Downloads: per OS / CPU; clangd isn't downloaded on macOS (Command Line Tools ship it).
            p = inst.Plan(Def("clangd"));
            Assert.False(p.CanInstall);
            Assert.Contains("macos-arm64", p.Reason);
            Assert.Equal(LanguageServerInstallKind.GitHubRelease, inst.Plan(Def("lua")).Recipe.Kind);
            inst.Platform = "linux-x64";
            Assert.True(inst.Plan(Def("clangd")).CanInstall);
            inst.Platform = "linux-arm64";
            Assert.False(inst.Plan(Def("clangd")).CanInstall);
            Assert.True(inst.Plan(Def("lua")).CanInstall);
            inst.Platform = "windows-x64";
            Assert.True(inst.Plan(Def("clangd")).CanInstall);

            // No sudo-free route at all: the reason says why.
            p = inst.Plan(Def("java"));
            Assert.False(p.CanInstall);
            Assert.Contains("jdtls", p.Reason);
        }

        [Fact]
        public void Every_Process_Runs_In_The_Managed_Folder_Never_Global()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, new LanguageServerInstallKit.FakeRunner());
            foreach (var id in new[] { "typescript", "bash", "yaml", "json", "python" })
            {
                var p = inst.Plan(Def(id));
                foreach (var s in p.Steps)
                {
                    Assert.Equal(root, s.WorkingDir);
                    Assert.DoesNotContain("-g", s.Args);
                    Assert.DoesNotContain("--global", s.Args);
                    Assert.Contains("--ignore-scripts", s.Args);
                    Assert.Equal(p.TargetDir, s.Args[Array.IndexOf(s.Args, "--prefix") + 1]);
                }
            }
        }

        [Fact]
        public void Copyable_Commands_Quote_Paths_With_Spaces_And_Apostrophes()
        {
            string dir = "/Users/o'brien/My Servers/typescript";
            string posix = LanguageServerInstaller.FormatCommandLine("npm", new[] { "install", "--prefix", dir, "typescript" }, null, false);
            Assert.Equal("npm install --prefix '/Users/o'\\''brien/My Servers/typescript' typescript", posix);
            string go = LanguageServerInstaller.FormatCommandLine("go", new[] { "install", "golang.org/x/tools/gopls@latest" },
                new Dictionary<string, string> { ["GOBIN"] = "/home/me/it's here/bin" }, false);
            Assert.Equal("GOBIN='/home/me/it'\\''s here/bin' go install golang.org/x/tools/gopls@latest", go);
            string win = LanguageServerInstaller.FormatCommandLine("npm", new[] { "install", "--prefix", @"C:\Users\O'Brien\App Data\servers\ts", "typescript" }, null, true);
            Assert.Equal("npm install --prefix \"C:\\Users\\O'Brien\\App Data\\servers\\ts\" typescript", win);
            Assert.Equal("''", LanguageServerInstaller.PosixQuote(""));
            Assert.Equal("a$b", LanguageServerInstaller.PosixQuote("a$b").Trim('\''));

            // cmd.exe shims (npm.cmd on Windows): the apostrophe / space path reaches the program as one argument.
            string cmdArgs = LanguageServerRegistry.BuildCmdExeArguments(@"C:\Program Files\nodejs\npm.cmd", new[] { "install", "--prefix", @"C:\Users\O'Brien & Co\servers" });
            Assert.Contains("^\"C:\\Users\\O'Brien ^& Co\\servers^\"", cmdArgs);

            // The plan's display matches its argument vector.
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, new LanguageServerInstallKit.FakeRunner(), OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");
            var plan = inst.Plan(Def("bash"));
            Assert.StartsWith("npm install --prefix ", plan.ManualCommand);
            Assert.Contains(OperatingSystem.IsWindows() ? "\"" + Path.Combine(root, "bash") + "\"" : LanguageServerInstaller.PosixQuote(Path.Combine(root, "bash")), plan.ManualCommand);
            Assert.EndsWith("bash-language-server", plan.ManualCommand);
        }

        private static string MakeExe(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name + (OperatingSystem.IsWindows() ? ".cmd" : ""));
            File.WriteAllText(p, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\n");
            return p;
        }

        [Fact]
        public void Resolution_Order_Is_User_Setting_Then_Managed_Folder_Then_Path()
        {
            var empty = LanguageServerUserSettings.Parse("", false);
            string emptyRoot = Path.Combine(root, "empty");
            var before = LanguageServerRegistry.Resolve("shellscript", null, empty, emptyRoot);
            Assert.False(before.FromManagedFolder);

            string managed = MakeExe(Path.Combine(root, "bash", "node_modules", ".bin"), "bash-language-server");
            var r = LanguageServerRegistry.Resolve("shellscript", null, empty, root);
            Assert.True(r.CanStart);
            Assert.True(r.FromManagedFolder);
            Assert.Equal(managed, r.Executable);
            Assert.Equal(new[] { "start" }, r.Args);

            // Extracted release archive layout (<pkg>/<name_version>/bin) and a private venv are found too.
            string lua = MakeExe(Path.Combine(root, "lua", "lua-language-server-3.19.2", "bin"), "lua-language-server");
            Assert.Equal(lua, LanguageServerRegistry.Resolve("lua", null, empty, root).Executable);
            string venv = MakeExe(Path.Combine(root, "python", "venv", OperatingSystem.IsWindows() ? "Scripts" : "bin"), "basedpyright-langserver");
            var py = LanguageServerRegistry.Resolve("python", null, empty, root);
            Assert.Equal(venv, py.Executable);
            Assert.Equal("basedpyright", py.DisplayName);

            // A user override wins over the managed folder.
            string mine = MakeExe(Path.Combine(root, "mine"), "my-bash-server");
            var o = LanguageServerRegistry.Resolve("shellscript", null, LanguageServerUserSettings.Parse("bash = \"" + mine + "\" --stdio", false), root);
            Assert.True(o.FromUserOverride);
            Assert.False(o.FromManagedFolder);
            Assert.Equal(mine, o.Executable);
            // ...and "off" still turns it off.
            Assert.True(LanguageServerRegistry.Resolve("shellscript", null, LanguageServerUserSettings.Parse("bash = off", false), root).Disabled);
        }

        [Fact]
        public void Failures_Are_Remembered_With_Backoff_And_A_User_Retry_Ignores_It()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var runner = new LanguageServerInstallKit.FakeRunner();
            runner.OnRun = (s, o, c) => { o("npm ERR! 404"); return 1; };
            var now = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, runner);
            inst.UtcNow = () => now;
            var bash = Def("bash");

            Assert.True(inst.ShouldAutoInstall(bash, out _));
            var r = LanguageServerInstallKit.Wait(inst.Start(bash, false));
            Assert.False(r.Success);
            Assert.Contains("exited with code 1", r.Message);
            Assert.Contains("npm ERR! 404", r.Log);
            Assert.StartsWith("npm install --prefix", r.Command);
            Assert.Equal(1, runner.Calls);

            // No loop: automatic attempts are refused within 24 h (also after a restart: the memo is on disk).
            Assert.False(inst.ShouldAutoInstall(bash, out string why));
            Assert.Contains("failed", why);
            Assert.Contains("Install/Update", why);
            var again = LanguageServerInstallKit.Wait(inst.Start(bash, false));
            Assert.False(again.Success);
            Assert.Equal(1, runner.Calls);
            var restarted = LanguageServerInstallKit.NewInstaller(root, probe, runner);
            restarted.UtcNow = () => now.AddHours(2);
            Assert.False(restarted.ShouldAutoInstall(bash, out _));
            // Other servers are not affected.
            Assert.True(restarted.ShouldAutoInstall(Def("yaml"), out _));

            // After the backoff it is tried again; a user-initiated install retries at once.
            now = now.AddHours(25);
            Assert.True(inst.ShouldAutoInstall(bash, out _));
            now = now.AddHours(-24);
            LanguageServerInstallKit.Wait(inst.Start(bash, true));
            Assert.Equal(2, runner.Calls);

            // Success clears the memo.
            runner.OnRun = (s, o, c) => { MakeExe(Path.Combine(root, "bash", "node_modules", ".bin"), "bash-language-server"); return 0; };
            Assert.True(LanguageServerInstallKit.Wait(inst.Start(bash, true)).Success);
            Assert.True(inst.ShouldAutoInstall(bash, out _));
        }

        [Fact]
        public void Offline_Mode_Refuses_Installs_Without_Running_Anything()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var runner = new LanguageServerInstallKit.FakeRunner();
            var dl = new LanguageServerInstallKit.FakeDownloader();
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, runner, "linux-x64", dl);
            inst.IsOffline = () => true;
            Assert.False(inst.ShouldAutoInstall(Def("typescript"), out string why));
            Assert.Contains("Offline Mode", why);
            var r = LanguageServerInstallKit.Wait(inst.Start(Def("typescript"), true));
            Assert.False(r.Success);
            Assert.True(r.BlockedOffline);
            Assert.Equal(0, runner.Calls);
            r = LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true));
            Assert.True(r.BlockedOffline);
            Assert.Empty(dl.Urls);
            // Offline refusals aren't failures: once online it installs right away.
            inst.IsOffline = () => false;
            Assert.True(inst.ShouldAutoInstall(Def("typescript"), out _));
        }

        [Fact]
        public void Concurrent_Requests_Share_One_Install_And_Installs_Can_Be_Cancelled()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var gate = new ManualResetEventSlim(false);
            var runner = new LanguageServerInstallKit.FakeRunner();
            runner.OnRun = (s, o, c) =>
            {
                gate.Wait(c);
                MakeExe(Path.Combine(root, "vscode-langservers-extracted", "node_modules", ".bin"), "vscode-css-language-server");
                MakeExe(Path.Combine(root, "vscode-langservers-extracted", "node_modules", ".bin"), "vscode-html-language-server");
                return 0;
            };
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, runner);
            var jobs = new LanguageServerInstallJob[8];
            Parallel.For(0, jobs.Length, i => jobs[i] = inst.Start(Def(i % 2 == 0 ? "css" : "html"), false));
            Assert.All(jobs, j => Assert.Same(jobs[0], j));
            Assert.Same(jobs[0], inst.GetActiveJob(Def("json")));
            Assert.True(inst.ShouldAutoInstall(Def("css"), out _)); // joining a running install is allowed
            gate.Set();
            Assert.True(LanguageServerInstallKit.Wait(jobs[0]).Success);
            Assert.Equal(1, runner.Calls);
            Assert.Null(inst.GetActiveJob(Def("css")));

            // cancellation: the job ends as cancelled and is not remembered as a failure
            var block = new LanguageServerInstallKit.FakeRunner();
            block.OnRun = (s, o, c) => { c.WaitHandle.WaitOne(); c.ThrowIfCancellationRequested(); return 0; };
            var inst2 = LanguageServerInstallKit.NewInstaller(Path.Combine(root, "two"), probe, block);
            var job = inst2.Start(Def("yaml"), false);
            LanguageServerInstallKit.WaitUntil(() => block.Calls == 1, what: "install started");
            job.Cancel();
            var r = LanguageServerInstallKit.Wait(job);
            Assert.True(r.Cancelled);
            Assert.False(r.Success);
            Assert.True(inst2.ShouldAutoInstall(Def("yaml"), out _));
        }

        private static string BuildLuaArchive(string dir)
        {
            string src = Path.Combine(dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "bin"));
            File.WriteAllText(Path.Combine(src, "bin", "lua-language-server"), "#!/bin/sh\necho fake\n");
            File.WriteAllText(Path.Combine(src, "bin", "lua-language-server.exe"), "MZ fake");
            File.WriteAllText(Path.Combine(src, "main.lua"), "-- fake");
            string zip = Path.Combine(dir, "lua-language-server-3.19.2-win32-x64.zip");
            ZipFile.CreateFromDirectory(src, zip);
            return zip;
        }

        private static string ReleaseJson(string name, long size, string digest, string url = null)
        {
            url ??= "https://github.com/LuaLS/lua-language-server/releases/download/3.19.2/" + name;
            return "{\"tag_name\":\"3.19.2\",\"assets\":[" +
                   "{\"name\":\"lua-language-server-3.19.2-submodules.zip\",\"size\":5,\"browser_download_url\":\"https://github.com/LuaLS/lua-language-server/releases/download/3.19.2/x.zip\"}," +
                   "{\"name\":\"" + name + "\",\"size\":" + size + (digest != null ? ",\"digest\":\"" + digest + "\"" : "") + ",\"browser_download_url\":\"" + url + "\"}]}";
        }

        [Fact]
        public void Release_Download_Is_Verified_With_Sha256_And_Extracted_Into_The_Managed_Folder()
        {
            string work = Path.Combine(root, "work");
            Directory.CreateDirectory(work);
            string zip = BuildLuaArchive(work);
            long size = new FileInfo(zip).Length;
            string sha = LanguageServerInstaller.Sha256File(zip);
            string managed = Path.Combine(root, "managed");
            var dl = new LanguageServerInstallKit.FakeDownloader { ArchivePath = zip };

            // wrong checksum -> refused, nothing installed
            dl.Json = ReleaseJson(Path.GetFileName(zip), size, "sha256:" + new string('0', 64));
            var inst = LanguageServerInstallKit.NewInstaller(managed, new LanguageServerInstallKit.FakeProbe(), new DefaultLanguageServerProcessRunner(), "windows-x64", dl);
            var r = LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true));
            Assert.False(r.Success);
            Assert.Contains("SHA-256 mismatch", r.Message);
            Assert.False(Directory.Exists(Path.Combine(managed, "lua")));
            Assert.Equal("https://api.github.com/repos/LuaLS/lua-language-server/releases/latest", dl.Urls[0]);

            // a download location outside the project's releases -> refused before downloading
            dl.Urls.Clear();
            dl.Json = ReleaseJson(Path.GetFileName(zip), size, "sha256:" + sha, "https://evil.example/lua.zip");
            r = LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true));
            Assert.Contains("unexpected download location", r.Message);
            Assert.Single(dl.Urls);

            // size mismatch without a digest -> refused
            dl.Json = ReleaseJson(Path.GetFileName(zip), size + 1, null);
            r = LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true));
            Assert.Contains("expected " + (size + 1), r.Message);

            // correct checksum -> extracted, found by the registry, staging cleaned up
            dl.Json = ReleaseJson(Path.GetFileName(zip), size, "sha256:" + sha.ToUpperInvariant());
            r = LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true));
            Assert.True(r.Success, r.Message + "\n" + r.Log);
            Assert.Contains("SHA-256 verified", r.Log);
            Assert.True(File.Exists(Path.Combine(managed, "lua", "main.lua")));
            var res = LanguageServerRegistry.Resolve("lua", null, LanguageServerUserSettings.Parse("", false), managed);
            Assert.True(res.FromManagedFolder);
            Assert.StartsWith(Path.Combine(managed, "lua", "bin"), res.Executable);
            Assert.Empty(Directory.GetDirectories(managed).Where(d => Path.GetFileName(d).StartsWith(".")));
            // an update replaces the folder in place
            Assert.True(LanguageServerInstallKit.Wait(inst.Start(Def("lua"), true)).Success);
            Assert.True(File.Exists(Path.Combine(managed, "lua", "main.lua")));
        }

        [Fact]
        public void Fake_Npm_Process_Installs_And_The_Manager_Starts_The_Server_For_Open_Documents()
        {
            string fakeNpm = LanguageServerInstallKit.CreateFakeNpm(Path.Combine(root, "tools dir"));
            if (fakeNpm == null) return; // no Python on this machine
            if (LanguageServerRegistry.FindExecutable("bash-language-server", null) != null) return; // a real server would be used instead
            string managed = Path.Combine(root, "servers");
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = fakeNpm;
            var installer = new LanguageServerInstaller(managed, probe, null, new LanguageServerInstallKit.FakeDownloader());
            installer.IsOffline = () => false;

            string ws = Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            string file = Path.Combine(ws, "run.sh");
            File.WriteAllText(file, "echo BAD\n");
            var output = new List<string>();
            var finished = new List<LanguageServerInstallResult>();
            using var m = new LanguageServerManager(a => a());
            m.RequestTimeoutMs = PerfBudget.Wait(m.RequestTimeoutMs);
            m.UserSettingsProvider = () => LanguageServerUserSettings.Parse("", false);
            m.Installer = installer;
            m.AutoInstallEnabled = () => true;
            m.InstallOutput += l => { lock (output) output.Add(l); };
            m.InstallFinished += r => { lock (finished) finished.Add(r); };
            var hints = new List<LanguageServerHint>();
            m.ServerMissing += h => hints.Add(h);
            m.SetWorkspaceRoot(ws);
            m.OpenDocument(file, () => "echo BAD\n");
            LanguageServerInstallKit.WaitUntil(() => { lock (finished) return finished.Count == 1; }, what: "install finished");
            Assert.True(finished[0].Success, finished[0].Message + "\n" + finished[0].Log);
            LanguageServerInstallKit.WaitUntil(() => m.GetStatus(file)?.State == LanguageServerState.Running, what: "server running");
            Assert.Equal("{ } Bash", m.GetStatus(file).StatusBarText);
            Assert.StartsWith(Path.Combine(managed, "bash"), m.GetInstanceForPath(file).Resolution.Executable);
            Assert.True(m.GetInstanceForPath(file).Resolution.FromManagedFolder);
            // diagnostics for the already-open document arrive without reopening it
            LanguageServerInstallKit.WaitUntil(() => LspDiagnosticsStore.Count(file) > 0, what: "diagnostics");
            Assert.Empty(hints);
            lock (output)
            {
                Assert.Contains(output, l => l.Contains("added 1 packages"));
                Assert.Contains(output, l => l.StartsWith("$ npm install --prefix"));
            }
            // the real process got the managed path (spaces + apostrophe) as one argument
            string calls = File.ReadAllText(Path.Combine(managed, "fake_npm_calls.txt"));
            Assert.Contains(Path.Combine(managed, "bash").Replace("\\", "\\\\"), calls);
            m.ShutdownAll(2000);
        }

        [Fact]
        public void Auto_Install_Respects_The_Setting_Overrides_And_Missing_Toolchains()
        {
            var probe = new LanguageServerInstallKit.FakeProbe();
            var runner = new LanguageServerInstallKit.FakeRunner();
            var installer = LanguageServerInstallKit.NewInstaller(Path.Combine(root, "servers"), probe, runner);
            string ws = Path.Combine(root, "ws2");
            Directory.CreateDirectory(ws);
            string file = Path.Combine(ws, "a.sh");
            File.WriteAllText(file, "echo hi\n");
            if (LanguageServerRegistry.FindExecutable("bash-language-server", null) != null) return;

            // npm missing -> today's hint, with the reason
            using (var m = new LanguageServerManager(a => a()))
            {
                var hints = new List<LanguageServerHint>();
                m.ServerMissing += h => hints.Add(h);
                m.UserSettingsProvider = () => LanguageServerUserSettings.Parse("", false);
                m.Installer = installer;
                m.AutoInstallEnabled = () => true;
                m.SetWorkspaceRoot(ws);
                m.OpenDocument(file, () => "echo hi\n");
                LanguageServerInstallKit.WaitUntil(() => hints.Count == 1, what: "hint");
                Assert.False(hints[0].InstallFailed);
                Assert.Contains("No Bash language server found", hints[0].Message);
                Assert.Contains("Node.js (npm) was not found", hints[0].Message);
                Assert.Equal(0, runner.Calls);
                Assert.Equal(LanguageServerState.NotInstalled, m.GetStatus(file).State);
                Assert.True(m.GetStatus(file).CanInstall);
            }

            probe.Tools["npm"] = Fake("npm");
            // setting off -> hint, no install
            using (var m = new LanguageServerManager(a => a()))
            {
                var hints = new List<LanguageServerHint>();
                m.ServerMissing += h => hints.Add(h);
                m.UserSettingsProvider = () => LanguageServerUserSettings.Parse("", false);
                m.Installer = installer;
                m.AutoInstallEnabled = () => false;
                m.SetWorkspaceRoot(ws);
                m.OpenDocument(file, () => "echo hi\n");
                LanguageServerInstallKit.WaitUntil(() => hints.Count == 1, what: "hint");
                Assert.Equal("No Bash language server found — install one with: " + hints[0].InstallCommand, hints[0].Message);
                Assert.Equal(0, runner.Calls);
            }
            // a user override that points nowhere -> never replaced by an install
            using (var m = new LanguageServerManager(a => a()))
            {
                m.UserSettingsProvider = () => LanguageServerUserSettings.Parse("bash = codeless-no-such-server", false);
                m.Installer = installer;
                m.AutoInstallEnabled = () => true;
                m.SetWorkspaceRoot(ws);
                m.OpenDocument(file, () => "echo hi\n");
                LanguageServerInstallKit.WaitUntil(() => m.GetStatus(file)?.State == LanguageServerState.NotInstalled, what: "not installed");
                Thread.Sleep(100);
                Assert.Equal(0, runner.Calls);
            }
            // a failing install -> one failure notification with the command, status back to "No Bash server"
            runner.OnRun = (s, o, c) => { o("npm ERR! network"); return 1; };
            using (var m = new LanguageServerManager(a => a()))
            {
                var hints = new List<LanguageServerHint>();
                m.ServerMissing += h => { lock (hints) hints.Add(h); };
                m.UserSettingsProvider = () => LanguageServerUserSettings.Parse("", false);
                m.Installer = installer;
                m.AutoInstallEnabled = () => true;
                m.SetWorkspaceRoot(ws);
                m.OpenDocument(file, () => "echo hi\n");
                LanguageServerInstallKit.WaitUntil(() => { lock (hints) return hints.Count == 1; }, what: "failure hint");
                Assert.True(hints[0].InstallFailed);
                Assert.Contains("Couldn't install the Bash language server", hints[0].Message);
                Assert.StartsWith("npm install --prefix", hints[0].InstallCommand);
                LanguageServerInstallKit.WaitUntil(() => m.GetStatus(file)?.State == LanguageServerState.NotInstalled, what: "back to not installed");
                Assert.Equal(1, runner.Calls);
            }
        }

        [Fact]
        public void Output_Listeners_Get_Every_Line_In_Order_Even_When_Subscribing_Late()
        {
            // The worker starts logging before callers subscribe (and callers join running jobs): nothing may be lost.
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = Fake("npm");
            var started = new ManualResetEventSlim(false);
            var go = new ManualResetEventSlim(false);
            var runner = new LanguageServerInstallKit.FakeRunner();
            runner.OnRun = (s, o, c) =>
            {
                o("early line");
                started.Set();
                go.Wait(c);
                for (int i = 0; i < 50; i++) o("late " + i);
                return 1;
            };
            var inst = LanguageServerInstallKit.NewInstaller(root, probe, runner);
            var job = inst.Start(Def("yaml"), true);
            Assert.True(started.Wait(PerfBudget.Wait(10000)));
            var first = new List<string>();
            job.AddOutputListener(l => { lock (first) first.Add(l); });
            go.Set();
            var second = new List<string>();
            LanguageServerInstallKit.Wait(job);
            job.AddOutputListener(l => second.Add(l)); // after completion: full replay
            lock (first)
            {
                Assert.StartsWith("[YAML] Installing via npm", first[0]);
                Assert.StartsWith("$ npm install --prefix", first[1]);
                Assert.Equal("  early line", first[2]);
                for (int i = 0; i < 50; i++) Assert.Equal("  late " + i, first[3 + i]);
                Assert.Equal(first, second);
            }
        }

        [Fact]
        public void Tool_In_A_Folder_With_Spaces_Apostrophes_And_Ampersands_Runs_With_Exact_Arguments()
        {
            // Windows: a .cmd tool runs through cmd.exe (the npm.cmd case); elsewhere a shell script.
            string tools = Path.Combine(root, "Program Files & Co", "node js");
            Directory.CreateDirectory(tools);
            string tool;
            if (OperatingSystem.IsWindows())
            {
                tool = Path.Combine(tools, "fake tool.cmd");
                File.WriteAllText(tool, "@echo off\r\necho ARGS:%*\r\n");
            }
            else
            {
                tool = Path.Combine(tools, "fake tool");
                File.WriteAllText(tool, "#!/bin/sh\nfor a in \"$@\"; do echo \"ARG:$a\"; done\n");
                File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string prefix = Path.Combine(root, "servers", "O'Brien & Sons", "bash");
            var step = new LanguageServerInstallStep { Exe = tool, Args = new[] { "install", "--prefix", prefix, "pkg" }, WorkingDir = root };
            var lines = new List<string>();
            int code = new DefaultLanguageServerProcessRunner().Run(step, l => { lock (lines) lines.Add(l); }, CancellationToken.None, PerfBudget.Wait(30000));
            Assert.True(code == 0, "exit " + code + ": " + string.Join("\n", lines));
            if (OperatingSystem.IsWindows())
                Assert.Contains(lines, l => l.Contains("ARGS:") && l.Contains("\"" + prefix + "\"") && l.Contains("install") && l.Contains("pkg"));
            else
                Assert.Equal(new[] { "ARG:install", "ARG:--prefix", "ARG:" + prefix, "ARG:pkg" }, lines);
        }

        [Fact]
        public void Status_Text_For_Installing_State()
        {
            var st = new LanguageServerStatus { LanguageId = "typescript", State = LanguageServerState.Installing, ServerName = "TypeScript" };
            Assert.Equal("⟳ Installing TypeScript language server…", st.StatusBarText);
            Assert.Contains("cancel", st.Tooltip);
        }

        [Fact]
        public void Default_Managed_Root_Is_Per_User_And_Settings_Default_On()
        {
            string d = LanguageServerInstaller.DefaultManagedRoot();
            Assert.True(Path.IsPathRooted(d));
            if (Environment.GetEnvironmentVariable(LanguageServerInstaller.ManagedRootEnvVar) == null)
                Assert.EndsWith(OperatingSystem.IsWindows() ? Path.Combine("codeLess", "servers") : Path.Combine(".codeless", "servers"), d);
            Assert.True(new IdePersistentSettings().AutoInstallLanguageServers);
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            Assert.True(ser.Deserialize<IdePersistentSettings>("{\"TabSize\":4}").AutoInstallLanguageServers);
            Assert.False(ser.Deserialize<IdePersistentSettings>("{\"AutoInstallLanguageServers\":false}").AutoInstallLanguageServers);
        }
    }
}
