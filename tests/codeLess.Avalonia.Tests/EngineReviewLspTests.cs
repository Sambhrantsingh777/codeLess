using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>ENGINE_REVIEW LSP-2 .. LSP-13: workspace edits, resource operations, snippets, process handling.</summary>
    public class EngineReviewLspTests : IDisposable
    {
        private readonly string root;

        public EngineReviewLspTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cl-lsp-review-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private string P(string name) => Path.Combine(root, name);

        private static LspWorkspaceEdit Rename(string from, string to, bool overwrite, bool ignoreIfExists)
        {
            var we = new LspWorkspaceEdit();
            we.Steps.Add(new LspWorkspaceEdit.Step { Resource = new LspResourceOperation { Kind = "rename", FilePath = from, NewFilePath = to, Overwrite = overwrite, IgnoreIfExists = ignoreIfExists } });
            return we;
        }

        private static LspWorkspaceEdit TextEdit(string path, int line, int c0, int c1, string text, int version = -1)
        {
            var we = new LspWorkspaceEdit();
            we.Steps.Add(new LspWorkspaceEdit.Step { FilePath = path, Version = version, Edits = new List<LspTextEdit> { new LspTextEdit(new LspRange(line, c0, line, c1), text) } });
            return we;
        }

        private static bool NameExistsExactly(string dir, string name) => Directory.GetFileSystemEntries(dir).Any(e => Path.GetFileName(e) == name);

        // ---------------------------------------------------------------- LSP-2

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LSP2_CaseOnlyRename_KeepsTheFile_WithAndWithoutOverwrite(bool overwrite)
        {
            File.WriteAllText(P("Foo.ts"), "export const a = 1;\n");
            var res = LspEditing.ApplyWorkspaceEdit(Rename(P("Foo.ts"), P("foo.ts"), overwrite, false), _ => null, null);
            Assert.True(res.Success, string.Join("; ", res.Errors));
            Assert.True(NameExistsExactly(root, "foo.ts"));
            Assert.Equal("export const a = 1;\n", File.ReadAllText(P("foo.ts")));
            Assert.Single(Directory.GetFiles(root));
        }

        [Fact]
        public void LSP2_RenameOntoOtherFile_WithOverwrite_ReplacesTarget_WithoutOverwriteFails_SourceKept()
        {
            File.WriteAllText(P("a.ts"), "A");
            File.WriteAllText(P("b.ts"), "B");
            var fail = LspEditing.ApplyWorkspaceEdit(Rename(P("a.ts"), P("b.ts"), false, false), _ => null, null);
            Assert.False(fail.Success);
            Assert.Equal("A", File.ReadAllText(P("a.ts")));
            Assert.Equal("B", File.ReadAllText(P("b.ts")));
            var ok = LspEditing.ApplyWorkspaceEdit(Rename(P("a.ts"), P("b.ts"), true, false), _ => null, null);
            Assert.True(ok.Success, string.Join("; ", ok.Errors));
            Assert.False(File.Exists(P("a.ts")));
            Assert.Equal("A", File.ReadAllText(P("b.ts")));
            Assert.Single(Directory.GetFileSystemEntries(root)); // no temp / backup leftovers
        }

        [Fact]
        public void LSP2_CaseOnlyRename_OfAFolder_Works()
        {
            Directory.CreateDirectory(P("Models"));
            File.WriteAllText(Path.Combine(P("Models"), "a.ts"), "A");
            var res = LspEditing.ApplyWorkspaceEdit(Rename(P("Models"), P("models"), false, false), _ => null, null);
            Assert.True(res.Success, string.Join("; ", res.Errors));
            Assert.True(NameExistsExactly(root, "models"));
            Assert.Equal("A", File.ReadAllText(Path.Combine(P("models"), "a.ts")));
        }

        // ---------------------------------------------------------------- LSP-11

        [Fact]
        public void LSP11_OverwriteWinsOverIgnoreIfExists_AndDeleteHonoursIgnoreIfNotExists()
        {
            File.WriteAllText(P("a.ts"), "A");
            File.WriteAllText(P("b.ts"), "B");
            var r = LspEditing.ApplyWorkspaceEdit(Rename(P("a.ts"), P("b.ts"), true, true), _ => null, null);
            Assert.True(r.Success);
            Assert.Equal("A", File.ReadAllText(P("b.ts")));

            File.WriteAllText(P("c.ts"), "C");
            File.WriteAllText(P("d.ts"), "D");
            var skip = LspEditing.ApplyWorkspaceEdit(Rename(P("c.ts"), P("d.ts"), false, true), _ => null, null);
            Assert.True(skip.Success);
            Assert.Equal("C", File.ReadAllText(P("c.ts")));

            var del = new LspWorkspaceEdit();
            del.Steps.Add(new LspWorkspaceEdit.Step { Resource = new LspResourceOperation { Kind = "delete", FilePath = P("missing.ts"), IgnoreIfNotExists = true } });
            Assert.True(LspEditing.ApplyWorkspaceEdit(del, _ => null, null).Success);
            var del2 = new LspWorkspaceEdit();
            del2.Steps.Add(new LspWorkspaceEdit.Step { Resource = new LspResourceOperation { Kind = "delete", FilePath = P("missing.ts") } });
            Assert.False(LspEditing.ApplyWorkspaceEdit(del2, _ => null, null).Success);

            var create = new LspWorkspaceEdit();
            create.Steps.Add(new LspWorkspaceEdit.Step { Resource = new LspResourceOperation { Kind = "create", FilePath = P("d.ts") } });
            Assert.False(LspEditing.ApplyWorkspaceEdit(create, _ => null, null).Success); // exists, neither overwrite nor ignoreIfExists
            Assert.Equal("D", File.ReadAllText(P("d.ts")));
        }

        [Fact]
        public void LSP11_CreateWithOverwrite_OnAnOpenFile_EmptiesTheBuffer_AsUndoableEdit()
        {
            File.WriteAllText(P("open.ts"), "old\n");
            var buf = new EditorTabBuffer { FilePath = P("open.ts"), Content = "old\nunsaved\n" };
            buf.SyncLinesFromContent();
            var create = new LspWorkspaceEdit();
            create.Steps.Add(new LspWorkspaceEdit.Step { Resource = new LspResourceOperation { Kind = "create", FilePath = P("open.ts"), Overwrite = true } });
            var r = LspEditing.ApplyWorkspaceEdit(create, p => LspText.PathKey(p) == LspText.PathKey(buf.FilePath) ? buf : null, null);
            Assert.True(r.Success);
            Assert.Equal("", LspEditing.BufferText(buf));
            Assert.Contains(buf, r.ChangedBuffers);
            Assert.True(buf.Undo());
            Assert.Contains("unsaved", LspEditing.BufferText(buf));
        }

        // ---------------------------------------------------------------- LSP-3

        [Fact]
        public void LSP3_EditToClosedLatin1File_KeepsEveryOtherByte()
        {
            byte[] latin1 = { (byte)'#', (byte)' ', (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', (byte)'x', (byte)'=', (byte)'1', (byte)'\n' };
            File.WriteAllBytes(P("l1.py"), latin1);
            var r = LspEditing.ApplyWorkspaceEdit(TextEdit(P("l1.py"), 1, 0, 1, "y"), _ => null, null);
            Assert.True(r.Success, string.Join("; ", r.Errors));
            byte[] expected = (byte[])latin1.Clone();
            expected[7] = (byte)'y';
            Assert.Equal(expected, File.ReadAllBytes(P("l1.py")));
        }

        [Fact]
        public void LSP3_EditPreservesBom_MixedLineEndings_AndUsesDominantEolForInsertedText()
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            File.WriteAllBytes(P("m.txt"), bom.Concat(Encoding.UTF8.GetBytes("a\r\nb\r\nc\nd\r\n")).ToArray());
            var r = LspEditing.ApplyWorkspaceEdit(TextEdit(P("m.txt"), 0, 1, 1, "1\n2"), _ => null, null);
            Assert.True(r.Success, string.Join("; ", r.Errors));
            Assert.Equal(bom.Concat(Encoding.UTF8.GetBytes("a1\r\n2\r\nb\r\nc\nd\r\n")).ToArray(), File.ReadAllBytes(P("m.txt")));
        }

        [Fact]
        public void LSP3_UnrepresentableText_FailsWithoutTouchingTheFile()
        {
            byte[] latin1 = { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' };
            File.WriteAllBytes(P("l2.txt"), latin1);
            var r = LspEditing.ApplyWorkspaceEdit(TextEdit(P("l2.txt"), 0, 0, 0, "→"), _ => null, null);
            Assert.False(r.Success);
            Assert.Equal(latin1, File.ReadAllBytes(P("l2.txt")));
        }

        // ---------------------------------------------------------------- LSP-6

        [Fact]
        public void LSP6_EditComputedBeforeTyping_IsRejectedAsAWhole()
        {
            var buf = new EditorTabBuffer { FilePath = P("x.py"), Content = "value = 1\nprint(value)\n" };
            buf.SyncLinesFromContent();
            File.WriteAllText(P("other.py"), "value\n");
            var guard = LspEditGuard.Capture(new[] { buf }, null);
            // The user types while the rename is computed.
            buf.Content = "# typed\nvalue = 1\nprint(value)\n";
            buf.SyncLinesFromContent();
            var we = TextEdit(P("x.py"), 0, 0, 5, "total");
            we.Steps.Add(new LspWorkspaceEdit.Step { FilePath = P("other.py"), Edits = new List<LspTextEdit> { new LspTextEdit(new LspRange(0, 0, 0, 5), "total") } });
            var r = LspEditing.ApplyWorkspaceEdit(we, p => LspText.PathKey(p) == LspText.PathKey(buf.FilePath) ? buf : null, null, guard);
            Assert.True(r.Rejected);
            Assert.Equal("# typed\nvalue = 1\nprint(value)\n", buf.Content);
            Assert.Equal("value\n", File.ReadAllText(P("other.py"))); // transactional: nothing applied
        }

        [Fact]
        public void LSP6_TextDocumentEditVersion_MustMatchTheVersionSentToTheServer()
        {
            var buf = new EditorTabBuffer { FilePath = P("v.py"), Content = "a = 1\n" };
            buf.SyncLinesFromContent();
            var guard = LspEditGuard.Capture(new[] { buf }, null);
            guard.SentVersion = _ => 7;
            Func<string, EditorTabBuffer> find = p => buf;
            Assert.NotNull(guard.Validate(TextEdit(P("v.py"), 0, 0, 1, "b", version: 6), find));
            Assert.Null(guard.Validate(TextEdit(P("v.py"), 0, 0, 1, "b", version: 7), find));
            Assert.Null(guard.Validate(TextEdit(P("v.py"), 0, 0, 1, "b", version: -1), find));
            var ok = LspEditing.ApplyWorkspaceEdit(TextEdit(P("v.py"), 0, 0, 1, "b", version: 7), find, null, guard);
            Assert.True(ok.Success);
            Assert.Equal("b = 1\n", buf.Content.Replace("\r\n", "\n"));
        }

        [Fact]
        public void LSP6_ServerInitiatedEdit_IsRejectedWhileTheServerHasNotSeenTheLatestTyping()
        {
            var buf = new EditorTabBuffer { FilePath = P("s.py"), Content = "a = 1\nb = 2\n" };
            buf.SyncLinesFromContent();
            var guard = new LspEditGuard { SentText = _ => "a = 1\n" }; // server is one keystroke behind
            Assert.NotNull(guard.Validate(TextEdit(P("s.py"), 0, 0, 1, "z"), _ => buf));
            guard.SentText = _ => "a = 1\nb = 2\n";
            Assert.Null(guard.Validate(TextEdit(P("s.py"), 0, 0, 1, "z"), _ => buf));
        }

        // ---------------------------------------------------------------- LSP-10

        [Theory]
        [InlineData("x$99999999999")]
        [InlineData("x${99999999999:name}")]
        [InlineData("x$\u0663 y")]               // Arabic-Indic digit three
        [InlineData("x${\u0663:a}")]
        public void LSP10_AbsurdTabStops_AreLiteral_NotAnException(string snippet)
        {
            int caret, sel;
            string text = LspText.ExpandSnippet(snippet, out caret, out sel);
            Assert.StartsWith("x", text);
            var exp = SnippetEngine.Expand(snippet, "", "    ", new Dictionary<string, string>());
            Assert.NotNull(exp);
        }

        // ---------------------------------------------------------------- LSP-5

        [Fact]
        public void LSP5_JdtlsDataDir_IsStable_PerProject_AndNotInTemp()
        {
            string a1 = LanguageServerRegistry.JdtlsDataDir("/work/project-a");
            string a2 = LanguageServerRegistry.JdtlsDataDir("/work/project-a/");
            string b = LanguageServerRegistry.JdtlsDataDir("/work/project-b");
            Assert.Equal(a1, a2);
            Assert.NotEqual(a1, b);
            Assert.False(a1.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
            Assert.Contains("project-a", Path.GetFileName(a1));
            // Known value: independent of string.GetHashCode randomization.
            Assert.Equal(AgentHash.Sha1Hex(Encoding.UTF8.GetBytes(LspText.PathKey("/work/project-a"))).Substring(0, 16), Path.GetFileName(a1).Substring("project-a-".Length));
        }

        // ---------------------------------------------------------------- LSP-8

        [Fact]
        public void LSP8_CmdShimArguments_QuoteAndCaretEscapeEverything()
        {
            string args = LanguageServerRegistry.BuildCmdExeArguments(@"C:\Users\R&D\AppData\Roaming\npm\pyright-langserver.cmd", new[] { "--stdio", "%PATH%", "a\"b" });
            // The program path sits in real quotes: cmd needs them to keep spaces in one token (& is literal inside them).
            Assert.Equal("/d /s /c \"\"C:\\Users\\R&D\\AppData\\Roaming\\npm\\pyright-langserver.cmd\" ^\"--stdio^\" ^\"^%PATH^%^\" ^\"a\\^\"b^\"\"", args);
            Assert.StartsWith("/d /s /c \"\"C:\\Users\\John Smith\\AppData\\Roaming\\npm\\npm.cmd\" ^\"install^\"",
                LanguageServerRegistry.BuildCmdExeArguments(@"C:\Users\John Smith\AppData\Roaming\npm\npm.cmd", new[] { "install" }));
            Assert.Equal("\"C:\\dir\\\\\"", LanguageServerRegistry.QuoteArgAlways(@"C:\dir\"));
            Assert.Equal("^\"a b^\"", TaskConfiguration.QuoteWindows("a b"));
            Assert.Equal("^\"50^%^\"", TaskConfiguration.QuoteWindows("50%"));
        }

        // ---------------------------------------------------------------- LSP-9

        [Fact]
        public void LSP9_Probe_TimesOut_AndAChattyStderrCannotBlockIt()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            var sw = Stopwatch.StartNew();
            Assert.Null(LanguageServerRegistry.RunAndCapture("/bin/sleep", "10", 600));
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(5000), "timeout not enforced: " + sw.ElapsedMilliseconds);
            string outp = LanguageServerRegistry.RunAndCapture("/bin/sh", "-c \"head -c 3000000 /dev/zero 1>&2; echo ok\"", 20000);
            Assert.Equal("ok", (outp ?? "").Trim());
        }

        // ---------------------------------------------------------------- LSP-12

        [Fact]
        public void LSP12_ClientCommands_AreHandledLocally_OnlyServerListedCommandsAreSent()
        {
            var server = new List<string> { "_typescript.applyCompletionCodeAction" };
            Assert.Equal(LanguageServerManager.LspCommandTarget.TriggerParameterHints, LanguageServerManager.ClassifyCommand("editor.action.triggerParameterHints", server));
            Assert.Equal(LanguageServerManager.LspCommandTarget.TriggerSuggest, LanguageServerManager.ClassifyCommand("editor.action.triggerSuggest", server));
            Assert.Equal(LanguageServerManager.LspCommandTarget.Server, LanguageServerManager.ClassifyCommand("_typescript.applyCompletionCodeAction", server));
            Assert.Equal(LanguageServerManager.LspCommandTarget.Ignore, LanguageServerManager.ClassifyCommand("editor.action.showReferences", server));
            Assert.Equal(LanguageServerManager.LspCommandTarget.Ignore, LanguageServerManager.ClassifyCommand(null, server));
        }

        // ---------------------------------------------------------------- LSP-13

        [Fact]
        public void LSP13_RequestOnAClosedConnection_FailsFast_EvenWithoutTimeout()
        {
            var input = new MemoryStream(new byte[0]); // EOF at once -> connection closes
            var conn = new LspJsonRpcConnection(input, new MemoryStream());
            var ep = new LspRpcEndpoint(conn);
            ep.Start();
            var sw = Stopwatch.StartNew();
            while (!ep.IsClosed && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Assert.True(ep.IsClosed);
            var t = ep.SendRequestAsync("x", null, CancellationToken.None, 0);
            try { t.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            Assert.True(t.IsCompleted, "request on a closed connection never completed");
            Assert.True(t.IsFaulted);
            Assert.Equal(0, ep.PendingCount);
            ep.Dispose();
        }

        // ---------------------------------------------------------------- LSP-7 (glob)

        [Theory]
        [InlineData("**/*.py", "/r/a/b.py", true)]
        [InlineData("**/*.py", "/r/a/b.pyx", false)]
        [InlineData("**/*.{py,pyi}", "/r/x.pyi", true)]
        [InlineData("**/Cargo.toml", "/r/crate/Cargo.toml", true)]
        [InlineData("src/*.ts", "/r/src/a.ts", true)]
        [InlineData("src/*.ts", "/r/src/sub/a.ts", false)]
        [InlineData("**/go.[ms][ou][dm]", "/r/m/go.mod", true)]
        public void LSP7_GlobMatching(string glob, string path, bool expected)
        {
            var w = new LspFileWatcher { Glob = LspGlob.ToRegex(glob), Root = "/r", Pattern = glob };
            Assert.Equal(expected, w.Matches(path, 2));
        }

        [Fact]
        public void LSP7_WatchKind_FiltersEventTypes_AndRelativePatternUsesItsBase()
        {
            var opts = LspJson.Obj("watchers", LspJson.Arr(
                LspJson.Obj("globPattern", LspJson.Obj("baseUri", LspText.PathToUri("/r/sub"), "pattern", "*.json"), "kind", 1)));
            var ws = LspFileWatcher.ParseMany(opts, "/r");
            Assert.Single(ws);
            Assert.True(ws[0].Matches("/r/sub/a.json", 1));
            Assert.False(ws[0].Matches("/r/sub/a.json", 2));   // change not watched (kind = create only)
            Assert.False(ws[0].Matches("/r/a.json", 1));       // outside the base
        }
    }

    /// <summary>LSP-4 / LSP-7 against the fake language server process.</summary>
    [Collection("SequentialWorkbench")]
    public class EngineReviewLspProcessTests : IDisposable
    {
        private readonly string root;
        private readonly string python;
        private readonly string script;
        private LanguageServerManager mgr;

        public EngineReviewLspProcessTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cl-lsp-proc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            python = LanguageServerRegistry.FindExecutable("python3", null) ?? LanguageServerRegistry.FindExecutable("python", null);
            script = Path.Combine(AppContext.BaseDirectory, "fake_lsp_server.py");
        }

        public void Dispose()
        {
            try { mgr?.Dispose(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        private LanguageServerManager NewManager(string extraArgs)
        {
            var m = new LanguageServerManager(a => a());
            m.ChangeDebounceMs = 20;
            m.RestartBackoffMs = 50;
            m.WatchedFilesDebounceMs = 30;
            string line = "python = \"" + python + "\" \"" + script + "\" " + extraArgs;
            m.UserSettingsProvider = () => LanguageServerUserSettings.Parse(line, false);
            m.SetWorkspaceRoot(root);
            mgr = m;
            return m;
        }

        private static void WaitUntil(Func<bool> cond, int ms, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return; Thread.Sleep(25); }
            Assert.Fail("Timed out waiting for " + what);
        }

        private static int CountProcesses(string tag)
        {
            var psi = new ProcessStartInfo("pgrep", "-f " + tag) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        [Fact]
        public void LSP4_RestartWhileStarting_LeavesExactlyOneServerProcess()
        {
            if (python == null || Path.DirectorySeparatorChar == '\\') return;
            string tag = "cltag" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var m = NewManager("--init-delay-ms 1200 --tag " + tag);
            string file = Path.Combine(root, "a.py");
            File.WriteAllText(file, "x = 1\n");
            m.OpenDocument(file, () => "x = 1\n");
            Thread.Sleep(300);                       // first server is waiting in initialize
            var inst = m.GetInstanceForPath(file);
            Assert.NotNull(inst);
            m.RestartInstance(inst);
            Thread.Sleep(150);
            m.RestartInstance(inst);                 // double restart while starting
            WaitUntil(() => inst.State == LanguageServerState.Running, 20000, "server running");
            Thread.Sleep(800);
            Assert.Equal(1, CountProcesses(tag));
            m.Dispose();
            WaitUntil(() => CountProcesses(tag) == 0, 10000, "all server processes gone");
        }

        [Fact]
        public void LSP7_FileEvents_AreSentToServersThatRegisteredWatchers()
        {
            if (python == null) return;
            var m = NewManager("--watch-py");
            string file = Path.Combine(root, "main.py");
            File.WriteAllText(file, "x = 1\n");
            m.OpenDocument(file, () => "x = 1\n");
            var inst = m.GetInstanceForPath(file);
            WaitUntil(() => inst.State == LanguageServerState.Running, 20000, "server running");
            WaitUntil(() => { return inst.RegisteredFileWatcherCount > 0; }, 10000, "watcher registration");
            string created = Path.Combine(root, "pkg", "new_mod.py");
            m.NotifyFileSystemChange(created, LanguageServerManager.FileCreated);
            m.NotifyFileSystemChange(created, LanguageServerManager.FileChanged); // collapses into "created"
            m.NotifyFileSystemChange(Path.Combine(root, "notes.txt"), LanguageServerManager.FileChanged); // not watched
            m.NotifyFileSystemChange(Path.Combine(root, ".git", "x.py"), LanguageServerManager.FileChanged); // .git ignored
            List<object> watched = null;
            WaitUntil(() =>
            {
                var t = m.RequestRawAsync(file, "fake/getState", u => null, CancellationToken.None, 5000);
                if (!t.Wait(6000)) return false;
                watched = LspJson.GetList(t.Result, "watched");
                return watched != null && watched.Count > 0;
            }, 10000, "didChangeWatchedFiles");
            Assert.Single(watched);
            var ev = (List<object>)watched[0];
            Assert.Equal(LspText.PathKey(created), LspText.PathKey(LspText.UriToPath((string)ev[0])));
            Assert.Equal(1L, Convert.ToInt64(ev[1]));
        }
    }
}
