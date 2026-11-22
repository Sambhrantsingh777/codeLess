using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// "In big JSON in .py files, text is shown as variables despite being in quotes": the painted slice of a long line
    /// (horizontal scroll, word-wrap rows) must be colored from the tokens of the whole logical line.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class TokenizationSliceHeadlessTests : IDisposable
    {
        private readonly string dir;

        public TokenizationSliceHeadlessTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "codeLess_Slice_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            EditorNicetyOptions.TextMateHighlighting = true;
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas, double w = 1000, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private (MainWindow, EditorTabBuffer) Open(string name, string content)
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, content);
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            return (w, tab);
        }

        private static string BigDictLine(int entries)
        {
            var sb = new StringBuilder("DATA = {");
            for (int i = 0; i < entries; i++) sb.Append("\"key_" + i + "\": \"some value with words and commas " + i + "\", ");
            sb.Append("\"end\": 1}");
            return sb.ToString();
        }

        /// <summary>Color of the painted character at <paramref name="col"/> of <paramref name="docLine"/> in the last frame.</summary>
        private static global::Avalonia.Media.Color? PaintedColorAt(AvaloniaVirtualEditorCanvas canvas, int docLine, int col)
        {
            foreach (var (line, sliceStart, tokens) in canvas.LastFrameTokens)
            {
                if (line != docLine) continue;
                int pos = sliceStart;
                foreach (var t in tokens)
                {
                    int len = (t.Text ?? "").Length;
                    if (col >= pos && col < pos + len) return t.Color;
                    pos += len;
                }
            }
            return null;
        }

        [AvaloniaFact]
        public void HorizontallyScrolledLongLine_SliceStartingInsideAString_IsStringColored()
        {
            string line = BigDictLine(60);
            var (w, tab) = Open("data.py", line + "\nprint(DATA)\n");
            EditorNicetyOptions.TextMateHighlighting = false; // the fallback tokenizer path (no grammar / grammar off)
            var canvas = w.PrimaryEditor.Canvas;
            canvas.CaptureFrameTokens = true;
            RenderOnce(canvas);
            // scroll so that the window (which starts 8 columns left of the scroll position) begins inside a value
            int target = line.IndexOf("words and", 1500, StringComparison.Ordinal);
            tab.ScrollLeftPx = (target + 8 - 4) * canvas.CharWidth;
            RenderOnce(canvas);
            var slices = canvas.LastFrameTokens.Where(t => t.DocLine == 0).ToList();
            Assert.Single(slices);
            Assert.True(slices[0].SliceStart > 0 && slices[0].SliceStart <= target, "slice " + slices[0].SliceStart);
            var str = AvaloniaColorHelper.ToAvColor(w.CurrentTheme.SyntaxString);
            Assert.Equal(str, PaintedColorAt(canvas, 0, target));
            Assert.Equal(str, PaintedColorAt(canvas, 0, target + 6));
            Assert.True(canvas.LastFrameFallbackLines > 0);
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        [AvaloniaFact]
        public void WrappedLongLine_ContinuationRowsKeepStringColors()
        {
            string line = BigDictLine(30);
            var (w, tab) = Open("wrapped.py", line + "\n");
            EditorNicetyOptions.TextMateHighlighting = false;
            var canvas = w.PrimaryEditor.Canvas;
            canvas.CaptureFrameTokens = true;
            tab.WordWrap = true;
            RenderOnce(canvas, 700, 600);
            var rows = canvas.LastFrameTokens.Where(t => t.DocLine == 0).ToList();
            Assert.True(rows.Count > 3, "wrapped rows: " + rows.Count);
            var str = AvaloniaColorHelper.ToAvColor(w.CurrentTheme.SyntaxString);
            int checkedRows = 0;
            foreach (var r in rows.Skip(1))
            {
                // every "with words" inside the row is a string, whatever the row's start column
                int at = line.IndexOf("words", r.SliceStart, StringComparison.Ordinal);
                int rowLen = r.Tokens.Sum(t => (t.Text ?? "").Length);
                if (at < 0 || at >= r.SliceStart + rowLen) continue;
                Assert.Equal(str, PaintedColorAt(canvas, 0, at));
                checkedRows++;
            }
            Assert.True(checkedRows >= 2, "rows checked: " + checkedRows);
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        [AvaloniaFact]
        public void LineLongerThanTheTextMateLimit_IsStillHighlighted_AndTripleQuotedJsonIsString()
        {
            string huge = BigDictLine(600); // > 20,000 chars: the TextMate tokenizer skips it (VS Code paints it plain)
            Assert.True(huge.Length > 20000);
            string src = huge + "\nblob = \"\"\"\n{\"name\": \"demo\", \"items\": [1, 2]}\n\"\"\"\nx = 1\n";
            var (w, tab) = Open("big.py", src);
            var canvas = w.PrimaryEditor.Canvas;
            canvas.CaptureFrameTokens = true;
            canvas.CompleteBackgroundTokenization();
            RenderOnce(canvas);
            Assert.True(canvas.LastFrameTextMateLines > 0, "MagicPython grammar is bundled");
            Assert.True(canvas.LastFrameFallbackLines >= 1, "the over-long line uses the fallback colors");
            var str = AvaloniaColorHelper.ToAvColor(w.CurrentTheme.SyntaxString);
            Assert.Equal(str, PaintedColorAt(canvas, 0, huge.IndexOf("words", StringComparison.Ordinal)));
            tab.ScrollLeftPx = 15000 * canvas.CharWidth;
            RenderOnce(canvas);
            int far = huge.IndexOf("words", 15040, StringComparison.Ordinal);
            Assert.Equal(str, PaintedColorAt(canvas, 0, far));

            // grammar off: the fallback tokenizer carries the triple-quoted block over its lines
            tab.ScrollLeftPx = 0;
            EditorNicetyOptions.TextMateHighlighting = false;
            RenderOnce(canvas);
            canvas.CompleteBackgroundTokenization();
            RenderOnce(canvas);
            Assert.Equal(str, PaintedColorAt(canvas, 2, 2));        // "name" inside the """ block
            Assert.Equal(str, PaintedColorAt(canvas, 2, 30));       // [1, 2] inside the block
            Assert.NotEqual(str, PaintedColorAt(canvas, 4, 0));     // x = 1 after the block
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }
    }
}
