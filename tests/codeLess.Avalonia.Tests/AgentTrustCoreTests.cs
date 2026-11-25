using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Shared temp roots for overlays and checkpoints (static, process-wide; never deleted while tests run).</summary>
    internal static class AgentTrustTestEnv
    {
        private static readonly object gate = new object();
        private static bool done;
        public static readonly string Root = Path.Combine(Path.GetTempPath(), "codeless-agenttrust-tests-" + Environment.ProcessId);

        public static void Init()
        {
            lock (gate)
            {
                if (done) return;
                // Roots of earlier test processes that are gone (each process gets its own root).
                foreach (string old in Directory.GetDirectories(Path.GetTempPath(), "codeless-agenttrust-tests-*"))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(old).Substring("codeless-agenttrust-tests-".Length), out pid) || pid == Environment.ProcessId) continue;
                    bool alive;
                    try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) alive = !p.HasExited; } catch { alive = false; }
                    if (!alive) Delete(old);
                }
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Delete(Root);
                Directory.CreateDirectory(Root);
                VerificationOverlay.OverlaysRoot = Path.Combine(Root, "overlays");
                AgentTurnCheckpointStore.CheckpointsRoot = Path.Combine(Root, "checkpoints");
                done = true;
            }
        }

        public static string NewDir(string prefix)
        {
            Init();
            string d = Path.Combine(Path.GetTempPath(), "cl-at-" + prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return TestTrust.Trust(Path.GetFullPath(d)); // verification / git / terminal copies need a trusted folder
        }

        public static string Git(string dir, params string[] args)
        {
            var r = GitProcess.Run(dir, args, 60000, CancellationToken.None, null, null);
            Assert.True(r.Success, "git " + string.Join(" ", args) + " failed:\n" + r.Combined);
            return r.Stdout;
        }

        public static string InitRepo(string dir)
        {
            Git(dir, "init", "-q", "-b", "main");
            Git(dir, "config", "user.name", "Agent Trust Tester");
            Git(dir, "config", "user.email", "agent@example.com");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "config", "core.autocrlf", "false");
            Git(dir, "config", "core.hooksPath", Path.Combine(dir, ".no-hooks"));
            return dir;
        }

        public static void Commit(string dir, string msg)
        {
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", msg);
        }

        public static void Delete(string dir)
        {
            try { VerificationOverlay.SafeDeleteTree(dir); } catch { }
        }

        public static IdePersistentSettings Settings(bool requireApproval = true)
        {
            var s = new IdePersistentSettings();
            s.RequireAgentToolApproval = requireApproval;
            return s;
        }
    }

    public class AgentTrustOverlayTests
    {
        public AgentTrustOverlayTests() { AgentTrustTestEnv.Init(); }

        [Fact]
        public void GitWorktreeOverlay_AppliesUncommittedAndStagedEdits_LinksNodeModules_AndCleansUp()
        {
            string ws = AgentTrustTestEnv.NewDir("ovgit");
            try
            {
                AgentTrustTestEnv.InitRepo(ws);
                File.WriteAllText(Path.Combine(ws, "a.txt"), "committed a\n");
                File.WriteAllText(Path.Combine(ws, "c.txt"), "to be deleted\n");
                File.WriteAllText(Path.Combine(ws, ".gitignore"), "node_modules/\n");
                AgentTrustTestEnv.Commit(ws, "init");
                File.WriteAllText(Path.Combine(ws, "a.txt"), "uncommitted a\n");
                File.WriteAllText(Path.Combine(ws, "b.txt"), "untracked b\n");
                File.Delete(Path.Combine(ws, "c.txt"));
                Directory.CreateDirectory(Path.Combine(ws, "node_modules", "left-pad"));
                File.WriteAllText(Path.Combine(ws, "node_modules", "left-pad", "index.js"), "module.exports = 1;\n");

                var staged = new Dictionary<string, string>
                {
                    { Path.Combine(ws, "a.txt"), "staged a\n" },
                    { Path.Combine(ws, "src", "new.txt"), "brand new\n" }
                };
                string err;
                var ov = VerificationOverlay.Create(ws, staged, null, new VerificationOverlayOptions(), out err);
                Assert.True(ov != null, err);
                string overlayRoot = ov.OverlayRoot;
                try
                {
                    Assert.Equal("git-clone", ov.Strategy);
                    Assert.Equal("staged a\n", File.ReadAllText(Path.Combine(ov.OverlayWorkspace, "a.txt")));
                    Assert.Equal("untracked b\n", File.ReadAllText(Path.Combine(ov.OverlayWorkspace, "b.txt")));
                    Assert.False(File.Exists(Path.Combine(ov.OverlayWorkspace, "c.txt")));
                    Assert.Equal("brand new\n", File.ReadAllText(Path.Combine(ov.OverlayWorkspace, "src", "new.txt")));
                    string nm = Path.Combine(ov.OverlayWorkspace, "node_modules");
                    Assert.True(File.Exists(Path.Combine(nm, "left-pad", "index.js")));
                    // APFS / reflink: a private copy-on-write clone; elsewhere a shared link that is reported as such.
                    if (ov.ClonedFolders.Contains("node_modules")) Assert.False(VerificationOverlay.IsLink(nm));
                    else { Assert.True(VerificationOverlay.IsLink(nm)); Assert.Contains("node_modules", ov.SharedFolders); }
                    // The user's files are untouched.
                    Assert.Equal("uncommitted a\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
                    Assert.False(File.Exists(Path.Combine(ws, "src", "new.txt")));
                    // A private clone: nothing is registered in the user's repository.
                    Assert.DoesNotContain(Path.GetFileName(overlayRoot), AgentTrustTestEnv.Git(ws, "worktree", "list", "--porcelain"));
                    Assert.Equal(Path.Combine(ws, "a.txt"), ov.MapToWorkspace(Path.Combine(ov.OverlayWorkspace, "a.txt")));
                }
                finally
                {
                    ov.Dispose();
                }
                Assert.False(Directory.Exists(overlayRoot));
                Assert.True(File.Exists(Path.Combine(ws, "node_modules", "left-pad", "index.js")), "cleanup must not follow the node_modules link");
                Assert.DoesNotContain(Path.GetFileName(overlayRoot), AgentTrustTestEnv.Git(ws, "worktree", "list", "--porcelain"));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void WorkspaceInRepoSubfolder_MapsPathsThroughTheSubfolder()
        {
            string repo = AgentTrustTestEnv.NewDir("ovsub");
            try
            {
                AgentTrustTestEnv.InitRepo(repo);
                Directory.CreateDirectory(Path.Combine(repo, "app"));
                File.WriteAllText(Path.Combine(repo, "app", "x.txt"), "x\n");
                File.WriteAllText(Path.Combine(repo, "root.txt"), "r\n");
                AgentTrustTestEnv.Commit(repo, "init");
                string ws = Path.Combine(repo, "app");
                string err;
                using (var ov = VerificationOverlay.Create(ws, new Dictionary<string, string> { { Path.Combine(ws, "x.txt"), "y\n" } }, null, new VerificationOverlayOptions(), out err))
                {
                    Assert.True(ov != null, err);
                    Assert.Equal(Path.Combine(ov.OverlayRoot, "app"), ov.OverlayWorkspace);
                    Assert.Equal("y\n", File.ReadAllText(Path.Combine(ov.OverlayWorkspace, "x.txt")));
                    Assert.True(File.Exists(Path.Combine(ov.OverlayRoot, "root.txt")));
                }
            }
            finally { AgentTrustTestEnv.Delete(repo); }
        }

        [Fact]
        public void CopyOverlay_SkipsBuildOutputs_EnforcesLimits_AndRejectsOutsidePaths()
        {
            string ws = AgentTrustTestEnv.NewDir("ovcopy");
            try
            {
                File.WriteAllText(Path.Combine(ws, "main.py"), "print(1)\n");
                Directory.CreateDirectory(Path.Combine(ws, "bin"));
                File.WriteAllText(Path.Combine(ws, "bin", "out.dll"), "binary");
                Directory.CreateDirectory(Path.Combine(ws, ".venv", "lib"));
                File.WriteAllText(Path.Combine(ws, ".venv", "lib", "site.py"), "#");
                string err;
                using (var ov = VerificationOverlay.Create(ws, new Dictionary<string, string> { { Path.Combine(ws, "main.py"), "print(2)\n" } }, new[] { Path.Combine(ws, "gone.py") }, new VerificationOverlayOptions { PreferGitWorktree = false }, out err))
                {
                    Assert.True(ov != null, err);
                    Assert.Equal("copy", ov.Strategy);
                    Assert.Equal("print(2)\n", File.ReadAllText(Path.Combine(ov.OverlayWorkspace, "main.py")));
                    Assert.False(Directory.Exists(Path.Combine(ov.OverlayWorkspace, "bin")));
                    // Copy-on-write clone where the file system supports it (APFS), else a shared link reported as such.
                    string venv = Path.Combine(ov.OverlayWorkspace, ".venv");
                    Assert.True(File.Exists(Path.Combine(venv, "lib", "site.py")));
                    Assert.True(ov.ClonedFolders.Contains(".venv") ? !VerificationOverlay.IsLink(venv) : VerificationOverlay.IsLink(venv) && ov.SharedFolders.Contains(".venv"));
                }
                Assert.Equal("print(1)\n", File.ReadAllText(Path.Combine(ws, "main.py")));

                var tiny = new VerificationOverlayOptions { PreferGitWorktree = false, MaxCopyFiles = 1 };
                File.WriteAllText(Path.Combine(ws, "second.py"), "x");
                Assert.Null(VerificationOverlay.Create(ws, null, null, tiny, out err));
                Assert.Contains("too large", err);

                string outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
                Assert.Null(VerificationOverlay.Create(ws, new Dictionary<string, string> { { outside, "evil" } }, null, new VerificationOverlayOptions { PreferGitWorktree = false }, out err));
                Assert.Contains("outside the workspace", err);
                Assert.False(File.Exists(outside));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void CleanupStale_RemovesOverlaysOfDeadProcesses_AndPrunesWorktrees_WithoutTouchingLinkTargets()
        {
            string ws = AgentTrustTestEnv.NewDir("ovstale");
            try
            {
                AgentTrustTestEnv.InitRepo(ws);
                File.WriteAllText(Path.Combine(ws, "a.txt"), "a\n");
                File.WriteAllText(Path.Combine(ws, ".gitignore"), "node_modules/\n");
                AgentTrustTestEnv.Commit(ws, "init");
                Directory.CreateDirectory(Path.Combine(ws, "node_modules"));
                File.WriteAllText(Path.Combine(ws, "node_modules", "keep.js"), "keep");
                string err;
                var ov = VerificationOverlay.Create(ws, null, null, new VerificationOverlayOptions(), out err);
                Assert.True(ov != null, err);
                // Simulate a crash: the process that owned the overlay is gone and Dispose never ran.
                string marker = Path.Combine(VerificationOverlay.OverlaysRoot, ov.Id + ".json");
                Assert.True(File.Exists(marker));
                File.WriteAllText(marker, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(marker), "\"pid\"\\s*:\\s*\\d+", "\"pid\":999999"));
                Assert.Contains("999999", File.ReadAllText(marker));

                int removed = VerificationOverlay.CleanupStale(TimeSpan.FromHours(12));
                Assert.True(removed >= 1);
                Assert.False(Directory.Exists(ov.OverlayRoot));
                Assert.False(File.Exists(marker));
                Assert.True(File.Exists(Path.Combine(ws, "node_modules", "keep.js")));
                Assert.DoesNotContain(ov.Id, AgentTrustTestEnv.Git(ws, "worktree", "list", "--porcelain"));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void SafeDeleteTree_RemovesSymlinksWithoutFollowingThem()
        {
            string target = AgentTrustTestEnv.NewDir("lnk-target");
            string tree = AgentTrustTestEnv.NewDir("lnk-tree");
            try
            {
                File.WriteAllText(Path.Combine(target, "precious.txt"), "do not delete");
                Directory.CreateDirectory(Path.Combine(tree, "sub"));
                Assert.True(VerificationOverlay.CreateDirectoryLink(Path.Combine(tree, "sub", "link"), target));
                VerificationOverlay.SafeDeleteTree(tree);
                Assert.False(Directory.Exists(tree));
                Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
            }
            finally { AgentTrustTestEnv.Delete(target); AgentTrustTestEnv.Delete(tree); }
        }

        [Fact]
        public void PorcelainParser_HandlesRenamesAndSpaces()
        {
            var e = VerificationOverlay.ParsePorcelainZ(" M a b.txt\0R  new.txt\0old.txt\0?? dir/u.txt\0");
            Assert.Equal(3, e.Count);
            Assert.Equal("a b.txt", e[0].Path);
            Assert.Equal("new.txt", e[1].Path);
            Assert.Equal("old.txt", e[1].OrigPath);
            Assert.Equal("dir/u.txt", e[2].Path);
        }
    }

    public class AgentTrustVerificationTests
    {
        public AgentTrustVerificationTests() { AgentTrustTestEnv.Init(); }

        [Fact]
        public void CommandResolution_PrecedenceAndAutoDetection()
        {
            string ws = AgentTrustTestEnv.NewDir("cmd");
            try
            {
                Assert.Null(VerificationCommandResolver.Resolve(ws, null));

                File.WriteAllText(Path.Combine(ws, "go.mod"), "module x\n");
                var c = VerificationCommandResolver.Resolve(ws, null);
                Assert.Equal("go build ./...", c.CommandLine);
                Assert.Equal("build", c.Kind);
                Assert.True(c.IsWorkspaceControlled);
                Assert.Contains("$go", c.ProblemMatchers.Cast<string>());

                File.WriteAllText(Path.Combine(ws, "Cargo.toml"), "[package]\nname='x'\n");
                File.Delete(Path.Combine(ws, "go.mod"));
                Assert.Equal("cargo check", VerificationCommandResolver.Resolve(ws, null).CommandLine);
                File.Delete(Path.Combine(ws, "Cargo.toml"));

                File.WriteAllText(Path.Combine(ws, "package.json"), "{\"scripts\":{\"test\":\"echo \\\"Error: no test specified\\\" && exit 1\",\"build\":\"tsc -p .\"}}");
                c = VerificationCommandResolver.Resolve(ws, null);
                Assert.Equal("npm run build", c.CommandLine); // placeholder npm test skipped
                File.WriteAllText(Path.Combine(ws, "package.json"), "{\"scripts\":{\"test\":\"jest\"}}");
                c = VerificationCommandResolver.Resolve(ws, null);
                Assert.Equal("npm run test", c.CommandLine);
                Assert.Equal("test", c.Kind);
                File.Delete(Path.Combine(ws, "package.json"));

                File.WriteAllText(Path.Combine(ws, "test_x.py"), "def test_a():\n    assert True\n");
                c = VerificationCommandResolver.Resolve(ws, null);
                Assert.EndsWith("-m pytest -q", c.CommandLine);
                Assert.Equal("test", c.Kind);

                File.WriteAllText(Path.Combine(ws, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
                c = VerificationCommandResolver.Resolve(ws, null);
                Assert.StartsWith("dotnet build App.csproj", c.CommandLine);
                Assert.Contains("$msCompile", c.ProblemMatchers.Cast<string>());

                // tasks.json default build task wins over auto-detection; background tasks are ignored.
                Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
                File.WriteAllText(Path.Combine(ws, ".vscode", "tasks.json"),
                    "{\"version\":\"2.0.0\",\"tasks\":[{\"label\":\"watch\",\"command\":\"tsc -w\",\"isBackground\":true,\"group\":{\"kind\":\"build\",\"isDefault\":true}}," +
                    "{\"label\":\"compile\",\"type\":\"shell\",\"command\":\"make\",\"args\":[\"all\"],\"group\":{\"kind\":\"test\",\"isDefault\":true},\"problemMatcher\":\"$gcc\"}]}");
                c = VerificationCommandResolver.Resolve(ws, null);
                Assert.Equal("make all", c.CommandLine);
                Assert.Equal("tasks.json: compile", c.Source);

                // User setting beats the task system; workspace settings beat the user; .codeless/verify beats everything.
                c = VerificationCommandResolver.Resolve(ws, "make check");
                Assert.Equal("make check", c.CommandLine);
                Assert.False(c.IsWorkspaceControlled);
                File.WriteAllText(Path.Combine(ws, ".vscode", "settings.json"), "{\n // comment\n \"codeless.verifyCommand\": \"npm run lint\",\n}");
                c = VerificationCommandResolver.Resolve(ws, "make check");
                Assert.Equal("npm run lint", c.CommandLine);
                Assert.True(c.IsWorkspaceControlled);
                Directory.CreateDirectory(Path.Combine(ws, ".codeless"));
                File.WriteAllText(Path.Combine(ws, ".codeless", "verify"), "# how to verify\n\npytest -q tests/unit\n");
                c = VerificationCommandResolver.Resolve(ws, "make check");
                Assert.Equal("pytest -q tests/unit", c.CommandLine);
                Assert.Equal(".codeless/verify", c.Source);
                Assert.Equal("test", c.Kind);
                Assert.Contains("$python", c.ProblemMatchers.Cast<string>());
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        private static VerificationCommand Sh(string script, string kind = "build", string matcher = "$msCompile")
        {
            var c = VerificationCommandResolver.Custom(script, "test", false);
            c.Kind = kind;
            c.ProblemMatchers.Clear();
            if (matcher != null) c.ProblemMatchers.Add(matcher);
            return c;
        }

        [Fact]
        public void Runner_ParsesErrors_MapsPaths_AndReportsPassFailTimeoutCancel()
        {
            if (OperatingSystem.IsWindows()) return;
            string dir = AgentTrustTestEnv.NewDir("run");
            string ws = AgentTrustTestEnv.NewDir("runws");
            try
            {
                File.WriteAllText(Path.Combine(dir, "Program.cs"), "x");
                string script = "printf '%s\\n' 'Program.cs(3,5): error CS1002: ; expected [/x/App.csproj]' 'Program.cs(3,5): error CS1002: ; expected [/x/App.csproj]' 'Lib.cs(10,1): warning CS0168: unused' 'Lib.cs(12,2): error CS0103: The name foo does not exist'; exit 1";
                var r = VerificationRunner.Run(Sh(script), dir, 30000, CancellationToken.None, null, p => p.Replace(dir, ws));
                Assert.Equal(VerificationStatus.Failed, r.Status);
                Assert.Equal(1, r.ExitCode);
                Assert.Equal(2, r.Errors.Count); // the duplicated MSBuild line is reported once
                Assert.Single(r.Warnings);
                Assert.Equal("✗ 2 errors", r.Badge);
                Assert.StartsWith(ws, r.Errors[0].FilePath);
                Assert.Equal(3, r.Errors[0].Line);
                Assert.Contains("Program.cs:3:5: error: ; expected", r.FormatErrorLines(10)[0]);
                Assert.Contains("✗ 2 errors", r.ToMarkdown());

                var ok = VerificationRunner.Run(Sh("echo all good"), dir, 30000, CancellationToken.None, null, null);
                Assert.Equal(VerificationStatus.Passed, ok.Status);
                Assert.Equal("✓ build passed", ok.Badge);
                Assert.Contains("all good", ok.OutputTail);

                var noMatch = VerificationRunner.Run(Sh("echo boom; exit 3", "test", null), dir, 30000, CancellationToken.None, null, null);
                Assert.Equal(VerificationStatus.Failed, noMatch.Status);
                Assert.Equal("✗ tests failed (exit 3)", noMatch.Badge);

                var pytest = VerificationRunner.Run(Sh("echo 'FAILED tests/test_calc.py::test_add - assert 3 == 4'; exit 1", "test", "$python"), dir, 30000, CancellationToken.None, null, null);
                Assert.Equal(VerificationStatus.Failed, pytest.Status);
                // Only added for pytest commands: this script isn't one, so the summary line has no matcher.
                Assert.Empty(pytest.Errors);
                var pytest2 = VerificationRunner.Run(Sh("echo 'FAILED tests/test_calc.py::test_add - assert 3 == 4'; exit 1 # pytest", "test", "$python"), dir, 30000, CancellationToken.None, null, null);
                Assert.Single(pytest2.Errors);
                Assert.Contains("assert 3 == 4", pytest2.Errors[0].Message);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var to = VerificationRunner.Run(Sh("sleep 30"), dir, 800, CancellationToken.None, null, null);
                Assert.Equal(VerificationStatus.TimedOut, to.Status);
                Assert.True(sw.ElapsedMilliseconds < 15000);

                var cts = new CancellationTokenSource(500);
                var cancelled = VerificationRunner.Run(Sh("sleep 30"), dir, 60000, cts.Token, null, null);
                Assert.Equal(VerificationStatus.Cancelled, cancelled.Status);

                var env = VerificationRunner.Run(Sh("test \"$CI\" = true && test \"$MSBUILDDISABLENODEREUSE\" = 1"), dir, 30000, CancellationToken.None, null, null);
                Assert.Equal(VerificationStatus.Passed, env.Status);
            }
            finally { AgentTrustTestEnv.Delete(dir); AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void Verifier_RunsInTemporaryCopy_NeverTouchesWorkspace_AndHonoursApproval()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = AgentTrustTestEnv.NewDir("verifier");
            try
            {
                AgentTrustTestEnv.InitRepo(ws);
                File.WriteAllText(Path.Combine(ws, "a.txt"), "broken\n");
                Directory.CreateDirectory(Path.Combine(ws, ".codeless"));
                File.WriteAllText(Path.Combine(ws, ".codeless", "verify"), "grep -q FIXED a.txt && test ! -f marker.txt\n");
                AgentTrustTestEnv.Commit(ws, "init");
                var staged = new Dictionary<string, string> { { Path.Combine(ws, "a.txt"), "FIXED\n" } };

                // Repository-chosen command: no approval UI => denied, nothing runs.
                var verifier = new AgentVerifier(AgentTrustTestEnv.Settings());
                var denied = verifier.Verify(ws, staged, new List<string>(), CancellationToken.None, null);
                Assert.Equal(VerificationStatus.Denied, denied.Status);

                int asked = 0;
                verifier.RequestApproval = req => { asked++; Assert.Contains("grep -q FIXED", req.CommandLine); Assert.True(req.CanAlwaysAllow); return VerificationApprovalDecision.AllowOnce; };
                var session = new List<string>();
                var pass = verifier.Verify(ws, staged, session, CancellationToken.None, null);
                Assert.Equal(VerificationStatus.Passed, pass.Status);
                Assert.Equal("git-clone", pass.Strategy);
                Assert.Equal(".codeless/verify", pass.Source);
                var fail = verifier.Verify(ws, new Dictionary<string, string> { { Path.Combine(ws, "a.txt"), "still broken\n" } }, session, CancellationToken.None, null);
                Assert.Equal(VerificationStatus.Failed, fail.Status);
                Assert.Equal(1, asked); // "allow once" covers the rest of the session
                Assert.Equal("broken\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
                Assert.Empty(Directory.GetDirectories(VerificationOverlay.OverlaysRoot).Where(d => File.Exists(Path.Combine(d, "a.txt")) && File.ReadAllText(Path.Combine(d, "a.txt")).Contains("FIXED")));

                // "Require agent tool approval" off => no prompt (same as run_terminal).
                var noPrompt = new AgentVerifier(AgentTrustTestEnv.Settings(requireApproval: false));
                Assert.Equal(VerificationStatus.Passed, noPrompt.Verify(ws, staged, new List<string>(), CancellationToken.None, null).Status);

                // No command anywhere => clear NoCommand result.
                File.Delete(Path.Combine(ws, ".codeless", "verify"));
                var none = noPrompt.Verify(ws, staged, new List<string>(), CancellationToken.None, null);
                Assert.Equal(VerificationStatus.NoCommand, none.Status);
                Assert.Contains(".codeless/verify", none.Message);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void ApprovalPolicy_BuildControlEditsAlwaysAsk_UserCommandsAreTrusted()
        {
            var settings = AgentTrustTestEnv.Settings();
            string ws = "/tmp/ws";
            var userCmd = VerificationCommandResolver.Custom("make check", "user setting", false);
            Assert.Null(VerificationApprovalPolicy.Evaluate(settings, ws, userCmd, new[] { "/tmp/ws/src/a.c" }, new List<string>()));
            var req = VerificationApprovalPolicy.Evaluate(settings, ws, userCmd, new[] { "/tmp/ws/package.json", "/tmp/ws/src/App.csproj", "/tmp/ws/conftest.py" }, new List<string>());
            Assert.NotNull(req);
            Assert.False(req.CanAlwaysAllow);
            Assert.Equal(3, req.BuildControlFiles.Count);

            var wsCmd = VerificationCommandResolver.Custom("npm test", ".codeless/verify", true);
            var session = new List<string>();
            var first = VerificationApprovalPolicy.Evaluate(settings, ws, wsCmd, new[] { "/tmp/ws/src/a.ts" }, session);
            Assert.NotNull(first);
            Assert.True(VerificationApprovalPolicy.Apply(null, first, VerificationApprovalDecision.AllowOnce, session));
            Assert.Null(VerificationApprovalPolicy.Evaluate(settings, ws, wsCmd, new[] { "/tmp/ws/src/a.ts" }, session));
            Assert.False(VerificationApprovalPolicy.Apply(null, first, VerificationApprovalDecision.Deny, session));

            settings.ApproveAgentVerifyCommand(ws, "npm test");
            Assert.Null(VerificationApprovalPolicy.Evaluate(settings, ws, wsCmd, new[] { "/tmp/ws/src/a.ts" }, new List<string>()));
            Assert.NotNull(VerificationApprovalPolicy.Evaluate(settings, "/tmp/other", wsCmd, new[] { "/tmp/other/a.ts" }, new List<string>()));

            Assert.True(VerificationApprovalPolicy.IsBuildControlFile("Directory.Build.props"));
            Assert.True(VerificationApprovalPolicy.IsBuildControlFile("crate/build.rs"));
            Assert.True(VerificationApprovalPolicy.IsBuildControlFile(".cargo/config.toml"));
            Assert.False(VerificationApprovalPolicy.IsBuildControlFile("src/main.rs"));
        }

        [Fact]
        public void SettingsDefaultsAndModeParsing()
        {
            var s = new IdePersistentSettings();
            Assert.Equal(AgentVerifyMode.Off, s.EffectiveAgentVerifyMode);
            Assert.Equal(300, s.EffectiveAgentVerifyTimeoutSeconds);
            Assert.Equal(2, s.EffectiveAgentRepairMaxAttempts);
            Assert.Equal(2, s.EffectiveAgentBestOfNCount);
            s.SetAgentVerifyMode(AgentVerifyMode.VerifyAndRepair);
            Assert.Equal("repair", s.AgentVerifyModeId);
            Assert.Equal(AgentVerifyMode.VerifyAndRepair, s.EffectiveAgentVerifyMode);
            s.AgentRepairMaxAttempts = 50;
            Assert.Equal(5, s.EffectiveAgentRepairMaxAttempts);
            // Round-trips through the settings serializer.
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var back = ser.Deserialize<IdePersistentSettings>(ser.Serialize(s));
            Assert.Equal(AgentVerifyMode.VerifyAndRepair, back.EffectiveAgentVerifyMode);
        }
    }

    public class AgentTrustRepairLoopTests
    {
        private static AgentExecutionTurnResult Turn(string path, string content)
        {
            var t = new AgentExecutionTurnResult();
            if (path != null) t.StagedFileContents[path] = content;
            return t;
        }

        private static VerificationResult Result(VerificationStatus s, int errors = 0)
        {
            var r = new VerificationResult { Status = s, CommandLine = "make", Kind = "build", ExitCode = s == VerificationStatus.Passed ? 0 : 2 };
            for (int i = 0; i < errors; i++) r.Errors.Add(new DiagnosticItem { Severity = "Error", RelativePath = "a.c", Line = i + 1, Message = "bad " + i });
            return r;
        }

        [Fact]
        public async Task PassesAfterOneRepair_AndRepairSeesErrorsAndStagedText()
        {
            string prompt = null;
            Dictionary<string, string> baseSeen = null;
            var events = new List<string>();
            var runner = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair,
                MaxRepairAttempts = 2,
                WorkspaceRoot = "/ws",
                Verify = (staged, ct) => staged["/ws/a.c"].Contains("fixed") ? Result(VerificationStatus.Passed) : Result(VerificationStatus.Failed, 2),
                RunRepairTurn = (p, b) => { prompt = p; baseSeen = b; return Task.FromResult(Turn("/ws/a.c", "int fixed;")); },
                Progress = e => events.Add(e.Kind)
            };
            var outcome = await runner.RunAsync(Turn("/ws/a.c", "int broken"), "make it compile", CancellationToken.None);
            Assert.Equal("passed", outcome.StopReason);
            Assert.Equal(1, outcome.RepairAttempts);
            Assert.Equal(2, outcome.Verifications.Count);
            Assert.Equal(1, outcome.FinalVerification.Attempt);
            Assert.Equal("int fixed;", outcome.FinalStaged["/ws/a.c"]);
            Assert.Contains("a.c:1: error: bad 0", prompt);
            Assert.Contains("### File: a.c", prompt);
            Assert.Contains("1: int broken", prompt);
            Assert.Contains("make it compile", prompt);
            Assert.Equal("int broken", baseSeen["/ws/a.c"]);
            Assert.Equal(new[] { "verify-start", "verify-done", "repair-start", "repair-done", "verify-start", "verify-done", "stopped" }, events);
        }

        [Fact]
        public async Task StopsAtMaxAttempts()
        {
            int n = 0;
            var runner = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair,
                MaxRepairAttempts = 2,
                Verify = (s, ct) => Result(VerificationStatus.Failed, 1),
                RunRepairTurn = (p, b) => Task.FromResult(Turn("/ws/a.c", "attempt " + (++n)))
            };
            var o = await runner.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None);
            Assert.Equal("max-attempts", o.StopReason);
            Assert.Equal(2, o.RepairAttempts);
            Assert.Equal(3, o.Verifications.Count);
            Assert.Equal("attempt 2", o.FinalStaged["/ws/a.c"]);
        }

        [Fact]
        public async Task StopsWhenModelMakesNoChange_OrReturnsNothing_OrFailureIsNotRepairable()
        {
            var same = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair, MaxRepairAttempts = 3,
                Verify = (s, ct) => Result(VerificationStatus.Failed, 1),
                RunRepairTurn = (p, b) => Task.FromResult(Turn("/ws/a.c", "v0"))
            };
            var o1 = await same.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None);
            Assert.Equal("no-changes", o1.StopReason);
            Assert.Single(o1.Verifications);

            var empty = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair, MaxRepairAttempts = 3,
                Verify = (s, ct) => Result(VerificationStatus.Failed, 1),
                RunRepairTurn = (p, b) => { var t = Turn(null, null); t.AssistantReply = "Vendor API Error (X):\nHTTP 500"; return Task.FromResult(t); }
            };
            var o2 = await empty.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None);
            Assert.Equal("model-error", o2.StopReason);
            Assert.Equal("v0", o2.FinalStaged["/ws/a.c"]); // previous proposal kept

            int repairs = 0;
            var timeout = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair,
                Verify = (s, ct) => Result(VerificationStatus.TimedOut),
                RunRepairTurn = (p, b) => { repairs++; return Task.FromResult(Turn("/ws/a.c", "zzz")); }
            };
            Assert.Equal("not-repairable", (await timeout.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None)).StopReason);
            Assert.Equal(0, repairs);

            var verifyOnly = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.Verify,
                Verify = (s, ct) => Result(VerificationStatus.Failed, 1),
                RunRepairTurn = (p, b) => { repairs++; return Task.FromResult(Turn("/ws/a.c", "zzz")); }
            };
            Assert.Equal("failed", (await verifyOnly.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None)).StopReason);
            Assert.Equal(0, repairs);

            var off = new AgentVerifiedTurnRunner { Mode = AgentVerifyMode.Off, Verify = (s, ct) => throw new InvalidOperationException() };
            Assert.Equal("off", (await off.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None)).StopReason);
            var noEdits = new AgentVerifiedTurnRunner { Mode = AgentVerifyMode.Verify, Verify = (s, ct) => throw new InvalidOperationException() };
            Assert.Equal("no-edits", (await noEdits.RunAsync(Turn(null, null), "x", CancellationToken.None)).StopReason);
        }

        [Fact]
        public async Task CancellationStopsTheLoop()
        {
            var cts = new CancellationTokenSource();
            var runner = new AgentVerifiedTurnRunner
            {
                Mode = AgentVerifyMode.VerifyAndRepair, MaxRepairAttempts = 5,
                Verify = (s, ct) => Result(VerificationStatus.Failed, 1),
                RunRepairTurn = (p, b) => { cts.Cancel(); return Task.FromResult(Turn("/ws/a.c", "new")); }
            };
            var o = await runner.RunAsync(Turn("/ws/a.c", "v0"), "x", cts.Token);
            Assert.Equal("cancelled", o.StopReason);
            Assert.Single(o.Verifications);
        }

        [Fact]
        public async Task VerifierExceptionsBecomeErrorResults()
        {
            var runner = new AgentVerifiedTurnRunner { Mode = AgentVerifyMode.VerifyAndRepair, Verify = (s, ct) => throw new IOException("disk full") };
            var o = await runner.RunAsync(Turn("/ws/a.c", "v0"), "x", CancellationToken.None);
            Assert.Equal("not-repairable", o.StopReason);
            Assert.Equal(VerificationStatus.Error, o.FinalVerification.Status);
            Assert.Contains("disk full", o.FinalVerification.Summary);
        }
    }

    public class AgentTrustCheckpointTests
    {
        public AgentTrustCheckpointTests() { AgentTrustTestEnv.Init(); }

        private static AgentChangeSet Stage(string ws, string turnId, Dictionary<string, string> staged)
        {
            var cs = AgentChangeSet.FromStaged(staged, ws, null, "turn " + turnId);
            cs.TurnId = turnId;
            return cs;
        }

        [Fact]
        public void AcceptRecordsCheckpoint_UndoRestores_RedoReapplies_EncodingIsByteExact()
        {
            string ws = AgentTrustTestEnv.NewDir("cp1");
            try
            {
                string a = Path.Combine(ws, "a.cs");
                byte[] original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("line1\r\nline2\r\n")).ToArray();
                File.WriteAllBytes(a, original);
                var store = new AgentTurnCheckpointStore(ws);
                var cs = Stage(ws, "t1", new Dictionary<string, string> { { a, "line1\r\nCHANGED\r\n" }, { Path.Combine(ws, "dir", "new.cs"), "class N {}\n" } });
                Assert.Equal(2, cs.Files.Count);
                Assert.True(cs.Files.First(f => f.FullPath == a).Added >= 1);
                var res = AgentChangeSetApplier.Accept(cs, cs.Files, store, false);
                Assert.Equal(2, res.Written.Count);
                byte[] written = File.ReadAllBytes(a);
                Assert.Equal(0xEF, written[0]); // BOM kept
                Assert.Contains("CHANGED\r\n", Encoding.UTF8.GetString(written)); // CRLF kept
                Assert.Single(store.Turns);
                Assert.Equal(2, store.Turns[0].Files.Count);
                Assert.Contains("created dir/new.cs", store.Turns[0].DescribeFiles());

                var plan = store.PlanUndo("t1", false, null);
                Assert.False(plan.HasConflicts);
                Assert.Equal(new[] { "restore", "delete" }, plan.Items.Select(i => i.Action).ToArray());
                var undo = store.Execute(plan, false);
                Assert.True(undo.Completed);
                Assert.Equal(original, File.ReadAllBytes(a));
                Assert.False(File.Exists(Path.Combine(ws, "dir", "new.cs")));
                Assert.True(store.Turns[0].Undone);
                Assert.True(store.CanRedo);

                var redo = store.Execute(store.PlanRedo(null), false);
                Assert.True(redo.Completed);
                Assert.Equal(written, File.ReadAllBytes(a));
                Assert.Equal("class N {}\n", File.ReadAllText(Path.Combine(ws, "dir", "new.cs")));
                Assert.False(store.Turns[0].Undone);

                // Persisted: a new store instance (app restart) sees the same timeline.
                var reloaded = new AgentTurnCheckpointStore(ws);
                Assert.Single(reloaded.Turns);
                Assert.Equal("turn t1", reloaded.Turns[0].Label);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void UndoDetectsConflicts_SkipsOrOverwritesWithBackup_AndDirtyBuffersCount()
        {
            string ws = AgentTrustTestEnv.NewDir("cp2");
            try
            {
                string a = Path.Combine(ws, "a.txt");
                File.WriteAllText(a, "v1\n");
                var store = new AgentTurnCheckpointStore(ws);
                var cs = Stage(ws, "t1", new Dictionary<string, string> { { a, "v2\n" } });
                AgentChangeSetApplier.Accept(cs, cs.Files, store, false);
                File.WriteAllText(a, "user edit after the turn\n");

                var plan = store.PlanUndo("t1", false, null);
                Assert.True(plan.HasConflicts);
                Assert.Contains("edited after", plan.Items[0].ConflictReason);
                var skipped = store.Execute(plan, false);
                Assert.False(skipped.Completed);
                Assert.Single(skipped.Skipped);
                Assert.Equal("user edit after the turn\n", File.ReadAllText(a));
                Assert.False(store.Turns[0].Undone);

                var forced = store.Execute(store.PlanUndo("t1", false, null), true);
                Assert.True(forced.Completed);
                Assert.Equal("v1\n", File.ReadAllText(a));
                Assert.True(forced.Backups.ContainsKey("a.txt"));
                Assert.Equal("user edit after the turn\n", File.ReadAllText(forced.Backups["a.txt"]));
                // Redo brings back exactly what was there before the undo (the user's version).
                store.Execute(store.PlanRedo(null), false);
                Assert.Equal("user edit after the turn\n", File.ReadAllText(a));

                // Unsaved editor changes are conflicts too.
                File.WriteAllText(a, "v1\n");
                var cs2 = Stage(ws, "t2", new Dictionary<string, string> { { a, "v3\n" } });
                AgentChangeSetApplier.Accept(cs2, cs2.Files, store, false);
                var dirtyPlan = store.PlanUndo("t2", false, new[] { a });
                Assert.True(dirtyPlan.HasConflicts);
                Assert.Contains("unsaved", dirtyPlan.Items[0].ConflictReason);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void UndoBackToHere_SpansLaterTurns_AndSingleUndoFlagsLaterTurnConflicts()
        {
            string ws = AgentTrustTestEnv.NewDir("cp3");
            try
            {
                string a = Path.Combine(ws, "a.txt");
                string b = Path.Combine(ws, "b.txt");
                File.WriteAllText(a, "a0\n");
                File.WriteAllText(b, "b0\n");
                var store = new AgentTurnCheckpointStore(ws);
                var t1 = Stage(ws, "t1", new Dictionary<string, string> { { a, "a1\n" } });
                AgentChangeSetApplier.Accept(t1, t1.Files, store, false);
                var t2 = Stage(ws, "t2", new Dictionary<string, string> { { a, "a2\n" }, { b, "b2\n" } });
                AgentChangeSetApplier.Accept(t2, t2.Files, store, false);

                var single = store.PlanUndo("t1", false, null);
                Assert.True(single.HasConflicts);
                Assert.Contains("later turn", single.Items[0].ConflictReason);

                var back = store.PlanUndo("t1", true, null);
                Assert.Equal(2, back.TurnIds.Count);
                Assert.False(back.HasConflicts);
                Assert.True(store.Execute(back, false).Completed);
                Assert.Equal("a0\n", File.ReadAllText(a));
                Assert.Equal("b0\n", File.ReadAllText(b));
                Assert.All(store.Turns, t => Assert.True(t.Undone));
                Assert.Null(store.PlanUndo("t2", false, null)); // already undone

                // A new change clears redo.
                var t3 = Stage(ws, "t3", new Dictionary<string, string> { { b, "b3\n" } });
                AgentChangeSetApplier.Accept(t3, t3.Files, store, false);
                Assert.False(store.CanRedo);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void TerminalChanges_InGitRepo_CreateDeleteRenameModify_AreTrackedAndUndone()
        {
            string ws = AgentTrustTestEnv.NewDir("cpgit");
            try
            {
                AgentTrustTestEnv.InitRepo(ws);
                File.WriteAllText(Path.Combine(ws, "clean.txt"), "clean\n");
                File.WriteAllText(Path.Combine(ws, "old.txt"), "rename me\n");
                File.WriteAllText(Path.Combine(ws, "del.txt"), "delete me\n");
                AgentTrustTestEnv.Commit(ws, "init");
                File.WriteAllText(Path.Combine(ws, "dirty.txt"), "untracked before the turn\n");

                var store = new AgentTurnCheckpointStore(ws);
                var tracker = new AgentTurnTracker(store, "run script");
                tracker.EnsureSnapshot();
                // What an agent terminal command might do:
                File.WriteAllText(Path.Combine(ws, "clean.txt"), "modified by command\n");
                File.Move(Path.Combine(ws, "old.txt"), Path.Combine(ws, "renamed.txt"));
                File.Delete(Path.Combine(ws, "del.txt"));
                File.WriteAllText(Path.Combine(ws, "dirty.txt"), "overwritten by command\n");
                File.WriteAllText(Path.Combine(ws, "created.txt"), "new\n");
                var changes = tracker.Complete();

                Assert.Equal(6, changes.Count); // the rename is a delete + a create
                var rec = store.FindTurn(tracker.TurnId);
                Assert.NotNull(rec);
                var desc = rec.DescribeFiles();
                Assert.Contains("renamed old.txt → renamed.txt", desc);
                Assert.Contains("deleted del.txt", desc);
                Assert.Contains("created created.txt", desc);
                Assert.Contains("modified clean.txt", desc);

                var r = store.Execute(store.PlanUndo(tracker.TurnId, false, null), false);
                Assert.True(r.Completed, string.Join(",", r.Skipped.Concat(r.Errors)));
                Assert.Equal("clean\n", File.ReadAllText(Path.Combine(ws, "clean.txt")));
                Assert.Equal("rename me\n", File.ReadAllText(Path.Combine(ws, "old.txt")));
                Assert.False(File.Exists(Path.Combine(ws, "renamed.txt")));
                Assert.Equal("delete me\n", File.ReadAllText(Path.Combine(ws, "del.txt")));
                Assert.Equal("untracked before the turn\n", File.ReadAllText(Path.Combine(ws, "dirty.txt")));
                Assert.False(File.Exists(Path.Combine(ws, "created.txt")));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void TerminalChanges_WithoutGit_UseSnapshot_AndNoChangeRecordsNothing()
        {
            string ws = AgentTrustTestEnv.NewDir("cpwalk");
            try
            {
                File.WriteAllText(Path.Combine(ws, "a.txt"), "a\n");
                File.WriteAllText(Path.Combine(ws, "b.txt"), "b\n");
                Directory.CreateDirectory(Path.Combine(ws, "node_modules"));
                var store = new AgentTurnCheckpointStore(ws);

                var idle = new AgentTurnTracker(store, "no-op");
                idle.EnsureSnapshot();
                Assert.Empty(idle.Complete());
                Assert.Empty(store.Turns);

                var tracker = new AgentTurnTracker(store, "cmd");
                tracker.EnsureSnapshot();
                File.WriteAllText(Path.Combine(ws, "a.txt"), "a changed\n");
                File.SetLastWriteTimeUtc(Path.Combine(ws, "a.txt"), DateTime.UtcNow.AddMinutes(1));
                File.Delete(Path.Combine(ws, "b.txt"));
                File.WriteAllText(Path.Combine(ws, "c.txt"), "c\n");
                File.WriteAllText(Path.Combine(ws, "node_modules", "ignored.js"), "x"); // dependency folder: not tracked
                var changes = tracker.Complete();
                Assert.Equal(3, changes.Count);
                Assert.True(store.Execute(store.PlanUndo(tracker.TurnId, false, null), false).Completed);
                Assert.Equal("a\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
                Assert.Equal("b\n", File.ReadAllText(Path.Combine(ws, "b.txt")));
                Assert.False(File.Exists(Path.Combine(ws, "c.txt")));
                Assert.True(File.Exists(Path.Combine(ws, "node_modules", "ignored.js")));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void VerifiedState_IsLostWhenAFileIsRejected_ButKeptWhenFilesAreAccepted()
        {
            string ws = AgentTrustTestEnv.NewDir("cpfp");
            try
            {
                string a = Path.Combine(ws, "a.txt"), b = Path.Combine(ws, "b.txt");
                File.WriteAllText(a, "a\n");
                File.WriteAllText(b, "b\n");
                var cs = Stage(ws, "t1", new Dictionary<string, string> { { a, "A\n" }, { b, "B\n" } });
                cs.AddVerification(new VerificationResult { Status = VerificationStatus.Passed });
                Assert.True(cs.IsVerifiedPassing);
                AgentChangeSetApplier.Accept(cs, new[] { cs.Files[0] }, null, false);
                Assert.True(cs.IsVerifiedPassing); // accepted file is now on disk: same verified state
                AgentChangeSetApplier.Reject(new[] { cs.Files[1] });
                Assert.False(cs.IsVerifiedPassing); // a subset was never verified on its own
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void AcceptRefusesFilesChangedSinceTheProposal()
        {
            string ws = AgentTrustTestEnv.NewDir("cpconf");
            try
            {
                string a = Path.Combine(ws, "a.txt");
                File.WriteAllText(a, "base\n");
                var cs = Stage(ws, "t1", new Dictionary<string, string> { { a, "proposal\n" } });
                Assert.False(cs.IsVerifiedPassing);
                cs.AddVerification(new VerificationResult { Status = VerificationStatus.Passed });
                Assert.True(cs.IsVerifiedPassing);
                cs.Files[0].ProposedText = "edited proposal\n";
                Assert.False(cs.IsVerifiedPassing); // verification no longer describes the proposal
                File.WriteAllText(a, "user changed it meanwhile\n");
                var res = AgentChangeSetApplier.Accept(cs, cs.Files, null, false);
                Assert.Single(res.Conflicts);
                Assert.Equal("user changed it meanwhile\n", File.ReadAllText(a));
                Assert.True(cs.Files[0].IsPending);
                AgentChangeSetApplier.Accept(cs, cs.Files, null, true);
                Assert.Equal("edited proposal\n", File.ReadAllText(a));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }
    }

    /// <summary>Minimal OpenAI-compatible chat server on 127.0.0.1 (non-streaming), replying per requested model.</summary>
    internal sealed class FakeOpenAiServer : IDisposable
    {
        private readonly TcpListener listener;
        private volatile bool running = true;
        public readonly int Port;
        public int Requests;
        public readonly Func<string, string> ReplyForModel;

        public FakeOpenAiServer(Func<string, string> replyForModel)
        {
            ReplyForModel = replyForModel;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var t = new Thread(Loop) { IsBackground = true };
            t.Start();
        }

        public string BaseUrl { get { return "http://127.0.0.1:" + Port + "/v1"; } }

        private void Loop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        private void Handle(TcpClient c)
        {
            try
            {
                using (c)
                using (var ns = c.GetStream())
                {
                    var buf = new MemoryStream();
                    var one = new byte[8192];
                    int headerEnd = -1, contentLength = 0;
                    while (true)
                    {
                        int n = ns.Read(one, 0, one.Length);
                        if (n <= 0) break;
                        buf.Write(one, 0, n);
                        string sofar = Encoding.UTF8.GetString(buf.ToArray());
                        if (headerEnd < 0)
                        {
                            headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (headerEnd >= 0)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(sofar.Substring(0, headerEnd), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success) contentLength = int.Parse(m.Groups[1].Value);
                            }
                        }
                        if (headerEnd >= 0 && buf.Length >= headerEnd + 4 + contentLength) break;
                    }
                    string all = Encoding.UTF8.GetString(buf.ToArray());
                    string body = headerEnd >= 0 ? all.Substring(headerEnd + 4) : "";
                    Interlocked.Increment(ref Requests);
                    var mm = System.Text.RegularExpressions.Regex.Match(body, "\"model\"\\s*:\\s*\"([^\"]+)\"");
                    string reply = ReplyForModel(mm.Success ? mm.Groups[1].Value : "");
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                    string json = ser.Serialize(new Dictionary<string, object>
                    {
                        { "choices", new object[] { new Dictionary<string, object> { { "message", new Dictionary<string, object> { { "role", "assistant" }, { "content", reply } } } } } }
                    });
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    byte[] hdr = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    ns.Write(hdr, 0, hdr.Length);
                    ns.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }
    }

    public class AgentTrustBestOfNTests
    {
        public AgentTrustBestOfNTests() { AgentTrustTestEnv.Init(); }

        internal static ByokVendor LocalVendor(string id, string name, string baseUrl, string model)
        {
            return new ByokVendor { Id = id, Name = name, BaseUrl = baseUrl, ApiKey = "sk-local-test", SelectedModel = model, DefaultHarnessId = "openai-reasoning-patch", Models = new[] { model } };
        }

        internal static string Patch(string file, string search, string replace)
        {
            return "### File: " + file + "\n<<<<<<< SEARCH\n" + search + "\n=======\n" + replace + "\n>>>>>>> REPLACE";
        }

        [Fact]
        public async Task TwoProvidersRunConcurrently_EachIsVerified_PassingCandidateRanksFirst_NothingIsWritten()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = AgentTrustTestEnv.NewDir("bon");
            using (var server = new FakeOpenAiServer(model => model == "good-model"
                ? Patch("calc.txt", "result = WRONG", "result = GOOD")
                : Patch("calc.txt", "result = WRONG", "result = STILL_WRONG\nextra line")))
            {
                try
                {
                    string calc = Path.Combine(ws, "calc.txt");
                    File.WriteAllText(calc, "header\nresult = WRONG\n");
                    var engine = new LlmHarnessAndAgentEngine(null);
                    engine.EnableSseStreaming = false;
                    engine.Vendors = new List<ByokVendor>
                    {
                        LocalVendor("custom", "Local A", server.BaseUrl, "good-model"),
                        LocalVendor("openai", "Local B", server.BaseUrl, "bad-model")
                    };
                    var coord = new AgentTrustCoordinator(AgentTrustTestEnv.Settings(requireApproval: false));
                    coord.CommandOverride = VerificationCommandResolver.Custom("grep -q 'result = GOOD' calc.txt", "test override", false);
                    var req = new AgentTurnRequest
                    {
                        Engine = engine, Mode = "composer", Prompt = "fix the result", Vendor = engine.Vendors[0], HarnessId = "auto",
                        WorkspaceRoot = ws, WorkspaceFiles = new List<string> { calc }
                    };
                    var run = coord.PlanBestOfN(req, 2);
                    Assert.Equal(2, run.Specs.Count);
                    var updates = new List<string>();
                    await coord.RunBestOfNAsync(req, run, true, r => { lock (updates) updates.Add(r.Spec.Vendor.SelectedModel + ":" + r.State); }, CancellationToken.None);

                    Assert.Equal(2, run.Results.Count);
                    Assert.True(server.Requests >= 2);
                    var best = run.Results[0];
                    Assert.Equal("good-model", best.Spec.Vendor.SelectedModel);
                    Assert.True(best.Passed);
                    Assert.True(best.Recommended);
                    Assert.Equal(1, best.FilesChanged);
                    Assert.Equal(0, best.EstimatedCostUsd); // loopback endpoint = free
                    var worst = run.Results[1];
                    Assert.Equal(VerificationStatus.Failed, worst.Verification.Status);
                    Assert.True(worst.Added + worst.Removed > best.Added + best.Removed);
                    Assert.True(best.LatencyMs >= 0);
                    Assert.Contains(updates, u => u == "good-model:verifying");
                    Assert.Equal("header\nresult = WRONG\n", File.ReadAllText(calc)); // nothing applied

                    var adopted = coord.Adopt(best);
                    Assert.Same(adopted, coord.ActiveChangeSet);
                    Assert.True(adopted.IsVerifiedPassing);
                    Assert.Equal("best-of-n", adopted.Origin);
                    // The user's own selection was not modified by candidate cloning.
                    Assert.Equal("good-model", engine.Vendors[0].SelectedModel);
                }
                finally { AgentTrustTestEnv.Delete(ws); }
            }
        }

        [Fact]
        public void Planner_RespectsOfflineMode_AndFillsWithOtherModels()
        {
            var cloud = new ByokVendor { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", ApiKey = "sk-123456", SelectedModel = "gpt-4o", Models = new[] { "gpt-4o", "gpt-4o-mini" } };
            var local = new ByokVendor { Id = "ollama", Name = "Ollama", BaseUrl = "http://127.0.0.1:11434/v1", SelectedModel = "qwen2.5-coder:7b", Models = new[] { "qwen2.5-coder:7b", "llama3.2:latest" } };
            var vendors = new List<ByokVendor> { cloud, local };
            string note;
            var online = BestOfNPlanner.Pick(vendors, cloud, "auto", 2, false, true, null, out note);
            Assert.Equal(new[] { "gpt-4o", "qwen2.5-coder:7b" }, online.Select(s => s.Vendor.SelectedModel).ToArray());

            var offline = BestOfNPlanner.Pick(vendors, cloud, "auto", 2, true, true, null, out note);
            Assert.All(offline, s => Assert.Equal("ollama", s.Vendor.Id));
            Assert.Equal(new[] { "qwen2.5-coder:7b", "llama3.2:latest" }, offline.Select(s => s.Vendor.SelectedModel).ToArray());
            Assert.Contains("Offline", note);

            var none = BestOfNPlanner.Pick(vendors, cloud, "auto", 2, true, false, null, out note);
            Assert.Empty(none);
            Assert.Contains("Offline Mode", note);

            double usd;
            Assert.True(AgentModelPricing.TryEstimate(cloud, 1000000, 0, out usd));
            Assert.Equal(2.5, usd, 3);
            Assert.False(AgentModelPricing.TryEstimate(new ByokVendor { Id = "x", BaseUrl = "https://x.example/v1", SelectedModel = "mystery" }, 10, 10, out usd));
        }

        [Fact]
        public async Task IsolatedCopy_RefusesTerminal_AndRepairTurnsPatchOnTopOfStagedText()
        {
            string ws = AgentTrustTestEnv.NewDir("iso");
            try
            {
                string f = Path.Combine(ws, "m.py");
                File.WriteAllText(f, "a = 1\nb = 2\n");
                var engine = new LlmHarnessAndAgentEngine(null);
                var copy = engine.CreateIsolatedCopy();
                var tool = copy.ExecuteWorkspaceTool("run_terminal", new Dictionary<string, object> { { "command", "touch hacked" } }, ws, null, null, new Dictionary<string, string>(), "agent", null);
                Assert.False(tool.Success);
                Assert.False(File.Exists(Path.Combine(ws, "hacked")));

                engine.CustomModelResponder = (sys, msgs) => Task.FromResult(Patch("m.py", "b = 20", "b = 200"));
                var req = new AgentTurnRequest { Engine = engine, Mode = "composer", WorkspaceRoot = ws, WorkspaceFiles = new List<string> { f } };
                var turn = await AgentTrustCoordinator.RunRepairTurnAsync(req, "fix", new Dictionary<string, string> { { f, "a = 1\nb = 20\n" } }, CancellationToken.None);
                Assert.Equal("a = 1\nb = 200\n", turn.StagedFileContents[f]);
                Assert.Equal("a = 1\nb = 2\n", File.ReadAllText(f));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }
    }

    /// <summary>Toggles the process-wide Offline Mode: runs with the other sequential tests.</summary>
    [Collection("SequentialWorkbench")]
    public class AgentTrustOfflineTests
    {
        [Fact]
        public async Task OfflineMode_BlocksCloudCandidates_AtTheNetworkLayerToo()
        {
            bool before = NetworkLedger.OfflineMode;
            try
            {
                NetworkLedger.SetOfflineMode(true);
                var engine = new LlmHarnessAndAgentEngine(null);
                var cloud = new ByokVendor { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", ApiKey = "sk-123456", SelectedModel = "gpt-4o" };
                var orch = new BestOfNOrchestrator
                {
                    RunCandidate = (spec, ct) => engine.CreateIsolatedCopy().RunAutonomousAgentLoopAsync("composer", "x", spec.Vendor, "auto", null, null, null, null, "")
                };
                var results = await orch.RunAsync(new List<BestOfNCandidateSpec> { new BestOfNCandidateSpec { Vendor = cloud, Label = "cloud" } }, "x", CancellationToken.None);
                Assert.Single(results);
                Assert.Equal(0, results[0].FilesChanged);
                Assert.Equal("Offline Mode", results[0].Turn.ProviderUsed);
            }
            finally { NetworkLedger.SetOfflineMode(before); }
        }
    }
}
