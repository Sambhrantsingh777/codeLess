using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Rendering, metrics and scrolling of the terminal grid, matched to VS Code's terminal (xterm.js):
    /// <list type="bullet">
    /// <item>Cells are the font's advance wide and its line height (device-pixel rounded) x terminal.integrated.lineHeight
    /// tall; the font size follows terminal.integrated.fontSize (12 px on macOS, 14 px elsewhere).</item>
    /// <item>Each frame copies the visible rows under one buffer lock (no half-applied writes from the PTY thread), and
    /// rows keep their shaped text while their cells are unchanged (keyed by content, so scrolling and streaming output
    /// reuse rows); only changed rows are shaped again.</item>
    /// <item>PTY output repaints at most once per frame (<see cref="TerminalRepaintGate"/>); DEC mode 2026 (synchronized
    /// output) keeps the last complete frame until the application finishes drawing.</item>
    /// <item>Box drawing, block elements and Powerline separators are drawn as shapes on the cell grid.</item>
    /// <item>Colours: bold ANSI colours in their bright variants and the minimum contrast ratio of 4.5 (xterm.js).</item>
    /// <item>The cursor lives on its own layer (<see cref="AvaloniaTerminalCursorLayer"/>): blinking repaints only it.</item>
    /// <item>Scrolling: <see cref="TerminalScrollModel"/> (exact position, rows snap like xterm.js, the view stays put
    /// while output arrives when scrolled back, optional 125 ms smooth scrolling of wheel notches).</item>
    /// </list>
    /// </summary>
    internal partial class AvaloniaTerminalGridCanvas
    {
        // ------------------------------------------------------------------ metrics
        internal static readonly string DefaultMonoFamily = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "Menlo, Monaco, SF Mono, monospace"
            : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Consolas, Cascadia Mono, Courier New, monospace"
                : "DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");

        private string metricsKey;
        private Typeface fontRegular, fontBold, fontItalic, fontBoldItalic;
        private double fontSize = 12, cellW = 7.2, cellH = 14, textTop, textH = 14;
        private int renderEpoch;

        /// <summary>Width of one cell (the font's advance).</summary>
        public double CellW { get { EnsureMetrics(); return cellW; } }
        /// <summary>Height of one row (font line height x terminal.integrated.lineHeight, device-pixel rounded).</summary>
        public double CellH { get { EnsureMetrics(); return cellH; } }
        /// <summary>Offset from the top of a row to the top of the text line (centres the glyphs when lineHeight &gt; 1).</summary>
        public double TextTopOffset { get { EnsureMetrics(); return textTop; } }
        /// <summary>Height of one text line of the terminal font.</summary>
        public double TextLineHeight { get { EnsureMetrics(); return textH; } }
        internal double FontSizePx { get { EnsureMetrics(); return fontSize; } }
        internal Typeface FontRegular { get { EnsureMetrics(); return fontRegular; } }

        private double RenderScale
        {
            get
            {
                double s = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
                return s > 0 ? s : 1.0;
            }
        }

        /// <summary>Recomputes the font and cell size when the terminal settings or the display scale changed.</summary>
        private void EnsureMetrics()
        {
            double scale = RenderScale;
            string userFamily = (TerminalOptions.FontFamily ?? "").Trim();
            string family = userFamily.Length == 0 ? DefaultMonoFamily : userFamily + ", " + DefaultMonoFamily;
            double size = TerminalOptions.EffectiveFontSize(MacKeymap.IsMacOS);
            double lh = TerminalOptions.EffectiveLineHeight;
            string key = family + "|" + size.ToString("R") + "|" + lh.ToString("R") + "|" + scale.ToString("R");
            if (key == metricsKey) return;
            metricsKey = key;
            fontSize = size;
            fontRegular = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
            fontBold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
            fontItalic = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
            fontBoldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);
            double advance = size * 0.6, lineH = Math.Ceiling(size * 1.17);
            try
            {
                using (var probe = new TextLayout("MMMMMMMMMM", fontRegular, size, Brushes.White, TextAlignment.Left, TextWrapping.NoWrap))
                {
                    double w10 = probe.WidthIncludingTrailingWhitespace / 10.0;
                    if (w10 > size * 0.3 && w10 < size * 1.0) advance = w10;
                    double th = probe.Height;
                    if (th >= size * 0.9 && th <= size * 2.0) lineH = th;
                }
            }
            catch { }
            TerminalCellMetrics.Compute(advance, lineH, lh, scale, out cellW, out cellH, out textTop);
            textH = Math.Min(cellH, Math.Ceiling(lineH * scale - 1e-6) / scale);
            InvalidateRenderCaches();
        }

        /// <summary>Settings changed (font, line height, contrast, cursor): drop cached rows and measure again.</summary>
        internal void ApplyTerminalOptions()
        {
            metricsKey = null;
            EnsureMetrics();
            InvalidateRenderCaches();
            ApplySize();
            InvalidateVisual();
        }

        /// <summary>Theme / palette changed: resolved colours and rows are rebuilt.</summary>
        internal void InvalidateRenderCaches()
        {
            renderEpoch++;
            rowCache.Clear();
            colorCache.Clear();
            layoutCache.Clear();
        }

        // ------------------------------------------------------------------ repaint scheduling
        private readonly TerminalRepaintGate repaintGate = new TerminalRepaintGate();
        private AvaloniaTerminalCursorLayer cursorLayer;
        internal TerminalRepaintGate RepaintGate => repaintGate;
        /// <summary>Grid repaints requested (tests: output coalescing, the cursor not repainting the grid).</summary>
        internal long GridInvalidateCount { get; private set; }
        /// <summary>Times the grid was painted.</summary>
        internal long GridRenderCount { get; private set; }

        internal AvaloniaTerminalCursorLayer CursorLayer => cursorLayer ??= new AvaloniaTerminalCursorLayer(this);

        /// <summary>Repaints the grid and, in the same frame, the cursor layer (which reads the grid's snapshot).</summary>
        public new void InvalidateVisual()
        {
            GridInvalidateCount++;
            base.InvalidateVisual();
            cursorLayer?.InvalidateVisual();
        }

        /// <summary>Reader thread: output arrived. Posts one repaint per painted frame.</summary>
        internal void OnPtyOutput(UiPost post, Action afterRepaint)
        {
            if (!repaintGate.OnOutput()) return;
            post.Post(() =>
            {
                InvalidateVisual();
                afterRepaint?.Invoke();
            });
        }

        // ------------------------------------------------------------------ snapshot
        private readonly TerminalViewportSnapshot snapshot = new TerminalViewportSnapshot();
        private readonly TerminalScrollModel scrollModel = new TerminalScrollModel();
        private long frameNo;
        private int lastCursorShape;
        private DispatcherTimer syncTimeout;
        internal TerminalViewportSnapshot Snapshot => snapshot;
        internal TerminalScrollModel ScrollModel => scrollModel;
        /// <summary>Rows shaped (row-cache misses) since the canvas was created.</summary>
        internal long RowsBuilt { get; private set; }
        /// <summary>Rows shaped by the last frame.</summary>
        internal int LastFrameRowsBuilt { get; private set; }
        internal int RowCacheCount => rowCache.Count;
        /// <summary>True when the last frame kept the previous screen because synchronized output was active.</summary>
        internal bool LastFrameHeldBySyncOutput { get; private set; }

        private void TakeSnapshot(Vt100TerminalBuffer buf)
        {
            LastFrameHeldBySyncOutput = false;
            // DEC 2026: keep the last complete frame while the application draws (at most 1 s, like other terminals)
            if (buf.SynchronizedOutput && snapshot.Rows == buf.Rows && snapshot.Cols == buf.Cols && snapshot.Version > 0 &&
                unchecked(Environment.TickCount - buf.SynchronizedOutputSinceTick) < 1000)
            {
                LastFrameHeldBySyncOutput = true;
                if (syncTimeout == null)
                {
                    syncTimeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                    syncTimeout.Tick += (s, e) => { syncTimeout.Stop(); InvalidateVisual(); };
                }
                if (!syncTimeout.IsEnabled) syncTimeout.Start();
                return;
            }
            syncTimeout?.Stop(); // the application finished its frame: no fallback repaint needed
            buf.SnapshotViewport(scrollModel, snapshot);
            scrollbackOffset = snapshot.ScrollbackOffset;
            if (snapshot.CursorShape != lastCursorShape)
            {
                lastCursorShape = snapshot.CursorShape;
                Dispatcher.UIThread.Post(owner.OnCursorShapeChanged, DispatcherPriority.Background);
            }
        }

        /// <summary>Absolute index (see <see cref="Vt100TerminalBuffer.ScrollbackPushCount"/>) of the top visible row.</summary>
        internal long TopAbsRow => snapshot.Version == 0 ? 0 : snapshot.ScrollbackPushCount - snapshot.ScrollbackOffset;

        // ------------------------------------------------------------------ brushes, colours, text
        private readonly Dictionary<Color, IImmutableSolidColorBrush> brushCache = new Dictionary<Color, IImmutableSolidColorBrush>();
        internal IImmutableSolidColorBrush GetBrush(Color c)
        {
            if (!brushCache.TryGetValue(c, out var b))
            {
                if (brushCache.Count > 512) brushCache.Clear();
                b = new ImmutableSolidColorBrush(c);
                brushCache[c] = b;
            }
            return b;
        }

        internal int BrushCacheCount => brushCache.Count;
        internal int TextCacheCount => layoutCache.Count;

        private readonly struct Resolved
        {
            public readonly Color Fg, Bg;
            public readonly bool DefaultBg;
            public Resolved(Color fg, Color bg, bool defBg) { Fg = fg; Bg = bg; DefaultBg = defBg; }
        }

        private const byte FlagNoContrast = 128; // not a TermCell flag: custom glyphs keep their exact colour (xterm.js)
        private readonly Dictionary<(int, int, byte), Resolved> colorCache = new Dictionary<(int, int, byte), Resolved>();

        private Resolved ResolveColors(TermCell cell, bool noContrast, Vt100TerminalBuffer buf)
        {
            byte flags = (byte)(cell.Flags & (TermCell.FLAG_INVERSE | TermCell.FLAG_DEFAULT_FG | TermCell.FLAG_DEFAULT_BG | TermCell.FLAG_DIM | TermCell.FLAG_BOLD));
            if (noContrast) flags |= FlagNoContrast;
            var key = (cell.FgArgb, cell.BgArgb, flags);
            if (colorCache.TryGetValue(key, out var r)) return r;
            if (colorCache.Count > 16384) colorCache.Clear();

            int fgA = cell.FgArgb, bgA = cell.BgArgb;
            int fR = (fgA >> 16) & 0xFF, fG = (fgA >> 8) & 0xFF, fB = fgA & 0xFF;
            int bR = (bgA >> 16) & 0xFF, bG = (bgA >> 8) & 0xFF, bB = bgA & 0xFF;
            bool light = owner.IsLightTerminal;
            bool isDefBg = (cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0 || (bR == 0 && bG == 0 && bB == 0) ||
                           (light && bR < 45 && bG < 45 && bB < 55) || (!light && bR > 235 && bG > 235 && bB > 235);
            bool isDefFg = (cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0 ||
                           (light && fR > 195 && fG > 195 && fB > 195) ||
                           (!light && fR < 50 && fG < 50 && fB < 55 && (fR + fG + fB) > 0);
            Color fg = isDefFg ? owner.TermFgColor : Color.FromRgb((byte)fR, (byte)fG, (byte)fB);
            Color bg = isDefBg ? owner.TermBgColor : Color.FromRgb((byte)bR, (byte)bG, (byte)bB);
            bool bold = (cell.Flags & TermCell.FLAG_BOLD) != 0;
            if (bold && !isDefFg && TerminalOptions.DrawBoldTextInBrightColors && buf != null && buf.AnsiPalette != null)
            {
                // terminal.integrated.drawBoldTextInBrightColors: bold ANSI 0-7 use their bright variants 8-15
                for (int i = 0; i < 8 && i + 8 < buf.AnsiPalette.Length; i++)
                {
                    if (buf.AnsiPalette[i].ToArgb() == fgA)
                    {
                        var p = buf.AnsiPalette[i + 8];
                        fg = Color.FromRgb(p.R, p.G, p.B);
                        break;
                    }
                }
            }
            if (light && !isDefFg)
            {
                if (fg.R > 200 && fg.G > 180 && fg.B < 100) fg = Color.FromRgb(148, 104, 0);
                else if (fg.R < 80 && fg.G > 180 && fg.B > 180) fg = Color.FromRgb(5, 122, 155);
                else if (fg.R < 80 && fg.G > 180 && fg.B < 150) fg = Color.FromRgb(16, 124, 65);
            }
            if ((cell.Flags & TermCell.FLAG_INVERSE) != 0)
            {
                var tmp = fg; fg = bg; bg = tmp;
                isDefBg = bg == owner.TermBgColor;
            }
            bool dim = (cell.Flags & TermCell.FLAG_DIM) != 0;
            double ratio = TerminalOptions.MinimumContrastRatio;
            if (!noContrast && ratio > 1)
            {
                int adj = TerminalContrast.Ensure(PackRgb(fg), PackRgb(bg), dim ? ratio / 2 : ratio);
                fg = Color.FromRgb((byte)((adj >> 16) & 0xFF), (byte)((adj >> 8) & 0xFF), (byte)(adj & 0xFF));
            }
            if (dim) fg = Color.FromArgb(128, fg.R, fg.G, fg.B); // xterm.js draws dim text at 50 % opacity
            r = new Resolved(fg, bg, isDefBg);
            colorCache[key] = r;
            return r;
        }

        private static int PackRgb(Color c) => unchecked((int)0xFF000000) | (c.R << 16) | (c.G << 8) | c.B;

        private readonly Dictionary<(string, int, Color), TextLayout> layoutCache = new Dictionary<(string, int, Color), TextLayout>();

        /// <summary>Shaped text for a run (cached across rows and frames; bounded).</summary>
        internal TextLayout GetLayout(string text, bool bold, bool italic, Color fg)
        {
            int style = (bold ? 1 : 0) | (italic ? 2 : 0);
            var key = (text, style, fg);
            if (!layoutCache.TryGetValue(key, out var tl))
            {
                if (layoutCache.Count >= 4096) layoutCache.Clear();
                var tf = bold ? (italic ? fontBoldItalic : fontBold) : (italic ? fontItalic : fontRegular);
                tl = new TextLayout(text, tf, fontSize, GetBrush(fg), TextAlignment.Left, TextWrapping.NoWrap);
                layoutCache[key] = tl;
            }
            return tl;
        }

        // ------------------------------------------------------------------ row cache
        private sealed class RowDraw
        {
            public TermCell[] Cells;
            public int Count;
            public int Epoch;
            public long LastFrame;
            public readonly List<(double X, double W, IBrush Brush)> Bgs = new List<(double, double, IBrush)>();
            public readonly List<(TextLayout Text, double X)> Texts = new List<(TextLayout, double)>();
            public readonly List<(double X1, double X2, IBrush Brush)> Underlines = new List<(double, double, IBrush)>();
            public readonly List<(TermGlyphPrim Prim, Color Color, Geometry Geo)> Shapes = new List<(TermGlyphPrim, Color, Geometry)>();
        }

        private readonly Dictionary<ulong, RowDraw> rowCache = new Dictionary<ulong, RowDraw>();
        private readonly List<TermGlyphPrim> primScratch = new List<TermGlyphPrim>();
        private Resolved[] resolvedScratch = new Resolved[256];
        private readonly System.Text.StringBuilder runSb = new System.Text.StringBuilder(256);

        private RowDraw GetRowDraw(TermCell[] cells, int count, Vt100TerminalBuffer buf)
        {
            ulong hash = TerminalViewportSnapshot.HashRow(cells, count);
            if (rowCache.TryGetValue(hash, out var rd) && rd.Epoch == renderEpoch && rd.Count == count && TerminalViewportSnapshot.SameCells(rd.Cells, cells, count))
            {
                rd.LastFrame = frameNo;
                return rd;
            }
            rd = new RowDraw { Cells = new TermCell[count], Count = count, Epoch = renderEpoch, LastFrame = frameNo };
            Array.Copy(cells, rd.Cells, count);
            BuildRow(rd, buf);
            rowCache[hash] = rd;
            RowsBuilt++;
            LastFrameRowsBuilt++;
            return rd;
        }

        private void BuildRow(RowDraw rd, Vt100TerminalBuffer buf)
        {
            var cells = rd.Cells;
            int n = rd.Count;
            double cw = cellW;
            Color termBg = owner.TermBgColor;
            // resolve every cell once (theme defaults, bold-bright, inverse, contrast, dim)
            if (resolvedScratch.Length < n) resolvedScratch = new Resolved[Math.Max(n, 256)];
            var rs = resolvedScratch;
            for (int i = 0; i < n; i++) rs[i] = ResolveColors(cells[i], TerminalBoxDrawing.IsCustomGlyph(cells[i].Char), buf);
            // backgrounds: coalesced spans of the same non-default colour
            int c = 0;
            while (c < n)
            {
                var res = rs[c];
                if (res.DefaultBg || res.Bg == termBg) { c++; continue; }
                int end = c + 1;
                while (end < n)
                {
                    var nr = rs[end];
                    if (nr.DefaultBg || nr.Bg != res.Bg) break;
                    end++;
                }
                // a touch wider so neighbouring spans never leave a hairline at fractional cell edges
                rd.Bgs.Add((PadLeft + c * cw, (end - c) * cw + 0.5, GetBrush(res.Bg)));
                c = end;
            }

            c = 0;
            while (c < n)
            {
                TermCell cell = cells[c];
                char ch = cell.Char;
                bool underline = (cell.Flags & TermCell.FLAG_UNDERLINE) != 0;
                if (ch <= ' ')
                {
                    if (underline) rd.Underlines.Add((PadLeft + c * cw, PadLeft + (c + 1) * cw, GetBrush(rs[c].Fg)));
                    c++;
                    continue;
                }
                bool bold = (cell.Flags & TermCell.FLAG_BOLD) != 0, italic = (cell.Flags & TermCell.FLAG_ITALIC) != 0;
                if (TerminalBoxDrawing.IsCustomGlyph(ch))
                {
                    var res = rs[c];
                    primScratch.Clear();
                    if (TerminalBoxDrawing.TryBuild(ch, PadLeft + c * cw, 0, cw, cellH, RenderScale, primScratch))
                        foreach (var p in primScratch) rd.Shapes.Add((p, res.Fg, BuildGeometry(p)));
                    c++;
                    continue;
                }
                if (ch > 126)
                {
                    if (char.IsLowSurrogate(ch)) { c++; continue; } // second cell of a wide astral glyph
                    string glyph = ch.ToString();
                    if (char.IsHighSurrogate(ch))
                    {
                        char lo = c + 1 < n ? cells[c + 1].Char : '\0';
                        if (!char.IsLowSurrogate(lo)) { c++; continue; }
                        glyph = new string(new[] { ch, lo });
                    }
                    var gres = rs[c];
                    rd.Texts.Add((GetLayout(glyph, bold, italic, gres.Fg), PadLeft + c * cw));
                    if (underline) rd.Underlines.Add((PadLeft + c * cw, PadLeft + (c + 1) * cw, GetBrush(gres.Fg)));
                    c++;
                    continue;
                }
                // contiguous printable ASCII sharing style and colour: one shaped run on the grid
                var rres = rs[c];
                int start = c;
                runSb.Clear();
                runSb.Append(ch);
                c++;
                while (c < n)
                {
                    TermCell next = cells[c];
                    char nch = next.Char;
                    if (nch < 32 || nch > 126) break;
                    if ((next.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE)) != (cell.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE)))
                        break;
                    if ((nch != ' ' || underline) && rs[c].Fg != rres.Fg) break;
                    runSb.Append(nch);
                    c++;
                }
                // trailing blanks carry no ink
                int len = runSb.Length;
                while (len > 0 && runSb[len - 1] == ' ' && !underline) len--;
                if (len > 0) rd.Texts.Add((GetLayout(runSb.ToString(0, len), bold, italic, rres.Fg), PadLeft + start * cw));
                if (underline) rd.Underlines.Add((PadLeft + start * cw, PadLeft + c * cw, GetBrush(rres.Fg)));
            }
        }

        private void TrimRowCache(int visibleRows)
        {
            int soft = Math.Max(128, visibleRows * 6);
            if (rowCache.Count <= soft) return;
            var stale = new List<ulong>();
            foreach (var kv in rowCache) if (kv.Value.LastFrame < frameNo - 2 || kv.Value.Epoch != renderEpoch) stale.Add(kv.Key);
            foreach (var k in stale) rowCache.Remove(k);
        }

        private void DrawRow(DrawingContext ctx, RowDraw rd, double y)
        {
            foreach (var bg in rd.Bgs) ctx.FillRectangle(bg.Brush, new Rect(bg.X, y, bg.W, cellH));
        }

        private void DrawRowInk(DrawingContext ctx, RowDraw rd, double y)
        {
            double ty = y + textTop;
            foreach (var t in rd.Texts) t.Text.Draw(ctx, new Point(t.X, ty));
            if (rd.Underlines.Count > 0)
            {
                double uy = y + Math.Floor(textTop + textH - 1) + 0.5;
                foreach (var u in rd.Underlines) ctx.DrawLine(GetPen(u.Brush, 1), new Point(u.X1, uy), new Point(u.X2, uy));
            }
            foreach (var s in rd.Shapes) DrawPrim(ctx, s.Prim, s.Color, s.Geo, y);
        }

        /// <summary>Row-relative geometry of a polygon / polyline glyph part (built once, kept with the cached row).</summary>
        private static Geometry BuildGeometry(TermGlyphPrim p)
        {
            if (p.Kind == TermGlyphPrim.FillRect || p.Points == null || p.Points.Length < 4) return null;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(p.Points[0], p.Points[1]), p.Kind == TermGlyphPrim.FillPolygon);
                for (int i = 2; i + 1 < p.Points.Length; i += 2) g.LineTo(new Point(p.Points[i], p.Points[i + 1]));
                g.EndFigure(p.Kind == TermGlyphPrim.FillPolygon);
            }
            return geo;
        }

        internal void DrawPrim(DrawingContext ctx, TermGlyphPrim p, Color color, Geometry geo, double y)
        {
            var c = p.Alpha < 0.999 ? Color.FromArgb((byte)Math.Round(color.A * p.Alpha), color.R, color.G, color.B) : color;
            var brush = GetBrush(c);
            if (p.Kind == TermGlyphPrim.FillRect)
            {
                ctx.FillRectangle(brush, new Rect(p.X, p.Y + y, p.W, p.H));
                return;
            }
            if (geo == null) return;
            using (ctx.PushTransform(Matrix.CreateTranslation(0, y)))
            {
                if (p.Kind == TermGlyphPrim.FillPolygon) ctx.DrawGeometry(brush, null, geo);
                else ctx.DrawGeometry(null, GetPen(brush, p.Thickness), geo);
            }
        }

        private readonly Dictionary<(IBrush, double), IPen> penCache = new Dictionary<(IBrush, double), IPen>();
        internal IPen GetPen(IBrush brush, double thickness)
        {
            var key = (brush, thickness);
            if (!penCache.TryGetValue(key, out var p))
            {
                if (penCache.Count > 256) penCache.Clear();
                p = new ImmutablePen((IImmutableBrush)brush, thickness);
                penCache[key] = p;
            }
            return p;
        }

        // ------------------------------------------------------------------ render
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            GridRenderCount++;
            repaintGate.OnFramePainting(); // before the snapshot: output from now on schedules the next frame
            EnsureMetrics();
            double w = Bounds.Width, h = Bounds.Height;
            var termBgBrush = GetBrush(owner.TermBgColor);
            context.FillRectangle(termBgBrush, new Rect(0, 0, w, h));
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            TakeSnapshot(buf);
            frameNo++;
            LastFrameRowsBuilt = 0;
            var snap = snapshot;
            int rows = snap.Rows, cols = snap.Cols;
            double cw = cellW;
            int maxVisCols = Math.Min(cols, Math.Max(1, (int)((w - PadLeft) / cw) + 1));
            GetSelectionView(out int sr0, out int sc0, out int sr1, out int sc1, out bool hasSel);
            IBrush selBrush = hasSel ? GetBrush(SelectionColor) : null;

            for (int r = 0; r < rows; r++)
            {
                double y = PadTop + r * cellH;
                if (y > h) break;
                var rd = GetRowDraw(snap.Cells[r], maxVisCols, buf);
                DrawRow(context, rd, y);
                if (hasSel && r >= sr0 && r <= sr1)
                {
                    int a = r == sr0 ? sc0 : 0, b = r == sr1 ? sc1 : cols - 1;
                    if (b >= a) context.FillRectangle(selBrush, new Rect(PadLeft + a * cw, y, (b - a + 1) * cw, cellH));
                }
                DrawRowInk(context, rd, y);
            }
            TrimRowCache(rows);

            // Underline the Cmd/Ctrl-hovered link (skipped if the row's text changed under it since detection).
            if (hoverLink != null && hoverRow >= 0 && hoverRow < rows &&
                string.Equals(snap.RowText(hoverRow), hoverLineText, StringComparison.Ordinal))
            {
                double ly = PadTop + hoverRow * cellH + Math.Floor(textTop + textH - 1) + 0.5;
                double lx1 = PadLeft + hoverLink.StartCol * cw;
                double lx2 = PadLeft + Math.Min(cols, hoverLink.EndColExclusive) * cw;
                context.DrawLine(GetPen(GetBrush(owner.TermFgColor), 1), new Point(lx1, ly), new Point(lx2, ly));
            }

            // IME caret geometry + composition preview at the cursor cell
            double icx = PadLeft + snap.CursorCol * cw;
            double icy = PadTop + snap.CursorRow * cellH;
            var newRect = new Rect(icx, icy, cw, cellH);
            if (newRect != imeCaretRect)
            {
                imeCaretRect = newRect;
                Dispatcher.UIThread.Post(() => imeClient.NotifyCaretChanged(), DispatcherPriority.Background);
            }
            if (IsImeComposing && scrollbackOffset == 0)
            {
                var pft = new TextLayout(imePreedit, fontRegular, fontSize, GetBrush(owner.AccentColor), TextAlignment.Left, TextWrapping.NoWrap,
                    textDecorations: TextDecorations.Underline);
                context.FillRectangle(termBgBrush, new Rect(icx, icy, pft.WidthIncludingTrailingWhitespace + 2, cellH));
                pft.Draw(context, new Point(icx, icy + textTop));
            }

            // Scrollback scrollbar (VS Code terminal): in the right padding, shown while hovering / dragging / just scrolled.
            // The thumb follows the exact scroll position (smooth), the rows snap.
            if (!snap.IsAlternateScreen)
            {
                int sb = snap.ScrollbackCount;
                bool recent = Environment.TickCount64 - lastScrollTicks < EditorScrollbar.AutoHideDelayMs;
                double exact = Math.Max(0, Math.Min(sb, scrollModel.Offset));
                scrollbar.Paint(context, owner.CurrentTheme, Bounds.Size, (sb + rows) * cellH, rows * cellH, (sb - exact) * cellH, IsPointerOver || recent);
            }
            else scrollbar.Paint(context, owner.CurrentTheme, Bounds.Size, 0, 1, 0, false);
        }

        /// <summary>terminal.selectionBackground: the editor selection colour of the theme (VS Code's default).</summary>
        private Color SelectionColor
        {
            get
            {
                var t = owner.CurrentTheme;
                if (t != null && !owner.IsLightTerminal == !t.IsLight)
                {
                    var c = AvaloniaColorHelper.ToAvColor(t.BgSelection);
                    if (c.A > 0) return c.A == 255 ? Color.FromArgb(200, c.R, c.G, c.B) : c;
                }
                return Color.FromArgb(120, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B);
            }
        }

        // ------------------------------------------------------------------ cursor (drawn by AvaloniaTerminalCursorLayer)
        /// <summary>Where and how to draw the cursor for the last painted frame; false when hidden.</summary>
        internal bool TryGetCursor(out Rect cell, out string style, out bool blink, out TermCell under)
        {
            cell = default; style = TerminalCursorStyle.Block; blink = false; under = default;
            if (snapshot.Version == 0 && owner?.ActiveBuffer != null)
                TakeSnapshot(owner.ActiveBuffer);
            var snap = snapshot;
            if (snap.Version == 0 || !snap.CursorVisible || IsImeComposing) return false;
            int viewRow = snap.CursorRow + snap.ScrollbackOffset; // still on screen when scrolled back a little (xterm.js)
            if (viewRow < 0 || viewRow >= snap.Rows) return false;
            TerminalCursorStyle.Resolve(TerminalOptions.CursorStyle, TerminalOptions.CursorStyleInactive, TerminalOptions.CursorBlinking,
                snap.CursorShape, owner.CursorHasFocus, out style, out blink);
            if (style == TerminalCursorStyle.None) return false;
            EnsureMetrics();
            cell = new Rect(PadLeft + snap.CursorCol * cellW, PadTop + viewRow * cellH, cellW, cellH);
            under = snap.Cell(viewRow, snap.CursorCol);
            return true;
        }

        /// <summary>Whether the cursor should blink now (settings + DECSCUSR, focused).</summary>
        internal bool CursorWantsBlink
        {
            get
            {
                TerminalCursorStyle.Resolve(TerminalOptions.CursorStyle, TerminalOptions.CursorStyleInactive, TerminalOptions.CursorBlinking,
                    snapshot.CursorShape, true, out _, out bool blink);
                return blink;
            }
        }

        // ------------------------------------------------------------------ scrolling
        private static readonly Stopwatch animationStopwatch = Stopwatch.StartNew();
        private bool animationFramePending;
        /// <summary>Time source of the scroll animation in ms (tests replace it with a fake clock).</summary>
        internal Func<double> AnimationClock { get; set; }
        internal double NowMs => AnimationClock != null ? AnimationClock() : animationStopwatch.Elapsed.TotalMilliseconds;

        /// <summary>Exact scroll position in rows back from the live screen (fractional while scrolling).</summary>
        internal double ExactScrollOffset => scrollModel.Offset;

        private void ApplyScrollChange()
        {
            var buf = owner.ActiveBuffer;
            int sb = buf?.ScrollbackCount ?? 0;
            scrollbackOffset = Math.Max(0, Math.Min(sb, (int)Math.Round(scrollModel.Offset, MidpointRounding.AwayFromZero)));
            lastScrollTicks = Environment.TickCount64;
            ClearHoverLink();
            if (lastPointerPos.HasValue) UpdateHoverLink(lastPointerPos.Value, false);
            InvalidateVisual();
        }

        /// <summary>Wheel / trackpad on the scrollback (positive delta = towards older output).</summary>
        internal void ScrollbackWheel(double delta, bool fast)
        {
            var buf = owner.ActiveBuffer;
            if (buf == null || buf.IsAlternateScreen) return;
            bool smooth = TerminalOptions.SmoothScrolling && (AvaloniaTerminalEmulatorControl.IsDesktopRuntime || AnimationClock != null);
            if (scrollModel.Wheel(delta, NowMs, CellH, fast, smooth, buf.ScrollbackCount, Math.Max(1, buf.Rows)))
            {
                ApplyScrollChange();
                if (scrollModel.IsAnimating) RequestScrollFrame();
            }
        }

        private void RequestScrollFrame()
        {
            if (animationFramePending) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return; // tests call AdvanceScrollAnimationForTest
            animationFramePending = true;
            top.RequestAnimationFrame(_ =>
            {
                animationFramePending = false;
                TickScrollAnimation();
            });
        }

        private void TickScrollAnimation()
        {
            if (!scrollModel.IsAnimating) return;
            bool more = scrollModel.Tick(NowMs);
            ApplyScrollChange();
            if (more) RequestScrollFrame();
        }

        /// <summary>Advances the smooth-scroll animation to <see cref="NowMs"/> (tests).</summary>
        internal void AdvanceScrollAnimationForTest() => TickScrollAnimation();

        /// <summary>Jumps to a scroll position (rows back from the live screen).</summary>
        internal void SetScrollOffset(double rows)
        {
            var buf = owner.ActiveBuffer;
            scrollModel.SetOffset(rows, buf?.ScrollbackCount ?? 0);
            ApplyScrollChange();
        }

        /// <summary>Back to the live screen (typing, paste: VS Code scrolls to the bottom on input).</summary>
        internal void ScrollToBottom()
        {
            if (scrollbackOffset == 0 && !scrollModel.IsScrolledBack) return;
            scrollModel.ScrollToBottom();
            scrollbackOffset = 0;
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ selection (absolute rows: follows the content)
        /// <summary>Selection in view rows / columns, clipped to the viewport (normalised).</summary>
        private void GetSelectionView(out int r0, out int c0, out int r1, out int c1, out bool visible)
        {
            r0 = c0 = r1 = c1 = 0;
            visible = false;
            if (!HasSelection()) return;
            long a0 = selStartAbs, a1 = selEndAbs;
            int x0 = selStartCol, x1 = selEndCol;
            if (a0 > a1 || (a0 == a1 && x0 > x1)) { long ta = a0; a0 = a1; a1 = ta; int tc = x0; x0 = x1; x1 = tc; }
            long top = TopAbsRow;
            int rows = snapshot.Rows;
            long v0 = a0 - top, v1 = a1 - top;
            if (v1 < 0 || v0 >= rows) return;
            r0 = (int)Math.Max(0, v0);
            c0 = v0 < 0 ? 0 : x0;
            r1 = (int)Math.Min(rows - 1, v1);
            c1 = v1 >= rows ? snapshot.Cols - 1 : x1;
            visible = true;
        }

        private long ViewRowToAbs(int viewRow) => TopAbsRow + viewRow;
    }
}
