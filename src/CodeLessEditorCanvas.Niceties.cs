using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code editor niceties for the WinForms editor (parity with AvaloniaEditorCanvas.Niceties.cs): bracket pair
    /// colorization + active bracket pair guide, indent guides with the active block, sticky scroll, word-under-caret
    /// highlights / Change All Occurrences, HTML/XML/JSX auto-close and linked tag rename, Emmet on Tab and TextMate
    /// grammar highlighting. Analysis is shared in codeLess.Core. STRICT C# 5.
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        private EditorSyntaxSession wfSession;
        private int wfVisTop, wfVisBottom;
        private bool wfHadGuesses;
        private long wfTokenDeadline, wfBracketDeadline;
        private System.Windows.Forms.Timer wfBgTimer;
        private System.Windows.Forms.Timer wfOccTimer;
        private string wfOccTarget;
        private bool wfOccWhole = true;
        private EditorTabBuffer wfOccBuf;
        private int wfOccLine = -1, wfOccCol = -1, wfOccVersion = -1;
        private bool wfOccHasSel;
        private ActiveIndentGuide wfGuide = ActiveIndentGuide.None;
        private int wfGuideVersion = -1, wfGuideCaret = -1, wfGuideTab = -1;
        private EditorTabBuffer wfGuideBuf;
        private BracketPair wfPair = BracketPair.None;
        private readonly List<KeyValuePair<Rectangle, int>> wfStickyHits = new List<KeyValuePair<Rectangle, int>>();
        private int wfStickyHover = -1;
        private List<int> wfLastSticky = new List<int>();
        private readonly Dictionary<int, List<StyledSpan>> wfFrameSpans = new Dictionary<int, List<StyledSpan>>();

        public List<int> LastStickyLines { get { return wfLastSticky; } }
        public string CurrentOccurrenceTarget { get { return wfOccTarget; } }
        public int LastFrameOccurrenceCount { get; private set; }
        public int LastFrameTextMateLines { get; private set; }
        /// <summary>Lines painted with the cached, multi-line aware fallback tokenizer in the last frame.</summary>
        public int LastFrameFallbackLines { get; private set; }
        public BracketPair ActiveBracketPair { get { return wfPair; } }
        public ActiveIndentGuide ActiveIndentGuideInfo { get { return wfGuide; } }

        private int NicetyTabSizeWF(EditorTabBuffer buf)
        {
            return TabWF(buf);
        }

        // ------------------------------------------------------------------ frame
        private void BeginNicetiesFrameWF(EditorTabBuffer buf, int visLines)
        {
            LastFrameOccurrenceCount = 0;
            LastFrameTextMateLines = 0;
            LastFrameFallbackLines = 0;
            wfHadGuesses = false;
            wfFrameSpans.Clear();
            wfSession = EditorSyntaxSession.For(buf);
            wfSession.Prepare(buf);
            wfVisTop = Math.Max(0, buf.ScrollTopLine);
            wfVisBottom = Math.Min(buf.Lines.Count - 1, buf.ScrollTopLine + visLines + 1);
            wfTokenDeadline = EditorSyntaxSession.DeadlineFromNow(EditorSyntaxSession.SyncTokenizeBudgetMs);
            wfBracketDeadline = EditorSyntaxSession.DeadlineFromNow(60);
            int version = wfSession.Brackets.Version;
            int tab = NicetyTabSizeWF(buf);
            if (EditorNicetyOptions.IndentGuides || EditorNicetyOptions.BracketPairGuides)
            {
                if (!ReferenceEquals(wfGuideBuf, buf) || wfGuideVersion != version || wfGuideCaret != buf.CursorLine || wfGuideTab != tab)
                {
                    wfGuideBuf = buf;
                    wfGuideVersion = version;
                    wfGuideCaret = buf.CursorLine;
                    wfGuideTab = tab;
                    wfGuide = EditorIndentGuides.FindActive(buf.Lines, buf.CursorLine, tab, wfSession.Brackets.Profile.IndentBased);
                }
            }
            wfPair = (EditorNicetyOptions.BracketPairGuides && wfSession.Brackets.Profile.Enabled)
                ? wfSession.Brackets.FindEnclosingPair(buf.Lines, buf.CursorLine, buf.CursorCol)
                : BracketPair.None;
            UpdateOccurrenceStateWF(buf, version);
        }

        private void EndNicetiesFrameWF(Graphics g, EditorTabBuffer buf, int codeAreaRight)
        {
            RenderStickyScrollWF(g, buf, codeAreaRight);
            if (wfSession != null && wfSession.NeedsBackgroundWork) EnsureBackgroundTimerWF();
        }

        private void EnsureBackgroundTimerWF()
        {
            if (wfBgTimer == null)
            {
                wfBgTimer = new System.Windows.Forms.Timer();
                wfBgTimer.Interval = 16;
                wfBgTimer.Tick += delegate
                {
                    EditorTabBuffer b = owner.CurrentBuffer;
                    if (b == null || !Visible || IsDisposed) { wfBgTimer.Stop(); return; }
                    EditorSyntaxSession s = EditorSyntaxSession.For(b);
                    bool repaint = s.AdvanceBackground(b, wfVisTop, wfVisBottom, 8);
                    if (repaint || (wfHadGuesses && s.TextMate != null && s.TextMate.ValidCount > wfVisBottom)) Invalidate();
                    if (!s.NeedsBackgroundWork) wfBgTimer.Stop();
                };
            }
            if (!wfBgTimer.Enabled) wfBgTimer.Start();
        }

        // ------------------------------------------------------------------ tokens
        /// <summary>
        /// Styled spans for [segStart, segEnd) of a line: TextMate spans of the whole line (cached per frame), or the
        /// fallback tokenizer's cached whole-line tokens cut to the segment. Tokenizing only a wrapped segment or the
        /// horizontally visible window flipped the quote parity when it started inside a string.
        /// </summary>
        private List<StyledSpan> GetLineSpansWF(EditorTabBuffer buf, int docLine, string fullLineText, StudioTheme th, int segStart, int segEnd)
        {
            List<StyledSpan> cached;
            if (wfFrameSpans.TryGetValue(docLine, out cached)) return cached;
            List<StyledSpan> spans = null;
            EditorSyntaxSession s = wfSession;
            bool cacheable = false;
            if (s != null && EditorNicetyOptions.TextMateHighlighting && s.TextMate != null && !s.IsTooLongForTextMate(fullLineText))
            {
                bool guess;
                spans = s.GetTextMateSpans(buf.Lines, docLine, wfVisTop, th, wfTokenDeadline, out guess);
                if (spans != null) { LastFrameTextMateLines++; cacheable = true; if (guess) wfHadGuesses = true; }
            }
            if (spans == null && s != null)
            {
                bool fbGuess;
                spans = s.GetFallbackSpans(buf.Lines, docLine, buf.Language, th, segStart, segEnd, true, wfTokenDeadline, out fbGuess);
                if (spans != null) LastFrameFallbackLines++;
                if (fbGuess) wfHadGuesses = true;
            }
            if (spans == null) spans = FallbackTokenStyles.SliceSpans(FallbackTokenizer.Tokenize(fullLineText, buf.Language), fullLineText.Length, segStart, segEnd, th, true);
            if (s != null && EditorNicetyOptions.BracketPairColorization && s.Brackets.Profile.Enabled)
                spans = EditorSyntaxSession.ApplyBracketColors(spans, s.Brackets.GetLine(buf.Lines, docLine, wfBracketDeadline), th);
            if (cacheable) wfFrameSpans[docLine] = spans;
            return spans;
        }

        private static bool IsAllUpper(string w)
        {
            foreach (char ch in w) if (char.IsLetter(ch) && !char.IsUpper(ch)) return false;
            return true;
        }

        /// <summary>
        /// Draws one visual row of a line (segment) from styled spans on the shared cell grid (QA #20): tabs advance to the
        /// next stop of the buffer's tab size, CJK / emoji take 2 cells, combining marks draw with their base character,
        /// so text, caret and selection line up. Plain ASCII runs are drawn as one string.
        /// </summary>
        private void DrawNicetyLineWF(Graphics g, EditorTabBuffer buf, int docLine, string fullLineText, int segStart, int segEnd, int y, int effScrollLeft, int codeAreaRight, StudioTheme th, int startVcol = 0)
        {
            if (string.IsNullOrEmpty(fullLineText) || segEnd <= segStart) return;
            int tab = TabWF(buf);
            if (!buf.WordWrap && segEnd - segStart > 2000)
            {
                // horizontally windowed: only the visible columns of a very long line are styled and drawn
                int firstCol = Math.Max(0, effScrollLeft - 8);
                int from = Math.Max(segStart, EditorTextLayout.CharIndexForVisualColumn(fullLineText, firstCol, tab));
                int cols = Math.Max(120, (codeAreaRight - GutterWidth) / Math.Max(1, CharWidth) + 24);
                if (from > segStart) { startVcol = EditorTextLayout.VisualColumn(fullLineText, from, tab); segStart = from; }
                segEnd = Math.Min(segEnd, segStart + cols);
                if (segEnd <= segStart) return;
            }
            List<StyledSpan> spans = GetLineSpansWF(buf, docLine, fullLineText, th, segStart, segEnd);
            int vis = startVcol;   // absolute visual column of 'pos' within this row
            int pos = segStart;
            foreach (StyledSpan sp in spans)
            {
                int s = Math.Max(sp.Start, segStart), e = Math.Min(sp.Start + sp.Length, segEnd);
                if (e <= s) { if (sp.Start >= segEnd) break; continue; }
                if (pos < s) { vis = EditorTextLayout.VisualColumnFrom(fullLineText, pos, s, tab, vis); pos = s; }
                int x = GutterWidth + 8 + (vis - effScrollLeft) * CharWidth;
                if (x > codeAreaRight) break;
                Font f = sp.Bold ? boldFont : (sp.Italic ? italicFont : regularFont);
                if (EditorTextLayout.IsSimpleRun(fullLineText, s, e))
                {
                    int w = e - s;
                    if (x + w * CharWidth >= GutterWidth)
                    {
                        string text = fullLineText.Substring(s, e - s);
                        if (text.Trim().Length > 0)
                            TextRenderer.DrawText(g, text, f, new Point(x, y), sp.Color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    vis += w;
                }
                else
                {
                    for (int i = s; i < e; i++)
                    {
                        char ch = fullLineText[i];
                        if (ch == '\t') { vis += tab - (vis % tab); continue; }
                        int cells = EditorTextLayout.CellsForChar(fullLineText, i);
                        int clusterEnd = i + ((char.IsHighSurrogate(ch) && i + 1 < e) ? 2 : 1);
                        while (clusterEnd < e && fullLineText[clusterEnd] != '\t' && EditorTextLayout.CellsForChar(fullLineText, clusterEnd) == 0) clusterEnd++;
                        int gx = GutterWidth + 8 + (vis - effScrollLeft) * CharWidth;
                        if (gx + CharWidth * Math.Max(1, cells) >= GutterWidth && gx <= codeAreaRight && !char.IsWhiteSpace(ch))
                            TextRenderer.DrawText(g, fullLineText.Substring(i, clusterEnd - i), f, new Point(gx, y), sp.Color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        vis += cells;
                        i = clusterEnd - 1;
                    }
                }
                pos = e;
            }
        }

        // ------------------------------------------------------------------ guides
        private void DrawNicetyGuidesWF(Graphics g, EditorTabBuffer buf, int docLine, int y, int effScrollLeft, int codeAreaRight, StudioTheme th)
        {
            if (wfSession == null) return;
            int tab = NicetyTabSizeWF(buf);
            if (EditorNicetyOptions.IndentGuides)
            {
                List<int> cols = EditorIndentGuides.GuideColumns(buf.Lines, docLine, tab, wfSession.Brackets.Profile.IndentBased);
                if (cols.Count > 0)
                {
                    using (Pen pen = new Pen(UiTokens.IndentGuide(th, th.BgEditor)))
                    using (Pen active = new Pen(UiTokens.IndentGuideActive(th, th.BgEditor)))
                    {
                        foreach (int c in cols)
                        {
                            int x = GutterWidth + 8 + (c - effScrollLeft) * CharWidth;
                            if (x <= GutterWidth || x >= codeAreaRight) continue;
                            bool isActive = wfGuide.IsValid && c == wfGuide.Column && docLine >= wfGuide.StartLine && docLine <= wfGuide.EndLine;
                            g.DrawLine(isActive ? active : pen, x, y, x, y + LineHeight);
                        }
                    }
                }
            }
            if (EditorNicetyOptions.BracketPairGuides && wfPair.IsValid && wfPair.CloseLine > wfPair.OpenLine + 1 && docLine > wfPair.OpenLine && docLine < wfPair.CloseLine)
            {
                int openIndent = Math.Max(0, EditorIndentGuides.IndentColumns(buf.Lines[wfPair.OpenLine], tab));
                int closeCol = CharColToVisualCol(buf.Lines[wfPair.CloseLine] ?? "", 0, wfPair.CloseCol);
                int col = Math.Min(openIndent, closeCol);
                int x = GutterWidth + 8 + (col - effScrollLeft) * CharWidth;
                if (x > GutterWidth && x < codeAreaRight)
                    using (Pen bp = new Pen(UiTokens.BracketPairGuide(th, wfPair.Level)))
                        g.DrawLine(bp, x, y, x, y + LineHeight);
            }
        }

        // ------------------------------------------------------------------ occurrences
        private void UpdateOccurrenceStateWF(EditorTabBuffer buf, int version)
        {
            if (!EditorNicetyOptions.HighlightOccurrences || buf.Lines.Count > 500000 || buf.HasMultiCursors) { wfOccTarget = null; return; }
            bool hasSel = buf.HasSelection();
            if (ReferenceEquals(wfOccBuf, buf) && wfOccLine == buf.CursorLine && wfOccCol == buf.CursorCol && wfOccVersion == version && wfOccHasSel == hasSel) return;
            bool contentChanged = !ReferenceEquals(wfOccBuf, buf) || wfOccVersion != version;
            wfOccBuf = buf; wfOccLine = buf.CursorLine; wfOccCol = buf.CursorCol; wfOccVersion = version; wfOccHasSel = hasSel;
            bool whole;
            string next = ComputeOccurrenceTargetWF(buf, out whole);
            if (!contentChanged && next != null && next == wfOccTarget && whole == wfOccWhole) return;
            wfOccTarget = null;
            if (next == null) return;
            if (wfOccTimer == null)
            {
                wfOccTimer = new System.Windows.Forms.Timer();
                wfOccTimer.Interval = UiTokens.OccurrenceHighlightDelayMs;
                wfOccTimer.Tick += delegate
                {
                    wfOccTimer.Stop();
                    EditorTabBuffer b = owner.CurrentBuffer;
                    if (b == null || !ReferenceEquals(b, wfOccBuf)) return;
                    bool w;
                    wfOccTarget = ComputeOccurrenceTargetWF(b, out w);
                    wfOccWhole = w;
                    Invalidate();
                };
            }
            wfOccTimer.Stop();
            wfOccTimer.Start();
        }

        private static string ComputeOccurrenceTargetWF(EditorTabBuffer buf, out bool whole)
        {
            return EditorOccurrences.GetHighlightTarget(buf, out whole);
        }

        /// <summary>Applies the debounced highlight now (tests).</summary>
        public void FlushOccurrenceHighlight()
        {
            EditorTabBuffer b = owner.CurrentBuffer;
            if (b == null) return;
            if (wfOccTimer != null) wfOccTimer.Stop();
            wfOccTarget = EditorNicetyOptions.HighlightOccurrences ? ComputeOccurrenceTargetWF(b, out wfOccWhole) : null;
            wfOccBuf = b; wfOccLine = b.CursorLine; wfOccCol = b.CursorCol; wfOccHasSel = b.HasSelection();
            EditorSyntaxSession s = EditorSyntaxSession.For(b);
            wfOccVersion = s.Brackets != null ? s.Brackets.Version : -1;
            Invalidate();
        }

        private void DrawOccurrencesWF(Graphics g, string fullLineText, int docLine, int segStart, int segEnd, int y, int effScrollLeft, int codeAreaRight, StudioTheme th, int segStartVcol = 0)
        {
            int tab = TabWF(owner.CurrentBuffer);
            if (wfOccTarget == null || string.IsNullOrEmpty(fullLineText) || fullLineText.Length > EditorOccurrences.MaxLineLengthForHighlight) return;
            List<OccurrenceMatch> matches = new List<OccurrenceMatch>();
            EditorOccurrences.FindInLine(fullLineText, docLine, wfOccTarget, wfOccWhole, true, matches);
            if (matches.Count == 0) return;
            Color borderColor = UiTokens.WordHighlightBorder(th);
            using (SolidBrush fill = new SolidBrush(wfOccWhole ? UiTokens.WordHighlightBackground(th) : UiTokens.SelectionHighlightBackground(th)))
            using (Pen border = new Pen(borderColor))
            {
                foreach (OccurrenceMatch m in matches)
                {
                    int s = Math.Max(segStart, m.Col), e = Math.Min(segEnd, m.Col + m.Length);
                    if (e <= s) continue;
                    int x1 = GutterWidth + 8 + (EditorTextLayout.VisualColumnFrom(fullLineText, segStart, s, tab, segStartVcol) - effScrollLeft) * CharWidth;
                    int x2 = GutterWidth + 8 + (EditorTextLayout.VisualColumnFrom(fullLineText, segStart, e, tab, segStartVcol) - effScrollLeft) * CharWidth;
                    if (x2 <= GutterWidth || x1 >= codeAreaRight) continue;
                    x1 = Math.Max(GutterWidth, x1);
                    g.FillRectangle(fill, x1, y + 1, Math.Max(1, x2 - x1), LineHeight - 2);
                    if (borderColor.A > 0) g.DrawRectangle(border, x1, y + 1, Math.Max(1, x2 - x1 - 1), LineHeight - 3);
                    LastFrameOccurrenceCount++;
                }
            }
        }

        // ------------------------------------------------------------------ bracket match
        private bool TryNicetyBracketMatchWF(EditorTabBuffer buf, out int brLineA, out int brColA, out int brLineB, out int brColB)
        {
            brLineA = brColA = brLineB = brColB = -1;
            if (wfSession == null) return false;
            BracketPair p;
            if (!wfSession.Brackets.TryFindMatchAt(buf.Lines, buf.CursorLine, buf.CursorCol, out p)) return false;
            brLineA = p.OpenLine; brColA = p.OpenCol; brLineB = p.CloseLine; brColB = p.CloseCol;
            return true;
        }

        private bool NicetyBracketsActiveWF { get { return wfSession != null && wfSession.Brackets.Profile.Enabled; } }

        // ------------------------------------------------------------------ sticky scroll
        private int StickyDocLineAtRow(int row)
        {
            if (row < 0 || row >= lastPaintedRows.Count) return -1;
            return lastPaintedRows[row].DocLine;
        }

        private void RenderStickyScrollWF(Graphics g, EditorTabBuffer buf, int codeAreaRight)
        {
            wfStickyHits.Clear();
            wfLastSticky = new List<int>();
            if (!EditorNicetyOptions.StickyScroll || wfSession == null || Height < LineHeight * 8 || lastPaintedRows.Count == 0) return;
            int maxLines = Math.Min(UiTokens.StickyScrollMaxLines, Math.Max(1, (Height / LineHeight) / 4));
            List<int> sticky = EditorStickyScroll.ComputeForViewport(buf.Lines, StickyDocLineAtRow, wfSession.Brackets, wfSession.Brackets.Profile,
                LspSymbolsStore.GetScopeRanges(buf.FilePath), NicetyTabSizeWF(buf), maxLines);
            // VS Code never pins a collapsed region's header (its body is not on screen)
            if (buf.FoldedLines != null && buf.FoldedLines.Count > 0) sticky.RemoveAll(delegate (int l) { return buf.FoldedLines.Contains(l); });
            if (sticky.Count == 0) return;
            wfLastSticky = sticky;
            StudioTheme th = owner.Theme;
            int effScrollLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;
            int bottom = sticky.Count * LineHeight + 4;
            using (SolidBrush bg = new SolidBrush(UiTokens.StickyScrollBackground(th)))
            using (SolidBrush hover = new SolidBrush(UiTokens.StickyScrollHover(th)))
            using (SolidBrush gutter = new SolidBrush(th.BgSidebar))
            using (Pen divider = new Pen(th.BorderSubtle))
            {
                g.FillRectangle(gutter, 0, 0, GutterWidth - 1, bottom);
                g.DrawLine(divider, GutterWidth - 1, 0, GutterWidth - 1, bottom);
                g.FillRectangle(bg, GutterWidth, 0, Math.Max(0, codeAreaRight - GutterWidth), bottom);
                for (int i = 0; i < sticky.Count; i++)
                {
                    int docLine = sticky[i];
                    int y = i * LineHeight + 4;
                    Rectangle row = new Rectangle(0, i == 0 ? 0 : y, codeAreaRight, i == 0 ? LineHeight + 4 : LineHeight);
                    if (i == wfStickyHover) g.FillRectangle(hover, GutterWidth, row.Y, Math.Max(0, codeAreaRight - GutterWidth), row.Height);
                    string numStr = (docLine + 1).ToString();
                    Size numSize = TextRenderer.MeasureText(numStr, gutterFont, new Size(200, 50), TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, numStr, gutterFont, new Point(GutterWidth - 18 - numSize.Width, y + 2), th.FgMuted, TextFormatFlags.NoPadding);
                    string text = buf.Lines[docLine] ?? "";
                    Region prev = g.Clip;
                    g.SetClip(new Rectangle(GutterWidth, y, Math.Max(1, codeAreaRight - GutterWidth), LineHeight));
                    DrawNicetyLineWF(g, buf, docLine, text, 0, Math.Min(text.Length, 2000), y + 2, effScrollLeft, codeAreaRight, th);
                    g.Clip = prev;
                    wfStickyHits.Add(new KeyValuePair<Rectangle, int>(row, docLine));
                }
            }
            Color shadow = UiTokens.StickyScrollShadow(th);
            Rectangle sr = new Rectangle(0, bottom, Math.Max(1, codeAreaRight), UiTokens.StickyScrollShadowHeight);
            using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(sr.X, sr.Y - 1, sr.Width, sr.Height + 1), shadow, Color.FromArgb(0, shadow), LinearGradientMode.Vertical))
                g.FillRectangle(lg, sr);
        }

        /// <summary>A caret moved onto a row covered by sticky lines is scrolled into view below them (VS Code).</summary>
        private void KeepCaretOutOfStickyScrollWF(EditorTabBuffer buf)
        {
            int n = wfLastSticky.Count;
            if (!EditorNicetyOptions.StickyScroll || n == 0 || buf.WordWrap || (buf.FoldedLines != null && buf.FoldedLines.Count > 0)) return;
            if (buf.CursorLine >= buf.ScrollTopLine && buf.CursorLine < buf.ScrollTopLine + n)
                buf.ScrollTopLine = Math.Max(0, buf.CursorLine - n);
        }

        /// <summary>Click on a sticky line reveals it at the top and puts the caret there (VS Code).</summary>
        public bool ClickStickyLineAt(Point pt)
        {
            for (int hi = 0; hi < wfStickyHits.Count; hi++)
            {
                KeyValuePair<Rectangle, int> hit = wfStickyHits[hi];
                if (!hit.Key.Contains(pt)) continue;
                EditorTabBuffer buf = owner.CurrentBuffer;
                if (buf == null) return false;
                int line = Math.Max(0, Math.Min(buf.Lines.Count - 1, hit.Value));
                buf.ClearExtraCarets();
                buf.CursorLine = line;
                string t = buf.Lines[line] ?? "";
                int ws = 0;
                while (ws < t.Length && (t[ws] == ' ' || t[ws] == '\t')) ws++;
                buf.CursorCol = ws;
                buf.ClearSelection();
                // the clicked header's ancestors stay pinned above it: reveal it right below them (VS Code)
                buf.ScrollTopLine = Math.Max(0, line - hi);
                if (hi < wfLastSticky.Count) wfLastSticky = wfLastSticky.GetRange(0, hi);
                owner.UpdateCursorStatusLabel();
                Invalidate();
                return true;
            }
            return false;
        }

        private bool UpdateStickyHoverWF(Point pt)
        {
            int idx = -1;
            for (int i = 0; i < wfStickyHits.Count; i++) if (wfStickyHits[i].Key.Contains(pt)) { idx = i; break; }
            if (idx != wfStickyHover) { wfStickyHover = idx; Invalidate(); }
            return idx >= 0;
        }

        // ------------------------------------------------------------------ input
        private TagLanguageMode TagModeWF(EditorTabBuffer buf)
        {
            return EditorTagEditing.ModeForFile(buf.FilePath ?? buf.FileName);
        }

        private static bool CaretInTagNameWF(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col < 0 || col > line.Length) return false;
            int k = col;
            while (k > 0 && EditorTagEditing.IsNameChar(line[k - 1])) k--;
            if (k > 0 && line[k - 1] == '<') return true;
            return k > 1 && line[k - 1] == '/' && line[k - 2] == '<';
        }

        private EditorTagEditing.LinkedEdit MaybeBeginLinkedTagEditWF(EditorTabBuffer buf)
        {
            if (!EditorNicetyOptions.LinkedTagEditing || buf.HasMultiCursors || buf.HasSelection()) return null;
            TagLanguageMode mode = TagModeWF(buf);
            if (mode == TagLanguageMode.None || buf.CursorLine < 0 || buf.CursorLine >= buf.Lines.Count) return null;
            if (!CaretInTagNameWF(buf.Lines[buf.CursorLine], buf.CursorCol)) return null;
            return EditorTagEditing.BeginLinkedEdit(buf.Lines, buf.CursorLine, buf.CursorCol, mode);
        }

        private void CompleteLinkedTagEditWF(EditorTabBuffer buf, EditorTagEditing.LinkedEdit le)
        {
            if (le == null) return;
            int delta = EditorTagEditing.CompleteLinkedEdit(buf.Lines, le, buf.CursorLine, buf.CursorCol);
            if (delta != int.MinValue && delta != 0)
            {
                buf.CursorCol = Math.Max(0, Math.Min(buf.Lines[buf.CursorLine].Length, buf.CursorCol + delta));
                buf.ClearSelection();
            }
        }

        /// <summary>Typed character with tag auto-close / close-tag completion / linked rename; false = default handling.</summary>
        private bool TryHandleNicetyKeyPressWF(char ch)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.HasMultiCursors || buf.HasSelection()) return false;
            TagLanguageMode mode = TagModeWF(buf);
            if (mode == TagLanguageMode.None) return false;
            bool closeChar = (ch == '>' || ch == '/') && EditorNicetyOptions.AutoCloseTags;
            EditorTagEditing.LinkedEdit le = (closeChar || EditorTagEditing.IsNameChar(ch)) ? MaybeBeginLinkedTagEditWF(buf) : null;
            if (!closeChar && le == null) return false;
            buf.PushUndoSnapshot(!closeChar);
            buf.GhostText = null;
            string line = buf.Lines[buf.CursorLine] ?? "";
            int col = Math.Max(0, Math.Min(line.Length, buf.CursorCol));
            line = line.Insert(col, ch.ToString());
            col++;
            if (closeChar)
            {
                buf.Lines[buf.CursorLine] = line;
                if (ch == '>')
                {
                    string close = EditorTagEditing.GetAutoCloseText(buf.Lines, buf.CursorLine, col, mode);
                    if (close != null) line = line.Substring(0, col) + close + line.Substring(col);
                }
                else
                {
                    string rest = EditorTagEditing.GetCloseTagCompletion(buf.Lines, buf.CursorLine, col, mode);
                    if (rest != null)
                    {
                        line = line.Substring(0, col) + rest + line.Substring(col);
                        col = Math.Min(line.Length, col + rest.Length + (rest.EndsWith(">") ? 0 : 1));
                    }
                }
            }
            buf.Lines[buf.CursorLine] = line;
            buf.CursorCol = col;
            buf.ClearSelection();
            CompleteLinkedTagEditWF(buf, le);
            return true;
        }

        /// <summary>Edit → Emmet: Expand Abbreviation (works even when "Emmet on Tab" is off, like VS Code's command).</summary>
        public bool ExpandEmmetAbbreviation(EditorTabBuffer buf)
        {
            bool prev = EditorNicetyOptions.EmmetOnTab;
            EditorNicetyOptions.EmmetOnTab = true;
            try
            {
                bool ok = TryHandleNicetyTabWF(buf);
                if (ok) Invalidate();
                return ok;
            }
            finally { EditorNicetyOptions.EmmetOnTab = prev; }
        }

        /// <summary>Emmet on Tab; false = Tab keeps its normal behaviour.</summary>
        private bool TryHandleNicetyTabWF(EditorTabBuffer buf)
        {
            if (!EditorNicetyOptions.EmmetOnTab || buf == null || buf.HasMultiCursors || buf.HasSelection()) return false;
            EmmetSyntax syntax = EmmetExpander.SyntaxForFile(buf.FilePath ?? buf.FileName);
            if (syntax == EmmetSyntax.None || buf.CursorLine < 0 || buf.CursorLine >= buf.Lines.Count) return false;
            string line = buf.Lines[buf.CursorLine] ?? "";
            bool inRule = syntax == EmmetSyntax.Css && EmmetExpander.IsInsideCssRule(buf.Lines, buf.CursorLine, buf.CursorCol);
            int start, caretOffset;
            string expansion;
            if (!EmmetExpander.TryExpand(line, buf.CursorCol, syntax, GetIndentUnit(), inRule, out start, out expansion, out caretOffset)) return false;
            buf.PushUndoSnapshot();
            buf.GhostText = null;
            int caretLine = buf.CursorLine;
            string before = line.Substring(0, start), after = line.Substring(Math.Min(line.Length, buf.CursorCol));
            string[] parts = expansion.Split('\n');
            int cl = 0, cc = caretOffset;
            for (int i = 0; i < parts.Length; i++)
            {
                if (cc <= parts[i].Length) { cl = i; break; }
                cc -= parts[i].Length + 1;
            }
            if (parts.Length == 1) buf.Lines[caretLine] = before + parts[0] + after;
            else
            {
                buf.Lines[caretLine] = before + parts[0];
                List<string> insert = new List<string>();
                for (int i = 1; i < parts.Length; i++) insert.Add(i == parts.Length - 1 ? parts[i] + after : parts[i]);
                buf.Lines.InsertRange(caretLine + 1, insert);
            }
            buf.CursorLine = caretLine + cl;
            buf.CursorCol = (cl == 0 ? before.Length : 0) + cc;
            buf.ClearSelection();
            return true;
        }

        /// <summary>
        /// Ctrl+F2 "Change All Occurrences": every occurrence of the selection (substring) or of the word under the caret
        /// (whole word, case-sensitive) becomes a selection; the primary caret stays on the current occurrence.
        /// </summary>
        public void ChangeAllOccurrences()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            string needle;
            bool whole;
            int curLine = buf.CursorLine, curCol = buf.CursorCol;
            if (buf.HasSelection())
            {
                needle = buf.GetSelectedText();
                whole = false;
                int eL, eC;
                buf.GetNormalizedSelection(out curLine, out curCol, out eL, out eC);
            }
            else
            {
                int ws, we;
                needle = EditorOccurrences.GetWordAt(buf.Lines[buf.CursorLine], buf.CursorCol, out ws, out we);
                whole = true;
                curCol = ws;
            }
            if (string.IsNullOrEmpty(needle) || needle.Contains("\n")) return;
            List<OccurrenceMatch> matches = EditorOccurrences.FindAll(buf.Lines, needle, whole, true);
            if (matches.Count == 0) return;
            int primary = 0;
            for (int i = 0; i < matches.Count; i++)
                if (matches[i].Line == curLine && matches[i].Col <= curCol && curCol <= matches[i].Col + matches[i].Length) { primary = i; break; }
            buf.ClearExtraCarets();
            OccurrenceMatch pm = matches[primary];
            buf.SelectionAnchorLine = pm.Line;
            buf.SelectionAnchorCol = pm.Col;
            buf.CursorLine = pm.Line;
            buf.CursorCol = pm.Col + pm.Length;
            for (int i = 0; i < matches.Count; i++)
            {
                if (i == primary) continue;
                OccurrenceMatch m = matches[i];
                EditorCaret c = new EditorCaret();
                c.AnchorLine = m.Line;
                c.AnchorCol = m.Col;
                c.CursorLine = m.Line;
                c.CursorCol = m.Col + m.Length;
                buf.ExtraCarets.Add(c);
            }
            owner.UpdateCursorStatusLabel();
            Invalidate();
        }
    }
}
