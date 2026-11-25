using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Per-row cache of shaped text for the editor canvas. Every frame used to create a FormattedText (text shaping +
    /// layout) for every token of every visible row: ~5-8 MB of garbage and most of the frame time while scrolling,
    /// so smooth scrolling stuttered on GC pauses. Rows keep their shaped runs while their text, tokens, slice and font
    /// are unchanged; scrolling only shapes the rows that come into view. Positions are stored in visual columns, so
    /// horizontal / fractional scrolling reuses the same runs.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        private sealed class CachedRun
        {
            public TextLayout Text;
            public int Vcol;
            public int Cells;
            public double Dy;
            public bool Plain;
        }

        private sealed class CachedRowText
        {
            public string LineRef;
            public int SliceStart, StartVcol, Tab, EndVcol;
            public double FontSize;
            public StudioTheme Theme;
            public List<SyntaxTokenSpan> Tokens;
            public readonly List<CachedRun> Runs = new List<CachedRun>();
            public long LastFrame;
        }

        private readonly Dictionary<long, CachedRowText> rowTextCache = new Dictionary<long, CachedRowText>();
        private readonly Dictionary<Color, IBrush> runBrushCache = new Dictionary<Color, IBrush>();
        private readonly Dictionary<long, TextLayout> lineNumberCache = new Dictionary<long, TextLayout>();
        private double lineNumberFontSize = -1;
        private StudioTheme lineNumberTheme;
        private long renderFrameNo;

        /// <summary>Rows shaped (cache misses) since the canvas was created (tests: scrolling reuses rows).</summary>
        public long ShapedRowCount { get; private set; }
        /// <summary>Rows currently held by the shaped-text cache (tests).</summary>
        public int CachedRowTextCount => rowTextCache.Count;

        private static long RowCacheKey(int docLine, int row, bool sticky) => ((long)docLine << 17) | ((long)(row & 0xFFFF) << 1) | (sticky ? 1L : 0L);

        /// <summary>Shaped runs of a row slice (cached; rebuilt when the text, tokens, slice, tab size, font or theme changed).</summary>
        private CachedRowText GetRowTextRuns(int docLine, int row, bool sticky, string lineText, int sliceStart, int startVcol, List<SyntaxTokenSpan> tokens, int tabStop)
        {
            long key = RowCacheKey(docLine, row, sticky);
            if (rowTextCache.TryGetValue(key, out var c) && ReferenceEquals(c.LineRef, lineText) && c.SliceStart == sliceStart && c.StartVcol == startVcol &&
                c.Tab == tabStop && c.FontSize == FontSizePx && ReferenceEquals(c.Theme, Theme) && SameTokens(c.Tokens, tokens))
            {
                c.LastFrame = renderFrameNo;
                return c;
            }
            if (c == null) { c = new CachedRowText(); rowTextCache[key] = c; }
            c.LineRef = lineText; c.SliceStart = sliceStart; c.StartVcol = startVcol; c.Tab = tabStop; c.FontSize = FontSizePx; c.Theme = Theme;
            c.Tokens = tokens; c.LastFrame = renderFrameNo;
            c.Runs.Clear();
            ShapedRowCount++;
            int vcol = startVcol;
            foreach (var tk in tokens)
            {
                var tf = tk.IsBold ? CachedFontBold : (tk.IsItalic ? CachedFontItalic : CachedFontRegular);
                var brush = RunBrush(tk.Color);
                string t = tk.Text ?? "";
                if (t.Length == 0) continue;
                bool simple = true;
                for (int ci = 0; ci < t.Length; ci++)
                {
                    char ch = t[ci];
                    if (ch == '\t' || ch >= '̀') { simple = false; break; }
                }
                if (simple)
                {
                    // plain monospace run: one shaped text on the grid
                    var ft = MakeLayout(t, tf, FontSizePx, brush);
                    c.Runs.Add(new CachedRun { Text = ft, Vcol = vcol, Cells = t.Length, Dy = (LineHeight - ft.Height) / 2.0, Plain = true });
                    vcol += t.Length;
                    continue;
                }
                // tabs / wide / astral / combining characters: each glyph cluster at its own cell
                for (int ci = 0; ci < t.Length; ci++)
                {
                    char ch = t[ci];
                    if (ch == '\t') { vcol += tabStop - (vcol % tabStop); continue; }
                    int cells = CellsForChar(t, ci);
                    int clusterEnd = ci + ((char.IsHighSurrogate(ch) && ci + 1 < t.Length) ? 2 : 1);
                    while (clusterEnd < t.Length && t[clusterEnd] != '\t' && CellsForChar(t, clusterEnd) == 0) clusterEnd++;
                    if (!char.IsLowSurrogate(ch) && !(ch == ' '))
                    {
                        string glyph = t.Substring(ci, clusterEnd - ci);
                        var gft = MakeLayout(glyph, tf, FontSizePx, brush);
                        c.Runs.Add(new CachedRun { Text = gft, Vcol = vcol, Cells = Math.Max(1, cells), Dy = (LineHeight - gft.Height) / 2.0 });
                    }
                    ci = clusterEnd - 1;
                    vcol += cells;
                }
            }
            c.EndVcol = vcol;
            return c;
        }

        /// <summary>
        /// A single-line text layout: shaped once and drawn from its glyph runs every frame (FormattedText re-formats
        /// its lines on every draw, so caching it saved nothing).
        /// </summary>
        private static TextLayout MakeLayout(string text, Typeface tf, double size, IBrush brush) =>
            new TextLayout(text, tf, size, brush, TextAlignment.Left, TextWrapping.NoWrap);

        private static bool SameTokens(List<SyntaxTokenSpan> a, List<SyntaxTokenSpan> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.Color != y.Color || x.IsBold != y.IsBold || x.IsItalic != y.IsItalic || !string.Equals(x.Text, y.Text, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private IBrush RunBrush(Color c)
        {
            if (!runBrushCache.TryGetValue(c, out var b))
            {
                if (runBrushCache.Count > 512) runBrushCache.Clear();
                runBrushCache[c] = b = new SolidColorBrush(c);
            }
            return b;
        }

        /// <summary>Draws a cached row: only the runs inside the text area. Returns the x right after the slice.</summary>
        private double DrawCachedRow(DrawingContext context, CachedRowText row, int docLine, double codeBaseX, double y, double codeAreaRight, bool record = true)
        {
            double cw = CharWidth;
            foreach (var run in row.Runs)
            {
                double x0 = codeBaseX + run.Vcol * cw;
                if (record && run.Plain && CaptureFrameTokens) LastFrameRunOrigins.Add((docLine, run.Vcol, x0));
                if (x0 + run.Cells * cw < GutterWidth || x0 > codeAreaRight) continue;
                run.Text.Draw(context, new Point(Math.Round(x0), Math.Round(y + run.Dy)));
            }
            return codeBaseX + row.EndVcol * cw;
        }

        /// <summary>Line-number text for the gutter (cached per number / weight while the font and theme stay).</summary>
        private TextLayout LineNumberText(int docLine, bool current, Typeface regular, Typeface bold, IBrush currentBrush, IBrush otherBrush)
        {
            double size = FontSizePx - 1.5;
            if (lineNumberFontSize != size || !ReferenceEquals(lineNumberTheme, Theme) || lineNumberCache.Count > 4096)
            {
                lineNumberCache.Clear();
                lineNumberFontSize = size;
                lineNumberTheme = Theme;
            }
            long key = ((long)docLine << 1) | (current ? 1L : 0L);
            if (!lineNumberCache.TryGetValue(key, out var ft))
            {
                ft = MakeLayout((docLine + 1).ToString(CultureInfo.InvariantCulture), current ? bold : regular, size, current ? currentBrush : otherBrush);
                lineNumberCache[key] = ft;
            }
            return ft;
        }

        /// <summary>Drops rows that have not been painted for a while (bounded memory; nearby rows stay for scrolling back).</summary>
        private void TrimRenderCaches(int visibleRows)
        {
            int soft = Math.Max(256, visibleRows * 8);
            if (rowTextCache.Count <= soft) return;
            var stale = new List<long>();
            foreach (var kv in rowTextCache) if (kv.Value.LastFrame < renderFrameNo - 30) stale.Add(kv.Key);
            foreach (long k in stale) rowTextCache.Remove(k);
            if (rowTextCache.Count > 4096)
            {
                stale.Clear();
                foreach (var kv in rowTextCache) if (kv.Value.LastFrame != renderFrameNo) stale.Add(kv.Key);
                foreach (long k in stale) rowTextCache.Remove(k);
            }
        }

        /// <summary>Forgets all shaped text (font family / size changes elsewhere, buffer switch).</summary>
        internal void ClearRenderCaches()
        {
            rowTextCache.Clear();
            lineNumberCache.Clear();
        }
    }
}
