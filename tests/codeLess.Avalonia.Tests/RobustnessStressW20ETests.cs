using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared 50,000-file temporary workspace fixture for W20 Area E large-workspace stress tests (E01-E05).
    /// Generated in Path.GetTempPath() (never in the repository) and deleted on Dispose.
    /// </summary>
    public sealed class LargeWorkspace50kFixture : IDisposable
    {
        public readonly string RootDir;
        public const int ModuleCount = 200;
        public const int FilesPerModule = 240; // 200 * 240 = 48,000 source files
        public const int ExcludedNodeModulesFiles = 1000;
        public const int GitIgnoredFiles = 1000;
        public const int TotalFilesOnDisk = ModuleCount * FilesPerModule + ExcludedNodeModulesFiles + GitIgnoredFiles; // 50,000
        public const string SearchNeedle = "UNIQUE_W20E_NEEDLE_ALPHA_98765";
        public readonly List<string> ExpectedNeedleFiles = new List<string>();

        public LargeWorkspace50kFixture()
        {
            RootDir = Path.Combine(Path.GetTempPath(), "codeless_w20e_50k_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(Path.Combine(RootDir, ".gitignore"), "ignored_out/\n*.ignored.log\n");

            string packagesDir = Path.Combine(RootDir, "packages");
            Directory.CreateDirectory(packagesDir);

            for (int m = 0; m < 10; m++)
            {
                string modDir = Path.Combine(packagesDir, "mod_" + m.ToString("D3"));
                ExpectedNeedleFiles.Add(Path.Combine(modDir, "file_007.ts"));
            }

            Parallel.For(0, ModuleCount, new ParallelOptions { MaxDegreeOfParallelism = 8 }, m =>
            {
                string modDir = Path.Combine(packagesDir, "mod_" + m.ToString("D3"));
                Directory.CreateDirectory(modDir);
                for (int f = 0; f < FilesPerModule; f++)
                {
                    string fp = Path.Combine(modDir, "file_" + f.ToString("D3") + ".ts");
                    if (m < 10 && f == 7)
                        File.WriteAllText(fp, "export const marker = '" + SearchNeedle + "';\n");
                    else
                        File.WriteAllText(fp, "export const v = " + (m * 1000 + f) + ";\n");
                }
            });

            string nmDir = Path.Combine(RootDir, "node_modules", "pkg_heavy");
            Directory.CreateDirectory(nmDir);
            Parallel.For(0, ExcludedNodeModulesFiles, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                string fp = Path.Combine(nmDir, "nm_" + i.ToString("D4") + ".js");
                File.WriteAllText(fp, "// " + SearchNeedle + " inside node_modules\nmodule.exports = " + i + ";\n");
            });

            string igDir = Path.Combine(RootDir, "ignored_out", "gen");
            Directory.CreateDirectory(igDir);
            Parallel.For(0, GitIgnoredFiles, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                string fp = Path.Combine(igDir, "gen_" + i.ToString("D4") + ".ts");
                File.WriteAllText(fp, "// " + SearchNeedle + " inside gitignored folder\nexport const g = " + i + ";\n");
            });
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// W20 Area E (robustness/stress) trust audit tests: E01..E20.
    /// Covers 50k-file workspaces, rapid typing in 200k-line files, opening/closing 200 tabs without leaks,
    /// 1,000-file watcher storms, crash-safe atomic saves (symlinks, Unix modes, ReadOnly, non-writable dir fallback,
    /// mid-stream write fault injection), and hot-exit backup recovery + corrupt backup resilience.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class RobustnessStressW20ETests : IClassFixture<LargeWorkspace50kFixture>, IDisposable
    {
        private readonly LargeWorkspace50kFixture _ws50k;
        private readonly string _tempDir;
        private readonly string _prevSettingsDir;
        private readonly string _prevBackupDir;

        public RobustnessStressW20ETests(LargeWorkspace50kFixture ws50k)
        {
            _ws50k = ws50k;
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _prevBackupDir = Environment.GetEnvironmentVariable("CODELESS_HOTEXIT_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), "codeless_w20e_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
            string settingsDir = Path.Combine(_tempDir, "settings");
            string backupDir = Path.Combine(_tempDir, "backups");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(backupDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", settingsDir);
            Environment.SetEnvironmentVariable("CODELESS_HOTEXIT_DIR", backupDir);
            EditorTabBuffer.MidStreamWriteFaultInjector = null;
        }

        public void Dispose()
        {
            EditorTabBuffer.MidStreamWriteFaultInjector = null;
            foreach (var w in App.WindowManager.GetOpenWindows().OfType<MainWindow>().ToList())
            {
                try { w.SuppressUnsavedPrompts = true; w.Close(); } catch { }
            }
            Dispatcher.UIThread.RunJobs();
            IdeSettingsWatcher.StopForTests();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            Environment.SetEnvironmentVariable("CODELESS_HOTEXIT_DIR", _prevBackupDir);
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch { }
            ForceFullGc();
        }

        private static void RenderCanvasOnce(AvaloniaVirtualEditorCanvas canvas)
        {
            canvas.Measure(new Size(1200, 800));
            canvas.Arrange(new Rect(0, 0, 1200, 800));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClearJitStackFrame()
        {
            Span<long> zero = stackalloc long[64];
            zero.Clear();
        }

        private static void ForceFullGc()
        {
            for (int i = 0; i < 6; i++)
            {
                try { global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
                Dispatcher.UIThread.RunJobs();
                ClearJitStackFrame();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            Dispatcher.UIThread.RunJobs();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        // =====================================================================
        // Sub-area 1: Large Workspace (~50k files) — E01..E05
        // =====================================================================

        [AvaloniaFact]
        public void E01_LargeWorkspace50kFiles_LoadWorkspace_PopulatesOnlyTopLevelLazily_AndStaysResponsive()
        {
            var sw = Stopwatch.StartNew();
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                w.LoadWorkspace(_ws50k.RootDir);
                Dispatcher.UIThread.RunJobs();
                sw.Stop();

                Assert.NotNull(w.ExplorerTree);
                Assert.NotEmpty(w.ExplorerTree.Nodes);
                var rootNode = w.ExplorerTree.Nodes[0];
                Assert.True(rootNode.IsExpanded);

                // Top-level children are populated, but subdirectories hold single __LAZY_DIR__ placeholders
                // rather than 50,000 eagerly constructed tree nodes.
                var packagesNode = rootNode.Nodes.FirstOrDefault(n => n.Text == "packages");
                Assert.NotNull(packagesNode);
                Assert.True(packagesNode.IsDirectory);
                Assert.False(packagesNode.IsExpanded);
                Assert.Single(packagesNode.Nodes);
                Assert.Equal("__LAZY_DIR__", packagesNode.Nodes[0].Tag);

                // Generous bound: loading a 50k-file workspace in headless mode must not hang or build 50k UI nodes.
                Assert.True(sw.ElapsedMilliseconds < 15000, $"LoadWorkspace took {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E02_LargeWorkspace50kFiles_ExplorerExpandAndRefresh_PopulatesSingleLevelOnly_NoQuadraticWork()
        {
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                w.LoadWorkspace(_ws50k.RootDir);
                var rootNode = w.ExplorerTree.Nodes[0];
                var packagesNode = rootNode.Nodes.First(n => n.Text == "packages");

                var sw = Stopwatch.StartNew();
                w.EnsureDirectoryNodePopulated(packagesNode);
                packagesNode.IsExpanded = true;
                w.ExplorerTree.RebuildTreeLayout();
                sw.Stop();

                // Expanding 'packages' populates the 200 module directories, each still lazy (__LAZY_DIR__).
                Assert.Equal(LargeWorkspace50kFixture.ModuleCount, packagesNode.Nodes.Count);
                Assert.All(packagesNode.Nodes, modNode =>
                {
                    Assert.True(modNode.IsDirectory);
                    Assert.Single(modNode.Nodes);
                    Assert.Equal("__LAZY_DIR__", modNode.Nodes[0].Tag);
                });
                Assert.True(sw.ElapsedMilliseconds < 5000, $"Expanding packages took {sw.ElapsedMilliseconds} ms");

                // Expand mod_000 -> populates its 240 files only
                var mod0 = packagesNode.Nodes[0];
                w.EnsureDirectoryNodePopulated(mod0);
                mod0.IsExpanded = true;
                Assert.Equal(LargeWorkspace50kFixture.FilesPerModule, mod0.Nodes.Count);

                // RefreshExplorerTree preserves expanded nodes (packages + mod_000) while keeping mod_001..mod_199 lazy
                sw.Restart();
                w.RefreshExplorerTree();
                sw.Stop();

                var refreshedRoot = w.ExplorerTree.Nodes[0];
                var refreshedPackages = refreshedRoot.Nodes.First(n => n.Text == "packages");
                Assert.True(refreshedPackages.IsExpanded);
                Assert.Equal(LargeWorkspace50kFixture.ModuleCount, refreshedPackages.Nodes.Count);
                Assert.True(refreshedPackages.Nodes[0].IsExpanded);
                Assert.Equal(LargeWorkspace50kFixture.FilesPerModule, refreshedPackages.Nodes[0].Nodes.Count);
                Assert.Single(refreshedPackages.Nodes[1].Nodes);
                Assert.Equal("__LAZY_DIR__", refreshedPackages.Nodes[1].Nodes[0].Tag);
                Assert.True(sw.ElapsedMilliseconds < 5000, $"RefreshExplorerTree took {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                w.Close();
            }
        }

        [Fact]
        public void E03_LargeWorkspace50kFiles_WorkspaceIgnoreRulesAndQuickOpenIndex_RespectExcludesAndSearchFast()
        {
            var rules = new WorkspaceIgnoreRules(_ws50k.RootDir, WorkspaceSearchEngine.DefaultExcludedFolders, respectGitIgnore: true);
            bool capped;
            var sw = Stopwatch.StartNew();
            var files = rules.EnumerateFiles(new[] { _ws50k.RootDir }, WorkspaceIgnoreRules.DefaultMaxIndexedFiles, CancellationToken.None, out capped);
            sw.Stop();

            Assert.False(capped);
            // 48,000 source files + .gitignore = 48,001 files; 1,000 node_modules + 1,000 gitignored files are pruned.
            Assert.Equal(LargeWorkspace50kFixture.ModuleCount * LargeWorkspace50kFixture.FilesPerModule + 1, files.Count);
            Assert.DoesNotContain(files, f => f.Contains("node_modules"));
            Assert.DoesNotContain(files, f => f.Contains("ignored_out"));

            var qoIndex = new QuickOpenFileIndex(_ws50k.RootDir, files);
            sw.Restart();
            var hits = qoIndex.Search("mod_123/file_045.ts", 50, null);
            sw.Stop();

            Assert.NotEmpty(hits);
            Assert.Equal("file_045.ts", qoIndex.GetName(hits[0].Index));
            Assert.Contains("mod_123/file_045.ts", qoIndex.GetRelativePath(hits[0].Index).Replace('\\', '/'));
            Assert.True(sw.ElapsedMilliseconds < 2000, $"QuickOpen search over 48k files took {sw.ElapsedMilliseconds} ms");
        }

        [Fact]
        public void E04_LargeWorkspace50kFiles_WorkspaceSearch_RespectsExcludesIncludeGlobsAndMaxResults()
        {
            var files = WorkspaceSearchEngine.EnumerateSearchFiles(
                new[] { _ws50k.RootDir },
                _ws50k.RootDir,
                WorkspaceSearchEngine.DefaultExcludedFolders,
                include: null,
                exclude: null,
                maxFiles: 100000,
                ct: CancellationToken.None);

            // Search across all 48,001 enumerated files for the needle that appears in 10 source files
            // (and in 1,000 excluded node_modules + 1,000 gitignored files).
            var res = WorkspaceSearchEngine.Search(
                _ws50k.RootDir,
                files,
                LargeWorkspace50kFixture.SearchNeedle,
                new WorkspaceSearchOptions { MaxResults = 1000 },
                CancellationToken.None,
                null);

            Assert.False(res.Cancelled);
            Assert.False(res.Truncated);
            Assert.Equal(10, res.Matches.Count);
            Assert.Equal(10, res.FilesMatched);
            Assert.All(res.Matches, m =>
            {
                Assert.DoesNotContain("node_modules", m.FilePath);
                Assert.DoesNotContain("ignored_out", m.FilePath);
            });

            // Include + Exclude glob filters prune to a single module
            var inc = GlobPathFilter.Parse("./packages/mod_000/**, ./packages/mod_001/**", _ws50k.RootDir);
            var exc = GlobPathFilter.Parse("**/mod_001/**", _ws50k.RootDir);
            var filteredFiles = WorkspaceSearchEngine.EnumerateSearchFiles(
                new[] { _ws50k.RootDir },
                _ws50k.RootDir,
                WorkspaceSearchEngine.DefaultExcludedFolders,
                inc,
                exc,
                maxFiles: 100000,
                ct: CancellationToken.None);
            Assert.Equal(LargeWorkspace50kFixture.FilesPerModule, filteredFiles.Count);

            // MaxResults truncation
            var truncRes = WorkspaceSearchEngine.Search(
                _ws50k.RootDir,
                files,
                LargeWorkspace50kFixture.SearchNeedle,
                new WorkspaceSearchOptions { MaxResults = 4 },
                CancellationToken.None,
                null);
            Assert.True(truncRes.Truncated);
            Assert.Equal(4, truncRes.Matches.Count);
        }

        [Fact]
        public void E05_LargeWorkspace50kFiles_SearchAndEnumeration_ArePromptlyCancellable()
        {
            // 1. Pre-cancelled token halts EnumerateSearchFiles and WorkspaceIgnoreRules.EnumerateFiles immediately
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var enumFiles = WorkspaceSearchEngine.EnumerateSearchFiles(
                    new[] { _ws50k.RootDir },
                    _ws50k.RootDir,
                    WorkspaceSearchEngine.DefaultExcludedFolders,
                    null,
                    null,
                    100000,
                    cts.Token);
                Assert.Empty(enumFiles);

                var rules = new WorkspaceIgnoreRules(_ws50k.RootDir, WorkspaceSearchEngine.DefaultExcludedFolders, true);
                bool capped;
                var ruleFiles = rules.EnumerateFiles(new[] { _ws50k.RootDir }, 100000, cts.Token, out capped);
                Assert.Empty(ruleFiles);
            }

            // 2. Cancelling mid-search when the first file match arrives stops scanning the remaining 48,000 files
            var allFiles = WorkspaceSearchEngine.EnumerateSearchFiles(
                new[] { _ws50k.RootDir },
                _ws50k.RootDir,
                WorkspaceSearchEngine.DefaultExcludedFolders,
                null,
                null,
                100000,
                CancellationToken.None);
            Assert.True(allFiles.Count >= 48000);

            using (var cts = new CancellationTokenSource())
            {
                var sw = Stopwatch.StartNew();
                var searchRes = WorkspaceSearchEngine.Search(
                    _ws50k.RootDir,
                    allFiles,
                    "export const v =",
                    new WorkspaceSearchOptions { MaxResults = 100000 },
                    cts.Token,
                    fileMatches => cts.Cancel());
                sw.Stop();

                Assert.True(searchRes.Cancelled);
                Assert.True(searchRes.FilesSearched < allFiles.Count, $"Expected early cancellation before scanning all {allFiles.Count} files, scanned {searchRes.FilesSearched}");
            }
        }

        // =====================================================================
        // Sub-area 2: Rapid Typing in 200k-Line Files — E06..E08
        // =====================================================================

        private string Create200kLineFile(out string originalText)
        {
            var sb = new StringBuilder(200000 * 48);
            sb.Append("namespace Stress200k\n{\n    public class MegaService\n    {\n");
            for (int i = 0; i < 200000 - 6; i++)
            {
                sb.Append("        public int Value").Append(i).Append(" = ").Append(i).Append("; // line filler\n");
            }
            sb.Append("    }\n}");
            originalText = sb.ToString();
            string filePath = Path.Combine(_tempDir, "Mega200k.cs");
            File.WriteAllText(filePath, originalText);
            return filePath;
        }

        [AvaloniaFact]
        public void E06_RapidTypingIn200kLineFile_PerKeystrokeIsIncremental_WithBoundedTimeAndAllocations()
        {
            string originalText;
            string filePath = Create200kLineFile(out originalText);

            var w = new MainWindow(_tempDir) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                Assert.NotNull(tab);
                Assert.Equal(200000, tab.Lines.Count);
                Assert.True(tab.IsLargeFile);

                // Jump directly to line 150,000 WITHOUT pre-warming CompleteBackgroundTokenization:
                // typing must still be fast and incremental (no synchronous 150k-line lex or full-file word scan).
                tab.CursorLine = 150000;
                tab.CursorCol = 8;
                tab.ScrollTopLine = 149990;
                var canvas = w.PrimaryEditor.Canvas;
                RenderCanvasOnce(canvas);

                // Warm 2 keystrokes so JIT and initial undo snapshot are settled, then measure 80 rapid keystrokes.
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/" });
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "/" });

                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 80; i++)
                {
                    char c = (char)('a' + (i % 26));
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
                }
                sw.Stop();
                long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                double allocPerKeyKb = allocDelta / 1024.0 / 80.0;
                double avgMsPerKey = sw.Elapsed.TotalMilliseconds / 80.0;

                // Also measure 10 rendered frames during typing at line 150,000
                var renderSw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++)
                {
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
                    RenderCanvasOnce(canvas);
                }
                renderSw.Stop();

                Assert.StartsWith("        //", tab.Lines[150000]);
                Assert.True(tab.IsDirty);
                // Rebuilding 200k lines (~10 MB string) per keystroke would allocate >10,000 KB/key.
                // Incremental per-keystroke editing stays well below 128 KB/key.
                Assert.True(allocPerKeyKb < 128.0, $"Expected incremental allocation < 128 KB/key on 200k-line file, got {allocPerKeyKb:F1} KB/key");
                Assert.True(avgMsPerKey < 50.0, $"Expected average edit time < 50 ms/key on 200k-line file, got {avgMsPerKey:F2} ms/key");
                Assert.True(renderSw.Elapsed.TotalMilliseconds / 10.0 < 250.0, $"Expected edit+render < 250 ms/key on 200k-line file, got {renderSw.Elapsed.TotalMilliseconds / 10.0:F2} ms/key");
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E07_RapidTypingIn200kLineFile_EnterAndBackspace_PreserveLineCountDirtyStateAndUndoRedo()
        {
            string originalText;
            string filePath = Create200kLineFile(out originalText);

            var w = new MainWindow(_tempDir) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                var canvas = w.PrimaryEditor.Canvas;
                tab.CursorLine = 100000;
                tab.CursorCol = 0;
                tab.ScrollTopLine = 99990;
                RenderCanvasOnce(canvas);

                for (int i = 0; i < 20; i++)
                {
                    w.SimulateShortcut(Key.Enter, KeyModifiers.None);
                }
                Assert.Equal(200020, tab.Lines.Count);
                Assert.True(tab.IsDirty);

                for (int i = 0; i < 20; i++)
                {
                    w.SimulateShortcut(Key.Back, KeyModifiers.None);
                }
                Assert.Equal(200000, tab.Lines.Count);

                // Undo all steps and verify the 200k-line buffer returns to clean and matches originalText
                int guard = 0;
                while (tab.UndoStack.Count > 0 && guard++ < 200)
                    tab.PerformUndo();
                Assert.False(tab.IsDirty);
                Assert.Equal(200000, tab.Lines.Count);
                Assert.Equal(originalText, tab.Content);
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E08_RapidTypingIn200kLineFile_WordBoundariesCoalesceSameLineUndo_WithoutLohArrayCopies()
        {
            string originalText;
            string filePath = Create200kLineFile(out originalText);

            var w = new MainWindow(_tempDir) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                var canvas = w.PrimaryEditor.Canvas;
                tab.CursorLine = 120000;
                tab.CursorCol = 8;

                string lineBefore = tab.Lines[120000];
                string sentenceWithSpaces = "/* alpha beta gamma delta epsilon zeta eta theta iota kappa */ ";
                foreach (char ch in sentenceWithSpaces)
                {
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = ch.ToString() });
                }

                // On a 200k-line large file, typing a multi-word phrase on the same line coalesces into 1 undo snapshot
                // rather than pushing a new 200k-line FullLines array on every space.
                Assert.Single(tab.UndoStack);
                Assert.StartsWith("        " + sentenceWithSpaces, tab.Lines[120000]);

                Assert.True(tab.PerformUndo());
                Assert.Equal(lineBefore, tab.Lines[120000]);
                Assert.False(tab.IsDirty);
            }
            finally
            {
                w.Close();
            }
        }

        // =====================================================================
        // Sub-area 3: Open/Close 200 Tabs — Zero Leaks — E09..E10
        // =====================================================================

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<WeakReference> OpenEditAndClose200TabsSequentiallyCore(MainWindow w, List<string> filePaths)
        {
            var weaks = new List<WeakReference>(filePaths.Count);
            foreach (string fp in filePaths)
            {
                var tab = w.OpenFileInTab(fp, preview: false);
                Assert.NotNull(tab);
                var session = EditorSyntaxSession.For(tab);
                session?.Prepare(tab);
                _ = WordBasedSuggestions.Build(tab, w.OpenTabs, 0, 0, null, "v");
                tab.InsertTextAtCursor("// edit ");
                tab.SyncContentFromLines();
                tab.IsDirty = true;
                weaks.Add(new WeakReference(tab));
                Assert.True(w.CloseTab(tab, forceWithoutPrompt: true));
            }
            return weaks;
        }

        [AvaloniaFact]
        public void E09_OpenAndClose200TabsSequentially_CollectsAllBuffersAndCapsClosedHistory()
        {
            string tabsDir = Path.Combine(_tempDir, "tabs200_seq");
            Directory.CreateDirectory(tabsDir);
            var paths = new List<string>(200);
            for (int i = 0; i < 200; i++)
            {
                string fp = Path.Combine(tabsDir, "tab_" + i.ToString("D3") + ".cs");
                File.WriteAllText(fp, "public class C" + i + " { public int X = " + i + "; }\n");
                paths.Add(fp);
            }

            var w = new MainWindow(tabsDir) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                var weaks = OpenEditAndClose200TabsSequentiallyCore(w, paths);
                Assert.Equal(200, weaks.Count);
                Assert.Empty(w.OpenTabs);
                Assert.Empty(w.EditorMruOrder);

                ForceFullGc();

                int alive = weaks.Count(wr => wr.IsAlive);
                Assert.Equal(0, alive);
            }
            finally
            {
                w.Close();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<WeakReference> Open200TabsAtOnceAndCloseAllCore(MainWindow w, List<string> filePaths)
        {
            var weaks = new List<WeakReference>(filePaths.Count);
            foreach (string fp in filePaths)
            {
                var tab = w.OpenFileInTab(fp, preview: false);
                Assert.NotNull(tab);
                var session = EditorSyntaxSession.For(tab);
                session?.Prepare(tab);
                weaks.Add(new WeakReference(tab));
            }
            Assert.Equal(200, w.OpenTabs.Count);
            w.ToggleSplitEditor();
            w.ToggleSplitEditor();
            w.CloseAllTabs();
            Assert.Empty(w.OpenTabs);
            return weaks;
        }

        [AvaloniaFact]
        public void E10_Open200TabsSimultaneously_AndCloseAll_ReleasesAllBuffersControlsAndLspBindings()
        {
            string tabsDir = Path.Combine(_tempDir, "tabs200_all");
            Directory.CreateDirectory(tabsDir);
            var paths = new List<string>(200);
            for (int i = 0; i < 200; i++)
            {
                string fp = Path.Combine(tabsDir, " bulk_" + i.ToString("D3") + ".cs");
                File.WriteAllText(fp, "namespace Bulk { public class B" + i + " { } }\n");
                paths.Add(fp);
            }

            var w = new MainWindow(tabsDir) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            try
            {
                var weaks = Open200TabsAtOnceAndCloseAllCore(w, paths);
                ForceFullGc();

                int alive = weaks.Count(wr => wr.IsAlive);
                Assert.Equal(0, alive);
                Assert.Empty(w.OpenTabs);
                Assert.Empty(w.EditorMruOrder);
            }
            finally
            {
                w.Close();
            }
        }

        // =====================================================================
        // Sub-area 4: File-Watcher Storms (1,000 Files Changed at Once) — E11..E14
        // =====================================================================

        private static void SimulateWatcherStorm(MainWindow w, IEnumerable<string> paths, bool structural)
        {
            var mi = typeof(MainWindow).GetMethod("QueueFsChange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(mi);
            foreach (string p in paths)
            {
                mi.Invoke(w, new object[] { p, structural });
            }
            Dispatcher.UIThread.RunJobs();
            w.DrainOneShotDebounceTimers();
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void E11_WatcherStorm1000Files_CoalescesUiPosts_AndFlushesQuicklyWithoutUiFreeze()
        {
            string ws = Path.Combine(_tempDir, "storm_ws");
            Directory.CreateDirectory(ws);
            var allPaths = new List<string>(1000);
            for (int i = 0; i < 1000; i++)
            {
                string p = Path.Combine(ws, "file_" + i.ToString("D4") + ".cs");
                if (i < 5) File.WriteAllText(p, "// initial " + i + "\n");
                allPaths.Add(p);
            }

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var open0 = w.OpenFileInTab(allPaths[0], preview: false);
                File.WriteAllText(allPaths[0], "// updated after storm\n");

                var sw = Stopwatch.StartNew();
                SimulateWatcherStorm(w, allPaths, structural: false);
                sw.Stop();

                Assert.Equal("// updated after storm\n", open0.Content);
                Assert.False(open0.IsDirty);
                Assert.True(sw.ElapsedMilliseconds < 5000, $"1000-file watcher storm flush took {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E12_WatcherStorm1000Files_OpenCleanBuffersReloadAutomatically_PreservingCursorAndCleanState()
        {
            string ws = Path.Combine(_tempDir, "storm_clean");
            Directory.CreateDirectory(ws);
            var paths = new List<string>(1000);
            for (int i = 0; i < 1000; i++)
            {
                string p = Path.Combine(ws, "item_" + i.ToString("D4") + ".txt");
                if (i < 3) File.WriteAllText(p, "line1\nline2_" + i + "\nline3\n");
                paths.Add(p);
            }

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var t0 = w.OpenFileInTab(paths[0], preview: false);
                var t1 = w.OpenFileInTab(paths[1], preview: false);
                var t2 = w.OpenFileInTab(paths[2], preview: false);
                t0.CursorLine = 1; t0.CursorCol = 3;

                // Modify all 3 on disk as part of a 1,000-file git checkout storm
                for (int i = 0; i < 3; i++)
                    File.WriteAllText(paths[i], "checkout_line1\ncheckout_line2_" + i + "\ncheckout_line3\n");

                SimulateWatcherStorm(w, paths, structural: false);

                Assert.Equal("checkout_line1\ncheckout_line2_0\ncheckout_line3\n", t0.Content);
                Assert.Equal("checkout_line1\ncheckout_line2_1\ncheckout_line3\n", t1.Content);
                Assert.Equal("checkout_line1\ncheckout_line2_2\ncheckout_line3\n", t2.Content);
                Assert.False(t0.IsDirty);
                Assert.False(t1.IsDirty);
                Assert.False(t2.IsDirty);
                Assert.Equal(1, t0.CursorLine);
                Assert.Equal(3, t0.CursorCol);
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E13_WatcherStorm1000Files_OpenDirtyBuffersAreNeverOverwritten_AndSurfaceConflict()
        {
            string ws = Path.Combine(_tempDir, "storm_dirty");
            Directory.CreateDirectory(ws);
            var paths = new List<string>(1000);
            for (int i = 0; i < 1000; i++)
            {
                string p = Path.Combine(ws, "src_" + i.ToString("D4") + ".cs");
                if (i < 4) File.WriteAllText(p, "class Original" + i + " {}\n");
                paths.Add(p);
            }

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var cleanTab = w.OpenFileInTab(paths[0], preview: false);
                var dirtyTab1 = w.OpenFileInTab(paths[1], preview: false);
                var dirtyTab2 = w.OpenFileInTab(paths[2], preview: false);

                dirtyTab1.Content = "class UserUnsavedEdit1 {}\n";
                dirtyTab1.SyncLinesFromContent();
                dirtyTab1.IsDirty = true;

                dirtyTab2.Content = "class UserUnsavedEdit2 {}\n";
                dirtyTab2.SyncLinesFromContent();
                dirtyTab2.IsDirty = true;

                // External checkout modifies all 3 files on disk
                File.WriteAllText(paths[0], "class DiskCheckout0 {}\n");
                File.WriteAllText(paths[1], "class DiskCheckout1 {}\n");
                File.WriteAllText(paths[2], "class DiskCheckout2 {}\n");

                SimulateWatcherStorm(w, paths, structural: false);

                // Clean tab reloaded:
                Assert.Equal("class DiskCheckout0 {}\n", cleanTab.Content);
                Assert.False(cleanTab.IsDirty);
                Assert.False(cleanTab.HasExternalDiskConflict);

                // Dirty tabs MUST NOT be overwritten and MUST flag conflict:
                Assert.Equal("class UserUnsavedEdit1 {}\n", dirtyTab1.Content);
                Assert.True(dirtyTab1.IsDirty);
                Assert.True(dirtyTab1.HasExternalDiskConflict);

                Assert.Equal("class UserUnsavedEdit2 {}\n", dirtyTab2.Content);
                Assert.True(dirtyTab2.IsDirty);
                Assert.True(dirtyTab2.HasExternalDiskConflict);

                Assert.Contains(w.NotificationsHistory, n => n.Message.Contains(dirtyTab1.FileName));
                Assert.Contains(w.NotificationsHistory, n => n.Message.Contains(dirtyTab2.FileName));
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E14_WatcherStorm1000Files_DeletedAndRecreatedFiles_UpdateIsDeletedOnDiskFlagCleanly()
        {
            string ws = Path.Combine(_tempDir, "storm_delete");
            Directory.CreateDirectory(ws);
            var paths = new List<string>(1000);
            for (int i = 0; i < 1000; i++)
            {
                string p = Path.Combine(ws, "del_" + i.ToString("D4") + ".txt");
                if (i < 2) File.WriteAllText(p, "initial_" + i + "\n");
                paths.Add(p);
            }

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var cleanTab = w.OpenFileInTab(paths[0], preview: false);
                var dirtyTab = w.OpenFileInTab(paths[1], preview: false);
                dirtyTab.Content = "unsaved_local_work\n";
                dirtyTab.SyncLinesFromContent();
                dirtyTab.IsDirty = true;

                // Delete backing files on disk during a 1,000-file storm
                File.Delete(paths[0]);
                File.Delete(paths[1]);
                SimulateWatcherStorm(w, paths, structural: true);

                Assert.True(cleanTab.IsDeletedOnDisk);
                Assert.True(dirtyTab.IsDeletedOnDisk);
                Assert.Equal("unsaved_local_work\n", dirtyTab.Content);

                // Recreate backing files on disk (e.g., branch switch back)
                File.WriteAllText(paths[0], "restored_0\n");
                File.WriteAllText(paths[1], "initial_1\n");
                SimulateWatcherStorm(w, paths, structural: true);

                Assert.False(cleanTab.IsDeletedOnDisk);
                Assert.Equal("restored_0\n", cleanTab.Content);
                Assert.False(dirtyTab.IsDeletedOnDisk);
                Assert.Equal("unsaved_local_work\n", dirtyTab.Content);
                Assert.True(dirtyTab.IsDirty);
            }
            finally
            {
                w.Close();
            }
        }

        // =====================================================================
        // Sub-area 5: Crash / Failure During Save Never Truncates Original File — E15..E17
        // =====================================================================

        [Fact]
        public void E15_AtomicSave_PreservesSymlinkTarget_UnixExecutableMode_ReadOnlyGuard_AndDirectoryNonWritableFallback()
        {
            // (a) Symlink target resolution: saving through a symlink writes the target and keeps the symlink intact
            string realDir = Path.Combine(_tempDir, "real_dir");
            Directory.CreateDirectory(realDir);
            string realFile = Path.Combine(realDir, "script.sh");
            File.WriteAllText(realFile, "#!/bin/sh\necho old\n");

            string linkPath = Path.Combine(_tempDir, "link_script.sh");
            bool symlinkCreated = false;
            try
            {
                File.CreateSymbolicLink(linkPath, Path.Combine("real_dir", "script.sh"));
                symlinkCreated = true;
            }
            catch { }

            // (b) Set executable bit (+x) on Unix before saving
            if (Path.DirectorySeparatorChar == '/')
            {
                File.SetUnixFileMode(realFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            string savePath = symlinkCreated ? linkPath : realFile;
            var buf = new EditorTabBuffer
            {
                FilePath = savePath,
                Content = "#!/bin/sh\necho new_atomic_save\n",
                FileEncoding = new UTF8Encoding(false),
                HasBom = false,
                EolSequence = "\n",
                HasFinalNewline = true
            };
            buf.SyncLinesFromContent();
            buf.WriteToDisk(savePath);

            Assert.Equal("#!/bin/sh\necho new_atomic_save\n", File.ReadAllText(realFile));
            if (symlinkCreated)
            {
                var linkInfo = new FileInfo(linkPath);
                Assert.NotNull(linkInfo.LinkTarget);
                Assert.Equal(Path.Combine("real_dir", "script.sh"), linkInfo.LinkTarget);
            }
            if (Path.DirectorySeparatorChar == '/')
            {
                var modeAfter = File.GetUnixFileMode(realFile);
                Assert.True((modeAfter & UnixFileMode.UserExecute) != 0, "Expected UserExecute (+x) bit to be preserved after atomic save");
            }

            // (e) Temp file pattern is ignored by WorkspaceIgnoreRules and no .codeless-tmp-* files remain
            Assert.True(EditorTabBuffer.IsAtomicSaveTempFile(".script.sh.codeless-tmp-1a2b3c4d"));
            var ignoreRules = new WorkspaceIgnoreRules(_tempDir, WorkspaceSearchEngine.DefaultExcludedFolders, false);
            Assert.True(ignoreRules.IsIgnoredFile(Path.Combine(realDir, ".script.sh.codeless-tmp-1a2b3c4d")));
            Assert.Empty(Directory.GetFiles(realDir, "*.codeless-tmp-*"));

            // (b2) ReadOnly file must NOT be silently overwritten
            string roFile = Path.Combine(_tempDir, "readonly.txt");
            File.WriteAllText(roFile, "do_not_overwrite\n");
            File.SetAttributes(roFile, FileAttributes.ReadOnly);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() =>
                    EditorTabBuffer.WriteBytesInPlace(roFile, Encoding.UTF8.GetBytes("overwritten\n")));
                Assert.Equal("do_not_overwrite\n", File.ReadAllText(roFile));
            }
            finally
            {
                File.SetAttributes(roFile, FileAttributes.Normal);
            }

            // (c) Directory not writable (0555), file writable (0644) -> falls back to in-place write after preparing bytes in memory
            if (Path.DirectorySeparatorChar == '/')
            {
                string lockedDir = Path.Combine(_tempDir, "locked_dir");
                Directory.CreateDirectory(lockedDir);
                string writableInLockedDir = Path.Combine(lockedDir, "inside.txt");
                File.WriteAllText(writableInLockedDir, "before_fallback\n");
                File.SetUnixFileMode(writableInLockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute); // 0500: no directory write
                try
                {
                    EditorTabBuffer.WriteBytesInPlace(writableInLockedDir, Encoding.UTF8.GetBytes("after_fallback\n"));
                    Assert.Equal("after_fallback\n", File.ReadAllText(writableInLockedDir));
                }
                finally
                {
                    File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }

        [Fact]
        public void E16_CrashDuringSave_MidStreamWriteFailure_LeavesOriginalFileByteForByteIntact_AndCleansUpTempFile()
        {
            string targetFile = Path.Combine(_tempDir, "critical_data.cs");
            byte[] preamble = Encoding.UTF8.GetPreamble();
            byte[] body = Encoding.UTF8.GetBytes("namespace Critical\r\n{\r\n    public class Data { public const int Version = 1; }\r\n}\r\n");
            byte[] originalBytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, originalBytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, originalBytes, preamble.Length, body.Length);
            File.WriteAllBytes(targetFile, originalBytes);

            int faultCount = 0;
            EditorTabBuffer.MidStreamWriteFaultInjector = (stream, path) =>
            {
                faultCount++;
                throw new IOException("Simulated power loss / disk full mid-stream after partial write");
            };

            try
            {
                var buf = new EditorTabBuffer
                {
                    FilePath = targetFile,
                    Content = "namespace Critical\n{\n    public class Data { public const int Version = 999999; // new long payload }\n}\n",
                    FileEncoding = new UTF8Encoding(true),
                    HasBom = true,
                    EolSequence = "\r\n",
                    HasFinalNewline = true
                };
                buf.SyncLinesFromContent();

                Assert.Throws<IOException>(() => buf.WriteToDisk(targetFile));
                Assert.Equal(1, faultCount);

                // Original file MUST be 100% byte-for-byte identical (never 0-byte truncated or half-written)
                byte[] diskBytesAfterCrash = File.ReadAllBytes(targetFile);
                Assert.Equal(originalBytes, diskBytesAfterCrash);

                // Also verify WriteBytesInPlace directly under mid-stream fault
                Assert.Throws<IOException>(() =>
                    EditorTabBuffer.WriteBytesInPlace(targetFile, Encoding.UTF8.GetBytes("corrupted replacement bytes that fail halfway")));
                Assert.Equal(2, faultCount);
                Assert.Equal(originalBytes, File.ReadAllBytes(targetFile));

                // Temp files must be cleaned up in finally
                Assert.Empty(Directory.GetFiles(_tempDir, "*.codeless-tmp-*"));
            }
            finally
            {
                EditorTabBuffer.MidStreamWriteFaultInjector = null;
            }
        }

        [AvaloniaFact]
        public void E17_FailedSave_KeepsTabDirty_PreservesUnsavedBuffer_AndAllowsSubsequentRetrySaveToSucceed()
        {
            string filePath = Path.Combine(_tempDir, "retry_save.cs");
            string initialText = "public class RetrySave { public int V = 1; }\n";
            File.WriteAllText(filePath, initialText);

            var w = new MainWindow(_tempDir) { SuppressUnsavedPrompts = true };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                Assert.NotNull(tab);
                Assert.False(tab.IsDirty);

                string editedText = "public class RetrySave { public int V = 2; // unsaved important work }\n";
                tab.Content = editedText;
                tab.SyncLinesFromContent();
                tab.IsDirty = true;

                // Inject mid-stream IOException during SaveBufferToDisk
                EditorTabBuffer.MidStreamWriteFaultInjector = (s, p) =>
                    throw new IOException("Simulated disk full mid-save");

                bool savedOnFault = w.SaveBufferToDisk(tab);
                Assert.False(savedOnFault);

                // Original file on disk is still intact, tab is still dirty with user's edits preserved
                Assert.Equal(initialText, File.ReadAllText(filePath));
                Assert.True(tab.IsDirty);
                Assert.Equal(editedText, tab.Content);
                Assert.Equal(initialText, tab.SavedContent);
                Assert.Empty(Directory.GetFiles(_tempDir, "*.codeless-tmp-*"));

                // Clear fault injector and retry SaveBufferToDisk -> succeeds cleanly
                EditorTabBuffer.MidStreamWriteFaultInjector = null;
                bool savedOnRetry = w.SaveBufferToDisk(tab);
                Assert.True(savedOnRetry);
                Assert.False(tab.IsDirty);
                Assert.Equal(editedText, File.ReadAllText(filePath));
                Assert.Equal(editedText, tab.SavedContent);
            }
            finally
            {
                EditorTabBuffer.MidStreamWriteFaultInjector = null;
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E21_SelfSaveInWatchedWorkspace_SuppressesTreeRefreshReloadAndConflict_WhileDetectingSubsequentExternalEdit()
        {
            string ws = Path.Combine(_tempDir, "self_save_watched_ws");
            Directory.CreateDirectory(ws);
            string filePath = Path.Combine(ws, "WatchedApp.cs");
            File.WriteAllText(filePath, "public class WatchedApp { public int X = 1; }\n");

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                Assert.NotNull(tab);

                var fsTreeDirtyField = typeof(MainWindow).GetField("fsTreeDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var fsChangedFilesField = typeof(MainWindow).GetField("fsChangedFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var onFsMethod = typeof(MainWindow).GetMethod("OnWorkspaceFsEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var queueFsMethod = typeof(MainWindow).GetMethod("QueueFsChange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(fsTreeDirtyField);
                Assert.NotNull(fsChangedFilesField);
                Assert.NotNull(onFsMethod);
                Assert.NotNull(queueFsMethod);

                // Stop the live OS FileSystemWatcher after opening so initial setup notifications from File.WriteAllText
                // cannot race asynchronously with the deterministic watcher-handler invocations below.
                w.StopWorkspaceWatcher();
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                fsTreeDirtyField.SetValue(w, false);
                ((HashSet<string>)fsChangedFilesField.GetValue(w)).Clear();

                // Edit and save the open file in the watched workspace
                tab.Content = "public class WatchedApp { public int X = 42; }\n";
                tab.SyncLinesFromContent();
                tab.IsDirty = true;
                Assert.True(w.SaveBufferToDisk(tab));
                Assert.False(tab.IsDirty);

                // Simulate OS watcher events produced by atomic save:
                // 1) Created/Changed on .WatchedApp.cs.codeless-tmp-12345678
                // 2) Renamed(.WatchedApp.cs.codeless-tmp-12345678 -> WatchedApp.cs) -> QueueFsChange(filePath, structural: false)
                // 3) Created(WatchedApp.cs) (in case the platform watcher reports Created instead of Renamed)
                string tmpFake = Path.Combine(ws, ".WatchedApp.cs.codeless-tmp-12345678");
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Created, ws, Path.GetFileName(tmpFake)) });
                queueFsMethod.Invoke(w, new object[] { filePath, false });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Created, ws, Path.GetFileName(filePath)) });

                // Self-save echo must be suppressed: no tree dirty flag, no queued file change, no reload, no conflict!
                Assert.False((bool)fsTreeDirtyField.GetValue(w));
                Assert.Empty((HashSet<string>)fsChangedFilesField.GetValue(w));
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                Assert.False(tab.IsDirty);
                Assert.False(tab.HasExternalDiskConflict);
                Assert.Empty(w.NotificationsHistory);

                // Now perform a genuine external edit immediately afterwards (within the 1500ms window!)
                string externalText = "public class WatchedApp { public int X = 777; // external tool edit }\n";
                File.WriteAllText(filePath, externalText);
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Changed, ws, Path.GetFileName(filePath)) });
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                Dispatcher.UIThread.RunJobs();

                // Genuine external edit on clean tab is immediately detected and reloaded:
                Assert.Equal(externalText, tab.Content);
                Assert.False(tab.IsDirty);
                Assert.False(tab.HasExternalDiskConflict);
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E22_IsSelfSaveEcho_ConcurrentBackgroundThreadHammering_WhileUiThreadOpensAndClosesTabs_NeverThrows()
        {
            string ws = Path.Combine(_tempDir, "concurrent_self_save_ws");
            Directory.CreateDirectory(ws);
            var files = new List<string>(24);
            for (int i = 0; i < 24; i++)
            {
                string fp = Path.Combine(ws, "Conc_" + i.ToString("D2") + ".cs");
                File.WriteAllText(fp, "public class Conc" + i + " { public int X = " + i + "; }\n");
                files.Add(fp);
            }

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var isSelfSaveMethod = typeof(MainWindow).GetMethod("IsSelfSaveEcho", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var queueFsMethod = typeof(MainWindow).GetMethod("QueueFsChange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(isSelfSaveMethod);
                Assert.NotNull(queueFsMethod);

                int stopFlag = 0;
                var bgExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
                var workers = new Task[4];
                for (int k = 0; k < workers.Length; k++)
                {
                    int workerId = k;
                    workers[k] = Task.Run(() =>
                    {
                        int iter = 0;
                        while (Volatile.Read(ref stopFlag) == 0)
                        {
                            try
                            {
                                string target = files[(iter + workerId) % files.Count];
                                _ = (bool)isSelfSaveMethod.Invoke(w, new object[] { target });
                                queueFsMethod.Invoke(w, new object[] { target, false });
                                iter++;
                            }
                            catch (System.Reflection.TargetInvocationException tie)
                            {
                                bgExceptions.Add(tie.InnerException ?? tie);
                            }
                            catch (Exception ex)
                            {
                                bgExceptions.Add(ex);
                            }
                        }
                    });
                }

                // UI thread rapidly opens, edits, saves, and closes tabs (Cmd+S + Cmd+W race window) while background threads hammer IsSelfSaveEcho
                for (int round = 0; round < 15; round++)
                {
                    foreach (string fp in files)
                    {
                        var tab = w.OpenFileInTab(fp, preview: false);
                        tab.Content = "public class ConcEdit { public int R = " + round + "; }\n";
                        tab.SyncLinesFromContent();
                        tab.IsDirty = true;
                        w.SaveBufferToDisk(tab);
                        w.CloseTab(tab, forceWithoutPrompt: true);
                    }
                    w.CreateNewUntitledTab();
                    w.CloseAllTabs();
                }

                Volatile.Write(ref stopFlag, 1);
                Task.WaitAll(workers, TimeSpan.FromSeconds(10));
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();

                Assert.Empty(bgExceptions);
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void E23_WindowsReplaceFileEventSequence_SuppressesSelfSaveEcho_WithoutTreeDirtyReloadOrConflict()
        {
            string ws = Path.Combine(_tempDir, "win_replacefile_ws");
            Directory.CreateDirectory(ws);
            string filePath = Path.Combine(ws, "WatchedApp.cs");
            File.WriteAllText(filePath, "public class WatchedApp { public int X = 1; }\n");

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                var tab = w.OpenFileInTab(filePath, preview: false);
                Assert.NotNull(tab);

                var fsTreeDirtyField = typeof(MainWindow).GetField("fsTreeDirty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var fsChangedFilesField = typeof(MainWindow).GetField("fsChangedFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var onFsMethod = typeof(MainWindow).GetMethod("OnWorkspaceFsEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var onRenamedMethod = typeof(MainWindow).GetMethod("OnWorkspaceFsRenamed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(fsTreeDirtyField);
                Assert.NotNull(fsChangedFilesField);
                Assert.NotNull(onFsMethod);
                Assert.NotNull(onRenamedMethod);

                w.StopWorkspaceWatcher();
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                fsTreeDirtyField.SetValue(w, false);
                ((HashSet<string>)fsChangedFilesField.GetValue(w)).Clear();

                // Save the open file in the workspace
                tab.Content = "public class WatchedApp { public int X = 100; }\n";
                tab.SyncLinesFromContent();
                tab.IsDirty = true;
                Assert.True(w.SaveBufferToDisk(tab));
                Assert.False(tab.IsDirty);

                // Simulate the exact Win32 ReplaceFileW (File.Replace) event sequence emitted on Windows:
                // 1) Created + Changed on .WatchedApp.cs.codeless-tmp-a1b2c3d4
                // 2) Renamed(WatchedApp.cs -> WatchedApp.cs~RF1a2b3c.TMP)
                // 3) Renamed(.WatchedApp.cs.codeless-tmp-a1b2c3d4 -> WatchedApp.cs)
                // 4) Deleted(WatchedApp.cs~RF1a2b3c.TMP)
                // 5) Deleted(WatchedApp.cs), Created(WatchedApp.cs), Changed(WatchedApp.cs)
                // 6) Changed on parent directory ws
                string codelessTmpName = ".WatchedApp.cs.codeless-tmp-a1b2c3d4";
                string rfTmpName = "WatchedApp.cs~RF1a2b3c.TMP";
                string targetName = Path.GetFileName(filePath);

                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Created, ws, codelessTmpName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Changed, ws, codelessTmpName) });
                onRenamedMethod.Invoke(w, new object[] { null, new RenamedEventArgs(WatcherChangeTypes.Renamed, ws, rfTmpName, targetName) });
                onRenamedMethod.Invoke(w, new object[] { null, new RenamedEventArgs(WatcherChangeTypes.Renamed, ws, targetName, codelessTmpName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Deleted, ws, rfTmpName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Deleted, ws, targetName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Created, ws, targetName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Changed, ws, targetName) });
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(ws), Path.GetFileName(ws)) });

                Assert.False((bool)fsTreeDirtyField.GetValue(w));
                Assert.Empty((HashSet<string>)fsChangedFilesField.GetValue(w));
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                Assert.False(tab.IsDirty);
                Assert.False(tab.HasExternalDiskConflict);
                Assert.Empty(w.NotificationsHistory);
                Assert.Equal("public class WatchedApp { public int X = 100; }\n", tab.Content);

                // Subsequent genuine external edit is still detected and reloaded:
                string extContent = "public class WatchedApp { public int X = 999; // external edit after ReplaceFileW }\n";
                File.WriteAllText(filePath, extContent);
                onFsMethod.Invoke(w, new object[] { null, new FileSystemEventArgs(WatcherChangeTypes.Changed, ws, targetName) });
                Dispatcher.UIThread.RunJobs();
                w.DrainOneShotDebounceTimers();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(extContent, tab.Content);
                Assert.False(tab.IsDirty);
                Assert.False(tab.HasExternalDiskConflict);
            }
            finally
            {
                w.Close();
            }
        }

        // =====================================================================
        // Sub-area 6: Hot-Exit / Backup Recovery & Corrupt Backup Resilience — E18..E20
        // =====================================================================

        [AvaloniaFact]
        public void E18_HotExit_AbnormalExitAndWindowClose_AtomicallyBacksUpAndRecoversDirtyFileAndUntitledBuffers()
        {
            string ws = Path.Combine(_tempDir, "hotexit_ws");
            Directory.CreateDirectory(ws);
            string trackedFile = Path.Combine(ws, "Tracked.cs");
            File.WriteAllText(trackedFile, "class Tracked { int x = 1; }\r\n", new UTF8Encoding(true));

            var w1 = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                w1.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                var fileTab = w1.OpenFileInTab(trackedFile, preview: false);
                fileTab.Content = "class Tracked { int x = 999; // hot-exit unsaved }\n";
                fileTab.SyncLinesFromContent();
                fileTab.CursorLine = 0;
                fileTab.CursorCol = 16;
                fileTab.IsDirty = true;

                var untitledTab = w1.CreateNewUntitledTab();
                Assert.True(untitledTab.IsUntitled);
                untitledTab.Content = "scratch pad unsaved notes\nsecond line";
                untitledTab.SyncLinesFromContent();
                untitledTab.IsDirty = true;

                w1.Close();
            }
            finally
            {
                try { w1.Close(); } catch { }
            }

            // Disk file was NOT overwritten on hot-exit close:
            Assert.Contains("int x = 1;", File.ReadAllText(trackedFile));

            // Reopen a new window on the same workspace and restore hot-exit backups:
            var w2 = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                w2.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                w2.RestoreHotExitBackups();

                var restoredFileTab = w2.OpenTabs.FirstOrDefault(t => string.Equals(t.FilePath, trackedFile, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(restoredFileTab);
                Assert.True(restoredFileTab.IsDirty);
                Assert.Equal("class Tracked { int x = 999; // hot-exit unsaved }\n", restoredFileTab.Content);
                Assert.True(restoredFileTab.HasBom);
                Assert.Equal("\r\n", restoredFileTab.EolSequence);
                Assert.Equal(0, restoredFileTab.CursorLine);
                Assert.Equal(16, restoredFileTab.CursorCol);

                var restoredUntitled = w2.OpenTabs.FirstOrDefault(t => t.IsUntitled);
                Assert.NotNull(restoredUntitled);
                Assert.True(restoredUntitled.IsDirty);
                Assert.Equal("scratch pad unsaved notes\nsecond line", restoredUntitled.Content);
            }
            finally
            {
                w2.Close();
            }
        }

        [AvaloniaFact]
        public void E19_HotExit_MultiWindowIsolation_TwoWorkspacesRecoverIndependentlyWithoutCrossContamination()
        {
            string wsA = Path.Combine(_tempDir, "multi_ws_A");
            string wsB = Path.Combine(_tempDir, "multi_ws_B");
            Directory.CreateDirectory(wsA);
            Directory.CreateDirectory(wsB);
            string fileA = Path.Combine(wsA, "Alpha.cs");
            string fileB = Path.Combine(wsB, "Beta.cs");
            File.WriteAllText(fileA, "class Alpha {}\n");
            File.WriteAllText(fileB, "class Beta {}\n");

            var winA1 = new MainWindow(wsA) { SuppressUnsavedPrompts = true };
            var winB1 = new MainWindow(wsB) { SuppressUnsavedPrompts = true };
            try
            {
                winA1.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                winB1.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;

                var tabA = winA1.OpenFileInTab(fileA, preview: false);
                tabA.Content = "class Alpha { /* dirty in window A */ }\n";
                tabA.SyncLinesFromContent();
                tabA.IsDirty = true;

                var tabB = winB1.OpenFileInTab(fileB, preview: false);
                tabB.Content = "class Beta { /* dirty in window B */ }\n";
                tabB.SyncLinesFromContent();
                tabB.IsDirty = true;

                winA1.Close();
                winB1.Close();
            }
            finally
            {
                try { winA1.Close(); } catch { }
                try { winB1.Close(); } catch { }
            }

            var winA2 = new MainWindow(wsA) { SuppressUnsavedPrompts = true };
            var winB2 = new MainWindow(wsB) { SuppressUnsavedPrompts = true };
            try
            {
                winA2.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                winB2.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                winA2.RestoreHotExitBackups();
                winB2.RestoreHotExitBackups();

                Assert.Single(winA2.OpenTabs);
                Assert.Equal(fileA, winA2.OpenTabs[0].FilePath);
                Assert.Equal("class Alpha { /* dirty in window A */ }\n", winA2.OpenTabs[0].Content);
                Assert.True(winA2.OpenTabs[0].IsDirty);

                Assert.Single(winB2.OpenTabs);
                Assert.Equal(fileB, winB2.OpenTabs[0].FilePath);
                Assert.Equal("class Beta { /* dirty in window B */ }\n", winB2.OpenTabs[0].Content);
                Assert.True(winB2.OpenTabs[0].IsDirty);
            }
            finally
            {
                winA2.Close();
                winB2.Close();
            }
        }

        [AvaloniaFact]
        public void E20_HotExit_CorruptTruncatedAndEmptyBackupFiles_DoNotCrashStartupOrCreatePhantomTabs()
        {
            string ws = Path.Combine(_tempDir, "corrupt_hotexit_ws");
            Directory.CreateDirectory(ws);
            string validFile = Path.Combine(ws, "Valid.cs");
            File.WriteAllText(validFile, "class Valid {}\n");

            // Write one legitimate backup entry
            var validBuf = new EditorTabBuffer
            {
                FilePath = validFile,
                FileName = "Valid.cs",
                RelativePath = "Valid.cs",
                Content = "class Valid { /* recovered */ }\n",
                SavedContent = "class Valid {}\n",
                IsDirty = true
            };
            validBuf.SyncLinesFromContent();
            HotExitService.WriteTabBackup(ws, validBuf, 0);

            // Inject corrupt, truncated, empty, and schema-invalid .json files into the workspace backup directory
            string backupDir = HotExitService.GetWorkspaceBackupDir(ws);
            File.WriteAllText(Path.Combine(backupDir, "00_truncated.json"), "{\"BackupId\":\"broken\",\"Content\":\"unterminated");
            File.WriteAllText(Path.Combine(backupDir, "01_empty.json"), "   ");
            File.WriteAllText(Path.Combine(backupDir, "02_empty_obj.json"), "{}");
            File.WriteAllText(Path.Combine(backupDir, "03_array.json"), "[1, 2, 3]");
            File.WriteAllText(Path.Combine(backupDir, "04_missing_path.json"), "{\"BackupId\":\"id_no_path\",\"IsUntitled\":false}");
            File.WriteAllText(Path.Combine(backupDir, "05_leftover.json.tmp.999"), "partial temp write");

            var loaded = HotExitService.LoadBackups(ws);
            Assert.Single(loaded);
            Assert.Equal(validFile, loaded[0].FilePath);

            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            try
            {
                w.Settings.HotExit = IdePersistentSettings.HotExitOnExitAndWindowClose;
                w.RestoreHotExitBackups();

                // Only the 1 valid backup tab is restored; zero phantom "Untitled-1" tabs from corrupt files!
                Assert.Single(w.OpenTabs);
                Assert.Equal(validFile, w.OpenTabs[0].FilePath);
                Assert.Equal("class Valid { /* recovered */ }\n", w.OpenTabs[0].Content);
                Assert.True(w.OpenTabs[0].IsDirty);
            }
            finally
            {
                w.Close();
            }
        }
    }
}
