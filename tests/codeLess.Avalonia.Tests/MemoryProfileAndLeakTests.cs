using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class MemoryProfileAndLeakTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempDir;

        public MemoryProfileAndLeakTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), "mem-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _tempDir);
        }

        public void Dispose()
        {
            foreach (var w in App.WindowManager.GetOpenWindows().OfType<MainWindow>().ToList())
            {
                try { w.SuppressUnsavedPrompts = true; w.Close(); } catch { }
            }
            Dispatcher.UIThread.RunJobs();
            IdeSettingsWatcher.StopForTests();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private static long CollectAndMeasure(out long heapSizeBytes, out long fragmentedBytes)
        {
            Dispatcher.UIThread.RunJobs();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Dispatcher.UIThread.RunJobs();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            long total = GC.GetTotalMemory(true);
            var info = GC.GetGCMemoryInfo();
            heapSizeBytes = info.HeapSizeBytes;
            fragmentedBytes = info.FragmentedBytes;
            return total;
        }

        private static string RepoRoot()
        {
            string cur = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(cur))
            {
                if (File.Exists(Path.Combine(cur, "codeLess.sln"))) return cur;
                cur = Path.GetDirectoryName(cur);
            }
            return "/tmp/codeless-work/mem";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference W1, WeakReference W2, WeakReference Tab, long EmptyWinBytes, long WsBytes, long TenFilesBytes, long PanelsBytes, long SecondWinBytes)
            RunProfileStepsCore(string repo, long baseBytes, List<string> lines)
        {
            long heap, frag;

            // Step 1: Startup with empty window
            long allocBeforeWin = GC.GetAllocatedBytesForCurrentThread();
            var w1 = new MainWindow(null) { SuppressUnsavedPrompts = true, Width = 1280, Height = 800 };
            w1.Measure(new Size(1280, 800));
            w1.Arrange(new Rect(0, 0, 1280, 800));
            Dispatcher.UIThread.RunJobs();
            long allocEmptyWin = GC.GetAllocatedBytesForCurrentThread() - allocBeforeWin;
            long emptyWinBytes = CollectAndMeasure(out heap, out frag);
            lines.Add($"step1_empty_window: live_bytes={emptyWinBytes} ({emptyWinBytes / 1048576.0:F2} MB) delta_mb={(emptyWinBytes - baseBytes) / 1048576.0:F2} thread_alloc_mb={allocEmptyWin / 1048576.0:F2} terms={w1.TerminalSessions.Count} lazy_ai={!w1.IsAgentEngineCreated} lazy_dbg={!w1.IsDebuggerCreated} lazy_blame={!w1.IsBlameServiceCreated}");

            // Step 2: Open workspace (this repo) — BM25 index remains lazy until requested
            w1.LoadWorkspace(repo);
            Dispatcher.UIThread.RunJobs();
            long wsBytes = CollectAndMeasure(out heap, out frag);
            int wsFiles = w1.WorkspaceFiles.Count;
            int explorerNodes = w1.ExplorerTree?.Nodes?.Count ?? 0;
            lines.Add($"step2_open_workspace: live_bytes={wsBytes} ({wsBytes / 1048576.0:F2} MB) delta_mb={(wsBytes - emptyWinBytes) / 1048576.0:F2} ws_files={wsFiles} explorer_roots={explorerNodes} ai_engine_created={w1.IsAgentEngineCreated}");

            // Step 3: Open 10 source files
            var candidateFiles = Directory.EnumerateFiles(Path.Combine(repo, "src", "codeLess.Core"), "*.cs")
                .OrderByDescending(f => new FileInfo(f).Length)
                .Take(10)
                .ToList();
            foreach (var f in candidateFiles)
            {
                var tab = w1.OpenFileInTab(f, preview: false);
                var session = EditorSyntaxSession.For(tab);
                session?.Prepare(tab);
            }
            Dispatcher.UIThread.RunJobs();
            long tenFilesBytes = CollectAndMeasure(out heap, out frag);
            int totalLines = w1.OpenTabs.Sum(t => t.Lines?.Count ?? 0);
            int cachedRows = w1.PrimaryEditor?.Canvas?.CachedRowTextCount ?? 0;
            lines.Add($"step3_open_10_files: live_bytes={tenFilesBytes} ({tenFilesBytes / 1048576.0:F2} MB) delta_mb={(tenFilesBytes - wsBytes) / 1048576.0:F2} open_tabs={w1.OpenTabs.Count} total_lines={totalLines} shaped_rows={cachedRows}");

            // Step 4: Open terminal, search, SCM, and agent panels
            w1.ShowSearchView(false);
            w1.ExecuteWorkspaceSearch("MainWindow");
            w1.SwitchSidebarView("git");
            w1.RefreshGitStatus();
            if (!w1.IsBottomPanelVisible) w1.InvokeMenuCommand("view.togglePanel");
            w1.InvokeMenuCommand("view.terminal");
            if (w1.TerminalSessions.Count == 0) w1.CreateNewTerminalSession();
            if (!w1.IsRightChatVisible) w1.InvokeMenuCommand("view.toggleChat");
            Dispatcher.UIThread.RunJobs();
            long panelsBytes = CollectAndMeasure(out heap, out frag);
            int searchHits = w1.WorkspaceSearchHits.Count;
            int gitEntries = w1.GitEntries.Count;
            lines.Add($"step4_open_panels: live_bytes={panelsBytes} ({panelsBytes / 1048576.0:F2} MB) delta_mb={(panelsBytes - tenFilesBytes) / 1048576.0:F2} search_hits={searchHits} git_entries={gitEntries} terms={w1.TerminalSessions.Count}");

            // Step 5: Open a second window
            var w2 = new MainWindow(null) { SuppressUnsavedPrompts = true, Width = 1280, Height = 800 };
            w2.Measure(new Size(1280, 800));
            w2.Arrange(new Rect(0, 0, 1280, 800));
            Dispatcher.UIThread.RunJobs();
            long secondWinBytes = CollectAndMeasure(out heap, out frag);
            lines.Add($"step5_second_window: live_bytes={secondWinBytes} ({secondWinBytes / 1048576.0:F2} MB) delta_mb={(secondWinBytes - panelsBytes) / 1048576.0:F2}");

            // Step 6: Close everything
            var w1Weak = new WeakReference(w1);
            var w2Weak = new WeakReference(w2);
            var tabWeak = new WeakReference(w1.OpenTabs.First());
            w2.Close();
            w1.CloseAllTabs();
            w1.CloseWorkspaceFolder();
            w1.Close();
            return (w1Weak, w2Weak, tabWeak, emptyWinBytes, wsBytes, tenFilesBytes, panelsBytes, secondWinBytes);
        }

        [AvaloniaFact]
        public void MemoryProfile_EndToEnd_StepsAndRetainers()
        {
            string repo = RepoRoot();
            var lines = new List<string>();
            long heap, frag;

            long baseBytes = CollectAndMeasure(out heap, out frag);
            lines.Add($"step0_baseline: live_bytes={baseBytes} ({baseBytes / 1048576.0:F2} MB) heap_bytes={heap} ({heap / 1048576.0:F2} MB)");

            var res = RunProfileStepsCore(repo, baseBytes, lines);

            long afterCloseBytes = CollectAndMeasure(out heap, out frag);
            lines.Add($"step6_after_close: live_bytes={afterCloseBytes} ({afterCloseBytes / 1048576.0:F2} MB) delta_vs_base_mb={(afterCloseBytes - baseBytes) / 1048576.0:F2} w1_alive={res.W1.IsAlive} w2_alive={res.W2.IsAlive} tab_alive={res.Tab.IsAlive}");

            // Theme allocation check
            long t0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) _ = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            long themeAlloc = GC.GetAllocatedBytesForCurrentThread() - t0;
            lines.Add($"theme_getbyid_50x_alloc_bytes={themeAlloc}");

            // Memory budget regression assertions (W4 deliverable 7):
            // 1. Managed heap after opening workspace + 10 source files < 45 MB delta over baseline
            double tenFilesDeltaMb = (res.TenFilesBytes - baseBytes) / 1048576.0;
            Assert.True(tenFilesDeltaMb < 45.0, $"Expected managed heap after opening 10 files < 45 MB above baseline, got {tenFilesDeltaMb:F2} MB");

            // 2. Per-extra-window managed delta < 18 MB
            double extraWindowDeltaMb = (res.SecondWinBytes - res.PanelsBytes) / 1048576.0;
            Assert.True(extraWindowDeltaMb < 18.0, $"Expected per-extra-window managed delta < 18 MB, got {extraWindowDeltaMb:F2} MB");

            // 3. Closed windows and tabs are collected
            Assert.False(res.Tab.IsAlive);
            Assert.False(res.W2.IsAlive);
            Assert.False(res.W1.IsAlive);
            Assert.Equal(0, themeAlloc);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference OpenEditAndCloseSingleTab(MainWindow w, string filePath)
        {
            var tab = w.OpenFileInTab(filePath, preview: false);
            Assert.NotNull(tab);
            var session = EditorSyntaxSession.For(tab);
            session?.Prepare(tab);
            var weak = new WeakReference(tab);
            Assert.True(w.CloseTab(tab, forceWithoutPrompt: true));
            return weak;
        }

        [AvaloniaFact]
        public void CloseTab_CollectsEditorTabBuffer_WeakReference()
        {
            string sampleFile = Path.Combine(RepoRoot(), "src", "codeLess.Core", "CoreModels.cs");
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true };
            try
            {
                var weakTab = OpenEditAndCloseSingleTab(w, sampleFile);
                CollectAndMeasure(out _, out _);
                Assert.False(weakTab.IsAlive, "Closed EditorTabBuffer should be garbage-collected after CloseTab.");
                Assert.Empty(w.OpenTabs);
                Assert.Empty(w.EditorMruOrder);
            }
            finally
            {
                w.Close();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference DocWeak, WeakReference ViewWeak, WeakReference TermWeak) OpenAndClosePanelsOnWindow(MainWindow w, string mdPath)
        {
            w.OpenFileInTab(mdPath, preview: false);
            var mdView = w.OpenMarkdownPreview(toSide: true);
            Assert.NotNull(mdView);
            var doc = w.AuxPane.ActiveDocument;
            Assert.NotNull(doc);
            var docWeak = new WeakReference(doc);
            var viewWeak = new WeakReference(mdView);

            var termEntry = w.CreateNewTerminalSession();
            Assert.NotNull(termEntry);
            var termWeak = new WeakReference(termEntry);

            w.CloseAuxPane();
            w.KillTerminalSession(termEntry);
            w.CloseAllTabs();
            return (docWeak, viewWeak, termWeak);
        }

        [AvaloniaFact]
        public void ClosePanel_CollectsAuxPaneDocumentAndTerminal_WeakReference()
        {
            string mdPath = Path.Combine(_tempDir, "sample.md");
            File.WriteAllText(mdPath, "# Hello\n\nMemory test markdown preview.\n");
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true };
            try
            {
                var (docWeak, viewWeak, termWeak) = OpenAndClosePanelsOnWindow(w, mdPath);
                CollectAndMeasure(out _, out _);
                Assert.False(docWeak.IsAlive, "Closed AuxPaneDocument should be collected after CloseAuxPane.");
                Assert.False(viewWeak.IsAlive, "Closed MarkdownPreviewView should be collected after CloseAuxPane.");
                Assert.False(termWeak.IsAlive, "Killed TerminalSessionEntry should be collected after KillTerminalSession.");
            }
            finally
            {
                w.Close();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreatePopulateAndCloseWindow(string repo)
        {
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            w.LoadWorkspace(repo);
            w.OpenFileInTab(Path.Combine(repo, "src", "codeLess.Core", "CoreModels.cs"), preview: false);
            w.ShowWorkbenchNotification("test-notif", "info", "Test notification", "Action", () => { }, canDoNotShowAgain: true);
            w.CommandPalette?.RecentCommandTitles?.Add("View: Toggle Terminal");
            var weak = new WeakReference(w);
            w.Close();
            return weak;
        }

        [AvaloniaFact]
        public void CloseWindow_CollectsMainWindow_WeakReference()
        {
            var weakWin = CreatePopulateAndCloseWindow(RepoRoot());
            CollectAndMeasure(out _, out _);
            Assert.False(weakWin.IsAlive, "Closed MainWindow should be garbage-collected after Close().");
        }

        [AvaloniaFact]
        public void Subsystems_AreLazyAtStartup_AndConstructedOnFirstUse()
        {
            var w = new MainWindow(null) { SuppressUnsavedPrompts = true };
            try
            {
                // At startup with an empty window, heavy subsystems are not yet constructed
                Assert.False(w.IsAgentEngineCreated);
                Assert.False(w.IsAgentTrustCreated);
                Assert.False(w.IsDebuggerCreated);
                Assert.False(w.IsBlameServiceCreated);
                Assert.False(w.IsLanguageServerManagerCreated);
                Assert.False(w.IsNetworkPanelCreated);
                Assert.False(w.IsAuxPaneVisible);

                // Loading a workspace keeps BM25/symbol indexing lazy until first AI/symbol query
                w.LoadWorkspace(RepoRoot());
                Assert.False(w.IsAgentEngineCreated);

                // Accessing Debugger constructs DebugController on demand
                Assert.NotNull(w.Debugger);
                Assert.True(w.IsDebuggerCreated);

                // Accessing BlameService constructs GitBlameService on demand
                Assert.NotNull(w.BlameService);
                Assert.True(w.IsBlameServiceCreated);

                // Accessing AgentTrust constructs AgentTrustCoordinator on demand
                Assert.NotNull(w.AgentTrust);
                Assert.True(w.IsAgentTrustCreated);

                // Accessing HarnessEngine.WorkspaceSymbolMap constructs LlmHarnessAndAgentEngine and indexes on demand
                Assert.NotNull(w.HarnessEngine);
                Assert.True(w.IsAgentEngineCreated);
                Assert.NotEmpty(w.HarnessEngine.WorkspaceSymbolMap);
            }
            finally
            {
                w.Close();
            }
        }

        [AvaloniaFact]
        public void SharedImmutableThemesAndIcons_ZeroDuplicateAllocations()
        {
            var t1 = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var t2 = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            Assert.Same(t1, t2);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                var t = StudioTheme.GetById("codeless-obsidian");
                Assert.NotNull(t);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);

            var w1 = new MainWindow(null) { SuppressUnsavedPrompts = true };
            var w2 = new MainWindow(null) { SuppressUnsavedPrompts = true };
            try
            {
                if (w1.Icon != null && w2.Icon != null)
                {
                    Assert.Same(w1.Icon, w2.Icon);
                }
            }
            finally
            {
                w1.Close();
                w2.Close();
            }
        }
    }
}
