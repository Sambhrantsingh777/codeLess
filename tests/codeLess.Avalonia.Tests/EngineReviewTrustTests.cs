using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>ENGINE_REVIEW LSP-1 (critical): Workspace Trust - store, gates in the engines.</summary>
    public class EngineReviewTrustCoreTests : IDisposable
    {
        private readonly List<string> dirs = new List<string>();

        private string NewDir(string tag, bool trusted)
        {
            string d = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cl-trust-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(d);
            dirs.Add(d);
            if (trusted) TestTrust.Trust(d);
            return d;
        }

        public void Dispose()
        {
            foreach (string d in dirs) { try { Directory.Delete(d, true); } catch { } }
            LanguageServerRegistry.ClearCache();
        }

        private static void Exe(string path, string content = "#!/bin/sh\necho hi\n")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            if (Path.DirectorySeparatorChar == '/') File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        [Fact]
        public void Store_FirstOpenIsUndecided_RestrictTrustRevoke_ArePersisted()
        {
            string ws = NewDir("store", false);
            Assert.Equal(WorkspaceTrustDecision.None, WorkspaceTrust.GetDecision(ws));
            Assert.True(WorkspaceTrust.NeedsPrompt(ws));
            Assert.True(WorkspaceTrust.IsRestricted(ws));

            WorkspaceTrust.Restrict(ws);
            Assert.Equal(WorkspaceTrustDecision.Restricted, WorkspaceTrust.GetDecision(ws));
            Assert.False(WorkspaceTrust.NeedsPrompt(ws));
            Assert.True(WorkspaceTrust.IsRestricted(ws));

            int raised = 0;
            Action h = () => Interlocked.Increment(ref raised);
            WorkspaceTrust.TrustChanged += h;
            try { WorkspaceTrust.Trust(ws); }
            finally { WorkspaceTrust.TrustChanged -= h; }
            Assert.True(raised >= 1);
            Assert.True(WorkspaceTrust.IsTrusted(ws));
            Assert.True(WorkspaceTrust.IsTrusted(Path.Combine(ws, "sub", "file.py")));
            Assert.Contains(WorkspaceTrust.GetTrustedFolders(), f => LspText.PathKey(f) == LspText.PathKey(ws));
            string json = File.ReadAllText(WorkspaceTrust.StorePath);
            Assert.Contains(Path.GetFileName(ws), json);

            WorkspaceTrust.Revoke(ws);
            Assert.Equal(WorkspaceTrustDecision.None, WorkspaceTrust.GetDecision(ws));
            Assert.DoesNotContain(Path.GetFileName(ws), File.ReadAllText(WorkspaceTrust.StorePath));
        }

        [Fact]
        public void Prompt_TrustParentFolder_CoversSiblings_ButNeverTheHomeFolder()
        {
            string parent = NewDir("parent", false);
            string a = Path.Combine(parent, "a");
            string b = Path.Combine(parent, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            WorkspaceTrust.ApplyPromptAnswer(a, true, true);
            Assert.True(WorkspaceTrust.IsTrusted(b));
            Assert.Equal(LspText.PathKey(parent), LspText.PathKey(WorkspaceTrust.TrustedRootFor(a)));
            WorkspaceTrust.Revoke(parent);
            Assert.Null(WorkspaceTrust.ParentFolderForPrompt(Path.Combine(LanguageServerRegistry.HomeDir(), "some-project")));
            string sibling = NewDir("sib", true);
            Assert.True(WorkspaceTrust.IsTrusted(sibling));
            Assert.False(WorkspaceTrust.IsTrusted(sibling + "-suffix")); // a name prefix is not "inside"
        }

        [Fact]
        public void LSP1_WorkspaceLocalServer_IsOnlyUsedInTrustedFolders()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string untrusted = NewDir("ts-r", false);
            string trusted = NewDir("ts-t", true);
            string name = "cl-fake-ls-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Exe(Path.Combine(untrusted, "node_modules", ".bin", name));
            Exe(Path.Combine(trusted, "node_modules", ".bin", name));
            LanguageServerRegistry.ClearCache();
            Assert.Null(LanguageServerRegistry.FindExecutable(name, untrusted));
            Assert.DoesNotContain(LanguageServerRegistry.SearchDirectories(untrusted), d => d.StartsWith(untrusted, StringComparison.Ordinal));
            Assert.Equal(Path.Combine(trusted, "node_modules", ".bin", name), LanguageServerRegistry.FindExecutable(name, trusted));

            Exe(Path.Combine(untrusted, ".venv", "bin", "python"));
            Exe(Path.Combine(trusted, ".venv", "bin", "python"));
            Assert.Null(LanguageServerRegistry.FindWorkspaceVenvPython(untrusted));
            Assert.NotNull(LanguageServerRegistry.FindWorkspaceVenvPython(trusted));
        }

        [Fact]
        public void LSP1_ServersThatRunProjectCode_AreRestricted_UntilTrusted()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string untrusted = NewDir("rust-r", false);
            string trusted = NewDir("rust-t", true);
            var user = LanguageServerUserSettings.Parse("rust = /bin/echo\njava = /bin/echo\ncsharp = /bin/echo", false);
            foreach (string lang in new[] { "rust", "java", "csharp" })
            {
                var r = LanguageServerRegistry.Resolve(lang, untrusted, user);
                Assert.True(r.Restricted, lang);
                Assert.False(r.CanStart);
                Assert.Contains("Restricted Mode", r.Problem);
                Assert.True(LanguageServerRegistry.Resolve(lang, trusted, user).CanStart, lang);
            }
        }

        [Fact]
        public void LSP1_TypeScriptServer_IsPinnedToANonWorkspaceTsserver_InRestrictedMode()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string ws = NewDir("tsws", false);
            Exe(Path.Combine(ws, "node_modules", "typescript", "lib", "tsserver.js"), "// workspace tsserver\n");
            string install = NewDir("tsinstall", false);
            string exe = Path.Combine(install, "bin", "typescript-language-server");
            Exe(exe);
            var user = LanguageServerUserSettings.Parse("typescript = " + exe + " --stdio", false);
            var none = LanguageServerRegistry.Resolve("typescript", ws, user);
            Assert.True(none.Restricted); // only the workspace's TypeScript exists -> don't start
            string global = Path.Combine(install, "lib", "node_modules", "typescript", "lib", "tsserver.js");
            Exe(global, "// global tsserver\n");
            var pinned = LanguageServerRegistry.Resolve("typescript", ws, user);
            Assert.True(pinned.CanStart);
            Assert.Equal(global, LspJson.GetString(LspJson.Get(pinned.InitializationOptions, "tsserver"), "path"));
            TestTrust.Trust(ws);
            var t = LanguageServerRegistry.Resolve("typescript", ws, user);
            Assert.Null(t.InitializationOptions); // trusted: the server may pick the workspace version itself
        }

        [Fact]
        public void LSP1_Pyright_DoesNotStart_WhenTheProjectConfigSelectsAVenv_InRestrictedMode()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string ws = NewDir("pyr", false);
            File.WriteAllText(Path.Combine(ws, "pyrightconfig.json"), "{ \"venvPath\": \".\", \"venv\": \".venv\" }");
            string install = NewDir("pyrbin", false);
            string exe = Path.Combine(install, "pyright-langserver");
            Exe(exe);
            var user = LanguageServerUserSettings.Parse("python = " + exe + " --stdio", false);
            Assert.True(LanguageServerRegistry.Resolve("python", ws, user).Restricted);
            File.Delete(Path.Combine(ws, "pyrightconfig.json"));
            Assert.True(LanguageServerRegistry.Resolve("python", ws, user).CanStart); // global pyright, global python: fine
        }

        [Fact]
        public void Git_DoesNotRunInAnUntrustedRepository_ButCloneVersionAndInitDo()
        {
            if (!GitCli.IsAvailable()) return;
            string ws = NewDir("git", false);
            var init = GitProcess.Run(ws, new[] { "init", "-q" }, 30000, CancellationToken.None, null, null);
            Assert.True(init.Success, init.Combined);
            // A planted fsmonitor would run on "git status" - in Restricted Mode git isn't started at all.
            string marker = Path.Combine(ws, "pwned.txt");
            File.AppendAllText(Path.Combine(ws, ".git", "config"), "[core]\n\tfsmonitor = \"touch '" + marker + "'; false\"\n");
            var st = GitProcess.Run(ws, new[] { "status" }, 30000, CancellationToken.None, null, null);
            Assert.False(st.Success);
            Assert.Contains("Restricted Mode", st.StartError);
            int code;
            Assert.Equal("", GitCli.Run(ws, 10000, out code, "status"));
            Assert.Equal(-1, code);
            bool to;
            Assert.Contains("Restricted Mode", LlmHarnessAndAgentEngine.RunProcessCaptureWithExitCode("git", "status", ws, 10000, out code, out to));
            Assert.False(File.Exists(marker));
            Assert.True(GitCli.IsAvailable());
        }

        [Fact]
        public void Interpreters_And_Verification_And_Debugging_AreGated()
        {
            string ws = NewDir("py", false);
            Directory.CreateDirectory(Path.Combine(ws, ".venv", "bin"));
            File.WriteAllText(Path.Combine(ws, ".venv", "pyvenv.cfg"), "home = /usr/bin\nversion = 3.12.1\n");
            File.WriteAllText(Path.Combine(ws, ".venv", "bin", "python"), "#!");
            var env = new PythonDiscoveryEnvironment { IsWindows = false, HomeDir = ws, GetEnv = n => null, FindExecutable = n => null, RunProcess = (a, b, c) => null, ProbeVersions = false };
            Assert.DoesNotContain(PythonInterpreterDiscovery.Discover(ws, env), i => i.Path.StartsWith(ws, StringComparison.Ordinal));

            var verifier = new AgentVerifier(new IdePersistentSettings());
            var vr = verifier.Verify(ws, new Dictionary<string, string>(), null, CancellationToken.None, null);
            Assert.Equal(VerificationStatus.Denied, vr.Status);
            Assert.Contains("Restricted Mode", vr.Message);

            var host = new FakeDebugHost { Root = ws };
            var ctl = new DebugController(host);
            ctl.StartAsync(new DebugLaunchConfig { Name = "x", Type = "debugpy", Request = "launch" }, Path.Combine(ws, "a.py")).Wait(5000);
            Assert.False(ctl.IsActive);
            Assert.Contains(host.Errors, e => e.Contains("Restricted Mode"));

            TestTrust.Trust(ws);
            Assert.Contains(PythonInterpreterDiscovery.Discover(ws, env), i => i.Path.StartsWith(ws, StringComparison.Ordinal));
        }
    }

    /// <summary>Workspace Trust UI (Avalonia headless): prompt, status item, banner, task gating, no-restart enablement.</summary>
    [Collection("SequentialWorkbench")]
            public class EngineReviewTrustHeadlessTests : IDisposable
    {
        private readonly string settingsPath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly string ws;

        public EngineReviewTrustHeadlessTests()
        {
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            ws = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cl-trust-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(ws);
        }

        public void Dispose()
        {
            MainWindow.ShowTrustPromptInHeadless = false;
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); } catch { }
            try { Directory.Delete(ws, true); } catch { }
        }

        private static bool Pump(Func<bool> cond, int ms = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return true;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            return cond();
        }

        [AvaloniaFact]
        public void UntrustedFolder_AsksOnce_ShowsRestrictedMode_BlocksTasks_TrustEnablesWithoutRestart()
        {
            MainWindow.ShowTrustPromptInHeadless = true;
            var w = new MainWindow(ws) { Width = 1200, Height = 800 };
            w.Show();
            try
            {
                Assert.True(Pump(() => w.ActiveModalId == "workspace-trust"), "first-open prompt not shown: " + w.ActiveModalId);
                Assert.True(w.IsWorkspaceRestricted);
                Assert.True(w.IsRestrictedModeStatusVisible);
                Assert.True(w.IsRestrictedModeBannerVisible);

                // Answer "Restricted Mode": remembered, not asked again.
                WorkspaceTrust.ApplyPromptAnswer(ws, false, false);
                w.ModalHost.CloseModal();
                Assert.Equal(WorkspaceTrustDecision.Restricted, WorkspaceTrust.GetDecision(ws));

                var task = new TaskDefinition { Label = "hello", Command = "echo hi > out.txt" };
                Assert.Null(w.RunTask(task));
                Assert.Contains("Restricted Mode", w.StatusMessageText);
                Assert.False(File.Exists(Path.Combine(ws, "out.txt")));

                // Trust from Manage Workspace Trust: features are enabled at once.
                w.ShowManageWorkspaceTrust();
                Assert.Equal("workspace-trust-manage", w.ActiveModalId);
                WorkspaceTrust.Trust(ws);
                Assert.True(Pump(() => !w.IsRestrictedModeStatusVisible && !w.IsRestrictedModeBannerVisible));
                w.ModalHost.CloseModal();
                var exec = w.RunTask(task);
                Assert.NotNull(exec);
                Assert.True(Pump(() => !exec.IsRunning && exec.EndedUtc != default(DateTime)));
                Assert.True(File.Exists(Path.Combine(ws, "out.txt")));

                // Revoking (Settings list / Manage dialog) brings Restricted Mode back without a restart.
                WorkspaceTrust.Revoke(ws);
                Assert.True(Pump(() => w.IsRestrictedModeStatusVisible));
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void TrustedFolder_HasNoPromptNoBanner_AndTaskChainsRunInOrder()
        {
            TestTrust.Trust(ws);
            MainWindow.ShowTrustPromptInHeadless = true;
            File.WriteAllText(Path.Combine(ws, "tasks.log"), "");
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "tasks.json"),
                "{ \"version\": \"2.0.0\", \"tasks\": [\n" +
                "  { \"label\": \"a\", \"type\": \"shell\", \"command\": \"echo a >> tasks.log\" },\n" +
                "  { \"label\": \"b\", \"type\": \"shell\", \"command\": \"echo b >> tasks.log\" },\n" +
                "  { \"label\": \"ab\", \"dependsOn\": [\"a\", \"b\"], \"dependsOrder\": \"sequence\" },\n" +
                "  { \"label\": \"final\", \"type\": \"shell\", \"command\": \"echo final >> tasks.log\", \"dependsOn\": [\"ab\"] }\n" +
                "] }\n");
            var w = new MainWindow(ws) { Width = 1200, Height = 800 };
            w.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.NotEqual("workspace-trust", w.ActiveModalId);
                Assert.False(w.IsRestrictedModeStatusVisible);
                Assert.False(w.IsRestrictedModeBannerVisible);
                bool? ok = null;
                w.RunTaskByLabel("final", (success, msg) => ok = success);
                Assert.True(Pump(() => ok.HasValue, 30000), "chain never completed");
                Assert.True(ok.Value);
                Assert.Equal("a\nb\nfinal\n", File.ReadAllText(Path.Combine(ws, "tasks.log")).Replace("\r\n", "\n").Replace(" \n", "\n"));
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
