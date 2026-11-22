using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code editor niceties for the Avalonia editor: bracket pair colorization and the active bracket pair guide,
    /// indent guides with the active block highlighted, sticky scroll, word-under-caret highlights and Change All
    /// Occurrences, HTML/XML/JSX auto-close / linked tag rename, Emmet on Tab, and TextMate grammar highlighting.
    /// All analysis lives in codeLess.Core (shared with the WinForms editor); this file only paints and routes input.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        private EditorSyntaxSession frameSession;
        private int frameVisTop, frameVisBottom;
        private bool frameHadGuesses;
        private long frameTokenDeadline, frameBracketDeadline;
        private DispatcherTimer nicetiesBgTimer;
        private DispatcherTimer occurrenceTimer;
        private string occurrenceTarget;
        private bool occurrenceWholeWord = true;
        private EditorTabBuffer occBuf;
        private int occLine = -1, occCol = -1, occVersion = -1;
        private bool occHasSel;
        private int guideVersion = -1, guideCaretLine = -1, guideTab = -1;
        private EditorTabBuffer guideBuf;
        private ActiveIndentGuide activeGuide = ActiveIndentGuide.None;
        private BracketPair activeBracketPair = BracketPair.None;
        private readonly List<(Rect Rect, int Line)> stickyHitBoxes = new List<(Rect, int)>();
        private List<int> lastStickyLines = new List<int>();
        private int stickyHoverIndex = -1;

        /// <summary>Doc lines pinned by sticky scroll in the last frame (tests / screenshot suite).</summary>
        public IReadOnlyList<int> LastStickyLines => lastStickyLines;
        /// <summary>Word / selection currently highlighted (null = none).</summary>
        public string CurrentOccurrenceTarget => occurrenceTarget;
        /// <summary>Occurrence rectangles painted in the last frame.</summary>
        public int LastFrameOccurrenceCount { get; private set; }
        /// <summary>Lines painted with TextMate tokens in the last frame (tests).</summary>
        public int LastFrameTextMateLines { get; private set; }
        /// <summary>Lines painted with the (cached, multi-line aware) fallback tokenizer in the last frame (tests).</summary>
        public int LastFrameFallbackLines { get; private set; }
        /// <summary>Tests: record the styled tokens of every painted slice in <see cref="LastFrameTokens"/>.</summary>
        public bool CaptureFrameTokens { get; set; }
        /// <summary>Styled tokens painted in the last frame per (doc line, slice start) when <see cref="CaptureFrameTokens"/> is on.</summary>
        public readonly List<(int DocLine, int SliceStart, List<SyntaxTokenSpan> Tokens)> LastFrameTokens = new List<(int, int, List<SyntaxTokenSpan>)>();
        /// <summary>Tests: (doc line, visual column, x) of every plain token run drawn in the last frame.</summary>
        public readonly List<(int DocLine, int Vcol, double X)> LastFrameRunOrigins = new List<(int, int, double)>();
        /// <summary>Left edge of the clip the code text of the last frame was drawn in (tests: never inside the gutter).</summary>
        public double LastFrameTextClipLeft { get; private set; }
        public BracketPair ActiveBracketPair => activeBracketPair;
        public ActiveIndentGuide ActiveIndentGuideInfo => activeGuide;
        public EditorSyntaxSession SyntaxSession => Buffer == null ? null : EditorSyntaxSession.For(Buffer);

        private int NicetyTabSize(EditorTabBuffer buf)
        {
            return TabFor(buf);
        }

        // ------------------------------------------------------------------ per-frame state
        private void BeginNicetiesFrame(EditorTabBuffer buf, Func<int, int> rowToDocLine, int maxRows)
        {
            LastFrameOccurrenceCount = 0;
            LastFrameTextMateLines = 0;
            LastFrameFallbackLines = 0;
            LastFrameTokens.Clear();
            LastFrameRunOrigins.Clear();
            frameHadGuesses = false;
            frameSession = EditorSyntaxSession.For(buf);
            frameSession.Prepare(buf);
            frameVisTop = Math.Max(0, rowToDocLine(0));
            int bottom = frameVisTop;
            for (int r = maxRows; r >= 0; r--) { int d = rowToDocLine(r); if (d >= 0) { bottom = d; break; } }
            frameVisBottom = bottom;
            frameTokenDeadline = EditorSyntaxSession.DeadlineFromNow(EditorSyntaxSession.SyncTokenizeBudgetMs);
            frameBracketDeadline = EditorSyntaxSession.DeadlineFromNow(60);

            var lines = buf.Lines;
            int version = frameSession.Brackets.Version;
            if (EditorNicetyOptions.IndentGuides || EditorNicetyOptions.BracketPairGuides)
            {
                int tab = NicetyTabSize(buf);
                if (!ReferenceEquals(guideBuf, buf) || guideVersion != version || guideCaretLine != buf.CursorLine || guideTab != tab)
                {
                    guideBuf = buf;
                    guideVersion = version;
                    guideCaretLine = buf.CursorLine;
                    guideTab = tab;
                    activeGuide = EditorIndentGuides.FindActive(lines, buf.CursorLine, tab, frameSession.Brackets.Profile.IndentBased);
                }
            }
            activeBracketPair = (EditorNicetyOptions.BracketPairGuides && frameSession.Brackets.Profile.Enabled)
                ? frameSession.Brackets.FindEnclosingPair(lines, buf.CursorLine, buf.CursorCol)
                : BracketPair.None;
            UpdateOccurrenceState(buf, version);
        }

        private void EndNicetiesFrame(EditorTabBuffer buf)
        {
            if (frameSession != null && frameSession.NeedsBackgroundWork) EnsureNicetiesBackgroundTimer();
        }

        private void EnsureNicetiesBackgroundTimer()
        {
            if (nicetiesBgTimer == null)
            {
                nicetiesBgTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
                nicetiesBgTimer.Tick += (s, e) =>
                {
                    var buf = Buffer;
                    if (buf == null || !IsVisible) { nicetiesBgTimer.Stop(); return; }
                    var session = EditorSyntaxSession.For(buf);
                    bool repaint = session.AdvanceBackground(buf, frameVisTop, frameVisBottom, 8);
                    if (repaint || (frameHadGuesses && session.TextMate != null && session.TextMate.ValidCount > frameVisBottom)) InvalidateVisual();
                    if (!session.NeedsBackgroundWork) nicetiesBgTimer.Stop();
                };
            }
            if (!nicetiesBgTimer.IsEnabled) nicetiesBgTimer.Start();
        }

        /// <summary>Runs pending background tokenization to completion (tests / benchmarks).</summary>
        public void CompleteBackgroundTokenization(int maxMs = 30000)
        {
            var buf = Buffer;
            if (buf == null) return;
            var session = EditorSyntaxSession.For(buf);
            session.Prepare(buf);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (session.NeedsBackgroundWork && sw.ElapsedMilliseconds < maxMs)
            {
                session.AdvanceBackground(buf, frameVisTop, frameVisBottom, 50);
                if (session.GrammarPending) System.Threading.Thread.Sleep(5);
            }
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ tokens
        /// <summary>Styled tokens for the drawn slice of a line (TextMate or built-in, plus bracket pair colors).</summary>
        private List<SyntaxTokenSpan> GetNicetyTokens(EditorTabBuffer buf, int docLine, string lineText, int sliceStart, string renderLineText)
        {
            var session = frameSession;
            int sliceLen = renderLineText.Length;
            List<StyledSpan> spans = null;
            if (session == null)
            {
                // no frame session: still tokenize the whole logical line, then cut the slice (quote parity)
                spans = FallbackTokenStyles.SliceSpans(FallbackTokenizer.Tokenize(lineText, buf.Language), lineText.Length, sliceStart, sliceStart + sliceLen, Theme, false);
            }
            else
            {
                // lines longer than the TextMate limit (VS Code paints them plain) keep the fallback colors
                if (EditorNicetyOptions.TextMateHighlighting && session.TextMate != null && !session.IsTooLongForTextMate(lineText))
                {
                    spans = session.GetTextMateSpans(buf.Lines, docLine, frameVisTop, Theme, frameTokenDeadline, out bool guess);
                    if (spans != null) { LastFrameTextMateLines++; if (guess) frameHadGuesses = true; }
                }
                if (spans == null)
                {
                    // full-line tokens (cached per line, multi-line strings / comments carried over) cut to the slice:
                    // tokenizing only the rendered slice flipped the quote parity when it started inside a string
                    spans = session.GetFallbackSpans(buf.Lines, docLine, buf.Language, Theme, sliceStart, sliceStart + sliceLen, false, frameTokenDeadline, out bool fbGuess);
                    if (spans != null) LastFrameFallbackLines++;
                    if (fbGuess) frameHadGuesses = true;
                }
                if (spans == null) spans = FallbackTokenStyles.SliceSpans(FallbackTokenizer.Tokenize(lineText, buf.Language), lineText.Length, sliceStart, sliceStart + sliceLen, Theme, false);
            }
            if (session != null && EditorNicetyOptions.BracketPairColorization && session.Brackets.Profile.Enabled)
            {
                var bd = session.Brackets.GetLine(buf.Lines, docLine, frameBracketDeadline);
                spans = EditorSyntaxSession.ApplyBracketColors(spans, bd, Theme);
            }
            var res = new List<SyntaxTokenSpan>(spans.Count);
            int sliceEnd = sliceStart + sliceLen;
            foreach (var sp in spans)
            {
                int s = Math.Max(sp.Start, sliceStart), e = Math.Min(sp.Start + sp.Length, sliceEnd);
                if (e <= s) continue;
                res.Add(new SyntaxTokenSpan
                {
                    Text = lineText.Substring(s, e - s),
                    Color = AvaloniaColorHelper.ToAvColor(sp.Color),
                    IsBold = sp.Bold,
                    IsItalic = sp.Italic
                });
            }
            if (CaptureFrameTokens) LastFrameTokens.Add((docLine, sliceStart, res));
            return res;
        }

        /// <summary>Draws a token run on the visual-column grid (same rules as the main text pass).</summary>
        private void DrawTokenRun(DrawingContext context, List<SyntaxTokenSpan> tokens, double codeBaseX, double y, int startVcol, double codeAreaRight)
        {
            int renderVcol = startVcol;
            int tabStop = TabFor(Buffer);
            foreach (var tk in tokens)
            {
                var tf = tk.IsBold ? CachedFontBold : (tk.IsItalic ? CachedFontItalic : CachedFontRegular);
                var brush = new SolidColorBrush(tk.Color);
                string t = tk.Text ?? "";
                for (int ci = 0; ci < t.Length; ci++)
                {
                    char ch = t[ci];
                    if (ch == '\t') { renderVcol += tabStop - (renderVcol % tabStop); continue; }
                    int cells = CellsForChar(t, ci);
                    string glyph = (char.IsHighSurrogate(ch) && ci + 1 < t.Length) ? t.Substring(ci, 2) : ch.ToString();
                    double gx0 = codeBaseX + renderVcol * CharWidth;
                    if (gx0 + CharWidth * Math.Max(1, cells) >= GutterWidth && gx0 <= codeAreaRight && !char.IsLowSurrogate(ch) && !char.IsWhiteSpace(ch))
                    {
                        var gft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSizePx, brush);
                        context.DrawText(gft, new Point(gx0, y + (LineHeight - gft.Height) / 2.0));
                    }
                    if (glyph.Length == 2) ci++;
                    renderVcol += cells;
                    if (codeBaseX + renderVcol * CharWidth > codeAreaRight + 40) return;
                }
            }
        }

        // ------------------------------------------------------------------ guides
        private void DrawNicetyGuides(DrawingContext context, EditorTabBuffer buf, int docLine, double y, double codeBaseX, double codeAreaRight)
        {
            var session = frameSession;
            if (session == null) return;
            var lines = buf.Lines;
            int tab = NicetyTabSize(buf);
            if (EditorNicetyOptions.IndentGuides)
            {
                List<int> cols = EditorIndentGuides.GuideColumns(lines, docLine, tab, session.Brackets.Profile.IndentBased);
                if (cols.Count > 0)
                {
                    var pen = new Pen(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.IndentGuide(Theme, Theme.BgEditor))), 1);
                    var activePen = new Pen(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.IndentGuideActive(Theme, Theme.BgEditor))), 1);
                    foreach (int c in cols)
                    {
                        double x = Math.Round(codeBaseX + c * CharWidth) + 0.5;
                        if (x < GutterWidth + 2 || x > codeAreaRight) continue;
                        bool active = activeGuide.IsValid && c == activeGuide.Column && docLine >= activeGuide.StartLine && docLine <= activeGuide.EndLine;
                        context.DrawLine(active ? activePen : pen, new Point(x, y), new Point(x, y + LineHeight));
                    }
                }
            }
            if (EditorNicetyOptions.BracketPairGuides && activeBracketPair.IsValid && activeBracketPair.CloseLine > activeBracketPair.OpenLine + 1 &&
                docLine > activeBracketPair.OpenLine && docLine < activeBracketPair.CloseLine)
            {
                int col = BracketGuideColumn(lines, activeBracketPair, tab);
                double x = Math.Round(codeBaseX + col * CharWidth) + 0.5;
                if (x >= GutterWidth + 2 && x <= codeAreaRight)
                {
                    var pen = new Pen(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.BracketPairGuide(Theme, activeBracketPair.Level))), 1);
                    context.DrawLine(pen, new Point(x, y), new Point(x, y + LineHeight));
                }
            }
        }

        /// <summary>Visual column of the bracket pair guide: the shallower of the opener line's indent and the closer.</summary>
        internal static int BracketGuideColumn(IList<string> lines, BracketPair p, int tab)
        {
            int openIndent = Math.Max(0, EditorIndentGuides.IndentColumns(lines[p.OpenLine], tab));
            string closeLine = lines[p.CloseLine] ?? "";
            int closeCol = VisualColumn(closeLine, p.CloseCol);
            return Math.Min(openIndent, closeCol);
        }

        // ------------------------------------------------------------------ occurrences
        private void UpdateOccurrenceState(EditorTabBuffer buf, int version)
        {
            if (!EditorNicetyOptions.HighlightOccurrences || buf.Lines.Count > 500000 || ExtraCursors.Count > 0)
            {
                occurrenceTarget = null;
                return;
            }
            bool hasSel = buf.HasSelection();
            if (ReferenceEquals(occBuf, buf) && occLine == buf.CursorLine && occCol == buf.CursorCol && occVersion == version && occHasSel == hasSel) return;
            bool contentChanged = !ReferenceEquals(occBuf, buf) || occVersion != version;
            occBuf = buf; occLine = buf.CursorLine; occCol = buf.CursorCol; occVersion = version; occHasSel = hasSel;
            string next = ComputeOccurrenceTarget(buf, out bool whole);
            if (!contentChanged && next != null && next == occurrenceTarget && whole == occurrenceWholeWord) return; // still on the same word
            occurrenceTarget = null;
            if (next == null) return;
            if (occurrenceTimer == null)
            {
                occurrenceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiTokens.OccurrenceHighlightDelayMs) };
                occurrenceTimer.Tick += (s, e) =>
                {
                    occurrenceTimer.Stop();
                    var b = Buffer;
                    if (b == null || !ReferenceEquals(b, occBuf)) return;
                    occurrenceTarget = ComputeOccurrenceTarget(b, out bool w);
                    occurrenceWholeWord = w;
                    InvalidateVisual();
                };
            }
            occurrenceTimer.Stop();
            occurrenceTimer.Start();
        }

        private static string ComputeOccurrenceTarget(EditorTabBuffer buf, out bool wholeWord)
        {
            return EditorOccurrences.GetHighlightTarget(buf, out wholeWord);
        }

        /// <summary>Applies the debounced highlight now (tests).</summary>
        public void FlushOccurrenceHighlight()
        {
            var b = Buffer;
            if (b == null) return;
            occurrenceTimer?.Stop();
            occurrenceTarget = EditorNicetyOptions.HighlightOccurrences ? ComputeOccurrenceTarget(b, out occurrenceWholeWord) : null;
            occBuf = b; occLine = b.CursorLine; occCol = b.CursorCol; occHasSel = b.HasSelection();
            occVersion = EditorSyntaxSession.For(b).Brackets?.Version ?? -1;
            InvalidateVisual();
        }

        private void DrawOccurrenceHighlights(DrawingContext context, EditorTabBuffer buf, EditorVisualRow vr, string lineText, double y, double codeBaseX, double codeAreaRight)
        {
            if (occurrenceTarget == null || string.IsNullOrEmpty(lineText) || lineText.Length > EditorOccurrences.MaxLineLengthForHighlight) return;
            int docLine = vr.DocLine;
            var matches = new List<OccurrenceMatch>();
            EditorOccurrences.FindInLine(lineText, docLine, occurrenceTarget, occurrenceWholeWord, true, matches);
            if (matches.Count == 0) return;
            var fill = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(occurrenceWholeWord ? UiTokens.WordHighlightBackground(Theme) : UiTokens.SelectionHighlightBackground(Theme)));
            var borderColor = UiTokens.WordHighlightBorder(Theme);
            Pen border = borderColor.A == 0 ? null : new Pen(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(borderColor)), 1);
            foreach (var m in matches)
            {
                int s0 = Math.Max(m.Col, vr.Start), e0 = vr.IsLastRow ? m.Col + m.Length : Math.Min(m.Col + m.Length, vr.End);
                if (e0 <= s0) continue;
                double x0 = RowColX(buf, vr, s0, codeBaseX);
                double x1 = RowColX(buf, vr, e0, codeBaseX);
                if (x1 < GutterWidth || x0 > codeAreaRight) continue;
                var r = new Rect(Math.Max(GutterWidth + 1, x0), y + 1, Math.Max(1, x1 - Math.Max(GutterWidth + 1, x0)), LineHeight - 2);
                context.DrawRectangle(fill, border, r, 2, 2);
                LastFrameOccurrenceCount++;
            }
        }

        // ------------------------------------------------------------------ bracket match (string/comment aware)
        private bool TryNicetyBracketMatch(EditorTabBuffer buf, out int line, out int col)
        {
            line = col = -1;
            var session = frameSession;
            if (session == null || !session.Brackets.Profile.Enabled) return false;
            if (!session.Brackets.TryFindMatchAt(buf.Lines, buf.CursorLine, buf.CursorCol, out BracketPair p)) return false;
            // highlight the partner (the bracket at the caret is highlighted by the caller's other box)
            if (p.OpenLine == buf.CursorLine && (p.OpenCol == buf.CursorCol || p.OpenCol == buf.CursorCol - 1)) { line = p.CloseLine; col = p.CloseCol; }
            else { line = p.OpenLine; col = p.OpenCol; }
            return true;
        }

        // ------------------------------------------------------------------ sticky scroll
        private void RenderStickyScroll(DrawingContext context, EditorTabBuffer buf, Func<int, int> rowToDocLine, double codeBaseX, double codeAreaRight)
        {
            stickyHitBoxes.Clear();
            lastStickyLines = new List<int>();
            if (!EditorNicetyOptions.StickyScroll || frameSession == null || Bounds.Height < LineHeight * 8) return;
            var lines = buf.Lines;
            List<StickySymbolRange> symbols = LspSymbolsStore.GetScopeRanges(buf.FilePath);
            int maxLines = Math.Min(UiTokens.StickyScrollMaxLines, Math.Max(1, (int)(Bounds.Height / LineHeight) / 4));
            List<int> sticky = EditorStickyScroll.ComputeForViewport(lines, rowToDocLine, frameSession.Brackets, frameSession.Brackets.Profile, symbols, NicetyTabSize(buf), maxLines);
            // VS Code never pins a collapsed region's header (its body is not on screen)
            if (buf.FoldedBlockRanges.Count > 0) sticky.RemoveAll(l => buf.FoldedBlockRanges.ContainsKey(l));
            if (sticky.Count == 0) return;
            lastStickyLines = sticky;
            var bg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.StickyScrollBackground(Theme)));
            var hover = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.StickyScrollHover(Theme)));
            var gutter = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.GutterBg));
            var lnBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.TextSecondary));
            for (int i = 0; i < sticky.Count; i++)
            {
                int docLine = sticky[i];
                double y = i * LineHeight;
                var rowRect = new Rect(0, y, codeAreaRight, LineHeight);
                context.FillRectangle(gutter, new Rect(0, y, GutterWidth, LineHeight));
                context.FillRectangle(i == stickyHoverIndex ? hover : bg, new Rect(GutterWidth, y, Math.Max(0, codeAreaRight - GutterWidth), LineHeight));
                var lnFt = new FormattedText((docLine + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedFontRegular, FontSizePx - 1.5, lnBrush);
                context.DrawText(lnFt, new Point(Math.Max(4, GutterWidth - 22 - lnFt.Width), y + (LineHeight - lnFt.Height) / 2.0));
                string text = lines[docLine] ?? "";
                string slice = text.Length > 2000 ? text.Substring(0, 2000) : text;
                var tokens = GetNicetyTokens(buf, docLine, text, 0, slice);
                using (context.PushClip(new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight)))
                    DrawTokenRun(context, tokens, codeBaseX, y, 0, codeAreaRight);
                stickyHitBoxes.Add((rowRect, docLine));
            }
            double bottom = sticky.Count * LineHeight;
            var shadow = AvaloniaColorHelper.ToAvColor(UiTokens.StickyScrollShadow(Theme));
            var grad = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(shadow, 0), new GradientStop(Color.FromArgb(0, shadow.R, shadow.G, shadow.B), 1) }
            };
            context.FillRectangle(grad, new Rect(0, bottom, codeAreaRight, UiTokens.StickyScrollShadowHeight));
        }

        /// <summary>Click on a sticky line: reveal that line at the top and put the caret on it (VS Code).</summary>
        private bool TryHandleStickyPointerPressed(Point pt)
        {
            for (int i = 0; i < stickyHitBoxes.Count; i++)
            {
                if (!stickyHitBoxes[i].Rect.Contains(pt)) continue;
                var buf = Buffer;
                if (buf == null) return false;
                int line = stickyHitBoxes[i].Line;
                // the clicked header's ancestors stay pinned above it: reveal it right below them (VS Code)
                lastStickyLines = lastStickyLines.Take(i).ToList();
                RevealLineAtTop(buf, line, i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The sticky lines cover the first rows: a caret moved onto one of them is scrolled into view below the widget
        /// (VS Code reveals with the sticky scroll height as offset).
        /// </summary>
        private void KeepCaretOutOfStickyScroll(EditorTabBuffer buf)
        {
            int n = lastStickyLines.Count;
            if (!EditorNicetyOptions.StickyScroll || n == 0) return;
            int cRow = CaretRowOf(buf, buf.CursorLine, buf.CursorCol, true);
            int dist = RowDistance(buf, buf.ScrollTopLine, buf.ScrollTopWrapRow, buf.CursorLine, cRow, n + 1);
            if (dist >= 0 && dist < n) ScrollByRows(buf, dist - n);
        }

        /// <summary>Clicks the sticky line under a canvas point (automation / tests); false when none is there.</summary>
        public bool ClickStickyLineAt(Point pt) => TryHandleStickyPointerPressed(pt);

        internal void RevealLineAtTop(EditorTabBuffer buf, int line, int rowsAbove = 0)
        {
            line = Math.Max(0, Math.Min(buf.Lines.Count - 1, line));
            buf.ClearSelection();
            ExtraCursors.Clear();
            buf.CursorLine = line;
            string t = buf.Lines[line] ?? "";
            int ws = 0;
            while (ws < t.Length && (t[ws] == ' ' || t[ws] == '\t')) ws++;
            buf.CursorCol = ws;
            buf.ScrollTopLine = line;
            buf.ScrollTopWrapRow = 0;
            ScrollByRows(buf, -rowsAbove);
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        private bool UpdateStickyHover(Point pt)
        {
            int idx = -1;
            for (int i = 0; i < stickyHitBoxes.Count; i++) if (stickyHitBoxes[i].Rect.Contains(pt)) { idx = i; break; }
            if (idx == stickyHoverIndex) return idx >= 0;
            stickyHoverIndex = idx;
            InvalidateVisual();
            return idx >= 0;
        }

        // ------------------------------------------------------------------ input: tags, emmet, change all
        private TagLanguageMode TagModeFor(EditorTabBuffer buf) => EditorTagEditing.ModeForFile(buf?.FilePath ?? buf?.FileName);

        /// <summary>Is the caret inside / at the end of a tag name on its line ("&lt;di|v", "&lt;/span|")?</summary>
        private static bool CaretInTagName(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col < 0 || col > line.Length) return false;
            int k = col;
            while (k > 0 && EditorTagEditing.IsNameChar(line[k - 1])) k--;
            if (k > 0 && line[k - 1] == '<') return true;
            return k > 1 && line[k - 1] == '/' && line[k - 2] == '<';
        }

        private EditorTagEditing.LinkedEdit MaybeBeginLinkedTagEdit(EditorTabBuffer buf)
        {
            if (!EditorNicetyOptions.LinkedTagEditing || ExtraCursors.Count > 0 || buf.HasSelection()) return null;
            var mode = TagModeFor(buf);
            if (mode == TagLanguageMode.None) return null;
            if (buf.CursorLine < 0 || buf.CursorLine >= buf.Lines.Count) return null;
            if (!CaretInTagName(buf.Lines[buf.CursorLine], buf.CursorCol)) return null;
            return EditorTagEditing.BeginLinkedEdit(buf.Lines, buf.CursorLine, buf.CursorCol, mode);
        }

        private void CompleteLinkedTagEdit(EditorTabBuffer buf, EditorTagEditing.LinkedEdit le)
        {
            if (le == null) return;
            int delta = EditorTagEditing.CompleteLinkedEdit(buf.Lines, le, buf.CursorLine, buf.CursorCol);
            if (delta != int.MinValue && delta != 0) buf.CursorCol = Math.Max(0, Math.Min(buf.Lines[buf.CursorLine].Length, buf.CursorCol + delta));
        }

        /// <summary>Typed text with tag auto-close / close-tag completion / linked rename. Returns false to use the default path.</summary>
        private bool TryHandleNicetyTextInput(string text)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(text) || text.Length != 1 || ExtraCursors.Count > 0 || buf.HasSelection()) return false;
            var mode = TagModeFor(buf);
            if (mode == TagLanguageMode.None) return false;
            char ch = text[0];
            bool closeChar = (ch == '>' || ch == '/') && EditorNicetyOptions.AutoCloseTags;
            var le = MaybeBeginLinkedTagEdit(buf);
            if (!closeChar && le == null) return false;
            buf.PushUndoSnapshot(coalesceTyping: !closeChar);
            buf.GhostTextSuggestion = "";
            buf.InsertTextAtCursor(text);
            if (closeChar)
            {
                string line = buf.Lines[buf.CursorLine];
                if (ch == '>')
                {
                    string close = EditorTagEditing.GetAutoCloseText(buf.Lines, buf.CursorLine, buf.CursorCol, mode);
                    if (close != null) buf.Lines[buf.CursorLine] = line.Substring(0, buf.CursorCol) + close + line.Substring(buf.CursorCol);
                }
                else
                {
                    string rest = EditorTagEditing.GetCloseTagCompletion(buf.Lines, buf.CursorLine, buf.CursorCol, mode);
                    if (rest != null)
                    {
                        buf.Lines[buf.CursorLine] = line.Substring(0, buf.CursorCol) + rest + line.Substring(buf.CursorCol);
                        buf.CursorCol += rest.Length + (rest.EndsWith(">") ? 0 : 1);
                        buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[buf.CursorLine].Length);
                    }
                }
            }
            CompleteLinkedTagEdit(buf, le);
            FinalizeBufferEdit(buf);
            TextTyped?.Invoke(text);
            return true;
        }

        /// <summary>Edit → Emmet: Expand Abbreviation (works even when "Emmet on Tab" is off, like VS Code's command).</summary>
        public bool ExpandEmmetAbbreviation()
        {
            bool prev = EditorNicetyOptions.EmmetOnTab;
            EditorNicetyOptions.EmmetOnTab = true;
            try
            {
                bool ok = TryHandleNicetyTab(Buffer);
                if (ok) InvalidateVisual();
                return ok;
            }
            finally { EditorNicetyOptions.EmmetOnTab = prev; }
        }

        /// <summary>Emmet expansion on Tab. Returns false (Tab behaves as usual) when nothing expands.</summary>
        private bool TryHandleNicetyTab(EditorTabBuffer buf)
        {
            if (!EditorNicetyOptions.EmmetOnTab || buf == null || ExtraCursors.Count > 0 || buf.HasSelection()) return false;
            var syntax = EmmetExpander.SyntaxForFile(buf.FilePath ?? buf.FileName);
            if (syntax == EmmetSyntax.None) return false;
            if (buf.CursorLine < 0 || buf.CursorLine >= buf.Lines.Count) return false;
            string line = buf.Lines[buf.CursorLine];
            bool inRule = syntax == EmmetSyntax.Css && EmmetExpander.IsInsideCssRule(buf.Lines, buf.CursorLine, buf.CursorCol);
            string unit = EditorIndentation.Unit(TabFor(buf), EditorIndentation.EffectiveInsertSpaces(buf, InsertSpacesSetting));
            if (!EmmetExpander.TryExpand(line, buf.CursorCol, syntax, unit, inRule, out int start, out string expansion, out int caretOffset)) return false;
            buf.PushUndoSnapshot();
            buf.GhostTextSuggestion = "";
            int caretLine = buf.CursorLine;
            string before = line.Substring(0, start), after = line.Substring(buf.CursorCol);
            string[] parts = expansion.Split('\n');
            // caret position inside the expansion
            int cl = 0, cc = caretOffset;
            for (int i = 0; i < parts.Length; i++)
            {
                if (cc <= parts[i].Length) { cl = i; break; }
                cc -= parts[i].Length + 1;
            }
            if (parts.Length == 1)
            {
                buf.Lines[caretLine] = before + parts[0] + after;
            }
            else
            {
                buf.Lines[caretLine] = before + parts[0];
                var insert = new List<string>();
                for (int i = 1; i < parts.Length; i++) insert.Add(i == parts.Length - 1 ? parts[i] + after : parts[i]);
                buf.Lines.InsertRange(caretLine + 1, insert);
            }
            buf.CursorLine = caretLine + cl;
            buf.CursorCol = (cl == 0 ? before.Length : 0) + cc;
            buf.ClearSelection();
            FinalizeBufferEdit(buf);
            return true;
        }

        /// <summary>
        /// Cmd/Ctrl+F2 "Change All Occurrences" (and Cmd/Ctrl+Shift+L): with the find widget open its matches, else every
        /// occurrence of the selection (substring) or of the word under the caret (whole word, case-sensitive). The
        /// primary caret stays on the current occurrence.
        /// </summary>
        public void ChangeAllOccurrences()
        {
            var buf = Buffer;
            if (buf == null) return;
            List<OccurrenceMatch> matches;
            int curLine = buf.CursorLine, curCol = buf.CursorCol;
            if (owner.IsFindOverlayVisible && owner.FindMatches.Count > 0)
            {
                matches = owner.FindMatches.Select(f => new OccurrenceMatch(f.Line, f.StartCol, f.Length)).ToList();
            }
            else
            {
                string needle;
                bool whole;
                if (buf.HasSelection())
                {
                    needle = buf.GetSelectedText();
                    whole = false;
                    buf.GetNormalizedSelection(out curLine, out curCol, out _, out _);
                }
                else
                {
                    needle = EditorOccurrences.GetWordAt(buf.Lines[buf.CursorLine], buf.CursorCol, out int ws, out int we);
                    whole = true;
                    curCol = ws;
                }
                if (string.IsNullOrEmpty(needle) || needle.Contains('\n')) return;
                matches = EditorOccurrences.FindAll(buf.Lines, needle, whole, true);
            }
            if (matches.Count == 0) return;
            int primary = 0;
            for (int i = 0; i < matches.Count; i++)
                if (matches[i].Line == curLine && matches[i].Col <= curCol && curCol <= matches[i].Col + matches[i].Length) { primary = i; break; }
            ExtraCursors.Clear();
            var pm = matches[primary];
            buf.SelectionStartLine = pm.Line;
            buf.SelectionStartCol = pm.Col;
            buf.CursorLine = pm.Line;
            buf.CursorCol = pm.Col + pm.Length;
            for (int i = 0; i < matches.Count; i++)
            {
                if (i == primary) continue;
                var m = matches[i];
                ExtraCursors.Add(new ExtraCursorCaret { AnchorLine = m.Line, AnchorCol = m.Col, Line = m.Line, Col = m.Col + m.Length });
            }
            owner.NotifyCaretMoved();
            owner.PublishStatus(matches.Count + " occurrences selected");
            InvalidateVisual();
        }
    }
}
