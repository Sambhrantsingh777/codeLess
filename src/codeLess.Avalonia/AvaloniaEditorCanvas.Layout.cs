using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>One screen row: a whole document line, or one soft-wrapped segment of it.</summary>
    public struct EditorVisualRow
    {
        public int DocLine;
        /// <summary>Index of the segment within the document line (0 = first row).</summary>
        public int Row;
        public int Start;
        public int End;
        /// <summary>Visual column the row's text starts at (the wrapped indent on continuation rows).</summary>
        public int StartVcol;
        public bool IsLastRow;
        public bool IsFirstRow => Row == 0 && !IsDeleted;
        /// <summary>
        /// A read-only row of the inline-diff "deleted lines" zone (QA #30) showing removed line
        /// <see cref="DeletedIndex"/> of the zone above <see cref="DocLine"/>; not part of the buffer.
        /// </summary>
        public bool IsDeleted;
        public int DeletedIndex;
        /// <summary>Text of the removed line on a deleted row (Start / End / StartVcol are its wrap segment).</summary>
        public string DeletedText;
        /// <summary>The zone sits after the last line (a deletion at the end of the file).</summary>
        public bool DeletedAfterLast;
    }

    /// <summary>
    /// Visual layout of the Avalonia editor: soft wrap (QA #11), folded regions, per-buffer tab size (QA #12) and the
    /// mapping between screen rows and document positions used by rendering, hit-testing and caret movement.
    /// Wrapping uses the shared core model (EditorWordWrap, VS Code's break rules, "same" wrapped indent) with a per-line
    /// cache, so only lines that become visible or change are laid out.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        /// <summary>Global editor.insertSpaces (per-file detection / .editorconfig override it through the buffer).</summary>
        public static bool InsertSpacesSetting = true;

        private readonly WrapLayoutCache wrapCache = new WrapLayoutCache();
        private EditorVisualRow hitRow;
        private bool hitRowValid;
        // caret drawn at the end of the previous row when it sits exactly on a soft break (after End / vertical moves)
        private int affinityLine = -1, affinityCol = -1;
        // sticky "desired column" of Up / Down per caret position (VS Code keeps it across short lines)
        private Dictionary<long, int> preferredVcolByPos = new Dictionary<long, int>();
        private Dictionary<long, int> nextPreferredVcolByPos;
        internal int preferredCaretVcol = -1;

        /// <summary>Effective tab size of the shown buffer (per-file detection / .editorconfig / setting).</summary>
        public int TabSize => TabFor(Buffer);
        /// <summary>Effective insert-spaces of the shown buffer.</summary>
        public bool InsertSpaces => EditorIndentation.EffectiveInsertSpaces(Buffer, InsertSpacesSetting);

        private int TabFor(EditorTabBuffer b) => EditorIndentation.EffectiveTabSize(b, DisplayTabSize);

        /// <summary>Visual column of a character index on a line, with the shown buffer's tab size.</summary>
        internal int Vcol(string line, int col) => EditorTextLayout.VisualColumn(line, col, TabSize);

        public bool IsWrapping => Buffer != null && Buffer.WordWrap;

        /// <summary>Cells per row when wrapping (viewport width, like VS Code's wordWrap "on").</summary>
        public int WrapColumns
        {
            get
            {
                double w = (Bounds.Width > 0 ? CodeAreaRight : 900) - GutterWidth - 8 - 14;
                return Math.Max(10, (int)Math.Floor(w / Math.Max(1.0, CharWidth)));
            }
        }

        /// <summary>Lines laid out by the wrap cache so far (tests: proves re-wrap is incremental).</summary>
        public long WrapComputations => wrapCache.Computations;

        internal WrapLayout WrapOf(EditorTabBuffer b, int docLine)
        {
            if (b == null || !b.WordWrap || docLine < 0 || docLine >= b.Lines.Count) return WrapLayout.Single;
            wrapCache.Configure(WrapColumns, TabFor(b), true);
            return wrapCache.Get(b.Lines[docLine]);
        }

        internal EditorVisualRow MakeRow(EditorTabBuffer b, int docLine, int row)
        {
            var wl = WrapOf(b, docLine);
            int len = (b.Lines[docLine] ?? "").Length;
            row = Math.Max(0, Math.Min(wl.RowCount - 1, row));
            return new EditorVisualRow
            {
                DocLine = docLine,
                Row = row,
                Start = wl.RowStarts[row],
                End = wl.RowEnd(row, len),
                StartVcol = wl.RowStartVcol(row),
                IsLastRow = row == wl.RowCount - 1,
            };
        }

        // ---------------------------------------------------------------- folds
        internal static int NextVisibleLine(EditorTabBuffer b, int docLine)
        {
            if (b.FoldedBlockRanges.Count > 0 && b.FoldedBlockRanges.TryGetValue(docLine, out int end) && end > docLine) return end + 1;
            return docLine + 1;
        }

        internal static int PrevVisibleLine(EditorTabBuffer b, int docLine)
        {
            int l = docLine - 1;
            if (l < 0) return -1;
            if (b.FoldedBlockRanges.Count > 0)
            {
                int h = EditorFoldState.HiddenBy(b, l);
                if (h >= 0) l = h;
            }
            return l;
        }

        /// <summary>First visible line at or above <paramref name="docLine"/> (a hidden line maps to its fold header).</summary>
        internal static int VisibleLineAtOrAbove(EditorTabBuffer b, int docLine)
        {
            docLine = Math.Max(0, Math.Min(b.Lines.Count - 1, docLine));
            if (b.FoldedBlockRanges.Count == 0) return docLine;
            int h = EditorFoldState.HiddenBy(b, docLine);
            return h >= 0 ? h : docLine;
        }

        // ---------------------------------------------------------------- viewport rows
        /// <summary>
        /// Normalizes the scroll position (folded / out of range lines, wrap sub-row). A negative
        /// <see cref="EditorTabBuffer.ScrollTopWrapRow"/> starts the viewport inside the deleted-lines zone above
        /// <see cref="EditorTabBuffer.ScrollTopLine"/> (QA #30).
        /// </summary>
        internal void NormalizeScroll(EditorTabBuffer b)
        {
            if (b == null) return;
            int top = VisibleLineAtOrAbove(b, b.ScrollTopLine);
            if (top != b.ScrollTopLine) { b.ScrollTopLine = top; b.ScrollTopWrapRow = 0; }
            int zone = DeletedZoneRows(b, top);
            int rows = b.WordWrap ? WrapOf(b, top).RowCount : 1;
            b.ScrollTopWrapRow = Math.Max(-zone, Math.Min(rows - 1, b.ScrollTopWrapRow));
        }

        // ---------------------------------------------------------------- inline diff "deleted lines" zones (QA #30)
        private object zonesHunksRef;
        private int zonesHunkCount = -1, zonesLineCount = -1, zonesWrapCols = -1, zonesTab = -1;
        private bool zonesWrap;
        private Dictionary<int, List<string>> zonesByLine = new Dictionary<int, List<string>>();
        private Dictionary<int, int> zoneRowsByLine = new Dictionary<int, int>();

        /// <summary>Removed lines of pending review hunks keyed by the buffer line they are shown above (Lines.Count = after the last line).</summary>
        internal Dictionary<int, List<string>> DeletedZones(EditorTabBuffer b)
        {
            if (b == null || b.DiffHunks == null || b.DiffHunks.Count == 0 || b.PreDiffContent == null)
            {
                if (zonesByLine.Count > 0) { zonesByLine = new Dictionary<int, List<string>>(); zoneRowsByLine = new Dictionary<int, int>(); }
                zonesHunksRef = null;
                return zonesByLine;
            }
            int cols = b.WordWrap ? WrapColumns : 0;
            int tab = TabFor(b);
            if (ReferenceEquals(zonesHunksRef, b.DiffHunks) && zonesHunkCount == b.DiffHunks.Count && zonesLineCount == b.Lines.Count &&
                zonesWrap == b.WordWrap && zonesWrapCols == cols && zonesTab == tab && !HunkPendingChanged(b))
                return zonesByLine;
            zonesHunksRef = b.DiffHunks;
            zonesHunkCount = b.DiffHunks.Count;
            zonesLineCount = b.Lines.Count;
            zonesWrap = b.WordWrap;
            zonesWrapCols = cols;
            zonesTab = tab;
            var map = new Dictionary<int, List<string>>();
            var rows = new Dictionary<int, int>();
            zonesPendingKey = PendingKey(b);
            foreach (var h in b.DiffHunks)
            {
                if (!h.IsPending || h.RemovedLines == null || h.RemovedLines.Count == 0) continue;
                int anchor = Math.Max(0, Math.Min(b.Lines.Count, h.StartLine));
                if (!map.TryGetValue(anchor, out var list)) { list = new List<string>(); map[anchor] = list; }
                list.AddRange(h.RemovedLines);
            }
            foreach (var kv in map)
            {
                int n = 0;
                foreach (var t in kv.Value) n += b.WordWrap ? WrapOfText(b, t).RowCount : 1;
                rows[kv.Key] = n;
            }
            zonesByLine = map;
            zoneRowsByLine = rows;
            return zonesByLine;
        }

        private string zonesPendingKey = "";

        private static string PendingKey(EditorTabBuffer b)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var h in b.DiffHunks) sb.Append(h.IsPending ? '1' : '0').Append(h.StartLine).Append(',');
            return sb.ToString();
        }

        private bool HunkPendingChanged(EditorTabBuffer b) => !string.Equals(zonesPendingKey, PendingKey(b), StringComparison.Ordinal);

        /// <summary>Screen rows of the deleted zone shown above <paramref name="line"/> (0 when none or the line is folded away).</summary>
        internal int DeletedZoneRows(EditorTabBuffer b, int line)
        {
            if (b == null || b.DiffHunks == null || b.DiffHunks.Count == 0 || b.PreDiffContent == null) return 0;
            DeletedZones(b);
            if (zoneRowsByLine.Count == 0 || !zoneRowsByLine.TryGetValue(line, out int n)) return 0;
            if (line < b.Lines.Count && b.FoldedBlockRanges.Count > 0 && EditorFoldState.HiddenBy(b, line) >= 0) return 0;
            return n;
        }

        internal WrapLayout WrapOfText(EditorTabBuffer b, string text)
        {
            if (b == null || !b.WordWrap) return WrapLayout.Single;
            wrapCache.Configure(WrapColumns, TabFor(b), true);
            return wrapCache.Get(text ?? "");
        }

        /// <summary>The rows of the zone above <paramref name="line"/> from zone row <paramref name="fromRow"/> on.</summary>
        private void AppendZoneRows(EditorTabBuffer b, int line, int fromRow, List<EditorVisualRow> rows, int maxRows)
        {
            if (!zonesByLine.TryGetValue(line, out var removed) || DeletedZoneRows(b, line) == 0) return;
            bool afterLast = line >= b.Lines.Count;
            int anchor = afterLast ? b.Lines.Count - 1 : line;
            int k = 0;
            for (int i = 0; i < removed.Count && rows.Count < maxRows; i++)
            {
                string t = removed[i] ?? "";
                var wl = WrapOfText(b, t);
                for (int r = 0; r < wl.RowCount && rows.Count < maxRows; r++, k++)
                {
                    if (k < fromRow) continue;
                    rows.Add(new EditorVisualRow
                    {
                        DocLine = anchor, Row = r, Start = wl.RowStarts[r], End = wl.RowEnd(r, t.Length), StartVcol = wl.RowStartVcol(r),
                        IsLastRow = r == wl.RowCount - 1, IsDeleted = true, DeletedIndex = i, DeletedText = t, DeletedAfterLast = afterLast
                    });
                }
            }
        }

        /// <summary>The rows on screen from the scroll position (at most <paramref name="maxRows"/>).</summary>
        internal List<EditorVisualRow> BuildViewportRows(EditorTabBuffer b, int maxRows)
        {
            var rows = new List<EditorVisualRow>(Math.Max(4, maxRows));
            if (b == null || b.Lines.Count == 0) return rows;
            NormalizeScroll(b);
            bool zones = DeletedZones(b).Count > 0;
            int line = b.ScrollTopLine;
            int sub = b.ScrollTopWrapRow;
            while (rows.Count < maxRows && line >= 0 && line < b.Lines.Count)
            {
                if (zones)
                {
                    // the read-only removed lines of a pending hunk come right above its first line (VS Code inline diff)
                    int z = DeletedZoneRows(b, line);
                    if (z > 0 && sub < 0) AppendZoneRows(b, line, z + sub, rows, maxRows);
                    else if (z > 0 && line != b.ScrollTopLine) AppendZoneRows(b, line, 0, rows, maxRows);
                }
                var wl = WrapOf(b, line);
                int len = (b.Lines[line] ?? "").Length;
                for (int r = Math.Max(0, sub); r < wl.RowCount && rows.Count < maxRows; r++)
                {
                    rows.Add(new EditorVisualRow
                    {
                        DocLine = line, Row = r, Start = wl.RowStarts[r], End = wl.RowEnd(r, len),
                        StartVcol = wl.RowStartVcol(r), IsLastRow = r == wl.RowCount - 1
                    });
                }
                sub = 0;
                line = NextVisibleLine(b, line);
            }
            if (zones && line >= b.Lines.Count) AppendZoneRows(b, b.Lines.Count, 0, rows, maxRows); // deletion at the end of the file
            return rows;
        }

        private int ViewportRowCapacity => Math.Max(1, (int)(Math.Max(1, Bounds.Height) / LineHeight) + 2);

        /// <summary>Row under a canvas y (clamped to the last row); also remembered for the following X hit test.</summary>
        private EditorVisualRow RowAtY(double y)
        {
            var b = Buffer;
            int idx = Math.Max(0, (int)Math.Floor((y + TopRowOffsetPx) / LineHeight));
            var rows = BuildViewportRows(b, idx + 1);
            if (rows.Count == 0) return new EditorVisualRow { DocLine = 0, End = 0, IsLastRow = true };
            var hit = rows[Math.Min(idx, rows.Count - 1)];
            if (!hit.IsDeleted) return hit;
            // a deleted (read-only) row: the caret goes to the start of the line below the zone, or the end of the
            // last line for a zone after it - it never enters the removed text
            int dl = hit.DocLine;
            if (hit.DeletedAfterLast)
            {
                var last = MakeRow(b, dl, WrapOf(b, dl).RowCount - 1);
                last.Start = last.End = (b.Lines[dl] ?? "").Length;
                return last;
            }
            var first = MakeRow(b, dl, 0);
            first.End = first.Start;
            return first;
        }

        /// <summary>Character boundary nearest to x on a row.</summary>
        private int ColForX(EditorTabBuffer b, EditorVisualRow row, double x)
        {
            string text = b.Lines[row.DocLine] ?? "";
            double rel = Math.Max(0, x - GutterWidth - 8 + (b.WordWrap ? 0 : b.ScrollLeftPx));
            int col = EditorTextLayout.CharIndexForVisualColumnInRange(text, row.Start, row.End, rel / CharWidth, TabFor(b), row.StartVcol);
            // the boundary at the end of a continuation row belongs to the next row: stay inside this one
            if (!row.IsLastRow && col >= row.End) col = Math.Max(row.Start, row.End - (row.End - row.Start > 0 ? 1 : 0));
            return Math.Max(0, Math.Min(text.Length, col));
        }

        /// <summary>X (canvas) of a character index on a row.</summary>
        internal double RowColX(EditorTabBuffer b, EditorVisualRow row, int col, double codeBaseX)
        {
            string text = b.Lines[row.DocLine] ?? "";
            int c = row.IsLastRow ? Math.Max(row.Start, col) : Math.Max(row.Start, Math.Min(row.End, col));
            return codeBaseX + EditorTextLayout.VisualColumnFrom(text, row.Start, c, TabFor(b), row.StartVcol) * CharWidth;
        }

        /// <summary>The row a caret position is drawn on (soft-break affinity: the next row, except right after End).</summary>
        internal int CaretRowOf(EditorTabBuffer b, int line, int col, bool primary)
        {
            var wl = WrapOf(b, line);
            if (wl.RowCount == 1) return 0;
            bool preferEnd = primary && line == affinityLine && col == affinityCol;
            return wl.RowOf(col, preferEnd);
        }

        private void SetAffinityEndOfRow(int line, int col) { affinityLine = line; affinityCol = col; }

        /// <summary>
        /// Number of screen rows from (l0, r0) forward to (l1, r1), capped at <paramref name="limit"/>; -1 when (l1, r1)
        /// is above. Deleted-zone rows count (they take screen space); negative rows are positions inside a zone.
        /// </summary>
        private int RowDistance(EditorTabBuffer b, int l0, int r0, int l1, int r1, int limit)
        {
            if (l1 < l0 || (l1 == l0 && r1 < r0)) return -1;
            int n = 0, line = l0, r = r0;
            while (n <= limit)
            {
                if (line == l1) return n + (r1 - r);
                n += WrapOf(b, line).RowCount - r;
                r = 0;
                int next = NextVisibleLine(b, line);
                if (next > l1 || next >= b.Lines.Count) return n; // target hidden / past end: treat as just below
                line = next;
                r = -DeletedZoneRows(b, line); // the zone above the next line is on screen before its first row
            }
            return limit + 1;
        }

        /// <summary>
        /// Moves (line, row) by <paramref name="delta"/> visual rows (clamped to the document). With
        /// <paramref name="includeZones"/> the rows of deleted zones are stepped through as negative rows (scrolling);
        /// caret motion skips them (the caret never enters removed text).
        /// </summary>
        private void StepRows(EditorTabBuffer b, ref int line, ref int row, int delta, bool includeZones = false)
        {
            while (delta > 0)
            {
                int rc = WrapOf(b, line).RowCount;
                if (row + 1 < rc) { row++; delta--; continue; }
                int next = NextVisibleLine(b, line);
                if (next >= b.Lines.Count) break;
                int z = includeZones ? DeletedZoneRows(b, next) : 0;
                line = next; row = z > 0 ? -z : 0; delta--;
            }
            while (delta < 0)
            {
                if (row > 0 || (includeZones && row > -DeletedZoneRows(b, line))) { row--; delta++; continue; }
                int prev = PrevVisibleLine(b, line);
                if (prev < 0) break;
                line = prev; row = WrapOf(b, prev).RowCount - 1; delta++;
            }
            if (!includeZones && row < 0) row = 0;
        }

        /// <summary>Scrolls by visual rows (wheel, Ctrl+Up/Down, drag auto-scroll).</summary>
        internal void ScrollByRows(EditorTabBuffer b, int delta)
        {
            if (b == null || delta == 0) return;
            NormalizeScroll(b);
            int line = b.ScrollTopLine, row = b.ScrollTopWrapRow;
            StepRows(b, ref line, ref row, delta, includeZones: true);
            b.ScrollTopLine = line;
            b.ScrollTopWrapRow = row;
        }

        /// <summary>Keeps the primary caret on screen in rows (wrap- and fold-aware); expands a fold that hides it.</summary>
        private void EnsureCaretRowVisible(EditorTabBuffer b)
        {
            int visRows = Math.Max(1, (int)(Bounds.Height / LineHeight) - 1);
            if (Bounds.Height <= 0) visRows = 30;
            // VS Code reveals a caret moved into a collapsed region by expanding it
            if (b.FoldedBlockRanges.Count > 0)
            {
                int guard = 0;
                int h;
                while ((h = EditorFoldState.HiddenBy(b, b.CursorLine)) >= 0 && guard++ < 64) b.FoldedBlockRanges.Remove(h);
            }
            NormalizeScroll(b);
            int cRow = CaretRowOf(b, b.CursorLine, b.CursorCol, true);
            int dist = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, b.CursorLine, cRow, visRows + 1);
            if (dist == 0 && b.ScrollTopFraction > 0) b.ScrollTopFraction = 0; // the caret row was partly scrolled out at the top
            if (dist < 0)
            {
                b.ScrollTopLine = b.CursorLine;
                b.ScrollTopWrapRow = cRow;
            }
            else if (dist >= visRows)
            {
                int line = b.CursorLine, row = cRow;
                StepRows(b, ref line, ref row, -(visRows - 1), includeZones: true);
                b.ScrollTopLine = line;
                b.ScrollTopWrapRow = row;
            }
        }

        /// <summary>Scrolls a document line into view (rows / folds aware) without moving the caret; animated when smooth scrolling is on.</summary>
        internal void RevealLine(EditorTabBuffer b, int line)
        {
            if (b == null || line < 0 || line >= b.Lines.Count) return;
            RunReveal(b, () => RevealLineCore(b, line), animate: true);
        }

        private void RevealLineCore(EditorTabBuffer b, int line)
        {
            if (b == null || line < 0 || line >= b.Lines.Count) return;
            int visRows = Bounds.Height > 0 ? Math.Max(1, (int)(Bounds.Height / LineHeight) - 1) : 30;
            if (EditorFoldState.HiddenBy(b, line) >= 0) return;
            NormalizeScroll(b);
            int dist = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, line, 0, visRows + 1);
            if (dist < 0) { b.ScrollTopLine = line; b.ScrollTopWrapRow = 0; }
            else if (dist >= visRows)
            {
                int l = line, r = 0;
                StepRows(b, ref l, ref r, -(visRows / 2), includeZones: true);
                b.ScrollTopLine = l;
                b.ScrollTopWrapRow = r;
            }
        }

        // ---------------------------------------------------------------- caret motion helpers
        private static long PosKey(int line, int col) => ((long)line << 32) | (uint)col;

        /// <summary>Up / Down by visual rows with a sticky desired column (VS Code); Up on the first row goes to column 0, Down on the last to the end.</summary>
        private void MoveCaretByRows(EditorTabBuffer b, int delta, bool isPrimary)
        {
            int line = b.CursorLine, col = b.CursorCol;
            string text = b.Lines[line] ?? "";
            int tab = TabFor(b);
            int r = CaretRowOf(b, line, col, isPrimary);
            var wl = WrapOf(b, line);
            int x;
            if (!preferredVcolByPos.TryGetValue(PosKey(line, col), out x))
                x = EditorTextLayout.VisualColumnFrom(text, wl.RowStarts[r], col, tab, wl.RowStartVcol(r));
            int targetLine = line, targetRow = r;
            StepRows(b, ref targetLine, ref targetRow, delta);
            bool moved = targetLine != line || targetRow != r;
            int newCol;
            bool endAffinity = false;
            if (!moved)
            {
                newCol = delta < 0 ? 0 : text.Length; // VS Code: Up on the first line -> start, Down on the last -> end
                targetRow = CaretRowOf(b, line, newCol, false);
            }
            else
            {
                var row = MakeRow(b, targetLine, targetRow);
                string t2 = b.Lines[targetLine] ?? "";
                newCol = EditorTextLayout.CharIndexForVisualColumnInRange(t2, row.Start, row.End, x, tab, row.StartVcol);
                if (!row.IsLastRow && newCol >= row.End) { newCol = row.End; endAffinity = true; }
            }
            b.CursorLine = targetLine;
            b.CursorCol = newCol;
            if (isPrimary)
            {
                if (endAffinity) SetAffinityEndOfRow(targetLine, newCol);
                else { affinityLine = -1; affinityCol = -1; }
            }
            if (moved && nextPreferredVcolByPos != null) nextPreferredVcolByPos[PosKey(targetLine, newCol)] = x;
        }

        /// <summary>Home on a wrapped row goes to the row start first, then to the line's first non-blank / column 0.</summary>
        private bool TryWrappedHome(EditorTabBuffer b)
        {
            if (!b.WordWrap) return false;
            bool aff = b.CursorLine == affinityLine && b.CursorCol == affinityCol;
            int t = EditorWrapNavigation.Home(WrapOf(b, b.CursorLine), b.CursorCol, aff); // shared with WinForms (QA #32)
            if (t < 0) return false;
            b.CursorCol = t;
            affinityLine = -1;
            return true;
        }

        /// <summary>End on a wrapped row goes to the end of that row first, then to the end of the line.</summary>
        private bool TryWrappedEnd(EditorTabBuffer b)
        {
            if (!b.WordWrap) return false;
            int len = (b.Lines[b.CursorLine] ?? "").Length;
            bool aff = b.CursorLine == affinityLine && b.CursorCol == affinityCol;
            int end = EditorWrapNavigation.End(WrapOf(b, b.CursorLine), len, b.CursorCol, aff); // shared with WinForms (QA #32)
            if (end < 0) return false;
            b.CursorCol = end;
            SetAffinityEndOfRow(b.CursorLine, end);
            return true;
        }

        private List<EditorVisualRow> lastFrameRows = new List<EditorVisualRow>();
        private readonly List<int> lastFrameChevronLines = new List<int>();

        /// <summary>Document lines that got a fold chevron in the last frame (tests).</summary>
        public IReadOnlyList<int> LastFrameFoldChevronLines => lastFrameChevronLines;

        /// <summary>Rows painted in the last frame (tests / screenshot suite).</summary>
        public IReadOnlyList<EditorVisualRow> LastFrameRows => lastFrameRows;

        private global::Avalonia.Threading.DispatcherTimer foldTimer;
        private int foldScheduledVersion = -1;

        /// <summary>Documents up to this many lines recompute folding ranges synchronously on the next frame; bigger ones debounce (VS Code computes them asynchronously too).</summary>
        public static int FoldSyncLineLimit = 5000;

        /// <summary>The folding model for painting / hover: up to date for small documents, otherwise the previous ranges (shifted by edits) until a debounced recomputation lands.</summary>
        private EditorFoldingModel FoldModelForFrame(EditorTabBuffer b)
        {
            var m = EditorFoldingModel.For(b);
            if (m == null) return null;
            int tab = TabFor(b);
            if (m.IsUpToDate(b, tab)) return m;
            if (b.Lines.Count <= FoldSyncLineLimit)
            {
                if (!m.Update(b, tab, 4)) EnsureFoldRecomputeTimer(restart: false);
            }
            else
            {
                // debounce: restart only when the text changed again, so idle repaints do not postpone the update
                bool edited = b.EditVersion != foldScheduledVersion;
                foldScheduledVersion = b.EditVersion;
                EnsureFoldRecomputeTimer(restart: edited);
            }
            return m;
        }

        /// <summary>Finishes / debounces a folding-range recomputation off the typing path, then repaints.</summary>
        private void EnsureFoldRecomputeTimer(bool restart)
        {
            if (foldTimer == null)
            {
                foldTimer = new global::Avalonia.Threading.DispatcherTimer(global::Avalonia.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
                foldTimer.Tick += (s, e) =>
                {
                    var b = Buffer;
                    if (b == null || !IsVisible) { foldTimer.Stop(); return; }
                    var m = EditorFoldingModel.For(b);
                    if (m.Update(b, TabFor(b), 25)) { foldTimer.Stop(); InvalidateVisual(); }
                };
            }
            if (restart) foldTimer.Stop();
            if (!foldTimer.IsEnabled) foldTimer.Start();
        }

        // ---------------------------------------------------------------- folding (shared core model)
        private EditorFoldingModel FoldModel(EditorTabBuffer b, int budgetMs)
        {
            var m = EditorFoldingModel.For(b);
            m?.Update(b, TabFor(b), budgetMs);
            return m;
        }

        /// <summary>Ends of foldable ranges (header -> last hidden line) for the shown buffer.</summary>
        public int GetFoldEndLine(int line)
        {
            var b = Buffer;
            if (b == null) return -1;
            return FoldModel(b, 0)?.EndOf(line) ?? -1;
        }
    }
}
