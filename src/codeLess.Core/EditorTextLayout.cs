using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace codeLess.Studio
{
    /// <summary>
    /// The visual column model shared by both editors (QA #20): a tab advances to the next tab stop of the real tab
    /// size, CJK / fullwidth / emoji occupy 2 cells, combining marks and zero-width characters 0, a surrogate pair is
    /// one glyph. Text, caret, selection, find highlights and hit-testing must all use this grid. STRICT C# 5.
    /// </summary>
    public static class EditorTextLayout
    {
        /// <summary>Cells of the character at <paramref name="i"/> (tabs excluded: see <see cref="AdvanceCells"/>). A low surrogate counts 0.</summary>
        public static int CellsForChar(string s, int i)
        {
            char c = s[i];
            if (char.IsLowSurrogate(c)) return 0; // counted with its high surrogate
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    if (Vt100TerminalBuffer.IsZeroWidthAstral(cp)) return 0;
                    return Vt100TerminalBuffer.IsWideAstral(cp) ? 2 : 1;
                }
                return 1;
            }
            if (c < 0x300) return 1; // ASCII / Latin fast path
            if (Vt100TerminalBuffer.IsZeroWidthBmp(c)) return 0;
            return Vt100TerminalBuffer.IsWideBmp(c) ? 2 : 1;
        }

        /// <summary>Cells the character at <paramref name="i"/> advances when drawn at visual column <paramref name="vcol"/>.</summary>
        public static int AdvanceCells(string s, int i, int vcol, int tabSize)
        {
            if (s[i] == '\t')
            {
                int t = Math.Max(1, tabSize);
                return t - (vcol % t);
            }
            return CellsForChar(s, i);
        }

        /// <summary>Visual column of character index <paramref name="col"/> (virtual space past the end counts 1 cell per column).</summary>
        public static int VisualColumn(string line, int col, int tabSize)
        {
            return VisualColumnFrom(line, 0, col, tabSize, 0);
        }

        /// <summary>Visual column of <paramref name="col"/> measured from <paramref name="start"/>, which sits at <paramref name="startVcol"/>.</summary>
        public static int VisualColumnFrom(string line, int start, int col, int tabSize, int startVcol)
        {
            if (string.IsNullOrEmpty(line)) return startVcol + Math.Max(0, col - start);
            int t = Math.Max(1, tabSize);
            int v = startVcol;
            int n = Math.Min(col, line.Length);
            for (int i = Math.Max(0, start); i < n; i++)
            {
                char c = line[i];
                if (c == '\t') v += t - (v % t);
                else if (c < 0x300) v++;
                else v += CellsForChar(line, i);
            }
            return v + Math.Max(0, col - Math.Max(line.Length, start));
        }

        /// <summary>Character index (caret boundary) nearest to visual column <paramref name="vcol"/>.</summary>
        public static int CharIndexForVisualColumn(string line, double vcol, int tabSize)
        {
            return CharIndexForVisualColumnInRange(line, 0, line == null ? 0 : line.Length, vcol, tabSize, 0);
        }

        /// <summary>Nearest caret boundary in [<paramref name="start"/>, <paramref name="end"/>] for a visual column of a row that starts at <paramref name="startVcol"/>.</summary>
        public static int CharIndexForVisualColumnInRange(string line, int start, int end, double vcol, int tabSize, int startVcol)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            end = Math.Min(end, line.Length);
            if (vcol <= startVcol) return start;
            int t = Math.Max(1, tabSize);
            int v = startVcol;
            for (int i = start; i < end; i++)
            {
                int w = line[i] == '\t' ? t - (v % t) : CellsForChar(line, i);
                if (w == 0) continue;
                if (vcol < v + w / 2.0) return i;
                v += w;
            }
            return end;
        }

        /// <summary>The character covering visual column <paramref name="vcol"/> in [start, end), or -1 past the end.</summary>
        public static int CharIndexUnderVisualColumn(string line, int start, int end, double vcol, int tabSize, int startVcol)
        {
            if (string.IsNullOrEmpty(line) || vcol < startVcol) return -1;
            end = Math.Min(end, line.Length);
            int t = Math.Max(1, tabSize);
            int v = startVcol;
            for (int i = start; i < end; i++)
            {
                int w = line[i] == '\t' ? t - (v % t) : CellsForChar(line, i);
                if (w == 0) continue;
                if (vcol < v + w) return i;
                v += w;
            }
            return -1;
        }

        /// <summary>Visual width of a whole line in cells.</summary>
        public static int VisualWidth(string line, int tabSize)
        {
            return line == null ? 0 : VisualColumn(line, line.Length, tabSize);
        }

        /// <summary>Caret position one character to the left (a surrogate pair is one character).</summary>
        public static int PrevCharBoundary(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col <= 0) return 0;
            col = Math.Min(col, line.Length);
            int c = col - 1;
            if (c > 0 && char.IsLowSurrogate(line[c]) && char.IsHighSurrogate(line[c - 1])) c--;
            return c;
        }

        /// <summary>Caret position one character to the right (a surrogate pair is one character).</summary>
        public static int NextCharBoundary(string line, int col)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            if (col >= line.Length) return line.Length;
            int c = col + 1;
            if (c < line.Length && char.IsLowSurrogate(line[c]) && char.IsHighSurrogate(line[c - 1])) c++;
            return c;
        }

        /// <summary>True when the run can be drawn as one string on the grid (no tabs, no wide / zero-width / astral characters).</summary>
        public static bool IsSimpleRun(string s, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                char c = s[i];
                if (c == '\t' || c >= '̀') return false;
            }
            return true;
        }
    }

    /// <summary>Soft-wrap layout of one document line: rows start at <see cref="RowStarts"/>; continuation rows are indented by <see cref="Indent"/> cells.</summary>
    public sealed class WrapLayout
    {
        public static readonly WrapLayout Single = new WrapLayout(new int[] { 0 }, 0);

        public readonly int[] RowStarts;
        public readonly int Indent;

        public WrapLayout(int[] rowStarts, int indent)
        {
            RowStarts = rowStarts;
            Indent = indent;
        }

        public int RowCount { get { return RowStarts.Length; } }

        /// <summary>First character index of the row after <paramref name="row"/> (or the line length for the last row).</summary>
        public int RowEnd(int row, int lineLength)
        {
            return row + 1 < RowStarts.Length ? RowStarts[row + 1] : lineLength;
        }

        /// <summary>Visual column at which row <paramref name="row"/>'s text starts.</summary>
        public int RowStartVcol(int row)
        {
            return row == 0 ? 0 : Indent;
        }

        /// <summary>Row showing the caret at <paramref name="col"/>; at a soft break the caret belongs to the next row unless <paramref name="preferEndOfRow"/>.</summary>
        public int RowOf(int col, bool preferEndOfRow)
        {
            int lo = 0, hi = RowStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (RowStarts[mid] <= col) lo = mid; else hi = mid - 1;
            }
            if (preferEndOfRow && lo > 0 && RowStarts[lo] == col) lo--;
            return lo;
        }
    }

    /// <summary>
    /// VS Code's "simple" monospace line breaking (QA #11): break opportunities after spaces / tabs / ")]}?|/&amp;.,;"
    /// and CJK punctuation, before "([{+" and between ideographs; a word longer than the row is broken anywhere;
    /// spaces at a break hang at the end of the row; continuation rows keep the line's indentation ("same").
    /// </summary>
    public static class EditorWordWrap
    {
        private const int ClassNone = 0, ClassBreakBefore = 1, ClassBreakAfter = 2, ClassIdeographic = 3;
        private const string BreakBeforeChars = "([{‘“〈《「『【〔（［｛｢£¥＄￡￥+＋";
        private const string BreakAfterChars = " \t})]?|/&.,;¢°′″‰℃、。｡､￠，．：；？！％・･ゝゞヽヾーァィゥェォッャュョヮヵヶぁぃぅぇぉっゃゅょゎゕゖㇰㇱㇲㇳㇴㇵㇶㇷㇸㇹㇺㇻㇼㇽㇾㇿ々〻ｧｨｩｪｫｬｭｮｯｰ”〉》」』】〕）］｝｣";

        private static int Classify(char c)
        {
            if (c < 128)
            {
                if (c == ' ' || c == '\t' || c == '}' || c == ')' || c == ']' || c == '?' || c == '|' || c == '/' || c == '&' || c == '.' || c == ',' || c == ';') return ClassBreakAfter;
                if (c == '(' || c == '[' || c == '{' || c == '+') return ClassBreakBefore;
                return ClassNone;
            }
            if (BreakAfterChars.IndexOf(c) >= 0) return ClassBreakAfter;
            if (BreakBeforeChars.IndexOf(c) >= 0) return ClassBreakBefore;
            if ((c >= '぀' && c <= 'ヿ') || (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿') ||
                (c >= '豈' && c <= '﫿') || (c >= '가' && c <= '힣'))
                return ClassIdeographic;
            return ClassNone;
        }

        private static bool CanBreak(int prevClass, char c, int cls)
        {
            return c != ' ' && c != '\t' &&
                   ((prevClass == ClassBreakAfter && cls != ClassBreakAfter) ||
                    (prevClass != ClassBreakBefore && cls == ClassBreakBefore) ||
                    (prevClass == ClassIdeographic && cls != ClassBreakAfter) ||
                    (cls == ClassIdeographic && prevClass != ClassBreakBefore));
        }

        /// <summary>Indent of continuation rows ("same" wrapping indent); 0 when it would leave no room for text.</summary>
        public static int WrappedIndent(string line, int cols, int tabSize)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            int f = 0;
            while (f < line.Length && (line[f] == ' ' || line[f] == '\t')) f++;
            if (f >= line.Length) return 0;
            int ind = EditorTextLayout.VisualColumn(line, f, tabSize);
            return ind + 2 > cols ? 0 : ind;
        }

        /// <summary>Breaks <paramref name="line"/> into rows of at most <paramref name="cols"/> cells.</summary>
        public static WrapLayout Compute(string line, int cols, int tabSize, bool sameIndent)
        {
            if (string.IsNullOrEmpty(line) || cols <= 0) return WrapLayout.Single;
            int t = Math.Max(1, tabSize);
            if ((long)line.Length * Math.Max(2, t) <= cols) return WrapLayout.Single;
            int indent = sameIndent ? WrappedIndent(line, cols, t) : 0;
            int rowCols = cols;
            List<int> starts = null;
            int rowStart = 0;
            int v = 0;                // visual column of the next character within the current row
            int breakOff = -1, breakV = 0;
            int prevClass = ClassNone;
            int n = line.Length;
            for (int i = 0; i < n; i++)
            {
                int charStart = i;
                char c = line[i];
                int w, cls;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(line[i + 1]))
                {
                    w = EditorTextLayout.CellsForChar(line, i);
                    cls = w == 2 ? ClassIdeographic : ClassNone;
                    i++;
                }
                else
                {
                    w = c == '\t' ? t - (v % t) : EditorTextLayout.CellsForChar(line, i);
                    cls = Classify(c);
                }
                if (charStart > rowStart && CanBreak(prevClass, c, cls)) { breakOff = charStart; breakV = v; }
                v += w;
                prevClass = cls;
                if (v <= rowCols || charStart == rowStart || c == ' ' || w == 0) continue;
                // the character starting at charStart overflows: break at the last opportunity, else right before it
                int rowTextCols = rowCols - (starts == null ? 0 : indent);
                if (breakOff <= rowStart || (v - breakV) > rowTextCols)
                {
                    breakOff = charStart;
                    breakV = v - w;
                }
                if (starts == null) starts = new List<int>();
                starts.Add(breakOff);
                rowStart = breakOff;
                // re-measure the carried-over text in the new row
                v = indent;
                for (int k = breakOff; k <= i; k++)
                {
                    if (char.IsLowSurrogate(line[k]) && k > breakOff) continue;
                    v += line[k] == '\t' ? t - (v % t) : EditorTextLayout.CellsForChar(line, k);
                }
                breakOff = -1;
                if (v > rowCols && i > rowStart)
                {
                    // a single carried character still overflowing (narrow viewport): force it onto its own row
                    starts.Add(charStart);
                    rowStart = charStart;
                    v = indent + w;
                }
            }
            if (starts == null) return WrapLayout.Single;
            int[] arr = new int[starts.Count + 1];
            arr[0] = 0;
            for (int k = 0; k < starts.Count; k++) arr[k + 1] = starts[k];
            return new WrapLayout(arr, indent);
        }
    }

    /// <summary>
    /// Per-line wrap cache keyed by line string identity (strings are immutable, so an edited line gets a new key):
    /// re-wrapping after an edit or a scroll only lays out lines not seen before. Cleared when the width or tab size changes.
    /// </summary>
    public sealed class WrapLayoutCache
    {
        private sealed class RefComparer : IEqualityComparer<string>
        {
            public bool Equals(string a, string b) { return ReferenceEquals(a, b); }
            public int GetHashCode(string s) { return RuntimeHelpers.GetHashCode(s); }
        }

        private readonly Dictionary<string, WrapLayout> map = new Dictionary<string, WrapLayout>(new RefComparer());
        private int cols = -1, tab = -1;
        private bool same = true;
        public int MaxEntries = 50000;
        public long Computations;

        public int Columns { get { return cols; } }

        public void Configure(int columns, int tabSize, bool sameIndent)
        {
            if (columns == cols && tabSize == tab && sameIndent == same) return;
            cols = columns;
            tab = tabSize;
            same = sameIndent;
            map.Clear();
        }

        public WrapLayout Get(string line)
        {
            if (string.IsNullOrEmpty(line) || cols <= 0) return WrapLayout.Single;
            if ((long)line.Length * Math.Max(2, tab) <= cols) return WrapLayout.Single;
            WrapLayout w;
            if (map.TryGetValue(line, out w)) return w;
            if (map.Count >= MaxEntries) map.Clear();
            w = EditorWordWrap.Compute(line, cols, tab, same);
            Computations++;
            map[line] = w;
            return w;
        }
    }
}
