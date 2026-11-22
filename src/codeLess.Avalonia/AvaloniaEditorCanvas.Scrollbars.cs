using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code-style editor scrollbars (QA #28): a 14 px vertical scrollbar at the right edge that doubles as the
    /// overview ruler (problems, find matches, source-control changes, merge conflicts, cursors), a 12 px horizontal
    /// scrollbar when lines overflow and word wrap is off. Both are wrap-, fold- and deleted-zone-aware (the
    /// shared <see cref="EditorVisualRowIndex"/>), the slider is draggable, a track click jumps there (VS Code's default;
    /// Alt/Option+click pages), and the slider shows while the pointer is over the editor, while dragging and briefly
    /// after scrolling; the ruler marks are always visible. Also hosts the read-only "deleted lines" rows of inline diff
    /// review (QA #30).
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        public static double ScrollbarWidth => EditorScrollbar.VerticalWidth;
        public static double HorizontalScrollbarHeight => EditorScrollbar.HorizontalHeight;

        private readonly EditorVisualRowIndex rowIndex = new EditorVisualRowIndex();
        private EditorScrollbarGeometry vGeom, hGeom;
        private Rect vBarRect, hBarRect;
        private bool vDragging, hDragging, overVBar, overHBar;
        private double dragGrab;
        private long lastScrollActivityTicks;
        private int lastRenderedTopRow = int.MinValue;
        private DispatcherTimer scrollbarHideTimer;
        private List<OverviewRulerMark> lastRulerMarks = new List<OverviewRulerMark>();

        // longest line (cells) of the document, for the horizontal scroll range
        private object widthLinesRef;
        private int widthVersion = int.MinValue, widthTab = -1, widthCells;
        private long widthFullTicks;

        /// <summary>Vertical scrollbar geometry of the last frame (tests / screenshots).</summary>
        public EditorScrollbarGeometry VerticalScrollbar => vGeom;
        public EditorScrollbarGeometry HorizontalScrollbar => hGeom;
        public Rect VerticalScrollbarRect => vBarRect;
        public Rect HorizontalScrollbarRect => hBarRect;
        /// <summary>Overview-ruler marks painted in the last frame.</summary>
        public IReadOnlyList<OverviewRulerMark> LastOverviewRulerMarks => lastRulerMarks;
        /// <summary>Whether the slider was painted in the last frame (auto-hide state).</summary>
        public bool IsScrollbarSliderShown { get; private set; }

        // ---------------------------------------------------------------- visual rows
        private static string FoldSignature(EditorTabBuffer b)
        {
            if (b.FoldedBlockRanges.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in b.FoldedBlockRanges.OrderBy(k => k.Key)) sb.Append(kv.Key).Append('-').Append(kv.Value).Append(';');
            return sb.ToString();
        }

        /// <summary>The document's visual rows (wrap, folds, deleted zones), rebuilt only when one of them changed.</summary>
        internal EditorVisualRowIndex RowIndexFor(EditorTabBuffer b)
        {
            int cols = b.WordWrap ? WrapColumns : 0;
            int tab = TabFor(b);
            var zones = DeletedZones(b);
            string sig = FoldSignature(b) + (zones.Count > 0 ? "|z" + zonesPendingKey + ":" + string.Join(",", zoneRowsByLine.Select(kv => kv.Key + "=" + kv.Value)) : "");
            if (!rowIndex.Matches(b.Lines, b.EditVersion, cols, tab, sig))
            {
                Dictionary<int, int> zoneRows = null;
                if (zones.Count > 0)
                {
                    zoneRows = new Dictionary<int, int>();
                    foreach (var kv in zoneRowsByLine) zoneRows[kv.Key] = DeletedZoneRows(b, kv.Key);
                }
                rowIndex.Build(b.Lines, b.EditVersion, cols, tab, s => WrapOfText(b, s).RowCount, b.FoldedBlockRanges, sig, zoneRows);
            }
            return rowIndex;
        }

        /// <summary>Visual row at the top of the viewport (negative wrap rows are inside a deleted zone).</summary>
        internal int TopVisualRow(EditorTabBuffer b)
        {
            var idx = RowIndexFor(b);
            NormalizeScroll(b);
            return Math.Max(0, idx.RowOfLine(b.ScrollTopLine) + b.ScrollTopWrapRow);
        }

        /// <summary>Scrolls so that visual row <paramref name="row"/> is at the top.</summary>
        public void ScrollToVisualRow(int row)
        {
            var b = Buffer;
            if (b == null) return;
            var idx = RowIndexFor(b);
            int line = idx.LineOfRow(Math.Max(0, Math.Min(idx.TotalRows - 1, row)), out int sub);
            b.ScrollTopLine = line;
            b.ScrollTopWrapRow = sub;
            NormalizeScroll(b);
            NoteScrollActivity();
            InvalidateVisual();
        }

        /// <summary>Total visual rows of the shown document (tests).</summary>
        public int TotalVisualRows => Buffer == null ? 0 : RowIndexFor(Buffer).TotalRows;

        private int VisibleRowCount => Math.Max(1, (int)(Math.Max(1, Bounds.Height) / LineHeight));

        /// <summary>Widest line of the document in cells (cached per edit / tab size).</summary>
        internal int MaxLineWidthCells(EditorTabBuffer b)
        {
            int tab = TabFor(b);
            if (ReferenceEquals(widthLinesRef, b.Lines) && widthTab == tab)
            {
                if (widthVersion == b.EditVersion) return widthCells;
                // typing: edits happen at the carets, so only their lines can have grown; a full pass (a shrunk widest
                // line) runs at most once a second, keeping keystrokes O(1) on big files
                if (Environment.TickCount64 - widthFullTicks < 1000)
                {
                    int m = widthCells;
                    var ls = b.Lines;
                    if (b.CursorLine >= 0 && b.CursorLine < ls.Count) m = Math.Max(m, EditorTextLayout.VisualWidth(ls[b.CursorLine] ?? "", tab));
                    foreach (var ec in ExtraCursors) if (ec.Line >= 0 && ec.Line < ls.Count) m = Math.Max(m, EditorTextLayout.VisualWidth(ls[ec.Line] ?? "", tab));
                    widthCells = m;
                    widthVersion = b.EditVersion;
                    return m;
                }
            }
            widthFullTicks = Environment.TickCount64;
            int max = 0;
            var lines = b.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = lines[i] ?? "";
                if ((long)s.Length * Math.Max(2, tab) <= max) continue; // cannot be wider than the widest so far
                int w = EditorTextLayout.VisualWidth(s, tab);
                if (w > max) max = w;
            }
            widthLinesRef = b.Lines;
            widthVersion = b.EditVersion;
            widthTab = tab;
            widthCells = max;
            return max;
        }

        /// <summary>Horizontal scroll range in pixels (0 while wrapping or when everything fits).</summary>
        internal double MaxScrollLeftPx(EditorTabBuffer b)
        {
            if (b == null || b.WordWrap) return 0;
            double content = MaxLineWidthCells(b) * CharWidth + CharWidth * 4;
            double view = Math.Max(1, CodeAreaRight - GutterWidth - 8);
            return Math.Max(0, content - view);
        }

        // ---------------------------------------------------------------- auto-hide
        private void NoteScrollActivity()
        {
            lastScrollActivityTicks = Environment.TickCount64;
            // only a visible editor in an open window needs the repaint that hides the slider (no timers for closed windows)
            if (global::Avalonia.VisualTree.VisualExtensions.GetVisualRoot(this) == null || !IsEffectivelyVisible || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
            if (scrollbarHideTimer == null)
            {
                scrollbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EditorScrollbar.AutoHideDelayMs + 50) };
                scrollbarHideTimer.Tick += (s, e) =>
                {
                    scrollbarHideTimer.Stop();
                    if (global::Avalonia.VisualTree.VisualExtensions.GetVisualRoot(this) != null && IsEffectivelyVisible) InvalidateVisual();
                };
            }
            scrollbarHideTimer.Stop();
            scrollbarHideTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            scrollbarHideTimer?.Stop(); // never tick for a closed window
        }

        private bool SliderShouldShow =>
            vDragging || hDragging || overVBar || overHBar || IsPointerOver ||
            Environment.TickCount64 - lastScrollActivityTicks < EditorScrollbar.AutoHideDelayMs;

        // ---------------------------------------------------------------- painting
        private void RenderScrollbars(DrawingContext context, EditorTabBuffer b, double w, double h)
        {
            var idx = RowIndexFor(b);
            int top = TopVisualRow(b);
            if (top != lastRenderedTopRow)
            {
                if (lastRenderedTopRow != int.MinValue) NoteScrollActivity();
                lastRenderedTopRow = top;
            }
            double x = w - ScrollbarWidth;
            vBarRect = new Rect(x, 0, ScrollbarWidth, h);
            vGeom = EditorScrollbar.Vertical(0, h, idx.TotalRows, VisibleRowCount, top);
            bool show = SliderShouldShow;
            IsScrollbarSliderShown = show && vGeom.Visible;

            // the ruler: transparent track with VS Code's 1 px border on its left edge
            context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.OverviewRulerBorder(Theme))), new Rect(x, 0, 1, h));

            // overview marks (always visible), on the same scale as the slider
            var findLines = owner.FindMatches.Count > 0 ? owner.FindMatches.Select(m => m.Line) : null;
            var cursorLines = new List<int> { b.CursorLine };
            foreach (var ec in ExtraCursors) cursorLines.Add(ec.Line);
            lastRulerMarks = EditorOverviewRuler.Collect(b, idx, findLines, cursorLines);
            AddProposedMarks(b, idx, lastRulerMarks);
            double ppr = vGeom.PixelsPerRow;
            if (!vGeom.Visible) ppr = idx.TotalRows > 0 ? Math.Min(LineHeight, h / Math.Max(1, idx.TotalRows - 1 + VisibleRowCount)) : 0;
            double rulerX = x + 1, rulerW = ScrollbarWidth - 1;
            var brushes = new Dictionary<int, IBrush>();
            foreach (var m in lastRulerMarks)
            {
                if (!brushes.TryGetValue(m.Kind, out var br))
                    brushes[m.Kind] = br = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.OverviewRulerMark(Theme, m.Kind)));
                EditorOverviewRuler.LaneBounds(m.Lane, rulerW, out double lx, out double lw);
                double my = m.Row * ppr;
                double mh;
                if (m.Kind == EditorOverviewRuler.KindCursor) { mh = UiTokens.OverviewRulerCursorHeight; my = my + Math.Max(0, ppr - mh) / 2; }
                else mh = Math.Max(UiTokens.OverviewRulerMinMark, m.RowCount * ppr);
                my = Math.Max(0, Math.Min(h - mh, my));
                context.FillRectangle(br, new Rect(rulerX + lx, my, lw, mh));
            }

            if (show && vGeom.Visible)
            {
                var c = vDragging ? UiTokens.ScrollbarSliderActive(Theme) : (overVBar ? UiTokens.ScrollbarSliderHover(Theme) : UiTokens.ScrollbarSlider(Theme));
                context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(c)), new Rect(x + 1, vGeom.ThumbStart, ScrollbarWidth - 1, vGeom.ThumbLength));
            }

            // horizontal: only without word wrap and when a line is wider than the viewport
            hBarRect = default;
            hGeom = default;
            if (!b.WordWrap)
            {
                double left = GutterWidth, right = CodeAreaRight;
                double viewPx = Math.Max(1, right - GutterWidth - 8);
                double contentPx = MaxLineWidthCells(b) * CharWidth + CharWidth * 4;
                hGeom = EditorScrollbar.Horizontal(left, right - left, contentPx, viewPx, b.ScrollLeftPx);
                if (hGeom.Visible)
                {
                    hBarRect = new Rect(left, h - HorizontalScrollbarHeight, right - left, HorizontalScrollbarHeight);
                    if (show)
                    {
                        var c = hDragging ? UiTokens.ScrollbarSliderActive(Theme) : (overHBar ? UiTokens.ScrollbarSliderHover(Theme) : UiTokens.ScrollbarSlider(Theme));
                        context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(c)), new Rect(hGeom.ThumbStart, h - HorizontalScrollbarHeight, hGeom.ThumbLength, HorizontalScrollbarHeight));
                    }
                }
            }
        }

        /// <summary>Pending review hunks (added lines and deleted zones) in the left lane, like VS Code's inline diff.</summary>
        private void AddProposedMarks(EditorTabBuffer b, EditorVisualRowIndex idx, List<OverviewRulerMark> marks)
        {
            if (b.DiffHunks == null || b.PreDiffContent == null) return;
            foreach (var hk in b.DiffHunks)
            {
                if (!hk.IsPending) continue;
                int line = Math.Max(0, Math.Min(b.Lines.Count - 1, hk.StartLine));
                int zone = hk.StartLine < b.Lines.Count ? DeletedZoneRows(b, hk.StartLine) : 0;
                int row0 = idx.RowOfLine(line) - zone;
                int rows = zone + Math.Max(0, hk.AddedLines.Count);
                if (hk.StartLine >= b.Lines.Count) { row0 = idx.TotalRows - 1; rows = 1; }
                marks.Add(new OverviewRulerMark { Row = Math.Max(0, row0), RowCount = Math.Max(1, rows), Lane = EditorOverviewRuler.LaneLeft, Kind = EditorOverviewRuler.KindProposed });
            }
        }

        /// <summary>One read-only row of a deleted-lines zone: red band, "−" in the gutter, the removed text highlighted.</summary>
        private void DrawDeletedRow(DrawingContext context, EditorTabBuffer b, EditorVisualRow vr, double y, double codeBaseX, double codeAreaRight)
        {
            context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.DiffRemovedGutterBackground(Theme))), new Rect(0, y, GutterWidth, LineHeight));
            context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.DiffRemovedLineBackground(Theme))), new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight));
            if (vr.Row == 0)
            {
                var minus = new FormattedText("−", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedFontRegular, FontSizePx - 1.5,
                    new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.OverviewRulerMark(Theme, EditorOverviewRuler.KindDeleted), 255)));
                context.DrawText(minus, new Point(GutterWidth - 30, y + (LineHeight - minus.Height) / 2.0));
            }
            string t = vr.DeletedText ?? "";
            int s = vr.Start, e = Math.Min(t.Length, vr.End);
            int startVcol = vr.StartVcol;
            if (!b.WordWrap && e - s > 2000)
            {
                int first = Math.Max(0, (int)(b.ScrollLeftPx / CharWidth) - 8);
                s = Math.Min(e, EditorTextLayout.CharIndexForVisualColumn(t, first, TabFor(b)));
                startVcol = EditorTextLayout.VisualColumn(t, s, TabFor(b));
                e = Math.Min(e, s + 2000);
            }
            if (e <= s) return;
            // whole-line tokens cut to the slice (a slice starting inside a string must not flip the quote parity)
            var tokens = SliceTokensOfText(t, b.Language, s, e);
            using (context.PushClip(new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight)))
                DrawTokenRun(context, tokens, codeBaseX, y, startVcol, codeAreaRight);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, List<CodeToken>> textTokenCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<string, List<CodeToken>>();

        /// <summary>
        /// Styled tokens for [s, e) of a line that is not part of the buffer (deleted rows): tokenized from the start of
        /// the line once per string instance (cached by reference), then cut to the slice.
        /// </summary>
        internal List<SyntaxTokenSpan> SliceTokensOfText(string text, string language, int s, int e)
        {
            text ??= "";
            var toks = textTokenCache.GetValue(text, x => FallbackTokenizer.Tokenize(x, language));
            var spans = FallbackTokenStyles.SliceSpans(toks, text.Length, s, e, Theme, false);
            var res = new List<SyntaxTokenSpan>(spans.Count);
            foreach (var sp in spans)
                res.Add(new SyntaxTokenSpan { Text = text.Substring(sp.Start, sp.Length), Color = AvaloniaColorHelper.ToAvColor(sp.Color), IsBold = sp.Bold, IsItalic = sp.Italic });
            return res;
        }

        /// <summary>After navigating to a hunk: if its deleted zone starts above the viewport, scroll it into view (VS Code reveals the whole change).</summary>
        internal void RevealDeletedZone(int line)
        {
            var b = Buffer;
            if (b == null || line < 0 || line >= b.Lines.Count) return;
            int z = DeletedZoneRows(b, line);
            if (z == 0) return;
            NormalizeScroll(b);
            int visRows = Bounds.Height > 0 ? Math.Max(1, (int)(Bounds.Height / LineHeight) - 1) : 30;
            int d = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, line, -z, visRows + 1);
            if (d >= 0 && d + z < visRows) return;
            if (d < 0 || z >= visRows) { b.ScrollTopLine = line; b.ScrollTopWrapRow = -z; ScrollByRows(b, -Math.Min(2, Math.Max(0, visRows - z - 1))); }
            InvalidateVisual();
        }

        /// <summary>Whether the screen row at <paramref name="y"/> (last frame) is a read-only deleted row.</summary>
        internal bool IsDeletedRowAt(double y)
        {
            int idx = (int)(y / LineHeight);
            return idx >= 0 && idx < lastFrameRows.Count && lastFrameRows[idx].IsDeleted;
        }

        // ---------------------------------------------------------------- pointer
        private bool IsOverVerticalScrollbar(Point pt) => pt.X >= Bounds.Width - ScrollbarWidth && pt.X <= Bounds.Width && pt.Y >= 0 && pt.Y <= Bounds.Height;
        private bool IsOverHorizontalScrollbar(Point pt) => hGeom.Visible && hBarRect.Contains(pt);

        /// <summary>Left button on a scrollbar: drag the slider, or jump (page with Alt / Option) on the track.</summary>
        private bool TryHandleScrollbarPressed(Point pt, PointerPressedEventArgs e)
        {
            var b = Buffer;
            if (b == null) return false;
            bool page = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            if (IsOverVerticalScrollbar(pt))
            {
                if (vGeom.Visible)
                {
                    int hit = EditorScrollbar.HitTest(vGeom, pt.Y);
                    if (hit == EditorScrollbar.HitThumb) dragGrab = pt.Y - vGeom.ThumbStart;
                    else if (page && hit != EditorScrollbar.HitNone)
                    {
                        ScrollByRows(b, hit == EditorScrollbar.HitBefore ? -Math.Max(1, VisibleRowCount - 1) : Math.Max(1, VisibleRowCount - 1));
                        NoteScrollActivity();
                        InvalidateVisual();
                        e.Handled = true;
                        return true;
                    }
                    else
                    {
                        ScrollToVisualRow((int)Math.Round(EditorScrollbar.ValueForJump(vGeom, pt.Y)));
                        dragGrab = vGeom.ThumbLength / 2.0;
                    }
                    vDragging = true;
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                }
                e.Handled = true;
                return true;
            }
            if (IsOverHorizontalScrollbar(pt))
            {
                int hit = EditorScrollbar.HitTest(hGeom, pt.X);
                if (hit == EditorScrollbar.HitThumb) dragGrab = pt.X - hGeom.ThumbStart;
                else if (page)
                {
                    double viewPx = Math.Max(1, CodeAreaRight - GutterWidth - 8);
                    SetScrollLeft(b, b.ScrollLeftPx + (hit == EditorScrollbar.HitBefore ? -viewPx : viewPx));
                    e.Handled = true;
                    return true;
                }
                else
                {
                    SetScrollLeft(b, EditorScrollbar.ValueForJump(hGeom, pt.X));
                    dragGrab = hGeom.ThumbLength / 2.0;
                }
                hDragging = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return true;
            }
            return false;
        }

        private void SetScrollLeft(EditorTabBuffer b, double px)
        {
            b.ScrollLeftPx = Math.Max(0, Math.Min(MaxScrollLeftPx(b), px));
            NoteScrollActivity();
            InvalidateVisual();
        }

        /// <summary>Pointer move: slider drag or hover state; true when the scrollbar consumed it.</summary>
        private bool TryHandleScrollbarMoved(Point pt)
        {
            var b = Buffer;
            if (b == null) return false;
            if (vDragging)
            {
                double v = EditorScrollbar.ValueForThumbStart(vGeom, pt.Y - dragGrab);
                ScrollToVisualRow((int)Math.Round(v));
                return true;
            }
            if (hDragging)
            {
                SetScrollLeft(b, EditorScrollbar.ValueForThumbStart(hGeom, pt.X - dragGrab));
                return true;
            }
            bool v1 = IsOverVerticalScrollbar(pt), h1 = !v1 && IsOverHorizontalScrollbar(pt);
            if (v1 != overVBar || h1 != overHBar) { overVBar = v1; overHBar = h1; InvalidateVisual(); }
            if (v1 || h1) { SetPointerCursor(StandardCursorType.Arrow); return true; }
            return false;
        }

        private bool EndScrollbarDrag()
        {
            bool was = vDragging || hDragging;
            vDragging = hDragging = false;
            if (was) InvalidateVisual();
            return was;
        }

        private void ClearScrollbarHover()
        {
            if (overVBar || overHBar) { overVBar = overHBar = false; InvalidateVisual(); }
        }

        // ---------------------------------------------------------------- automation (tests / screenshot suite)
        /// <summary>Simulates a left click at a canvas point on a scrollbar (tests); false when the point is not on one.</summary>
        public bool ClickScrollbarAt(Point pt, bool alt = false)
        {
            var b = Buffer;
            if (b == null) return false;
            bool page = alt;
            if (IsOverVerticalScrollbar(pt) && vGeom.Visible)
            {
                int hit = EditorScrollbar.HitTest(vGeom, pt.Y);
                if (hit == EditorScrollbar.HitThumb) return true;
                if (page) ScrollByRows(b, hit == EditorScrollbar.HitBefore ? -Math.Max(1, VisibleRowCount - 1) : Math.Max(1, VisibleRowCount - 1));
                else ScrollToVisualRow((int)Math.Round(EditorScrollbar.ValueForJump(vGeom, pt.Y)));
                InvalidateVisual();
                return true;
            }
            if (IsOverHorizontalScrollbar(pt))
            {
                SetScrollLeft(b, EditorScrollbar.ValueForJump(hGeom, pt.X));
                return true;
            }
            return false;
        }

        /// <summary>Scrolls by visual rows like the mouse wheel (tests).</summary>
        public void ScrollByRowsForTest(int delta)
        {
            if (Buffer == null) return;
            ScrollByRows(Buffer, delta);
            InvalidateVisual();
        }

        /// <summary>A plain left click in the text area at a canvas point (tests): the same hit test / affinity as the pointer path.</summary>
        public void ClickAtForTest(Point pt)
        {
            var b = Buffer;
            if (b == null) return;
            ExtraCursors.Clear();
            int line = ScreenYToDocLine(pt.Y);
            int col = ScreenXToDocCol(line, pt.X);
            b.ClearSelection();
            b.CursorLine = line;
            b.CursorCol = col;
            ApplyHitAffinity(b);
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        /// <summary>Drags the vertical slider so that its top lands at <paramref name="thumbTop"/> (tests).</summary>
        public void DragVerticalSliderTo(double thumbTop) => ScrollToVisualRow((int)Math.Round(EditorScrollbar.ValueForThumbStart(vGeom, thumbTop)));
    }
}
