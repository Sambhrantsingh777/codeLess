using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Regressions reported on the macOS build: nested repositories missing from the Explorer, New Folder not
    /// working in an existing project, git status for sub-folder workspaces / nested repos, and quit handling.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class ExplorerAndGitRegressionTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackup;
        private readonly string _root;

        public ExplorerAndGitRegressionTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _settingsBackup = File.Exists(_settingsFilePath) ? File.ReadAllText(_settingsFilePath) : null;
            _root = Path.Combine(Path.GetTempPath(), "codeLess_Regress_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (_settingsBackup != null) File.WriteAllText(_settingsFilePath, _settingsBackup);
                else if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);
            }
            catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private static bool GitAvailable()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static void Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.name=t");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("user.email=t@t");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("commit.gpgsign=false");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p.WaitForExit(15000);
            Assert.True(p.ExitCode == 0, "git " + string.Join(" ", args) + ": " + p.StandardError.ReadToEnd());
        }

        private static MainWindow OpenWindow(string ws)
        {
            var w = new MainWindow(ws);
            w.SuppressUnsavedPrompts = true;
            w.Show();
            return w;
        }

        [AvaloniaFact]
        public void NestedRepo_IsVisibleInExplorer_EvenWhenGitignoredOrNamedLikeBuildOutput()
        {
            // outer/.gitignore ignores the nested repo (the common "repo inside repo" setup)
            File.WriteAllText(Path.Combine(_root, ".gitignore"), "inner-service/\nbuild\n");
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
            Directory.CreateDirectory(Path.Combine(_root, "inner-service", ".git"));
            File.WriteAllText(Path.Combine(_root, "inner-service", "main.go"), "package main\n");
            Directory.CreateDirectory(Path.Combine(_root, "build", ".git")); // nested repo named "build"
            File.WriteAllText(Path.Combine(_root, "app.py"), "print(1)\n");
            File.WriteAllText(Path.Combine(_root, "logo.png"), "\x89PNG");

            var w = OpenWindow(_root);
            var names = w.ExplorerTree.Nodes[0].Nodes.Select(n => n.Text).ToList();
            Assert.Contains("inner-service", names);
            Assert.Contains("build", names);
            Assert.Contains("logo.png", names);      // binaries are listed like in VS Code
            Assert.DoesNotContain(".git", names);    // VCS metadata stays hidden

            var inner = w.ExplorerTree.Nodes[0].Nodes.First(n => n.Text == "inner-service");
            w.EnsureDirectoryNodePopulated(inner);
            Assert.Contains(inner.Nodes, n => n.Text == "main.go");
            Assert.DoesNotContain(inner.Nodes, n => n.Text == ".git");
            w.Close();
        }

        [AvaloniaFact]
        public void NewFolder_InExpandedSubfolder_IsCreatedRevealedAndKeepsOtherFoldersExpanded()
        {
            Directory.CreateDirectory(Path.Combine(_root, "src", "core"));
            Directory.CreateDirectory(Path.Combine(_root, "docs"));
            File.WriteAllText(Path.Combine(_root, "src", "core", "a.cs"), "class A {}\n");
            File.WriteAllText(Path.Combine(_root, "docs", "readme.md"), "# docs\n");

            var w = OpenWindow(_root);
            var root = w.ExplorerTree.Nodes[0];
            var docs = root.Nodes.First(n => n.Text == "docs");
            w.EnsureDirectoryNodePopulated(docs);
            docs.IsExpanded = true;
            w.ExplorerTree.RebuildTreeLayout();

            string srcDir = Path.Combine(_root, "src");
            string created = w.CreateFolderInDirectory(srcDir, "features/auth");
            Assert.NotNull(created);
            Assert.True(Directory.Exists(Path.Combine(srcDir, "features", "auth")));

            var node = w.ExplorerTree.FindNodeByPath(created);
            Assert.NotNull(node);
            Assert.Same(node, w.ExplorerTree.SelectedNode);
            Assert.Contains(node, w.ExplorerTree.VisibleFlatRows);       // ancestors were expanded
            var docsAfter = w.ExplorerTree.FindNodeByPath(Path.Combine(_root, "docs"));
            Assert.True(docsAfter.IsExpanded);                          // unrelated expansion preserved

            // Duplicate and path-escaping names are rejected without throwing
            Assert.Null(w.CreateFolderInDirectory(srcDir, "features"));
            Assert.Null(w.CreateFolderInDirectory(srcDir, "../../escape"));
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_root), "escape")));
            w.Close();
        }

        [AvaloniaFact]
        public void NewFile_WithSubPath_CreatesParentsAndOpensTab()
        {
            File.WriteAllText(Path.Combine(_root, "x.txt"), "x");
            var w = OpenWindow(_root);
            string f = w.CreateFileInDirectory(_root, "lib/util/helpers.ts");
            Assert.NotNull(f);
            Assert.True(File.Exists(f));
            Assert.Equal(f, w.ActiveTab.FilePath);
            Assert.NotNull(w.ExplorerTree.FindNodeByPath(f));
            w.Close();
        }

        [AvaloniaFact]
        public void RenameFolder_RepointsOpenEditorsInside()
        {
            Directory.CreateDirectory(Path.Combine(_root, "old"));
            string file = Path.Combine(_root, "old", "m.py");
            File.WriteAllText(file, "x = 1\n");
            var w = OpenWindow(_root);
            var tab = w.OpenFileInTab(file);
            Assert.True(w.RenameExplorerPath(Path.Combine(_root, "old"), "renamed"));
            Assert.Equal(Path.Combine(_root, "renamed", "m.py"), tab.FilePath);
            Assert.True(File.Exists(tab.FilePath));
            w.Close();
        }

        [AvaloniaFact]
        public void ExternalFileCreation_ShowsUpAfterRefresh()
        {
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
            var w = OpenWindow(_root);
            Directory.CreateDirectory(Path.Combine(_root, "made-in-terminal"));
            w.RefreshExplorerTree();
            Assert.Contains(w.ExplorerTree.Nodes[0].Nodes, n => n.Text == "made-in-terminal");
            w.Close();
        }

        [Fact]
        public void GitStatus_SubfolderWorkspace_AndNestedRepo_UseCorrectRepoRoots()
        {
            if (!GitAvailable()) return;
            string outer = Path.Combine(_root, "outer");
            string sub = Path.Combine(outer, "packages", "web");
            string nested = Path.Combine(sub, "vendor-lib");
            Directory.CreateDirectory(nested);
            Git(outer, "init", "-q");
            File.WriteAllText(Path.Combine(sub, "index.js"), "1\n");
            File.WriteAllText(Path.Combine(outer, "root.txt"), "r\n");
            Git(outer, "add", "-A");
            Git(outer, "commit", "-q", "-m", "init");
            Git(nested, "init", "-q");
            File.WriteAllText(Path.Combine(nested, "lib file.js"), "new\n"); // space in name
            File.WriteAllText(Path.Combine(sub, "index.js"), "2\n");
            File.WriteAllText(Path.Combine(outer, "root.txt"), "changed\n");

            var entries = LlmHarnessAndAgentEngine.GetGitStatusEntries(sub);

            var idx = entries.Single(e => e.FullPath == Path.Combine(sub, "index.js"));
            Assert.Equal("packages/web/index.js", idx.RepoRelativePath);
            Assert.Equal("index.js", idx.RelativePath);

            var lib = entries.Single(e => e.FullPath == Path.Combine(nested, "lib file.js"));
            Assert.Equal('?', lib.IndexStatus);
            Assert.Equal(nested, lib.RepoRoot.TrimEnd('/'));
            Assert.Equal("lib file.js", lib.RepoRelativePath);

            Assert.DoesNotContain(entries, e => e.FullPath.EndsWith("root.txt"));       // outside opened folder
            Assert.DoesNotContain(entries, e => e.FullPath.TrimEnd('/') == nested && e.RepoRoot != nested); // no "?? vendor-lib/"
        }

        [AvaloniaFact]
        public void Scm_StageNestedRepoFile_RunsInNestedRepo()
        {
            if (!GitAvailable()) return;
            Git(_root, "init", "-q");
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a\n");
            string nested = Path.Combine(_root, "plugin");
            Directory.CreateDirectory(nested);
            Git(nested, "init", "-q");
            File.WriteAllText(Path.Combine(nested, "p.txt"), "p\n");

            var w = OpenWindow(_root);
            w.RefreshGitStatus();
            var entry = w.GitEntries.Single(e => e.FullPath == Path.Combine(nested, "p.txt"));
            w.ExecuteGitStageFile(entry);
            var after = w.GitEntries.Single(e => e.FullPath == Path.Combine(nested, "p.txt"));
            Assert.True(after.IsStaged);
            w.Close();
        }

        [AvaloniaFact]
        public void Quit_WithDirtyTab_PromptsAndCancelKeepsWindowOpen()
        {
            string file = Path.Combine(_root, "d.txt");
            File.WriteAllText(file, "x");
            var w = new MainWindow(_root);
            w.Show();
            var tab = w.OpenFileInTab(file);
            tab.IsDirty = true;
            int prompts = 0;
            w.UnsavedPromptOverride = dirty => { prompts++; return UnsavedPromptResult.Cancel; };
            w.Close();
            Assert.Equal(1, prompts);
            Assert.True(w.IsVisible);

            w.UnsavedPromptOverride = dirty => UnsavedPromptResult.DontSave;
            w.Close();
            Assert.False(w.IsVisible);
        }
    }
}

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class MultiRootAndReviewRegressionTests : IDisposable
    {
        private readonly string _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string _settingsBackup;
        private readonly string _root;

        public MultiRootAndReviewRegressionTests()
        {
            _settingsBackup = File.Exists(_settingsFilePath) ? File.ReadAllText(_settingsFilePath) : null;
            _root = Path.Combine(Path.GetTempPath(), "codeLess_MR_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (_settingsBackup != null) File.WriteAllText(_settingsFilePath, _settingsBackup);
                else if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);
            }
            catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        [AvaloniaFact]
        public void NewTerminal_InMultiRootWorkspace_AsksWhichFolder()
        {
            string a = Path.Combine(_root, "api"); string b = Path.Combine(_root, "web");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "x.txt"), "x");
            var w = new MainWindow(a);
            w.SuppressUnsavedPrompts = true;
            w.Show();

            // Single folder: no prompt.
            int asked = 0;
            w.TerminalFolderPickerOverride = roots => { asked++; return roots[1]; };
            w.RequestNewTerminal();
            Assert.Equal(0, asked);

            // Second folder added: the picker is offered both roots, and cancelling creates nothing.
            var field = typeof(MainWindow).GetField("additionalWorkspaceFolders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ((System.Collections.Generic.List<string>)field.GetValue(w)).Add(b);
            System.Collections.Generic.IReadOnlyList<string> offered = null;
            w.TerminalFolderPickerOverride = roots => { asked++; offered = roots; return null; };
            int before = w.TerminalSessionCount;
            w.RequestNewTerminal();
            Assert.Equal(1, asked);
            Assert.Equal(2, offered.Count);
            Assert.Contains(a, offered);
            Assert.Contains(b, offered);
            Assert.Equal(before, w.TerminalSessionCount);

            // Picking a folder creates the terminal there.
            w.TerminalFolderPickerOverride = roots => b;
            w.RequestNewTerminal();
            Assert.Equal(before + 1, w.TerminalSessionCount);
            Assert.Equal(b, w.ActiveTerminalWorkingDirectory);
            w.Close();
        }

        [Fact]
        public void GitStatusZ_FirstRecordUnstagedModified_IsParsedCorrectly()
        {
            try { using (var p = Process.Start(new ProcessStartInfo("git", "--version") { UseShellExecute = false, RedirectStandardOutput = true })) p.WaitForExit(5000); }
            catch { return; }
            string repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(repo);
            void Git(params string[] args)
            {
                var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var x in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }) psi.ArgumentList.Add(x);
                foreach (var x in args) psi.ArgumentList.Add(x);
                using var p = Process.Start(psi); p.WaitForExit(15000);
            }
            Git("init", "-q");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "1\n");
            Git("add", "-A"); Git("commit", "-q", "-m", "i");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "2\n"); // " M a.txt" is the FIRST record

            var engine = new LlmHarnessAndAgentEngine(new IdePersistentSettings());
            var items = engine.QueryGitWorkspaceStatus(repo);
            var e = items.Single();
            Assert.Equal(Path.Combine(repo, "a.txt"), e.FilePath);
            Assert.Equal("a.txt", e.RepoRelativePath);
            Assert.Equal("M", e.StatusCode);
            Assert.False(e.IsStaged);

            var av = LlmHarnessAndAgentEngine.GetGitStatusEntries(repo).Single();
            Assert.Equal(' ', av.IndexStatus);
            Assert.Equal('M', av.WorkingTreeStatus);
        }

        [AvaloniaFact]
        public void OpeningExecutableBinary_NeverLaunchesIt()
        {
            string exe = Path.Combine(_root, "setup.sh.bin");
            File.WriteAllBytes(exe, new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0, 0, 0, 1 });
            Assert.True(MainWindow.IsLikelyBinaryFile(exe));
            Assert.DoesNotContain(".exe", MainWindow.SafeViewerExtensions);
            Assert.DoesNotContain(".app", MainWindow.SafeViewerExtensions);
            Assert.DoesNotContain(".command", MainWindow.SafeViewerExtensions);
            Assert.Contains(".png", MainWindow.SafeViewerExtensions);
            var w = new MainWindow(_root);
            w.Show();
            Assert.Null(w.OpenFileInTab(exe));
            w.Close();
        }
    }
}

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class MultiCursorEditingTests : IDisposable
    {
        private readonly string _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string _settingsBackup;
        private readonly string _root;
        public MultiCursorEditingTests()
        {
            _settingsBackup = File.Exists(_settingsFilePath) ? File.ReadAllText(_settingsFilePath) : null;
            _root = Path.Combine(Path.GetTempPath(), "codeLess_MC_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
        }
        public void Dispose()
        {
            try { if (_settingsBackup != null) File.WriteAllText(_settingsFilePath, _settingsBackup); else if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath); } catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private (MainWindow w, EditorTabBuffer tab, AvaloniaVirtualEditorCanvas canvas) Setup(string text)
        {
            string f = Path.Combine(_root, "m.txt");
            File.WriteAllText(f, text);
            var w = new MainWindow(_root);
            w.SuppressUnsavedPrompts = true;
            w.Show();
            var tab = w.OpenFileInTab(f);
            return (w, tab, w.PrimaryEditor.Canvas);
        }

        private static void Type(AvaloniaVirtualEditorCanvas c, string text) =>
            c.RaiseEvent(new global::Avalonia.Input.TextInputEventArgs { RoutedEvent = global::Avalonia.Input.InputElement.TextInputEvent, Text = text });

        private static void ThreeCarets(EditorTabBuffer tab, AvaloniaVirtualEditorCanvas c, int col)
        {
            tab.CursorLine = 0; tab.CursorCol = col; tab.ClearSelection();
            c.ExtraCursors.Clear();
            c.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = col, AnchorLine = -1, AnchorCol = -1 });
            c.ExtraCursors.Add(new ExtraCursorCaret { Line = 2, Col = col, AnchorLine = -1, AnchorCol = -1 });
        }

        [AvaloniaFact]
        public void TypingBackspaceDeleteAndEnter_ApplyToEveryCursor()
        {
            var (w, tab, c) = Setup("foo\nfoo\nfoo\n");
            ThreeCarets(tab, c, 3);
            Type(c, "X");
            Assert.Equal(new[] { "fooX", "fooX", "fooX" }, tab.Lines.Take(3).ToArray());
            w.SimulateShortcut(global::Avalonia.Input.Key.Back, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal(new[] { "foo", "foo", "foo" }, tab.Lines.Take(3).ToArray());
            w.SimulateShortcut(global::Avalonia.Input.Key.Left, global::Avalonia.Input.KeyModifiers.None); // all carets move to col 2
            w.SimulateShortcut(global::Avalonia.Input.Key.Delete, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal(new[] { "fo", "fo", "fo" }, tab.Lines.Take(3).ToArray());
            w.SimulateShortcut(global::Avalonia.Input.Key.Enter, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal(new[] { "fo", "", "fo", "", "fo", "" }, tab.Lines.Take(6).ToArray());
            Assert.Equal(2, c.ExtraCursors.Count);
            Assert.All(c.ExtraCursors, ec => Assert.Equal(0, ec.Col));
            w.Close();
        }

        [AvaloniaFact]
        public void PastingOneLinePerCursor_DistributesLines_AndUndoIsOneStep()
        {
            var (w, tab, c) = Setup("a\nb\nc\n");
            ThreeCarets(tab, c, 1);
            Type(c, "1\n2\n3");
            Assert.Equal(new[] { "a1", "b2", "c3" }, tab.Lines.Take(3).ToArray());
            tab.PerformUndo();
            Assert.Equal(new[] { "a", "b", "c" }, tab.Lines.Take(3).ToArray());
            w.Close();
        }

        [AvaloniaFact]
        public void BackspaceAndArrows_NeverSplitEmoji()
        {
            var (w, tab, c) = Setup("x\U0001F680\n");
            tab.CursorLine = 0; tab.CursorCol = 3; tab.ClearSelection();
            w.SimulateShortcut(global::Avalonia.Input.Key.Left, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal(1, tab.CursorCol); // stepped over both UTF-16 halves
            w.SimulateShortcut(global::Avalonia.Input.Key.Right, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal(3, tab.CursorCol);
            w.SimulateShortcut(global::Avalonia.Input.Key.Back, global::Avalonia.Input.KeyModifiers.None);
            Assert.Equal("x", tab.Lines[0]);
            w.Close();
        }
    }
}
