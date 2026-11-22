using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// WinForms editor scrollbars with the VS Code overview ruler (QA #28), same model as the Avalonia editor
    /// (codeLess.Core EditorScrollbar / EditorVisualRowIndex / EditorOverviewRuler): a 14 px vertical scrollbar at the
    /// right edge (the minimap sits left of it, like VS Code) with problems / find matches / source control / merge
    /// conflict / cursor marks, a 12 px horizontal scrollbar sized by the widest line of the document, wrap- and
    /// fold-aware thumb, drag with the grab offset kept, click-to-jump (Alt+click pages), and a slider that shows while
    /// the pointer is over the editor, while dragging and briefly after scrolling. STRICT C# 5.
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        private readonly EditorVisualRowIndex wfRowIndex = new EditorVisualRowIndex();
        private EditorScrollbarGeometry wfVGeom, wfHGeom;
        private Rectangle wfHBarRect = Rectangle.Empty;
        private bool wfVDrag, wfHDrag, wfOverVBar, wfOverHBar, wfMouseInside;
        private double wfDragGrab;
        private int wfLastTopRow = int.MinValue;
        private int wfLastScrollTick;
        private System.Windows.Forms.Timer wfSbHideTimer;
        private readonly List<OverviewRulerMark> wfRulerMarks = new List<OverviewRulerMark>();
        private object wfWidthLinesRef;
        private int wfWidthVersion = int.MinValue, wfWidthTab = -1, wfWidthCells, wfWidthFullTick;

        private static int VScrollWidthWF { get { return EditorScrollbar.VerticalWidth; } }
        private static int HScrollHeightWF { get { return EditorScrollbar.HorizontalHeight; } }
        private int VScrollXWF { get { return this.Width - VScrollWidthWF; } }
        private int MinimapLeftWF { get { return this.Width - VScrollWidthWF - MinimapWidth; } }
        /// <summary>Right edge of the text area (the minimap and the vertical scrollbar are to its right).</summary>
        private int CodeAreaRightWF { get { return this.Width - VScrollWidthWF - (EffectiveShowMinimap ? MinimapWidth : 0); } }

        private bool IsOverMinimapWF(Point p) { return EffectiveShowMinimap && p.X >= MinimapLeftWF && p.X < VScrollXWF; }
        private bool IsOverVScrollWF(Point p) { return p.X >= VScrollXWF && p.X <= this.Width; }
        private bool IsOverHScrollWF(Point p) { return wfHGeom.Visible && wfHBarRect.Contains(p); }

        // ------------------------------------------------------------------ rows
        private EditorVisualRowIndex RowIndexWF(EditorTabBuffer buf)
        {
            int cols = buf.WordWrap ? GetWrapCols() : 0;
            int tab = TabWF(buf);
            List<KeyValuePair<int, int>> folds = null;
            string sig = "";
            if (buf.FoldedLines != null && buf.FoldedLines.Count > 0)
            {
                folds = new List<KeyValuePair<int, int>>();
                List<int> headers = new List<int>(buf.FoldedLines);
                headers.Sort();
                StringBuilder sb = new StringBuilder();
                foreach (int h in headers)
                {
                    int end = FoldEndWF(buf, h);
                    if (end <= h) continue;
                    folds.Add(new KeyValuePair<int, int>(h, end));
                    sb.Append(h).Append('-').Append(end).Append(';');
                }
                sig = sb.ToString();
            }
            if (!wfRowIndex.Matches(buf.Lines, buf.EditVersion, cols, tab, sig))
            {
                EditorTabBuffer b = buf;
                wfRowIndex.Build(buf.Lines, buf.EditVersion, cols, tab, delegate(string s) { wfWrapCache.Configure(GetWrapCols(), TabWF(b), true); return wfWrapCache.Get(s).RowCount; }, folds, sig);
            }
            return wfRowIndex;
        }

        /// <summary>Total visual rows (wrap / folds) of the shown document.</summary>
        public int TotalVisualRowsWF { get { EditorTabBuffer b = owner.CurrentBuffer; return b == null ? 0 : RowIndexWF(b).TotalRows; } }

        private void ScrollToVisualRowWF(EditorTabBuffer buf, int row)
        {
            EditorVisualRowIndex idx = RowIndexWF(buf);
            int sub;
            int line = idx.LineOfRow(Math.Max(0, Math.Min(idx.TotalRows - 1, row)), out sub);
            buf.ScrollTopLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, line));
            NoteScrollActivityWF();
            owner.DismissAutocomplete();
            Invalidate();
        }

        /// <summary>Scroll by visible lines (folded regions are skipped; the wheel used to land inside them).</summary>
        private void ScrollLinesWF(EditorTabBuffer buf, int delta)
        {
            int line = buf.ScrollTopLine;
            if (buf.FoldedLines == null || buf.FoldedLines.Count == 0) line = line + delta;
            else
            {
                while (delta > 0) { int n = NextVisibleLineWF(buf, line); if (n >= buf.Lines.Count) break; line = n; delta--; }
                while (delta < 0) { int p = PrevVisibleLineWF(buf, line); if (p < 0) break; line = p; delta++; }
            }
            buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), line));
            NoteScrollActivityWF();
        }

        private int MaxLineWidthCellsWF(EditorTabBuffer buf)
        {
            int tab = TabWF(buf);
            if (ReferenceEquals(wfWidthLinesRef, buf.Lines) && wfWidthTab == tab)
            {
                if (wfWidthVersion == buf.EditVersion) return wfWidthCells;
                // typing: only the caret lines can have grown; a full pass runs at most once a second
                if (Math.Abs(unchecked(Environment.TickCount - wfWidthFullTick)) < 1000)
                {
                    int m = wfWidthCells;
                    if (buf.CursorLine >= 0 && buf.CursorLine < buf.Lines.Count) m = Math.Max(m, EditorTextLayout.VisualWidth(buf.Lines[buf.CursorLine] ?? "", tab));
                    if (buf.ExtraCarets != null)
                        foreach (EditorCaret ec in buf.ExtraCarets)
                            if (ec.Line >= 0 && ec.Line < buf.Lines.Count) m = Math.Max(m, EditorTextLayout.VisualWidth(buf.Lines[ec.Line] ?? "", tab));
                    wfWidthCells = m;
                    wfWidthVersion = buf.EditVersion;
                    return m;
                }
            }
            wfWidthFullTick = Environment.TickCount;
            int max = 0;
            for (int i = 0; i < buf.Lines.Count; i++)
            {
                string s = buf.Lines[i] ?? "";
                if ((long)s.Length * Math.Max(2, tab) <= max) continue;
                int w = EditorTextLayout.VisualWidth(s, tab);
                if (w > max) max = w;
            }
            wfWidthLinesRef = buf.Lines;
            wfWidthVersion = buf.EditVersion;
            wfWidthTab = tab;
            wfWidthCells = max;
            return max;
        }

        /// <summary>Largest horizontal scroll offset (columns): the widest line of the whole document fits.</summary>
        private int MaxScrollLeftColWF(EditorTabBuffer buf)
        {
            if (buf == null || buf.WordWrap) return 0;
            int visCols = Math.Max(10, (CodeAreaRightWF - GutterWidth - 16) / Math.Max(6, CharWidth));
            return Math.Max(0, MaxLineWidthCellsWF(buf) + 4 - visCols);
        }

        // ------------------------------------------------------------------ auto-hide
        private void NoteScrollActivityWF()
        {
            wfLastScrollTick = Environment.TickCount;
            if (wfSbHideTimer == null)
            {
                wfSbHideTimer = new System.Windows.Forms.Timer();
                wfSbHideTimer.Interval = EditorScrollbar.AutoHideDelayMs + 50;
                wfSbHideTimer.Tick += delegate { wfSbHideTimer.Stop(); if (!IsDisposed) Invalidate(); };
            }
            wfSbHideTimer.Stop();
            wfSbHideTimer.Start();
        }

        private bool SliderShownWF
        {
            get
            {
                return wfVDrag || wfHDrag || wfOverVBar || wfOverHBar || wfMouseInside ||
                       Math.Abs(unchecked(Environment.TickCount - wfLastScrollTick)) < EditorScrollbar.AutoHideDelayMs;
            }
        }

        // ------------------------------------------------------------------ painting
        private void PaintScrollbarsWF(Graphics g, EditorTabBuffer buf, StudioTheme th)
        {
            EditorVisualRowIndex idx = RowIndexWF(buf);
            int top = idx.RowOfLine(buf.ScrollTopLine);
            if (top != wfLastTopRow)
            {
                if (wfLastTopRow != int.MinValue) NoteScrollActivityWF();
                wfLastTopRow = top;
            }
            int h = this.Height;
            int x = VScrollXWF;
            int visRows = Math.Max(1, h / Math.Max(1, LineHeight));
            wfVGeom = EditorScrollbar.Vertical(0, h, idx.TotalRows, visRows, top);
            bool show = SliderShownWF;

            using (SolidBrush border = new SolidBrush(UiTokens.OverviewRulerBorder(th)))
                g.FillRectangle(border, x, 0, 1, h);

            // overview ruler marks, same scale as the slider
            List<int> findLines = null;
            if (owner.IsFindBarVisible && owner.ActiveSearchMatches.Count > 0)
            {
                findLines = new List<int>(owner.ActiveSearchMatches.Count);
                foreach (CodeLessEditorControl.SearchMatch m in owner.ActiveSearchMatches) findLines.Add(m.Line);
            }
            List<int> cursorLines = new List<int>();
            cursorLines.Add(buf.CursorLine);
            if (buf.ExtraCarets != null) foreach (EditorCaret ec in buf.ExtraCarets) cursorLines.Add(ec.Line);
            wfRulerMarks.Clear();
            wfRulerMarks.AddRange(EditorOverviewRuler.Collect(buf, idx, findLines, cursorLines));
            if (buf.DiffHunks != null && buf.PreDiffContent != null)
            {
                foreach (EditorDiffHunk hk in buf.DiffHunks)
                {
                    if (!hk.IsPending) continue;
                    OverviewRulerMark pm = new OverviewRulerMark();
                    pm.Row = idx.RowOfLine(Math.Max(0, Math.Min(buf.Lines.Count - 1, hk.StartLine)));
                    pm.RowCount = Math.Max(1, hk.AddedLines.Count);
                    pm.Lane = EditorOverviewRuler.LaneLeft;
                    pm.Kind = EditorOverviewRuler.KindProposed;
                    wfRulerMarks.Add(pm);
                }
            }
            double ppr = wfVGeom.Visible ? wfVGeom.PixelsPerRow : Math.Min(LineHeight, (double)h / Math.Max(1, idx.TotalRows - 1 + visRows));
            int rulerX = x + 1, rulerW = VScrollWidthWF - 1;
            Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
            try
            {
                foreach (OverviewRulerMark m in wfRulerMarks)
                {
                    SolidBrush br;
                    if (!brushes.TryGetValue(m.Kind, out br)) { br = new SolidBrush(UiTokens.OverviewRulerMark(th, m.Kind)); brushes[m.Kind] = br; }
                    double lx, lw;
                    EditorOverviewRuler.LaneBounds(m.Lane, rulerW, out lx, out lw);
                    double my = m.Row * ppr, mh;
                    if (m.Kind == EditorOverviewRuler.KindCursor) { mh = UiTokens.OverviewRulerCursorHeight; my += Math.Max(0, ppr - mh) / 2; }
                    else mh = Math.Max(UiTokens.OverviewRulerMinMark, m.RowCount * ppr);
                    my = Math.Max(0, Math.Min(h - mh, my));
                    g.FillRectangle(br, (float)(rulerX + lx), (float)my, (float)lw, (float)mh);
                }
            }
            finally { foreach (SolidBrush b in brushes.Values) b.Dispose(); }

            if (show && wfVGeom.Visible)
            {
                Color c = wfVDrag ? UiTokens.ScrollbarSliderActive(th) : (wfOverVBar ? UiTokens.ScrollbarSliderHover(th) : UiTokens.ScrollbarSlider(th));
                using (SolidBrush sb = new SolidBrush(c))
                    g.FillRectangle(sb, x + 1, (float)wfVGeom.ThumbStart, VScrollWidthWF - 1, (float)wfVGeom.ThumbLength);
            }

            // horizontal: widest line of the document, hidden while wrapping
            wfHBarRect = Rectangle.Empty;
            wfHGeom = new EditorScrollbarGeometry();
            hScrollMaxCol = MaxScrollLeftColWF(buf);
            if (!buf.WordWrap)
            {
                int left = GutterWidth, right = CodeAreaRightWF;
                double viewPx = Math.Max(1, right - GutterWidth - 8);
                double contentPx = (MaxLineWidthCellsWF(buf) + 4) * (double)CharWidth;
                wfHGeom = EditorScrollbar.Horizontal(left, right - left, contentPx, viewPx, buf.ScrollLeftCol * (double)CharWidth);
                if (wfHGeom.Visible)
                {
                    wfHBarRect = new Rectangle(left, h - HScrollHeightWF, right - left, HScrollHeightWF);
                    if (show)
                    {
                        Color c = wfHDrag ? UiTokens.ScrollbarSliderActive(th) : (wfOverHBar ? UiTokens.ScrollbarSliderHover(th) : UiTokens.ScrollbarSlider(th));
                        using (SolidBrush sb = new SolidBrush(c))
                            g.FillRectangle(sb, (float)wfHGeom.ThumbStart, h - HScrollHeightWF, (float)wfHGeom.ThumbLength, HScrollHeightWF);
                    }
                }
            }
            hScrollTrackRect = wfHBarRect;
        }

        // ------------------------------------------------------------------ mouse
        /// <summary>Left button on a scrollbar: drag the slider, jump on the track (Alt+click pages). True when consumed.</summary>
        private bool TryScrollbarMouseDownWF(MouseEventArgs e)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return false;
            bool page = (Control.ModifierKeys & Keys.Alt) == Keys.Alt;
            if (IsOverVScrollWF(e.Location))
            {
                if (e.Button != MouseButtons.Left || !wfVGeom.Visible) return true;
                int hit = EditorScrollbar.HitTest(wfVGeom, e.Y);
                int visRows = Math.Max(1, this.Height / Math.Max(1, LineHeight));
                if (hit == EditorScrollbar.HitThumb) wfDragGrab = e.Y - wfVGeom.ThumbStart;
                else if (page && hit != EditorScrollbar.HitNone)
                {
                    ScrollLinesWF(buf, hit == EditorScrollbar.HitBefore ? -Math.Max(1, visRows - 1) : Math.Max(1, visRows - 1));
                    Invalidate();
                    return true;
                }
                else
                {
                    ScrollToVisualRowWF(buf, (int)Math.Round(EditorScrollbar.ValueForJump(wfVGeom, e.Y)));
                    wfDragGrab = wfVGeom.ThumbLength / 2.0;
                }
                wfVDrag = true;
                Capture = true;
                Invalidate();
                return true;
            }
            if (IsOverHScrollWF(e.Location))
            {
                if (e.Button != MouseButtons.Left) return true;
                int hit = EditorScrollbar.HitTest(wfHGeom, e.X);
                if (hit == EditorScrollbar.HitThumb) wfDragGrab = e.X - wfHGeom.ThumbStart;
                else if (page)
                {
                    int visCols = Math.Max(10, (CodeAreaRightWF - GutterWidth - 16) / Math.Max(6, CharWidth));
                    SetScrollLeftColWF(buf, buf.ScrollLeftCol + (hit == EditorScrollbar.HitBefore ? -visCols : visCols));
                    return true;
                }
                else
                {
                    SetScrollLeftColWF(buf, (int)Math.Round(EditorScrollbar.ValueForJump(wfHGeom, e.X) / Math.Max(1, CharWidth)));
                    wfDragGrab = wfHGeom.ThumbLength / 2.0;
                }
                wfHDrag = true;
                Capture = true;
                Invalidate();
                return true;
            }
            return false;
        }

        private void SetScrollLeftColWF(EditorTabBuffer buf, int col)
        {
            buf.ScrollLeftCol = Math.Max(0, Math.Min(MaxScrollLeftColWF(buf), col));
            NoteScrollActivityWF();
            Invalidate();
        }

        /// <summary>Drag / hover on the scrollbars. True when consumed (cursor already set).</summary>
        private bool TryScrollbarMouseMoveWF(MouseEventArgs e)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (!wfMouseInside) { wfMouseInside = true; Invalidate(); }
            if (buf == null) return false;
            if (wfVDrag)
            {
                ScrollToVisualRowWF(buf, (int)Math.Round(EditorScrollbar.ValueForThumbStart(wfVGeom, e.Y - wfDragGrab)));
                return true;
            }
            if (wfHDrag)
            {
                SetScrollLeftColWF(buf, (int)Math.Round(EditorScrollbar.ValueForThumbStart(wfHGeom, e.X - wfDragGrab) / Math.Max(1, CharWidth)));
                return true;
            }
            bool v = IsOverVScrollWF(e.Location), hb = !v && IsOverHScrollWF(e.Location);
            if (v != wfOverVBar || hb != wfOverHBar) { wfOverVBar = v; wfOverHBar = hb; Invalidate(); }
            if (v || hb || IsOverMinimapWF(e.Location)) { this.Cursor = Cursors.Arrow; return v || hb; }
            return false;
        }

        private void EndScrollbarDragWF()
        {
            if (wfVDrag || wfHDrag) { wfVDrag = wfHDrag = false; Capture = false; Invalidate(); }
        }

        private void OnMouseLeaveScrollbarsWF()
        {
            wfMouseInside = false;
            wfOverVBar = wfOverHBar = false;
            NoteScrollActivityWF();
            Invalidate();
        }
    }
}
