using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Painting, scrolling and cursor of the Windows terminal, matched to VS Code's terminal (xterm.js) with the same
    /// shared pieces as the macOS / Linux terminal (codeLess.Core TerminalFeel.cs / TerminalBoxDrawing.cs):
    /// one buffer snapshot per paint (no half-applied writes), at most one repaint per frame for PTY output and only the
    /// rows that changed are invalidated, DEC 2026 synchronized output, exact scroll position with snapped rows and the
    /// view staying put while output arrives when scrolled back, optional smooth scrolling of wheel notches, cursor
    /// style / blinking / inactive outline from the settings and DECSCUSR (blinking repaints only the cursor cell),
    /// box drawing as shapes, bold-bright ANSI colours and the minimum contrast ratio, cached brushes. STRICT C# 5.
    /// </summary>
    public partial class CodeLessTerminalEmulatorControl
    {
        private readonly TerminalScrollModel scrollModel = new TerminalScrollModel();
        private readonly TerminalRepaintGate repaintGate = new TerminalRepaintGate();
        private readonly TerminalViewportSnapshot snapshot = new TerminalViewportSnapshot();
        private readonly TerminalViewportSnapshot probeSnapshot = new TerminalViewportSnapshot();
        private readonly Dictionary<int, SolidBrush> brushCache = new Dictionary<int, SolidBrush>();
        private readonly Dictionary<long, Color[]> colorCache = new Dictionary<long, Color[]>();
        private readonly List<TermGlyphPrim> primScratch = new List<TermGlyphPrim>();
        private ulong[] paintedRowHashes = new ulong[0];
        private int paintedCursorRow = -1, paintedCursorCol = -1;
        private int textTop;
        private int lastCursorShape;
        private System.Windows.Forms.Timer scrollAnimTimer;
        private System.Windows.Forms.Timer resizeColsTimer;
        private System.Windows.Forms.Timer syncTimeoutTimer;
        private static readonly Stopwatch feelClock = Stopwatch.StartNew();

        /// <summary>Paints since creation (tests / diagnostics).</summary>
        public long PaintCount;
        /// <summary>Rows painted by the last paint (only the invalidated rows are painted).</summary>
        public int LastPaintRows;

        // ------------------------------------------------------------------ settings
        /// <summary>Applies the terminal settings (font size / family / line height, cursor, contrast, smooth scrolling).</summary>
        public void ApplyTerminalOptions()
        {
            ApplyTerminalFontFromOptions();
            colorCache.Clear();
            cursorBlinkOn = true;
            UpdateBlinkTimer();
            this.Invalidate();
        }

        private void ApplyTerminalFontFromOptions()
        {
            string fam = (TerminalOptions.FontFamily ?? "").Trim();
            if (fam.Length > 0)
            {
                int comma = fam.IndexOf(',');
                if (comma > 0) fam = fam.Substring(0, comma);
                fam = fam.Trim().Trim('\'', '"');
            }
            if (fam.Length == 0) fam = "Consolas"; // VS Code's editor / terminal font on Windows
            // terminal.integrated.fontSize is in CSS pixels (14 on Windows): 96 DPI points
            float pt = (float)(TerminalOptions.EffectiveFontSize(false) * 72.0 / 96.0);
            SetTerminalFont(fam, pt);
        }

        /// <summary>Cell height for a font line height: lineHeight 1 = the font's own line height (no extra padding).</summary>
        private void ApplyLineHeight(int fontLineHeight)
        {
            int lh = Math.Max(8, fontLineHeight);
            cellHeight = Math.Max(lh, (int)Math.Floor(lh * TerminalOptions.EffectiveLineHeight));
            textTop = (cellHeight - lh) / 2;
        }

        // ------------------------------------------------------------------ output / repaint
        /// <summary>Reader thread: at most one posted repaint per painted frame; only changed rows are invalidated.</summary>
        private void OnSessionOutputFeel()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (!repaintGate.OnOutput()) return;
            try
            {
                this.BeginInvoke(new Action(InvalidateChangedRows));
            }
            catch
            {
                repaintGate.Reset();
            }
        }

        private void InvalidateChangedRows()
        {
            if (this.IsDisposed) return;
            if (session == null || session.Buffer == null || paintedRowHashes.Length == 0 || !this.Visible)
            {
                repaintGate.Reset();
                this.Invalidate();
                return;
            }
            Vt100TerminalBuffer buf = session.Buffer;
            buf.SnapshotViewport(scrollModel, probeSnapshot);
            if (probeSnapshot.Rows != paintedRowHashes.Length || probeSnapshot.ScrollbackOffset != scrollbackOffset)
            {
                this.Invalidate();
                return;
            }
            bool any = false;
            int maxCols = probeSnapshot.Cols;
            for (int r = 0; r < probeSnapshot.Rows; r++)
            {
                ulong h = TerminalViewportSnapshot.HashRow(probeSnapshot.Cells[r], maxCols);
                if (h != paintedRowHashes[r])
                {
                    this.Invalidate(new Rectangle(0, padTop + r * cellHeight, this.ClientSize.Width, cellHeight + 1));
                    any = true;
                }
            }
            int cr = probeSnapshot.CursorRow + probeSnapshot.ScrollbackOffset;
            if (cr != paintedCursorRow || probeSnapshot.CursorCol != paintedCursorCol)
            {
                InvalidateCell(paintedCursorRow, paintedCursorCol);
                InvalidateCell(cr, probeSnapshot.CursorCol);
                any = true;
            }
            if (probeSnapshot.ScrollbackCount != snapshot.ScrollbackCount) InvalidateScrollbar();
            if (!any) repaintGate.Reset(); // nothing visible changed: let the next output ask again
        }

        private void InvalidateCell(int viewRow, int col)
        {
            if (viewRow < 0 || col < 0) return;
            this.Invalidate(new Rectangle(padLeft + col * cellWidth - 1, padTop + viewRow * cellHeight, cellWidth * 2 + 2, cellHeight));
        }

        private void InvalidateScrollbar()
        {
            this.Invalidate(new Rectangle(Math.Max(0, this.ClientSize.Width - 14), 0, 14, this.ClientSize.Height));
        }

        // ------------------------------------------------------------------ scrolling
        /// <summary>Exact scroll position (rows back from the live screen; fractional while scrolling).</summary>
        public double ExactScrollOffset { get { return scrollModel.Offset; } }
        public int ScrollbackOffsetRows { get { return scrollbackOffset; } }

        private void WheelScrollback(int wheelDelta, bool fast)
        {
            if (session == null || session.Buffer == null || session.Buffer.IsAlternateScreen) return;
            Vt100TerminalBuffer buf = session.Buffer;
            if (scrollModel.Wheel(wheelDelta / 120.0, feelClock.Elapsed.TotalMilliseconds, cellHeight, fast, TerminalOptions.SmoothScrolling, buf.ScrollbackCount, Math.Max(1, buf.Rows)))
            {
                AfterScrollChange();
                if (scrollModel.IsAnimating) StartScrollAnimation();
            }
        }

        private void StartScrollAnimation()
        {
            if (scrollAnimTimer == null)
            {
                scrollAnimTimer = new System.Windows.Forms.Timer { Interval = 15 };
                scrollAnimTimer.Tick += (s, e) =>
                {
                    bool more = scrollModel.Tick(feelClock.Elapsed.TotalMilliseconds);
                    AfterScrollChange();
                    if (!more) scrollAnimTimer.Stop();
                };
            }
            if (!scrollAnimTimer.Enabled) scrollAnimTimer.Start();
        }

        private void AfterScrollChange()
        {
            int sb = (session != null && session.Buffer != null) ? session.Buffer.ScrollbackCount : 0;
            scrollbackOffset = Math.Max(0, Math.Min(sb, (int)Math.Round(scrollModel.Offset, MidpointRounding.AwayFromZero)));
            this.Invalidate();
        }

        private void SetScrollOffsetRows(double rows)
        {
            int sb = (session != null && session.Buffer != null) ? session.Buffer.ScrollbackCount : 0;
            scrollModel.SetOffset(rows, sb);
            AfterScrollChange();
        }

        private void ScrollToBottomFeel()
        {
            if (scrollbackOffset == 0 && !scrollModel.IsScrolledBack) return;
            scrollModel.ScrollToBottom();
            scrollbackOffset = 0;
            this.Invalidate();
        }

        // ------------------------------------------------------------------ resize (VS Code TerminalResizeDebouncer)
        private void ResizeSession(int cols, int rows)
        {
            Vt100TerminalBuffer buf = session.Buffer;
            if (buf != null && TerminalResizePolicy.ShouldDebounceColumns(buf.ScrollbackCount + buf.Rows, buf.Cols, cols))
            {
                session.Resize(buf.Cols, rows);
                if (resizeColsTimer == null)
                {
                    resizeColsTimer = new System.Windows.Forms.Timer { Interval = TerminalResizePolicy.DebounceColumnsMs };
                    resizeColsTimer.Tick += (s, e) =>
                    {
                        resizeColsTimer.Stop();
                        RecalculateGridDimensionsNow();
                    };
                }
                resizeColsTimer.Stop();
                resizeColsTimer.Start();
                return;
            }
            if (resizeColsTimer != null) resizeColsTimer.Stop();
            session.Resize(cols, rows);
        }

        // ------------------------------------------------------------------ cursor
        private void UpdateBlinkTimer()
        {
            if (blinkTimer == null) return;
            bool want = this.Focused && CursorWantsBlink();
            if (want && !blinkTimer.Enabled) { cursorBlinkOn = true; blinkTimer.Start(); }
            else if (!want && blinkTimer.Enabled) { blinkTimer.Stop(); cursorBlinkOn = true; }
            InvalidateCursorCell();
        }

        private bool CursorWantsBlink()
        {
            string style;
            bool blink;
            TerminalCursorStyle.Resolve(UseBarCursor ? TerminalCursorStyle.Line : TerminalOptions.CursorStyle, TerminalOptions.CursorStyleInactive,
                TerminalOptions.CursorBlinking, snapshot.CursorShape, true, out style, out blink);
            return blink;
        }

        private void InvalidateCursorCell()
        {
            if (paintedCursorRow >= 0) InvalidateCell(paintedCursorRow, paintedCursorCol);
            else this.Invalidate();
        }

        private void OnBlinkTick()
        {
            if (!this.Focused) return;
            cursorBlinkOn = !cursorBlinkOn;
            InvalidateCursorCell(); // only the cursor cell repaints
        }

        // ------------------------------------------------------------------ colours
        private SolidBrush BrushFor(Color c)
        {
            int key = c.ToArgb();
            SolidBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                if (brushCache.Count > 512)
                {
                    foreach (SolidBrush old in brushCache.Values) old.Dispose();
                    brushCache.Clear();
                }
                b = new SolidBrush(c);
                brushCache[key] = b;
            }
            return b;
        }

        /// <summary>[fg, bg] to paint for a cell (theme defaults, bold-bright, inverse, contrast, dim blended into bg).</summary>
        private Color[] ResolveCell(TermCell cell, bool isLight, Color effBg, Color effFg, bool noContrast, Vt100TerminalBuffer buf)
        {
            int flags = cell.Flags & (TermCell.FLAG_INVERSE | TermCell.FLAG_DEFAULT_FG | TermCell.FLAG_DEFAULT_BG | TermCell.FLAG_DIM | TermCell.FLAG_BOLD);
            if (noContrast) flags |= 128;
            long key = ((long)(uint)cell.FgArgb << 32) ^ ((long)(uint)cell.BgArgb * 1315423911L) ^ ((long)flags << 56);
            Color[] res;
            if (colorCache.TryGetValue(key, out res)) return res;
            if (colorCache.Count > 16384) colorCache.Clear();
            Color cfg = cell.Fg, cbg = cell.Bg;
            bool inv = (cell.Flags & TermCell.FLAG_INVERSE) != 0;
            bool isDefBg = (cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0 || (cbg.R == 0 && cbg.G == 0 && cbg.B == 0) ||
                           (isLight && (cbg.ToArgb() == Color.FromArgb(24, 24, 27).ToArgb() || cbg.ToArgb() == Color.FromArgb(7, 8, 12).ToArgb()));
            bool isDefFg = (cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0;
            Color fg = isDefFg ? effFg : cfg;
            Color bg = isDefBg ? effBg : cbg;
            if ((cell.Flags & TermCell.FLAG_BOLD) != 0 && !isDefFg && TerminalOptions.DrawBoldTextInBrightColors && buf != null && buf.AnsiPalette != null)
            {
                for (int i = 0; i < 8 && i + 8 < buf.AnsiPalette.Length; i++)
                {
                    if (buf.AnsiPalette[i].ToArgb() == cell.FgArgb) { fg = buf.AnsiPalette[i + 8]; break; }
                }
            }
            if (isLight && !inv && fg.GetBrightness() > 0.78f) fg = effFg;
            if (inv) { Color t = fg; fg = bg; bg = t; }
            bool dim = (cell.Flags & TermCell.FLAG_DIM) != 0;
            double ratio = TerminalOptions.MinimumContrastRatio;
            if (!noContrast && ratio > 1)
                fg = Color.FromArgb(TerminalContrast.Ensure(fg.ToArgb() | unchecked((int)0xFF000000), bg.ToArgb() | unchecked((int)0xFF000000), dim ? ratio / 2 : ratio));
            if (dim) fg = StudioTheme.Blend(fg, bg, 0.5); // xterm.js: 50 % opacity
            res = new Color[] { Color.FromArgb(255, fg.R, fg.G, fg.B), Color.FromArgb(255, bg.R, bg.G, bg.B) };
            colorCache[key] = res;
            return res;
        }

        // ------------------------------------------------------------------ paint
        protected override void OnPaint(PaintEventArgs e)
        {
            repaintGate.OnFramePainting(); // before the snapshot: output from now on schedules the next paint
            PaintCount++;
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            bool isLight = (theme != null && (theme.IsLight || theme.BgEditor.GetBrightness() > 0.5f)) ||
                           (session != null && session.Buffer != null && session.Buffer.IsLightTheme) ||
                           this.BackColor.GetBrightness() > 0.5f;
            Color effBg = isLight ? Color.FromArgb(255, 255, 255) : this.BackColor;
            Color effFg = isLight ? Color.FromArgb(59, 59, 59) : this.ForeColor;
            g.Clear(effBg);
            LastPaintRows = 0;
            if (session == null || session.Buffer == null) return;

            Vt100TerminalBuffer buf = session.Buffer;
            bool held = buf.SynchronizedOutput && snapshot.Version > 0 && snapshot.Rows == buf.Rows && snapshot.Cols == buf.Cols &&
                        unchecked(Environment.TickCount - buf.SynchronizedOutputSinceTick) < 1000;
            if (held)
            {
                if (syncTimeoutTimer == null)
                {
                    syncTimeoutTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                    syncTimeoutTimer.Tick += (s, ev) => { syncTimeoutTimer.Stop(); this.Invalidate(); };
                }
                if (!syncTimeoutTimer.Enabled) syncTimeoutTimer.Start();
            }
            else
            {
                if (syncTimeoutTimer != null) syncTimeoutTimer.Stop();
                buf.SnapshotViewport(scrollModel, snapshot);
            }
            TerminalViewportSnapshot snap = snapshot;
            scrollbackOffset = snap.ScrollbackOffset;
            if (snap.CursorShape != lastCursorShape)
            {
                lastCursorShape = snap.CursorShape;
                try { this.BeginInvoke(new Action(UpdateBlinkTimer)); } catch { }
            }

            int rows = snap.Rows, cols = snap.Cols;
            int topVirtualRow = snap.IsAlternateScreen ? 0 : snap.ScrollbackCount - snap.ScrollbackOffset;
            if (paintedRowHashes.Length != rows) paintedRowHashes = new ulong[rows];
            Color selBg = theme != null && theme.BgSelection.A > 0
                ? theme.BgSelection
                : (isLight ? Color.FromArgb(173, 214, 255) : Color.FromArgb(38, 79, 120));
            Rectangle clip = e.ClipRectangle;
            StringBuilder runChars = new StringBuilder(cols);

            for (int sr = 0; sr < rows; sr++)
            {
                TermCell[] line = snap.Cells[sr];
                ulong rowHash = TerminalViewportSnapshot.HashRow(line, cols);
                int y = padTop + sr * cellHeight;
                if (y + cellHeight <= clip.Top || y >= clip.Bottom)
                {
                    // changed after the invalidation was computed: paint it next (its stored hash stays the painted one)
                    if (rowHash != paintedRowHashes[sr]) this.Invalidate(new Rectangle(0, y, this.ClientSize.Width, cellHeight + 1));
                    continue;
                }
                bool wholeRow = y >= clip.Top && y + cellHeight <= clip.Bottom && clip.Left <= 0 && clip.Right >= this.ClientSize.Width;
                if (wholeRow) paintedRowHashes[sr] = rowHash;
                else if (rowHash != paintedRowHashes[sr]) this.Invalidate(new Rectangle(0, y, this.ClientSize.Width, cellHeight + 1)); // partly repainted
                LastPaintRows++;
                int vr = topVirtualRow + sr;

                // 1. backgrounds (coalesced spans), then the selection on top of them
                int c0 = 0;
                while (c0 < cols)
                {
                    Color b0 = ResolveCell(line[c0], isLight, effBg, effFg, false, buf)[1];
                    if (b0.ToArgb() == effBg.ToArgb()) { c0++; continue; }
                    int end = c0 + 1;
                    while (end < cols && ResolveCell(line[end], isLight, effBg, effFg, false, buf)[1].ToArgb() == b0.ToArgb()) end++;
                    g.FillRectangle(BrushFor(b0), padLeft + c0 * cellWidth, y, (end - c0) * cellWidth, cellHeight);
                    c0 = end;
                }
                if (hasSelection)
                {
                    int a = -1;
                    for (int c = 0; c <= cols; c++)
                    {
                        bool sel = c < cols && IsCellSelected(vr, c);
                        if (sel && a < 0) a = c;
                        if (!sel && a >= 0)
                        {
                            g.FillRectangle(BrushFor(selBg), padLeft + a * cellWidth, y, (c - a) * cellWidth, cellHeight);
                            a = -1;
                        }
                    }
                }

                // 2. text runs sharing colour / style; box drawing as shapes on the cell grid
                int cIdx = 0;
                while (cIdx < cols)
                {
                    TermCell first = line[cIdx];
                    char ch = first.Char == '\0' ? ' ' : first.Char;
                    byte styleMask = (byte)(first.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE));
                    if (ch == ' ' && (first.Flags & TermCell.FLAG_UNDERLINE) == 0) { cIdx++; continue; }
                    if (TerminalBoxDrawing.IsCustomGlyph(ch))
                    {
                        Color gfg = ResolveCell(first, isLight, effBg, effFg, true, buf)[0];
                        primScratch.Clear();
                        if (TerminalBoxDrawing.TryBuild(ch, padLeft + cIdx * cellWidth, y, cellWidth, cellHeight, 1.0, primScratch))
                            DrawPrims(g, primScratch, gfg);
                        cIdx++;
                        continue;
                    }
                    Color runFg = ResolveCell(first, isLight, effBg, effFg, false, buf)[0];
                    int runStartCol = cIdx;
                    runChars.Length = 0;
                    runChars.Append(ch);
                    cIdx++;
                    while (cIdx < cols)
                    {
                        TermCell next = line[cIdx];
                        char nch = next.Char == '\0' ? ' ' : next.Char;
                        if (TerminalBoxDrawing.IsCustomGlyph(nch)) break;
                        byte nextStyle = (byte)(next.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE));
                        if (nextStyle != styleMask) break;
                        if (nch != ' ' && ResolveCell(next, isLight, effBg, effFg, false, buf)[0].ToArgb() != runFg.ToArgb()) break;
                        runChars.Append(nch);
                        cIdx++;
                    }
                    Font runFont = regularFont;
                    if ((styleMask & TermCell.FLAG_BOLD) != 0) runFont = boldFont;
                    else if ((styleMask & TermCell.FLAG_ITALIC) != 0) runFont = italicFont;
                    string sRun = runChars.ToString().TrimEnd(' ');
                    if (sRun.Length > 0)
                    {
                        TextRenderer.DrawText(g, sRun, runFont, new Point(padLeft + runStartCol * cellWidth, y + textTop), runFg,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    if ((styleMask & TermCell.FLAG_UNDERLINE) != 0)
                    {
                        int ux1 = padLeft + runStartCol * cellWidth;
                        int ux2 = padLeft + cIdx * cellWidth;
                        g.FillRectangle(BrushFor(runFg), ux1, y + textTop + Math.Max(1, cellHeight - textTop * 2) - 1, ux2 - ux1, 1);
                    }
                }

                // 3. hovered link underline
                if (vr == hoveredVRow && hoveredLinkStartCol >= 0 && hoveredLinkEndCol >= hoveredLinkStartCol)
                {
                    Color linkCol = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248);
                    int lx1 = padLeft + hoveredLinkStartCol * cellWidth;
                    int lx2 = padLeft + (hoveredLinkEndCol + 1) * cellWidth;
                    g.FillRectangle(BrushFor(linkCol), lx1, y + cellHeight - 2, lx2 - lx1, 1);
                }
            }

            PaintCursor(g, snap, buf, isLight, effBg, effFg);
            if (paintedCursorRow >= 0)
            {
                Rectangle cell = new Rectangle(padLeft + paintedCursorCol * cellWidth, padTop + paintedCursorRow * cellHeight, cellWidth, cellHeight);
                if (!clip.Contains(cell)) InvalidateCell(paintedCursorRow, paintedCursorCol); // moved outside this paint's area
            }
            PaintScrollbar(g, snap, isLight);
        }

        private void DrawPrims(Graphics g, List<TermGlyphPrim> prims, Color color)
        {
            for (int i = 0; i < prims.Count; i++)
            {
                TermGlyphPrim p = prims[i];
                Color c = p.Alpha < 0.999 ? Color.FromArgb((int)Math.Round(255 * p.Alpha), color) : color;
                if (p.Kind == TermGlyphPrim.FillRect)
                {
                    g.FillRectangle(BrushFor(c), (float)p.X, (float)p.Y, (float)p.W, (float)p.H);
                    continue;
                }
                PointF[] pts = new PointF[p.Points.Length / 2];
                for (int k = 0; k < pts.Length; k++) pts[k] = new PointF((float)p.Points[k * 2], (float)p.Points[k * 2 + 1]);
                SmoothingMode keep = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (p.Kind == TermGlyphPrim.FillPolygon) g.FillPolygon(BrushFor(c), pts);
                else using (Pen pen = new Pen(c, (float)p.Thickness)) g.DrawLines(pen, pts);
                g.SmoothingMode = keep;
            }
        }

        private void PaintCursor(Graphics g, TerminalViewportSnapshot snap, Vt100TerminalBuffer buf, bool isLight, Color effBg, Color effFg)
        {
            paintedCursorRow = -1;
            paintedCursorCol = -1;
            if (!snap.CursorVisible || session.HasExited) return;
            int viewRow = snap.CursorRow + snap.ScrollbackOffset; // stays visible when scrolled back a little (xterm.js)
            if (viewRow < 0 || viewRow >= snap.Rows) return;
            paintedCursorRow = viewRow;
            paintedCursorCol = snap.CursorCol;
            string style;
            bool blink;
            TerminalCursorStyle.Resolve(UseBarCursor ? TerminalCursorStyle.Line : TerminalOptions.CursorStyle, TerminalOptions.CursorStyleInactive,
                TerminalOptions.CursorBlinking, snap.CursorShape, this.Focused, out style, out blink);
            if (style == TerminalCursorStyle.None || (blink && !cursorBlinkOn)) return;
            int cx = padLeft + snap.CursorCol * cellWidth;
            int cy = padTop + viewRow * cellHeight;
            double rx, ry, rw, rh;
            TerminalCursorStyle.Geometry(style, cx, cy, cellWidth, cellHeight, TerminalOptions.CursorWidth, out rx, out ry, out rw, out rh);
            // VS Code: terminalCursor.foreground = terminal foreground, terminalCursor.background = terminal background
            Color curColor = effFg;
            if (style == TerminalCursorStyle.Outline)
            {
                using (Pen cp = new Pen(curColor, 1f))
                    g.DrawRectangle(cp, (int)rx, (int)ry, (int)rw - 1, (int)rh - 1);
                return;
            }
            g.FillRectangle(BrushFor(curColor), (int)rx, (int)ry, (int)Math.Max(1, rw), (int)Math.Max(1, rh));
            if (style != TerminalCursorStyle.Block) return;
            TermCell under = snap.Cell(viewRow, snap.CursorCol);
            if (under.Char > ' ' && !char.IsSurrogate(under.Char) && !TerminalBoxDrawing.IsCustomGlyph(under.Char))
            {
                Font f = (under.Flags & TermCell.FLAG_BOLD) != 0 ? boldFont : ((under.Flags & TermCell.FLAG_ITALIC) != 0 ? italicFont : regularFont);
                TextRenderer.DrawText(g, under.Char.ToString(), f, new Point(cx, cy + textTop), effBg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void PaintScrollbar(Graphics g, TerminalViewportSnapshot snap, bool isLight)
        {
            int sbCount = snap.ScrollbackCount;
            int rows = snap.Rows;
            int trackW = 10;
            int trackX = this.ClientSize.Width - trackW - 2;
            int trackH = Math.Max(20, this.ClientSize.Height - 4);
            if (trackX <= 20) return;
            Color trackBg = isLight
                ? Color.FromArgb(isMouseInside ? 28 : 14, 100, 116, 139)
                : Color.FromArgb(isMouseInside ? 28 : 14, 148, 163, 184);
            g.FillRectangle(BrushFor(trackBg), trackX, 2, trackW, trackH);
            double visibleFraction = sbCount > 0 ? ((double)rows / Math.Max(1, sbCount + rows)) : 1.0;
            int thumbH = Math.Max(24, (int)(trackH * Math.Min(1.0, visibleFraction)));
            // the thumb follows the exact scroll position (smooth), the rows snap
            double exact = Math.Max(0, Math.Min(sbCount, scrollModel.Offset));
            double posFraction = sbCount > 0 ? (1.0 - exact / Math.Max(1, sbCount)) : 1.0;
            int thumbY = 2 + (int)Math.Round((trackH - thumbH) * posFraction);
            Color thumbCol = isLight
                ? Color.FromArgb(isMouseInside ? 140 : 85, 100, 116, 139)
                : Color.FromArgb(isMouseInside ? 120 : 75, 128, 128, 138);
            g.FillRectangle(BrushFor(thumbCol), trackX + 1, thumbY, trackW - 2, thumbH);
        }

        private void DisposeFeel()
        {
            foreach (SolidBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            if (scrollAnimTimer != null) { scrollAnimTimer.Stop(); scrollAnimTimer.Dispose(); scrollAnimTimer = null; }
            if (resizeColsTimer != null) { resizeColsTimer.Stop(); resizeColsTimer.Dispose(); resizeColsTimer = null; }
            if (syncTimeoutTimer != null) { syncTimeoutTimer.Stop(); syncTimeoutTimer.Dispose(); syncTimeoutTimer = null; }
        }
    }
}
