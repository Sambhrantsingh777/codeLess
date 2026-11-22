using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Keystroke latency on a 50,000-line file (QA #15): every typed character used to rebuild the whole document text
    /// (twice per key) and compare it with the saved text. Measures TextInput -> buffer edit -> render, headless, in the
    /// steady state (background tokenization finished, as on the desktop after a few seconds), plus managed bytes
    /// allocated per keystroke. Set CODELESS_BENCH_REPORT to append the numbers to a file.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorTypingLatencyBench : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string dir;

        public EditorTypingLatencyBench(ITestOutputHelper output)
        {
            this.output = output;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_TypeBench_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas)
        {
            canvas.Measure(new Size(1200, 800));
            canvas.Arrange(new Rect(0, 0, 1200, 800));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        [AvaloniaFact]
        public void Typing_In50kLineFile_IsFast_AndKeepsUndoDirtyAndContentCorrect()
        {
            var sb = new StringBuilder();
            sb.Append("namespace Bench\n{\n    public class Big\n    {\n");
            for (int i = 0; i < 50000 - 6; i++)
                sb.Append("        public int Field").Append(i).Append(" = ").Append(i).Append("; // filler text for width\n");
            sb.Append("    }\n}\n");
            string file = Path.Combine(dir, "Big.cs");
            File.WriteAllText(file, sb.ToString());
            string original = File.ReadAllText(file).Replace("\r\n", "\n");

            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            var canvas = w.PrimaryEditor.Canvas;
            Assert.True(tab.Lines.Count >= 50000);
            tab.CursorLine = 25000; tab.CursorCol = 8; tab.ScrollTopLine = 24990;
            RenderOnce(canvas);
            canvas.CompleteBackgroundTokenization(); // steady state: the desktop finishes this from a background timer
            RenderOnce(canvas);

            var typeMs = new List<double>();
            var sw = new Stopwatch();
            string typed = "var x = 42; ";
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            int keys = 0;
            for (int rep = 0; rep < 20; rep++)
            {
                foreach (char c in typed)
                {
                    sw.Restart();
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
                    RenderOnce(canvas);
                    sw.Stop();
                    typeMs.Add(sw.Elapsed.TotalMilliseconds);
                    keys++;
                }
            }
            double allocPerKeyKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / keys;
            // the edit itself (no repaint): must not allocate anything proportional to the document
            long editAlloc0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 40; i++)
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "z" });
            double editAllocKb = (GC.GetAllocatedBytesForCurrentThread() - editAlloc0) / 1024.0 / 40;
            for (int i = 0; i < 40; i++) w.SimulateShortcut(Key.Back, KeyModifiers.None);
            var enterMs = new List<double>();
            var backMs = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                sw.Restart();
                w.SimulateShortcut(Key.Enter, KeyModifiers.None);
                RenderOnce(canvas);
                sw.Stop();
                enterMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                w.SimulateShortcut(Key.Back, KeyModifiers.None);
                RenderOnce(canvas);
                sw.Stop();
                backMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            double typeMed = BenchUtil.Median(typeMs), enterMed = BenchUtil.Median(enterMs), backMed = BenchUtil.Median(backMs);
            typeMs.Sort();
            double typeP95 = typeMs[(int)(typeMs.Count * 0.95)];
            string report = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "typing-latency 50k lines (edit+render): char median {0:F2} ms p95 {1:F2} ms | Enter median {2:F2} ms | Backspace(join) median {3:F2} ms | allocated per keystroke: {4:F0} KB incl. repaint, {5:F0} KB for the edit",
                typeMed, typeP95, enterMed, backMed, allocPerKeyKb, editAllocKb);
            output.WriteLine(report);
            string rp = Environment.GetEnvironmentVariable("CODELESS_BENCH_REPORT");
            if (!string.IsNullOrEmpty(rp)) BenchUtil.Append(rp, report);

            // correctness: the typed text is there, the document is dirty, undo restores the original exactly
            Assert.StartsWith("        " + typed, tab.Lines[25000]);
            Assert.True(tab.IsDirty);
            Assert.Contains(typed, tab.Content);
            int guard = 0;
            while (tab.UndoStack.Count > 0 && guard++ < 1000) tab.PerformUndo();
            Assert.Equal(original, tab.Content);
            Assert.False(tab.IsDirty);

            // generous bounds (a shared headless box): the old path rebuilt ~5 MB of text per keystroke
            Assert.True(typeMed < 4.0, report);
            Assert.True(editAllocKb < 256, report);
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }
    }
}

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// QA #17: cost of the per-edit (debounced) analysis that the old 2,500-line / 180 KB "large file" cut-off switched
    /// off, so the new thresholds can be justified with numbers. Appends to CODELESS_BENCH_REPORT when set.
    /// Timing-sensitive: runs in the sequential collection so parallel CPU-heavy tests cannot skew it.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class LargeFileThresholdBench
    {
        [Fact]
        public void GutterDiffAndAnalysis_StayCheapUpToTheNewThreshold()
        {
            var report = new System.Text.StringBuilder("large-file-threshold:");
            foreach (int n in new[] { 10000, 50000, 100000, 200000 })
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++) sb.Append("    public int Field").Append(i).Append(" = ").Append(i).Append("; // value\n");
                string text = sb.ToString();
                var buf = new EditorTabBuffer { FilePath = "/tmp/t" + n + ".cs", Language = "cs", Content = text, SavedContent = text, GitHeadContent = text };
                buf.SyncLinesFromContent();
                buf.Lines[n / 2] = buf.Lines[n / 2] + " // edited";
                buf.SyncContentFromLines(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                buf.RefreshGutterDiff();
                double gutter = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
                double ast = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var folds = EditorFoldingModel.ComputeFor(buf.Lines, buf.FilePath, 4);
                double fold = sw.Elapsed.TotalMilliseconds;
                Assert.Single(buf.GutterModifiedLines);
                report.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0} lines: gutter {1:F1} ms, analysis {2:F1} ms, folding {3:F1} ms, large={4};", n, gutter, ast, fold, buf.IsLargeFile));
                if (n <= 100000) Assert.True(gutter < 250 && ast < 1500, report.ToString());
            }
            string rp = System.Environment.GetEnvironmentVariable("CODELESS_BENCH_REPORT");
            if (!string.IsNullOrEmpty(rp)) BenchUtil.Append(rp, report.ToString());
        }
    }
}
