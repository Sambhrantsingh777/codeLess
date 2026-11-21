using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>File-watcher events update the Quick Open / index list incrementally; ignored folders never touch it.</summary>
    [Collection("SequentialWorkbench")]
    public class WorkspaceIncrementalIndexHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly string ws;

        public WorkspaceIncrementalIndexHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            ws = Path.Combine(Path.GetTempPath(), "codeLess_IncrIndex_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, ".gitignore"), "generated/\n*.log\n");
            File.WriteAllText(Path.Combine(ws, "src", "a.ts"), "export const a = 1;\n");
        }

        public void Dispose()
        {
            try
            {
                if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(ws, true); } catch { }
        }

        private static void Invoke(MainWindow w, string method, params object[] args)
        {
            var mi = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(mi);
            mi.Invoke(w, args);
        }

        [AvaloniaFact]
        public void WatcherEvents_UpdateFileListIncrementally()
        {
            var window = new MainWindow(ws);
            window.Show();
            window.ScheduleBackgroundReindex();   // headless: synchronous gitignore-aware walk
            Assert.Contains(window.WorkspaceFiles, f => f.EndsWith("a.ts"));
            int reindexBefore = window.PerfReindexCount;

            // New source folder with files (e.g. git checkout) -> added without a full re-walk.
            string newDir = Path.Combine(ws, "src", "feature");
            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(newDir, "b.ts"), "b");
            File.WriteAllText(Path.Combine(newDir, "c.ts"), "c");
            File.WriteAllText(Path.Combine(newDir, "debug.log"), "ignored");
            Invoke(window, "QueueFsChange", newDir, true);
            // Churn inside ignored folders: never enters the list.
            string nm = Path.Combine(ws, "node_modules", "pkg");
            Directory.CreateDirectory(nm);
            File.WriteAllText(Path.Combine(nm, "index.js"), "x");
            Invoke(window, "QueueFsChange", Path.Combine(nm, "index.js"), true);
            string gen = Path.Combine(ws, "src", "generated");
            Directory.CreateDirectory(gen);
            File.WriteAllText(Path.Combine(gen, "g.ts"), "x");
            Invoke(window, "QueueFsChange", Path.Combine(gen, "g.ts"), true);
            Invoke(window, "FlushFsChanges");
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(window.WorkspaceFiles, f => f.EndsWith(Path.Combine("feature", "b.ts")));
            Assert.Contains(window.WorkspaceFiles, f => f.EndsWith(Path.Combine("feature", "c.ts")));
            Assert.DoesNotContain(window.WorkspaceFiles, f => f.EndsWith("debug.log"));
            Assert.DoesNotContain(window.WorkspaceFiles, f => f.Contains("node_modules"));
            Assert.DoesNotContain(window.WorkspaceFiles, f => f.EndsWith("g.ts"));
            Assert.Equal(reindexBefore, window.PerfReindexCount);   // no full re-walk
            Assert.True(window.PerfIncrementalIndexUpdates > 0);

            // Folder deleted -> its files leave the list.
            Directory.Delete(newDir, true);
            Invoke(window, "QueueFsChange", newDir, true);
            Invoke(window, "FlushFsChanges");
            Assert.DoesNotContain(window.WorkspaceFiles, f => f.Contains(Path.Combine("src", "feature")));
            Assert.Equal(reindexBefore, window.PerfReindexCount);

            // A .gitignore edit invalidates the rules and falls back to one full re-index.
            File.WriteAllText(Path.Combine(ws, ".gitignore"), "generated/\n");
            Invoke(window, "QueueFsChange", Path.Combine(ws, ".gitignore"), true);
            Invoke(window, "FlushFsChanges");
            Assert.Equal(reindexBefore + 1, window.PerfReindexCount);

            window.SuppressUnsavedPrompts = true;
            window.Close();
        }

        [AvaloniaFact]
        public void QuickOpen_BigWorkspace_UsesIndexAndFindsDeepFiles()
        {
            // > QuickOpenInlineFileLimit files, one of them 12 levels deep (the old walker stopped at depth 7 / 12k files).
            string deep = Path.Combine(ws, "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "UniqueQuokkaWidget.tsx"), "x");
            string bulk = Path.Combine(ws, "bulk");
            for (int d = 0; d < 6; d++)
            {
                string dir = Path.Combine(bulk, "d" + d);
                Directory.CreateDirectory(dir);
                for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(dir, "f" + i + ".ts"), "");
            }
            var window = new MainWindow(ws);
            window.Show();
            window.ScheduleBackgroundReindex();
            Assert.True(window.WorkspaceFiles.Count > MainWindow.QuickOpenInlineFileLimit);
            window.OpenQuickOpen();
            var palette = window.CommandPalette;
            Assert.NotNull(palette.QuickOpenFileSearch);
            Assert.True(palette.AllEntries.Count(e => !string.IsNullOrEmpty(e.FilePath)) < 100); // not materialised
            palette.SearchInput.Text = "UniqueQuokka";
            palette.RefreshFilter();
            Assert.Contains(palette.FilteredEntries, e => (e.FilePath ?? "").EndsWith("UniqueQuokkaWidget.tsx"));
            palette.Close();
            Assert.Null(palette.QuickOpenFileSearch);
            window.Close();
        }
    }
}
