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
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared terminal "feel" logic (codeLess.Core TerminalFeel.cs / TerminalBoxDrawing.cs and the buffer, used by both
    /// terminal UIs; user report: the terminal felt and animated unlike VS Code): exact scroll positions from precise
    /// deltas, the view staying put while output arrives when scrolled back, smooth wheel notches, output repaint
    /// coalescing, cursor styles / blinking / DECSCUSR, box drawing aligned to the cell grid, contrast, the scrollback
    /// ring, synchronized output and parser throughput. In the sequential collection because it touches the static
    /// TerminalOptions and the settings file.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class TerminalFeelCoreTests
    {
        private readonly ITestOutputHelper output;
        public TerminalFeelCoreTests(ITestOutputHelper output) { this.output = output; }

        // ------------------------------------------------------------------ scrolling
        [Fact]
        public void Scroll_PreciseTrackpadDeltas_AccumulateFractionally_AndRowsSnap()
        {
            var m = new TerminalScrollModel();
            m.Sync(0, 1000);
            double t = 1000;
            // a trackpad gesture: a stream of small deltas 16 ms apart
            for (int i = 0; i < 7; i++) { m.Wheel(0.1, t, 14, false, false, 1000, 30); t += 16; }
            double off = m.Offset;
            Assert.True(off > 1 && Math.Abs(off - Math.Round(off)) > 0.01, "fractional offset expected, got " + off);
            Assert.Equal((int)Math.Round(off, MidpointRounding.AwayFromZero), m.DisplayOffset);
            // precise deltas are 1:1 (points / 50 -> pixels -> rows): nothing lost, nothing rounded per event
            double before = m.Offset;
            m.Wheel(0.14, t, 14, false, false, 1000, 30); t += 16;
            Assert.Equal(before + 0.14 * 50 / 14, m.Offset, 6);
            // a fast flick is not capped at 10 rows per event any more
            before = m.Offset;
            m.Wheel(6.3, t, 14, false, false, 1000, 30); t += 16;
            Assert.True(m.Offset - before > 20, "flick moved " + (m.Offset - before));
            // and back down to the live screen, clamped
            for (int i = 0; i < 40; i++) { m.Wheel(-5.3, t, 14, false, false, 1000, 30); t += 16; }
            Assert.Equal(0, m.Offset);
            Assert.Equal(0, m.DisplayOffset);
            Assert.False(m.IsScrolledBack);
        }

        [Fact]
        public void Scroll_NotchesScrollThreeRows_AltIsFiveTimesFaster()
        {
            var m = new TerminalScrollModel();
            m.Sync(0, 1000);
            m.Wheel(1.0, 1000, 14, false, false, 1000, 30);
            Assert.Equal(3, m.Offset, 6);
            m.Wheel(1.0, 2000, 14, true, false, 1000, 30);
            Assert.Equal(18, m.Offset, 6);
        }

        [Fact]
        public void Scroll_StaysOnTheSameRowsWhileOutputArrives_WhenScrolledBack()
        {
            var m = new TerminalScrollModel();
            m.Sync(100, 100);
            m.SetOffset(10, 100);
            Assert.Equal(10, m.DisplayOffset);
            m.Sync(150, 100); // 50 rows pushed (and 50 trimmed: the scrollback is full)
            Assert.Equal(60, m.Offset, 6);
            Assert.Equal(60, m.DisplayOffset);
            m.Sync(200, 100); // the rows being read were trimmed: clamp to the oldest row
            Assert.Equal(100, m.Offset, 6);

            // at the bottom the view follows the output
            m.ScrollToBottom();
            m.Sync(300, 100);
            Assert.Equal(0, m.Offset);

            // with a real buffer: the same text stays at the top of the view
            var vt = new Vt100TerminalBuffer(40, 10, false);
            for (int i = 0; i < 100; i++) vt.FeedText("line " + i + "\r\n");
            var model = new TerminalScrollModel();
            var snap = new TerminalViewportSnapshot();
            vt.SnapshotViewport(model, snap);
            model.SetOffset(20, vt.ScrollbackCount);
            vt.SnapshotViewport(model, snap);
            string top = snap.RowText(0);
            for (int i = 100; i < 130; i++) vt.FeedText("line " + i + "\r\n");
            vt.SnapshotViewport(model, snap);
            Assert.Equal(top, snap.RowText(0));
            Assert.Equal(50, snap.ScrollbackOffset);
        }

        [Fact]
        public void Scroll_SmoothScrolling_AnimatesNotches_ButNotTrackpads()
        {
            var m = new TerminalScrollModel();
            m.Sync(0, 1000);
            Assert.True(m.Wheel(1.0, 1000, 14, false, true, 1000, 30));
            Assert.True(m.IsAnimating);
            Assert.Equal(0, m.Offset); // nothing jumped yet
            Assert.Equal(3, m.Target, 6);
            m.Tick(1000 + TerminalOptions.SmoothScrollDurationMs * 0.4);
            Assert.True(m.Offset > 0.5 && m.Offset < 3, "mid-animation " + m.Offset);
            Assert.True(m.Offset > 1.5, "ease-out moves most of the way early: " + m.Offset);
            m.Tick(1000 + TerminalOptions.SmoothScrollDurationMs + 1);
            Assert.False(m.IsAnimating);
            Assert.Equal(3, m.Offset, 6);

            // a second notch while animating adds to the target (retargets from where it is)
            m.Wheel(1.0, 5000, 14, false, true, 1000, 30);
            m.Tick(5040);
            m.Wheel(1.0, 5060, 14, false, true, 1000, 30);
            Assert.Equal(9, m.Target, 6);

            // precise streams apply 1:1 even with smooth scrolling on (only the gesture's isolated first event animates)
            var p = new TerminalScrollModel();
            p.Sync(0, 1000);
            p.Wheel(0.1, 1000, 14, false, true, 1000, 30);
            Assert.True(p.IsAnimating);
            double tt = 1000;
            for (int i = 0; i < 5; i++) { tt += 16; p.Wheel(0.1, tt, 14, false, true, 1000, 30); }
            p.Tick(tt + 500);
            Assert.False(p.IsAnimating);
            Assert.Equal(0.3 + 5 * 0.1 * 50 / 14, p.Offset, 6);
        }

        // ------------------------------------------------------------------ output coalescing
        [Fact]
        public void RepaintGate_ManyOutputChunks_OneRepaintPerFrame_EvenFromSeveralThreads()
        {
            var gate = new TerminalRepaintGate();
            int posts = 0;
            for (int i = 0; i < 1000; i++) if (gate.OnOutput()) posts++;
            Assert.Equal(1, posts);
            gate.OnFramePainting();
            for (int i = 0; i < 1000; i++) if (gate.OnOutput()) posts++;
            Assert.Equal(2, posts);
            gate.OnFramePainting();

            int concurrent = 0;
            Parallel.For(0, 4, _ => { for (int i = 0; i < 2000; i++) if (gate.OnOutput()) Interlocked.Increment(ref concurrent); });
            Assert.Equal(1, concurrent);
            Assert.Equal(10000, gate.OutputEvents);
            Assert.Equal(3, gate.RepaintRequests);
        }

        // ------------------------------------------------------------------ cursor
        [Fact]
        public void Cursor_DefaultsMatchVsCode_AndDecscusrOverrides()
        {
            var s = new IdePersistentSettings();
            Assert.Equal("block", s.TerminalCursorStyle);
            Assert.Equal("outline", s.TerminalCursorStyleInactive);
            Assert.False(s.TerminalCursorBlinking); // VS Code: terminal.integrated.cursorBlinking = false
            Assert.False(s.TerminalSmoothScrolling);
            Assert.Equal(1.0, s.TerminalLineHeight);
            Assert.Equal(4.5, s.TerminalMinimumContrastRatio);

            string style; bool blink;
            TerminalCursorStyle.Resolve("block", "outline", false, 0, true, out style, out blink);
            Assert.Equal("block", style); Assert.False(blink);
            TerminalCursorStyle.Resolve("block", "outline", false, 5, true, out style, out blink); // vim insert mode
            Assert.Equal("line", style); Assert.True(blink);
            TerminalCursorStyle.Resolve("line", "outline", true, 2, true, out style, out blink);
            Assert.Equal("block", style); Assert.False(blink);
            TerminalCursorStyle.Resolve("underline", "outline", true, 0, true, out style, out blink);
            Assert.Equal("underline", style); Assert.True(blink);
            TerminalCursorStyle.Resolve("block", "outline", true, 5, false, out style, out blink); // unfocused
            Assert.Equal("outline", style); Assert.False(blink);
            TerminalCursorStyle.Resolve("block", "none", true, 0, false, out style, out blink);
            Assert.Equal("none", style);
            Assert.Equal("line", TerminalCursorStyle.NormalizeActive("bar"));
            Assert.Equal("block", TerminalCursorStyle.NormalizeActive("wobble"));

            double x, y, w, h;
            TerminalCursorStyle.Geometry("line", 10, 20, 7, 14, 1, out x, out y, out w, out h);
            Assert.Equal(new[] { 10.0, 20, 1, 14 }, new[] { x, y, w, h });
            TerminalCursorStyle.Geometry("underline", 10, 20, 7, 14, 1, out x, out y, out w, out h);
            Assert.Equal(new[] { 10.0, 33, 7, 1 }, new[] { x, y, w, h });
            TerminalCursorStyle.Geometry("block", 10, 20, 7, 14, 1, out x, out y, out w, out h);
            Assert.Equal(new[] { 10.0, 20, 7, 14 }, new[] { x, y, w, h });

            // xterm.js blink: solid right after input, then 600 ms on / off; never when blinking is off
            Assert.True(TerminalCursorStyle.BlinkVisible(true, 100));
            Assert.False(TerminalCursorStyle.BlinkVisible(true, 700));
            Assert.True(TerminalCursorStyle.BlinkVisible(true, 1300));
            Assert.True(TerminalCursorStyle.BlinkVisible(false, 700));
        }

        [Fact]
        public void Buffer_DecscusrAndSynchronizedOutput()
        {
            var vt = new Vt100TerminalBuffer(40, 10, false);
            string reply = null;
            vt.WriteBackCallback = r => reply = r;
            vt.FeedText("\x1b[5 q");
            Assert.Equal(5, vt.CursorShape);
            vt.FeedText("\x1b[2 q");
            Assert.Equal(2, vt.CursorShape);
            vt.FeedText("\x1b[0 q");
            Assert.Equal(0, vt.CursorShape);
            vt.FeedText("\x1b[?2026$p");
            Assert.Equal("\x1b[?2026;2$y", reply);
            vt.FeedText("\x1b[?2026h");
            Assert.True(vt.SynchronizedOutput);
            vt.FeedText("\x1b[?2026$p");
            Assert.Equal("\x1b[?2026;1$y", reply);
            vt.FeedText("\x1b[?2026l");
            Assert.False(vt.SynchronizedOutput);
            vt.FeedText("\x1b[6 q\x1b[?2026h" + "\x1b" + "c"); // RIS resets both
            Assert.Equal(0, vt.CursorShape);
            Assert.False(vt.SynchronizedOutput);
        }

        // ------------------------------------------------------------------ box drawing
        private static TermGlyphPrim One(char ch, double x, double y, double w, double h, double scale)
        {
            var l = new List<TermGlyphPrim>();
            Assert.True(TerminalBoxDrawing.TryBuild(ch, x, y, w, h, scale, l));
            Assert.Single(l);
            return l[0];
        }

        private static List<TermGlyphPrim> All(char ch, double x, double y, double w, double h, double scale)
        {
            var l = new List<TermGlyphPrim>();
            Assert.True(TerminalBoxDrawing.TryBuild(ch, x, y, w, h, scale, l));
            return l;
        }

        [Fact]
        public void BoxDrawing_LinesSpanTheCell_AndAlignAcrossGlyphs()
        {
            const double w = 7.224, h = 14, scale = 2;
            double x = 14 + 5 * w, y = 6 + 3 * h;
            double left = Math.Round(x * scale) / scale, right = Math.Round((x + w) * scale) / scale;
            double top = Math.Round(y * scale) / scale, bottom = Math.Round((y + h) * scale) / scale;

            var v = One('│', x, y, w, h, scale);
            Assert.Equal(top, v.Y, 6);
            Assert.Equal(bottom, v.Y + v.H, 6); // full row height: stacked rows join (font glyphs left gaps)
            Assert.True(v.X > left && v.X + v.W < right);
            Assert.Equal(0, (v.X * scale) % 1, 6); // on device pixels
            Assert.Equal(0, (v.W * scale) % 1, 6);

            var hz = One('─', x, y, w, h, scale);
            var hzNext = One('─', x + w, y, w, h, scale);
            Assert.True(hz.X <= left + 1e-9 && hz.X + hz.W >= right - 1e-9);
            Assert.True(hzNext.X <= hz.X + hz.W + 1e-9); // neighbouring cells always touch
            Assert.Equal(hz.Y, hzNext.Y, 6);

            // every glyph puts its vertical line where │ is, and its horizontal line where ─ is
            foreach (char ch in "┼├┤┌┐└┘┬┴╷╵")
            {
                var prims = All(ch, x, y, w, h, scale);
                var vert = prims.Where(p => p.W < p.H).ToList();
                var horiz = prims.Where(p => p.W > p.H).ToList();
                foreach (var p in vert) { Assert.Equal(v.X, p.X, 6); Assert.Equal(v.W, p.W, 6); }
                foreach (var p in horiz) { Assert.Equal(hz.Y, p.Y, 6); Assert.Equal(hz.H, p.H, 6); }
            }
            // heavy lines are twice as thick and centred
            var heavy = One('┃', x, y, w, h, scale);
            Assert.Equal(v.W * 2, heavy.W, 6);

            // ┌ joins: its vertical arm starts at the horizontal line and its horizontal arm starts at the vertical line
            var corner = All('┌', x, y, w, h, scale);
            var cv = corner.Single(p => p.W < p.H);
            var ch2 = corner.Single(p => p.W > p.H);
            Assert.Equal(ch2.Y, cv.Y, 6);
            Assert.Equal(cv.X, ch2.X, 6);
            Assert.Equal(bottom, cv.Y + cv.H, 6);
            Assert.Equal(right, ch2.X + ch2.W, 6);

            // double lines: two parallel lines, the double corner closes its outer edge
            var dbl = All('║', x, y, w, h, scale);
            Assert.Equal(2, dbl.Count(p => p.W < p.H && p.Y <= top + 1e-9));
            Assert.True(All('╔', x, y, w, h, scale).Count >= 4);
        }

        [Fact]
        public void BoxDrawing_BlocksArcsAndPowerline_FillTheCell()
        {
            const double w = 7.5, h = 15, scale = 1;
            double x = 20, y = 30;
            var full = One('█', x, y, w, h, scale);
            Assert.Equal(new[] { 20.0, 30, 8, 15 }, new[] { full.X, full.Y, full.W, full.H }); // snapped: 20..28 (27.5 rounds up)
            var upper = One('▀', x, y, w, h, scale);
            var lower = One('▄', x, y, w, h, scale);
            Assert.Equal(full.Y, upper.Y, 6);
            Assert.Equal(full.Y + full.H, lower.Y + lower.H, 6);
            Assert.Equal(full.H, upper.H + lower.H, 6);
            Assert.Equal(0.5, One('▒', x, y, w, h, scale).Alpha, 6);
            Assert.Equal(3, All('▙', x, y, w, h, scale).Count);

            // ╭ starts at the bottom edge on the vertical line's centre and ends at the right edge on the horizontal one's
            var v = One('│', x, y, w, h, scale);
            var hz = One('─', x, y, w, h, scale);
            var arc = One('╭', x, y, w, h, scale);
            Assert.Equal(TermGlyphPrim.StrokePolyline, arc.Kind);
            Assert.Equal(v.X + v.W / 2, arc.Points[0], 6);
            Assert.Equal(y + h, arc.Points[1], 6);
            Assert.Equal(x + w + 0.5, arc.Points[arc.Points.Length - 2], 6); // right edge (snapped 28)
            Assert.Equal(hz.Y + hz.H / 2, arc.Points[arc.Points.Length - 1], 6);

            var pl = One('\uE0B0', x, y, w, h, scale);
            Assert.Equal(TermGlyphPrim.FillPolygon, pl.Kind);
            Assert.Equal(y, pl.Points[1], 6);
            Assert.Equal(y + h, pl.Points[5], 6);

            Assert.False(TerminalBoxDrawing.IsCustomGlyph('a'));
            Assert.False(TerminalBoxDrawing.TryBuild('a', x, y, w, h, scale, new List<TermGlyphPrim>()));
            // every character of the ranges produces shapes
            for (char c = '─'; c <= '▟'; c++) Assert.NotEmpty(All(c, x, y, w, h, scale));
        }

        // ------------------------------------------------------------------ colours, metrics, settings
        [Fact]
        public void Contrast_LightensTooDarkText_LikeXtermJs()
        {
            int bg = unchecked((int)0xFF1E1E1E), darkBlue = unchecked((int)0xFF0037DA);
            int adj = TerminalContrast.Ensure(darkBlue, bg, 4.5);
            Assert.NotEqual(darkBlue, adj);
            Assert.True(TerminalContrast.Ratio(TerminalContrast.RelativeLuminance(adj), TerminalContrast.RelativeLuminance(bg)) >= 4.5);
            int white = unchecked((int)0xFFE5E5E5);
            Assert.Equal(white, TerminalContrast.Ensure(white, bg, 4.5));
            Assert.Equal(darkBlue, TerminalContrast.Ensure(darkBlue, bg, 1));
            // on a light background dark-enough text stays, too-light text is darkened
            int lightBg = unchecked((int)0xFFFFFFFF), yellow = unchecked((int)0xFFE5E510);
            int y2 = TerminalContrast.Ensure(yellow, lightBg, 4.5);
            Assert.True(TerminalContrast.RelativeLuminance(y2) < TerminalContrast.RelativeLuminance(yellow));
        }

        [Fact]
        public void CellMetrics_LineHeightOne_IsTheFontLineHeight_OnDevicePixels()
        {
            double cw, ch, top;
            TerminalCellMetrics.Compute(7.224, 13.97, 1.0, 2.0, out cw, out ch, out top);
            Assert.Equal(7.224, cw, 6); // runs are drawn as text: the cell is the real advance
            Assert.Equal(14, ch, 6);    // Menlo 12 px: ceil(13.97 * 2) / 2
            Assert.Equal(0, top, 6);
            TerminalCellMetrics.Compute(7.224, 13.97, 1.2, 2.0, out cw, out ch, out top);
            Assert.Equal(16.5, ch, 6);  // floor(28 * 1.2) / 2
            Assert.Equal(1.0, top, 6);  // round((33 - 28) / 2) / 2 (banker's rounding: 2.5 -> 2)
            Assert.Equal(12, TerminalOptions.DefaultFontSize(true));
            Assert.Equal(14, TerminalOptions.DefaultFontSize(false));
        }

        [Fact]
        public void Settings_RoundTrip_ApplyFrom_AndOldFilesKeepDefaults()
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var s = new IdePersistentSettings
            {
                TerminalCursorStyle = "underline",
                TerminalCursorStyleInactive = "none",
                TerminalCursorBlinking = true,
                TerminalSmoothScrolling = true,
                TerminalFontSize = 15,
                TerminalLineHeight = 1.3,
                TerminalFontFamily = "JetBrains Mono",
                TerminalMinimumContrastRatio = 1
            };
            var back = ser.Deserialize<IdePersistentSettings>(ser.Serialize(s));
            Assert.Equal("underline", back.TerminalCursorStyle);
            Assert.Equal("none", back.TerminalCursorStyleInactive);
            Assert.True(back.TerminalCursorBlinking);
            Assert.True(back.TerminalSmoothScrolling);
            Assert.Equal(15, back.TerminalFontSize);
            Assert.Equal(1.3, back.TerminalLineHeight, 6);
            Assert.Equal("JetBrains Mono", back.TerminalFontFamily);
            Assert.Equal(1, back.TerminalMinimumContrastRatio);

            var old = ser.Deserialize<IdePersistentSettings>("{\"TabSize\":4}");
            Assert.Equal("block", old.TerminalCursorStyle);
            Assert.False(old.TerminalCursorBlinking);
            Assert.Equal(1.0, old.TerminalLineHeight);

            var keepStyle = TerminalOptions.CursorStyle; var keepBlink = TerminalOptions.CursorBlinking; var keepSmooth = TerminalOptions.SmoothScrolling;
            var keepSize = TerminalOptions.FontSize; var keepLh = TerminalOptions.LineHeight; var keepFam = TerminalOptions.FontFamily;
            var keepCr = TerminalOptions.MinimumContrastRatio; var keepInactive = TerminalOptions.CursorStyleInactive;
            try
            {
                EditorNicetyOptions.ApplyFrom(back); // terminal settings travel with the editor niceties (both UIs)
                Assert.Equal("underline", TerminalOptions.CursorStyle);
                Assert.True(TerminalOptions.CursorBlinking);
                Assert.True(TerminalOptions.SmoothScrolling);
                Assert.Equal(15, TerminalOptions.EffectiveFontSize(true));
                Assert.Equal(1.3, TerminalOptions.EffectiveLineHeight, 6);
                Assert.Equal("block", TerminalOptions.CycleCursorStyle()); // underline -> block
                var copy = new IdePersistentSettings();
                EditorNicetyOptions.CopyTo(copy);
                Assert.True(copy.TerminalSmoothScrolling);
                Assert.Equal("JetBrains Mono", copy.TerminalFontFamily);
            }
            finally
            {
                TerminalOptions.CursorStyle = keepStyle; TerminalOptions.CursorBlinking = keepBlink; TerminalOptions.SmoothScrolling = keepSmooth;
                TerminalOptions.FontSize = keepSize; TerminalOptions.LineHeight = keepLh; TerminalOptions.FontFamily = keepFam;
                TerminalOptions.MinimumContrastRatio = keepCr; TerminalOptions.CursorStyleInactive = keepInactive;
            }
        }

        [Fact]
        public void StudioSettingsLoad_KeepsTerminalAndEditorSmoothnessSettings()
        {
            string path = IdePersistentSettings.GetSettingsFilePath();
            bool had = File.Exists(path);
            string backup = had ? File.ReadAllText(path) : null;
            try
            {
                var s = new IdePersistentSettings { TerminalCursorBlinking = true, TerminalCursorStyle = "line", TerminalLineHeight = 1.25, EditorSmoothScrolling = false, EditorStickyScroll = false };
                s.Save();
                var loaded = StudioSettings.Load(); // the macOS app loads through StudioSettings
                Assert.True(loaded.TerminalCursorBlinking);
                Assert.Equal("line", loaded.TerminalCursorStyle);
                Assert.Equal(1.25, loaded.TerminalLineHeight, 6);
                Assert.False(loaded.EditorSmoothScrolling); // was silently reset to the default on every restart
                Assert.False(loaded.EditorStickyScroll);
            }
            finally
            {
                if (had) File.WriteAllText(path, backup); else File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ buffer
        [Fact]
        public void ScrollbackRing_DropsOldestRows_InOrder()
        {
            var ring = new TermRowRing();
            for (int i = 0; i < 300; i++) ring.Add(new[] { new TermCell { Char = (char)('0' + i % 10) } }, 100);
            Assert.Equal(100, ring.Count);
            for (int i = 0; i < 100; i++) Assert.Equal((char)('0' + (200 + i) % 10), ring[i][0].Char);
            ring.Clear();
            Assert.Equal(0, ring.Count);
            ring.Add(new TermCell[0], 5);
            Assert.Equal(1, ring.Count);

            var vt = new Vt100TerminalBuffer(20, 5, false) { MaxScrollbackLines = 50 };
            for (int i = 0; i < 120; i++) vt.FeedText("row " + i + "\r\n");
            Assert.Equal(50, vt.ScrollbackCount);
            Assert.Equal(116, vt.ScrollbackPushCount); // 120 line feeds on a 5-row screen push 116 rows
            Assert.Contains("row 119", vt.GetScrollbackAndScreenText(10));
            vt.GetAbsoluteRange(out long first, out long last);
            Assert.Equal(66, first);
            Assert.Equal("row 66", RowText(vt, first));
            Assert.Equal("row 119", RowText(vt, vt.ScrollbackPushCount + 3));
        }

        private static string RowText(Vt100TerminalBuffer vt, long abs)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < vt.Cols; c++) { char ch = vt.GetAbsoluteCell(abs, c).Char; sb.Append(ch == '\0' ? ' ' : ch); }
            return sb.ToString().TrimEnd();
        }

        [Fact]
        public void Parser_100kLines_StaysFast()
        {
            var vt = new Vt100TerminalBuffer(120, 30, false);
            var line = Encoding.UTF8.GetBytes("\x1b[32m12345\x1b[0m output line with some text in it\r\n");
            var chunk = new byte[8192];
            int per = chunk.Length / line.Length;
            for (int i = 0; i < per; i++) Buffer.BlockCopy(line, 0, chunk, i * line.Length, line.Length);
            vt.ProcessBytes(chunk, per * line.Length); // warm-up
            int lines = 0;
            var sw = Stopwatch.StartNew();
            while (lines < 100000) { vt.ProcessBytes(chunk, per * line.Length); lines += per; }
            sw.Stop();
            output.WriteLine("parse 100k coloured lines (120x30): " + sw.ElapsedMilliseconds + " ms (was ~1 s: a cell copy of the whole screen and a 5000-entry list shift per line feed)");
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(600), "100k lines took " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(5000, vt.ScrollbackCount);
        }
    }
}
