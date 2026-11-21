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
