using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>A large file opens in the paged viewer without ever being loaded into an EditorTabBuffer.</summary>
    [Collection("SequentialWorkbench")]
    public class LargeFileViewerHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly string ws;

        public LargeFileViewerHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            ws = Path.Combine(Path.GetTempPath(), "codeLess_LargeFileTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "small.cs"), "class Small { }\n");
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

        /// <summary>Writes a ~sizeMb MB log with numbered lines ("... line=N ...").</summary>
        private string WriteLog(string name, int sizeMb)
        {
            string p = Path.Combine(ws, name);
            using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                long target = (long)sizeMb * 1024 * 1024;
                long written = 0;
                var sb = new StringBuilder();
                for (long i = 0; written < target; i++)
                {
                    sb.Clear();
                    sb.Append("2026-11-22T10:00:00Z INFO [worker-").Append(i % 16).Append("] request served line=").Append(i + 1);
                    if (i % 100000 == 77) sb.Append(" UNIQUE_MARKER");
                    sb.Append('\n');
                    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    fs.Write(bytes, 0, bytes.Length);
                    written += bytes.Length;
                }
            }
            return p;
        }

        private static void Pump(Func<bool> until, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs();
                if (until()) return;
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void LargeFile_OpensInViewer_WithoutLoadingIntoEditorBuffer()
        {
            string log = WriteLog("huge.log", 60);
            var window = new MainWindow(ws);
            window.Show();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tab = window.OpenFileInTab(log, preview: false);
            sw.Stop();
            Assert.NotNull(tab);
            Assert.True(tab.IsLargeFileViewer);
            Assert.Equal("", tab.Content);
            Assert.Single(tab.Lines);              // nothing was loaded into the List<string> buffer
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(2000), "open took " + sw.ElapsedMilliseconds + " ms");
            Assert.Contains(tab, window.LargeFileTabs);

            var editor = window.PrimaryEditor;
            Assert.True(editor.IsShowingLargeFile);
            Assert.False(editor.Canvas.IsVisible);
            var viewer = editor.LargeFileViewer;
            Assert.Same(tab.LargeDocument, viewer.Document);
            Assert.True(viewer.IsEditOfferEnabled); // 60 MB is under the default 128 MB edit limit

            Assert.True(tab.LargeDocument.WaitForIndex(30000));
            Pump(() => viewer.BannerStatusText.Contains("lines"), 3000);
            Assert.Contains("lines", viewer.BannerStatusText);
            Assert.Contains("UTF-8", viewer.BannerStatusText);
            long lines = tab.LargeDocument.LineCount;
            Assert.True(lines > 500000);

            // Go to line (1-based) through the normal editor command path.
            editor.GoToLine(400000);
            Assert.Equal(399999, viewer.Viewport.CaretLine);
            Assert.Contains("line=400000", tab.LargeDocument.GetLine(viewer.Viewport.CaretLine));

            // Virtualised render: only the visible rows are decoded.
            viewer.Viewport.Measure(new global::Avalonia.Size(800, 600));
            viewer.Viewport.Arrange(new global::Avalonia.Rect(0, 0, 800, 600));
            var target = new global::Avalonia.Media.Imaging.RenderTargetBitmap(new global::Avalonia.PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                viewer.Viewport.Render(ctx);
            Assert.InRange(viewer.Viewport.LastRenderedLineCount, 1, 200);

            // Streaming search with count + navigation.
            var search = viewer.StartSearch("UNIQUE_MARKER", true, false);
            Assert.NotNull(search);
            Pump(() => search.IsCompleted && viewer.ActiveMatchIndex >= 0, 30000);
            Assert.True(search.IsCompleted);
            long expected = (lines - 1 + 100000 - 78) / 100000; // lines with i % 100000 == 77
            Assert.Equal(expected, search.TotalMatches);
            Assert.True(viewer.ActiveMatchIndex >= 0);
            Assert.Contains("of", viewer.FindStatusText);
            int before = viewer.ActiveMatchIndex;
            viewer.NavigateMatch(1);
            Assert.Equal((before + 1) % search.StoredMatchCount, viewer.ActiveMatchIndex);
            var m = search.GetMatch(viewer.ActiveMatchIndex);
            Assert.Equal(m.Line, viewer.Viewport.CaretLine);

            // Selection copy goes through the viewer, not the (hidden) text canvas.
            Assert.Equal("UNIQUE_MARKER", viewer.CopySelection());

            // Saving the viewer tab is refused (it would otherwise write the empty placeholder buffer over the file).
            long sizeBefore = new FileInfo(log).Length;
            Assert.False(window.SaveBufferToDisk(tab));
            Assert.Equal(sizeBefore, new FileInfo(log).Length);

            // Keys routed to the hidden canvas (Edit menu paste etc.) never dirty the placeholder buffer.
            window.SimulateShortcut(Key.V, MacKeymap.PrimaryModifier);
            Assert.False(tab.IsDirty);
            Assert.Single(tab.Lines);

            // Switching to a small file shows the normal editor; closing the viewer tab disposes its document.
            var small = window.OpenFileInTab(Path.Combine(ws, "small.cs"));
            Assert.False(small.IsLargeFileViewer);
            Assert.False(editor.IsShowingLargeFile);
            Assert.True(editor.Canvas.IsVisible);
            var doc = tab.LargeDocument;
            window.CloseTab(tab, forceWithoutPrompt: true);
            Assert.True(doc.IsDisposed);
            Assert.DoesNotContain(tab, window.LargeFileTabs);
            window.Close();
        }

        [AvaloniaFact]
        public void LargeFile_Follow_AppendsNewLines_AndOpenInEditorWorksUnderLimit()
        {
            string log = WriteLog("tail.log", 2);
            var window = new MainWindow(ws);
            window.Show();
            window.CurrentSettings.LargeFileThresholdMB = 1;

            var tab = window.OpenFileInTab(log);
            Assert.True(tab.IsLargeFileViewer);
            var viewer = window.PrimaryEditor.LargeFileViewer;
            Assert.True(tab.LargeDocument.WaitForIndex(10000));
            long before = tab.LargeDocument.LineCount;

            viewer.SetFollow(true);
            Assert.True(viewer.FollowEnabled);
            using (var fs = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var b = Encoding.UTF8.GetBytes("appended one\nappended two\n");
                fs.Write(b, 0, b.Length);
            }
            viewer.PollNow();
            Pump(() => tab.LargeDocument.LineCount == before + 2 && tab.LargeDocument.IsIndexComplete, 10000);
            Assert.Equal(before + 2, tab.LargeDocument.LineCount);
            Assert.Equal("appended two", tab.LargeDocument.GetLine(before));
            Assert.Contains("following", viewer.BannerStatusText);
            viewer.SetFollow(false);

            // 2 MB is under the default 128 MB edit limit, so "Open in Editor" loads it into a normal tab.
            Assert.True(viewer.IsEditOfferEnabled);
            window.OpenLargeFileInNormalEditor(log);
            var edit = window.ActiveTab;
            Assert.NotNull(edit);
            Assert.False(edit.IsLargeFileViewer);
            Assert.Equal(Path.GetFullPath(log), edit.FilePath);
            Assert.True(edit.Lines.Count > 1000);
            Assert.Empty(window.LargeFileTabs);
            window.Close();
        }
    }
}
