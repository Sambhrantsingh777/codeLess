using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Geometry of an editor scrollbar track and its slider (QA #28), shared by both editors. Sizes follow VS Code:
    /// editor.scrollbar.verticalScrollbarSize 14, horizontalScrollbarSize 12, minimum slider 20 px. The scroll range
    /// matches the editors' "scroll beyond last line" behaviour: the last row can be scrolled to the top, so the
    /// scrollable content is (totalRows - 1 + visibleRows) rows.
    /// </summary>
    public struct EditorScrollbarGeometry
    {
        public double TrackStart;
        public double TrackLength;
        public double ThumbStart;
        public double ThumbLength;
        /// <summary>Rows of content mapped onto the track (rows beyond the last line included).</summary>
        public double ContentRows;
        public double MaxTopRow;
        public bool Visible;

        public double ThumbEnd { get { return ThumbStart + ThumbLength; } }

        /// <summary>Track pixels per content row (overview-ruler marks use the same scale as the slider).</summary>
        public double PixelsPerRow { get { return ContentRows > 0 ? TrackLength / ContentRows : 0; } }
    }

    public static class EditorScrollbar
    {
        public const int VerticalWidth = 14;
        public const int HorizontalHeight = 12;
        public const int MinThumb = 20;
        /// <summary>The slider fades out this long after the last scroll when the pointer is not over the editor (VS Code "auto").</summary>
        public const int AutoHideDelayMs = 900;

        public const int HitNone = 0, HitThumb = 1, HitBefore = 2, HitAfter = 3;

        /// <summary>Vertical geometry for <paramref name="totalRows"/> visual rows with <paramref name="visibleRows"/> on screen and <paramref name="topRow"/> at the top.</summary>
        public static EditorScrollbarGeometry Vertical(double trackStart, double trackLength, double totalRows, double visibleRows, double topRow)
        {
            EditorScrollbarGeometry g = new EditorScrollbarGeometry();
            g.TrackStart = trackStart;
            g.TrackLength = Math.Max(0, trackLength);
            totalRows = Math.Max(1, totalRows);
            visibleRows = Math.Max(1, visibleRows);
            g.MaxTopRow = Math.Max(0, totalRows - 1);
            g.ContentRows = totalRows - 1 + visibleRows;
            g.Visible = g.TrackLength > 0 && g.MaxTopRow > 0;
            if (!g.Visible) { g.ThumbStart = trackStart; g.ThumbLength = g.TrackLength; return g; }
            double len = g.TrackLength * visibleRows / g.ContentRows;
            g.ThumbLength = Math.Min(g.TrackLength, Math.Max(Math.Min(MinThumb, g.TrackLength), len));
            double t = Math.Max(0, Math.Min(g.MaxTopRow, topRow));
            g.ThumbStart = trackStart + (g.TrackLength - g.ThumbLength) * (t / g.MaxTopRow);
            return g;
        }

        /// <summary>Horizontal geometry: content / viewport / offset in pixels. Hidden when the content fits.</summary>
        public static EditorScrollbarGeometry Horizontal(double trackStart, double trackLength, double contentPx, double viewportPx, double offsetPx)
        {
            EditorScrollbarGeometry g = new EditorScrollbarGeometry();
            g.TrackStart = trackStart;
            g.TrackLength = Math.Max(0, trackLength);
            viewportPx = Math.Max(1, viewportPx);
            g.MaxTopRow = Math.Max(0, contentPx - viewportPx);
            g.ContentRows = Math.Max(viewportPx, contentPx);
            g.Visible = g.TrackLength > 0 && (g.MaxTopRow > 0.5 || offsetPx > 0.5);
            if (!g.Visible) { g.ThumbStart = trackStart; g.ThumbLength = g.TrackLength; return g; }
            if (offsetPx > g.MaxTopRow) { g.MaxTopRow = offsetPx; g.ContentRows = offsetPx + viewportPx; }
            double len = g.TrackLength * viewportPx / g.ContentRows;
            g.ThumbLength = Math.Min(g.TrackLength, Math.Max(Math.Min(MinThumb, g.TrackLength), len));
            g.ThumbStart = trackStart + (g.TrackLength - g.ThumbLength) * (g.MaxTopRow > 0 ? Math.Max(0, Math.Min(1, offsetPx / g.MaxTopRow)) : 0);
            return g;
        }

        public static int HitTest(EditorScrollbarGeometry g, double pos)
        {
            if (!g.Visible || pos < g.TrackStart || pos > g.TrackStart + g.TrackLength) return HitNone;
            if (pos < g.ThumbStart) return HitBefore;
            if (pos > g.ThumbEnd) return HitAfter;
            return HitThumb;
        }

        /// <summary>Scroll position (row / px) that puts the slider's start at <paramref name="thumbStart"/>.</summary>
        public static double ValueForThumbStart(EditorScrollbarGeometry g, double thumbStart)
        {
            double range = g.TrackLength - g.ThumbLength;
            if (range <= 0 || g.MaxTopRow <= 0) return 0;
            double r = Math.Max(0, Math.Min(1, (thumbStart - g.TrackStart) / range));
            return r * g.MaxTopRow;
        }

        /// <summary>Click on the track (VS Code default, scrollByPage off): the slider centres on the click.</summary>
        public static double ValueForJump(EditorScrollbarGeometry g, double pos)
        {
            return ValueForThumbStart(g, pos - g.ThumbLength / 2.0);
        }

        /// <summary>Track coordinate of a content row (overview-ruler marks, same scale as the slider).</summary>
        public static double PositionOfRow(EditorScrollbarGeometry g, double row)
        {
            return g.TrackStart + row * g.PixelsPerRow;
        }
    }

    /// <summary>
    /// Visual rows of a document (soft wrap and folded regions), for the scrollbar size / position and the overview
    /// ruler. Built in one pass: lines that fit the wrap width are one row without running the wrapper (only long lines
    /// are wrapped), so a 50k-line file costs a few ms; unwrapped documents without folds need no table at all.
    /// </summary>
    public sealed class EditorVisualRowIndex
    {
        private int[] prefix;      // prefix[i] = rows before line i (hidden lines add 0); null = identity
        private int[] headerOf;    // hidden line -> its fold header (-1 visible); null = no folds
        private int[] zoneAbove;   // read-only rows shown above a line (deleted zones); null = none
        private int lineCount;
        public int TotalRows;

        // cache key
        private object linesRef;
        private int version = int.MinValue, wrapCols = -2, tabSize = -1;
        private string foldKey;

        public bool IsIdentity { get { return prefix == null; } }
        /// <summary>Incremented by every rebuild (a running scroll animation in rows is stale once it changes).</summary>
        public int BuildCount;

        /// <summary>True when the index still describes the given document state.</summary>
        public bool Matches(object lines, int editVersion, int wrapColumns, int tab, string foldSignature)
        {
            return ReferenceEquals(lines, linesRef) && editVersion == version && wrapColumns == wrapCols && tab == tabSize &&
                   string.Equals(foldSignature ?? "", foldKey ?? "", StringComparison.Ordinal);
        }

        /// <summary>
        /// Rebuilds the table. <paramref name="wrapColumns"/> &lt;= 0: no wrapping. <paramref name="wrapRows"/> returns the
        /// row count of a (long) line. <paramref name="folded"/>: header -&gt; last hidden line.
        /// </summary>
        public void Build(IList<string> lines, int editVersion, int wrapColumns, int tab, Func<string, int> wrapRows,
                          ICollection<KeyValuePair<int, int>> folded, string foldSignature)
        {
            Build(lines, editVersion, wrapColumns, tab, wrapRows, folded, foldSignature, null);
        }

        /// <summary>
        /// As above; <paramref name="rowsAboveLine"/> adds read-only rows shown above a line (inline-diff deleted zones,
        /// QA #30). <see cref="RowOfLine"/> is the line's first real row; rows of its zone come right before it.
        /// </summary>
        public void Build(IList<string> lines, int editVersion, int wrapColumns, int tab, Func<string, int> wrapRows,
                          ICollection<KeyValuePair<int, int>> folded, string foldSignature, IDictionary<int, int> rowsAboveLine)
        {
            BuildCount++;
            linesRef = lines;
            version = editVersion;
            wrapCols = wrapColumns;
            tabSize = tab;
            foldKey = foldSignature ?? "";
            lineCount = lines == null ? 0 : lines.Count;
            bool hasFolds = folded != null && folded.Count > 0;
            bool hasZones = rowsAboveLine != null && rowsAboveLine.Count > 0;
            zoneAbove = null;
            if (wrapColumns <= 0 && !hasFolds && !hasZones)
            {
                prefix = null;
                headerOf = null;
                TotalRows = Math.Max(1, lineCount);
                return;
            }
            if (hasZones)
            {
                zoneAbove = new int[lineCount];
                foreach (KeyValuePair<int, int> z in rowsAboveLine)
                    if (z.Key >= 0 && z.Key < lineCount && z.Value > 0) zoneAbove[z.Key] = z.Value; // a zone after the last line is not scrolled to
            }
            headerOf = null;
            if (hasFolds)
            {
                headerOf = new int[lineCount];
                for (int i = 0; i < lineCount; i++) headerOf[i] = -1;
                List<KeyValuePair<int, int>> sorted = new List<KeyValuePair<int, int>>(folded);
                sorted.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return a.Key.CompareTo(b.Key); });
                foreach (KeyValuePair<int, int> f in sorted)
                {
                    if (f.Key < 0 || f.Key >= lineCount || f.Value <= f.Key) continue;
                    if (headerOf[f.Key] >= 0) continue; // the header itself is hidden by an outer fold
                    int end = Math.Min(lineCount - 1, f.Value);
                    for (int l = f.Key + 1; l <= end; l++) if (headerOf[l] < 0) headerOf[l] = f.Key;
                }
            }
            prefix = new int[lineCount + 1];
            int rows = 0;
            for (int i = 0; i < lineCount; i++)
            {
                prefix[i] = rows;
                if (headerOf != null && headerOf[i] >= 0) continue;
                if (zoneAbove != null) rows += zoneAbove[i];
                int r = 1;
                if (wrapColumns > 0)
                {
                    string s = lines[i] ?? "";
                    if (s.Length > wrapColumns / 2 && EditorTextLayout.VisualWidth(s, tab) > wrapColumns && wrapRows != null)
                        r = Math.Max(1, wrapRows(s));
                }
                rows += r;
            }
            prefix[lineCount] = rows;
            TotalRows = Math.Max(1, rows);
        }

        /// <summary>First visual row of a line (a hidden line maps to its fold header's row).</summary>
        public int RowOfLine(int line)
        {
            if (lineCount == 0) return 0;
            line = Math.Max(0, Math.Min(lineCount - 1, line));
            if (prefix == null) return line;
            if (headerOf != null && headerOf[line] >= 0) line = headerOf[line];
            return prefix[line] + (zoneAbove != null ? zoneAbove[line] : 0);
        }

        /// <summary>Read-only zone rows shown above a line (0 when none).</summary>
        public int ZoneRowsAbove(int line)
        {
            if (zoneAbove == null || line < 0 || line >= lineCount) return 0;
            if (headerOf != null && headerOf[line] >= 0) return 0;
            return zoneAbove[line];
        }

        /// <summary>Rows of a line on screen (0 when hidden).</summary>
        public int RowsOfLine(int line)
        {
            if (lineCount == 0) return 1;
            line = Math.Max(0, Math.Min(lineCount - 1, line));
            if (prefix == null) return 1;
            return prefix[line + 1] - prefix[line] - ZoneRowsAbove(line);
        }

        /// <summary>The visible line containing visual row <paramref name="row"/> and the row inside it.</summary>
        public int LineOfRow(int row, out int subRow)
        {
            subRow = 0;
            if (lineCount == 0) return 0;
            if (prefix == null) return Math.Max(0, Math.Min(lineCount - 1, row));
            row = Math.Max(0, Math.Min(TotalRows - 1, row));
            int lo = 0, hi = lineCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (prefix[mid] <= row) lo = mid; else hi = mid - 1;
            }
            // skip hidden lines (0 rows) back to the visible line that owns the row
            while (lo > 0 && prefix[lo + 1] == prefix[lo]) lo--;
            subRow = row - RowOfLine(lo); // negative: inside the zone above the line
            return lo;
        }
    }

    /// <summary>One overview-ruler decoration (VS Code editorOverviewRuler.*).</summary>
    public struct OverviewRulerMark
    {
        public int Row;
        public int RowCount;
        public int Lane;
        public int Kind;
    }

    /// <summary>
    /// Overview ruler on the vertical scrollbar (QA #28): which marks to draw and in which lane, like VS Code —
    /// source control changes in the left lane, find matches in the centre, problems on the right, merge conflicts
    /// and cursors across the full width. Rows come from <see cref="EditorVisualRowIndex"/> (wrap / fold aware).
    /// </summary>
    public static class EditorOverviewRuler
    {
        public const int LaneLeft = 1, LaneCenter = 2, LaneRight = 4, LaneFull = 7;

        public const int KindError = 1, KindWarning = 2, KindInfo = 3, KindFindMatch = 4, KindAdded = 5, KindModified = 6,
                         KindDeleted = 7, KindCursor = 8, KindConflictCurrent = 9, KindConflictIncoming = 10, KindProposed = 11;

        /// <summary>
        /// Collects the marks for a buffer. <paramref name="findMatchLines"/> are document lines with find matches,
        /// <paramref name="cursorLines"/> the lines of every caret (primary first).
        /// </summary>
        public static List<OverviewRulerMark> Collect(EditorTabBuffer buf, EditorVisualRowIndex rows, IEnumerable<int> findMatchLines, IEnumerable<int> cursorLines)
        {
            List<OverviewRulerMark> marks = new List<OverviewRulerMark>();
            if (buf == null || buf.Lines == null || rows == null) return marks;
            int n = buf.Lines.Count;

            // source control (left lane), merged into runs of consecutive rows
            AddLineSet(marks, rows, buf.GutterAddedLines, n, LaneLeft, KindAdded);
            AddLineSet(marks, rows, buf.GutterModifiedLines, n, LaneLeft, KindModified);
            if (buf.GutterDeletedAfterLine != null)
            {
                foreach (int l in buf.GutterDeletedAfterLine)
                {
                    int line = Math.Max(0, Math.Min(n - 1, l + 1 >= n ? n - 1 : l + 1));
                    marks.Add(Mark(rows.RowOfLine(line), 1, LaneLeft, KindDeleted));
                }
            }

            // merge conflicts (full width)
            if (n <= 400000)
            {
                foreach (ConflictBlock b in ConflictDecorations.Blocks(buf))
                {
                    AddRange(marks, rows, b.StartLine, b.CurrentEndExclusive - 1, LaneFull, KindConflictCurrent);
                    AddRange(marks, rows, b.SeparatorLine, b.EndLine, LaneFull, KindConflictIncoming);
                }
            }

            // find matches (centre lane)
            if (findMatchLines != null)
            {
                int last = -1;
                foreach (int l in findMatchLines)
                {
                    if (l == last || l < 0 || l >= n) continue;
                    last = l;
                    marks.Add(Mark(rows.RowOfLine(l), 1, LaneCenter, KindFindMatch));
                }
            }

            // problems (right lane): warnings first so errors paint on top
            if (buf.Diagnostics != null && buf.Diagnostics.Count > 0)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (DiagnosticItem d in buf.Diagnostics)
                    {
                        int kind = SeverityKind(d.Severity);
                        int want = pass == 0 ? KindInfo : (pass == 1 ? KindWarning : KindError);
                        if (kind != want) continue;
                        int l = d.Line - 1;
                        if (l < 0 || l >= n) continue;
                        marks.Add(Mark(rows.RowOfLine(l), 1, LaneRight, kind));
                    }
                }
            }

            // cursors (full width, thin)
            if (cursorLines != null)
                foreach (int l in cursorLines)
                    if (l >= 0 && l < n) marks.Add(Mark(rows.RowOfLine(l), 1, LaneFull, KindCursor));
            return marks;
        }

        public static int SeverityKind(string severity)
        {
            if (string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)) return KindError;
            if (string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)) return KindWarning;
            return KindInfo;
        }

        private static OverviewRulerMark Mark(int row, int count, int lane, int kind)
        {
            OverviewRulerMark m = new OverviewRulerMark();
            m.Row = row; m.RowCount = Math.Max(1, count); m.Lane = lane; m.Kind = kind;
            return m;
        }

        private static void AddRange(List<OverviewRulerMark> marks, EditorVisualRowIndex rows, int first, int last, int lane, int kind)
        {
            if (last < first) return;
            int r0 = rows.RowOfLine(first);
            int r1 = rows.RowOfLine(last) + Math.Max(1, rows.RowsOfLine(last));
            marks.Add(Mark(r0, Math.Max(1, r1 - r0), lane, kind));
        }

        private static void AddLineSet(List<OverviewRulerMark> marks, EditorVisualRowIndex rows, HashSet<int> set, int n, int lane, int kind)
        {
            if (set == null || set.Count == 0) return;
            List<int> sorted = new List<int>(set);
            sorted.Sort();
            int runStart = -1, prev = -2;
            foreach (int l in sorted)
            {
                if (l < 0 || l >= n) continue;
                if (l != prev + 1)
                {
                    if (runStart >= 0) AddRange(marks, rows, runStart, prev, lane, kind);
                    runStart = l;
                }
                prev = l;
            }
            if (runStart >= 0) AddRange(marks, rows, runStart, prev, lane, kind);
        }

        /// <summary>Horizontal extent of a lane inside a ruler of <paramref name="width"/> pixels (x offset, width).</summary>
        public static void LaneBounds(int lane, double width, out double x, out double w)
        {
            double third = width / 3.0;
            switch (lane)
            {
                case LaneLeft: x = 0; w = third; return;
                case LaneCenter: x = third; w = third; return;
                case LaneRight: x = 2 * third; w = width - 2 * third; return;
                default: x = 0; w = width; return;
            }
        }
    }
}
