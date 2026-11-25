using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace codeLess.Studio
{
    /// <summary>Runtime switches for the editor niceties (mirrors the persisted settings; read by both editors).</summary>
    public static class EditorNicetyOptions
    {
        public static bool BracketPairColorization = true;
        public static bool BracketPairGuides = true;
        public static bool IndentGuides = true;
        public static bool StickyScroll = true;
        public static bool HighlightOccurrences = true;
        public static bool AutoCloseTags = true;
        public static bool LinkedTagEditing = true;
        public static bool EmmetOnTab = true;
        public static bool TextMateHighlighting = true;
        public static bool EmptySelectionClipboard = true;
        public static bool UseTabStops = true;
        public static bool AcceptSuggestionOnEnter = true;
        public static int CursorSurroundingLines = 0;
        public static string RenderWhitespace = "selection";

        public static void ApplyFrom(IdePersistentSettings s)
        {
            if (s == null) return;
            BracketPairColorization = s.EditorBracketPairColorization;
            BracketPairGuides = s.EditorBracketPairGuides;
            IndentGuides = s.EditorIndentGuides;
            StickyScroll = s.EditorStickyScroll;
            HighlightOccurrences = s.EditorHighlightOccurrences;
            AutoCloseTags = s.EditorAutoCloseTags;
            LinkedTagEditing = s.EditorLinkedTagEditing;
            EmmetOnTab = s.EditorEmmetOnTab;
            TextMateHighlighting = s.EditorTextMateHighlighting;
            EmptySelectionClipboard = s.EditorEmptySelectionClipboard;
            UseTabStops = s.EditorUseTabStops;
            AcceptSuggestionOnEnter = s.EditorAcceptSuggestionOnEnter;
            CursorSurroundingLines = s.EditorCursorSurroundingLines;
            RenderWhitespace = string.IsNullOrEmpty(s.EditorRenderWhitespace) ? (s.ShowRenderWhitespace ? "all" : "selection") : s.EditorRenderWhitespace;
            EditorWordNavigation.WordSeparators = string.IsNullOrEmpty(s.EditorWordSeparators) ? EditorWordNavigation.DefaultWordSeparators : s.EditorWordSeparators;
            EditorScrollOptions.ApplyFrom(s); // smooth scrolling / caret settings travel with the editor switches
            TerminalOptions.ApplyFrom(s); // terminal cursor / font / scrolling settings too
        }

        public static void CopyTo(IdePersistentSettings s)
        {
            if (s == null) return;
            s.EditorBracketPairColorization = BracketPairColorization;
            s.EditorBracketPairGuides = BracketPairGuides;
            s.EditorIndentGuides = IndentGuides;
            s.EditorStickyScroll = StickyScroll;
            s.EditorHighlightOccurrences = HighlightOccurrences;
            s.EditorAutoCloseTags = AutoCloseTags;
            s.EditorLinkedTagEditing = LinkedTagEditing;
            s.EditorEmmetOnTab = EmmetOnTab;
            s.EditorTextMateHighlighting = TextMateHighlighting;
            s.EditorEmptySelectionClipboard = EmptySelectionClipboard;
            s.EditorUseTabStops = UseTabStops;
            s.EditorAcceptSuggestionOnEnter = AcceptSuggestionOnEnter;
            s.EditorCursorSurroundingLines = CursorSurroundingLines;
            s.EditorRenderWhitespace = RenderWhitespace;
            s.EditorWordSeparators = EditorWordNavigation.WordSeparators;
            EditorScrollOptions.CopyTo(s);
            TerminalOptions.CopyTo(s);
        }
    }

    /// <summary>Persisted editor nicety switches (defaults on; absent keys keep the defaults when loading old files).</summary>
    public partial class IdePersistentSettings
    {
        private bool editorBracketPairColorization = true;
        private bool editorBracketPairGuides = true;
        private bool editorIndentGuides = true;
        private bool editorStickyScroll = true;
        private bool editorHighlightOccurrences = true;
        private bool editorAutoCloseTags = true;
        private bool editorLinkedTagEditing = true;
        private bool editorEmmetOnTab = true;
        private bool editorTextMateHighlighting = true;
        private bool editorEmptySelectionClipboard = true;
        private bool editorUseTabStops = true;
        private bool editorAcceptSuggestionOnEnter = true;
        private int editorCursorSurroundingLines = 0;
        private string editorRenderWhitespace = "selection";
        private string editorWordSeparators = EditorWordNavigation.DefaultWordSeparators;

        public bool EditorBracketPairColorization { get { return editorBracketPairColorization; } set { editorBracketPairColorization = value; } }
        public bool EditorBracketPairGuides { get { return editorBracketPairGuides; } set { editorBracketPairGuides = value; } }
        public bool EditorIndentGuides { get { return editorIndentGuides; } set { editorIndentGuides = value; } }
        public bool EditorStickyScroll { get { return editorStickyScroll; } set { editorStickyScroll = value; } }
        public bool EditorHighlightOccurrences { get { return editorHighlightOccurrences; } set { editorHighlightOccurrences = value; } }
        public bool EditorAutoCloseTags { get { return editorAutoCloseTags; } set { editorAutoCloseTags = value; } }
        public bool EditorLinkedTagEditing { get { return editorLinkedTagEditing; } set { editorLinkedTagEditing = value; } }
        public bool EditorEmmetOnTab { get { return editorEmmetOnTab; } set { editorEmmetOnTab = value; } }
        public bool EditorTextMateHighlighting { get { return editorTextMateHighlighting; } set { editorTextMateHighlighting = value; } }
        public bool EditorEmptySelectionClipboard { get { return editorEmptySelectionClipboard; } set { editorEmptySelectionClipboard = value; } }
        public bool EditorUseTabStops { get { return editorUseTabStops; } set { editorUseTabStops = value; } }
        public bool EditorAcceptSuggestionOnEnter { get { return editorAcceptSuggestionOnEnter; } set { editorAcceptSuggestionOnEnter = value; } }
        public int EditorCursorSurroundingLines { get { return editorCursorSurroundingLines; } set { editorCursorSurroundingLines = Math.Max(0, value); } }
        public string EditorRenderWhitespace { get { return editorRenderWhitespace; } set { editorRenderWhitespace = value ?? "selection"; } }
        public string EditorWordSeparators { get { return editorWordSeparators; } set { editorWordSeparators = string.IsNullOrEmpty(value) ? EditorWordNavigation.DefaultWordSeparators : value; } }
    }

    /// <summary>A colored run of a line: [Start, Start + Length) in character indices.</summary>
    public struct StyledSpan
    {
        public int Start;
        public int Length;
        public Color Color;
        public bool Bold;
        public bool Italic;

        public StyledSpan(int start, int length, Color color, bool bold, bool italic)
        {
            Start = start;
            Length = length;
            Color = color;
            Bold = bold;
            Italic = italic;
        }
    }

    /// <summary>
    /// Per-buffer syntax state shared by both editors: the incremental bracket analysis and, when a TextMate grammar
    /// exists for the file, the incremental TextMate tokenization. Call <see cref="Prepare"/> once per paint.
    /// </summary>
    public sealed class EditorSyntaxSession
    {
        private static readonly ConditionalWeakTable<EditorTabBuffer, EditorSyntaxSession> table = new ConditionalWeakTable<EditorTabBuffer, EditorSyntaxSession>();

        public static EditorSyntaxSession For(EditorTabBuffer buf)
        {
            if (buf == null) return null;
            return table.GetValue(buf, delegate (EditorTabBuffer b) { return new EditorSyntaxSession(); });
        }

        private string path;
        private bool pathInitialized;
        private EditorBracketCache brackets;
        private TextMateDocument textMate;
        private string scope;
        private int registryGeneration = -1;
        private bool grammarPending;
        private FallbackTokenCache fallback;
        private bool fallbackSynced;
        private bool fallbackUsed;

        /// <summary>Lines whose start state the paint pass may compute synchronously before falling back to a guess.</summary>
        public static int SyncTokenizeBudgetMs = 12;

        public EditorBracketCache Brackets { get { return brackets; } }
        public TextMateDocument TextMate { get { return textMate; } }
        public string GrammarScope { get { return scope; } }
        public bool GrammarPending { get { return grammarPending; } }

        /// <summary>
        /// The path the grammar is resolved from: the file path, or for an untitled editor its name plus the extension of
        /// its language mode ("Untitled-1" + "py"), so new files are highlighted in the workspace's language.
        /// </summary>
        public static string SyntaxPathFor(EditorTabBuffer buf)
        {
            if (buf == null) return "";
            if (!string.IsNullOrEmpty(buf.FilePath)) return buf.FilePath;
            string name = buf.FileName ?? "";
            string ext = "";
            try { ext = System.IO.Path.GetExtension(name); } catch { }
            if (string.IsNullOrEmpty(ext))
            {
                string lang = UntitledFileDefaults.NormalizeLanguageExtension(buf.Language);
                if (!string.IsNullOrEmpty(lang) && lang != "txt") return name + "." + lang;
            }
            return name;
        }

        private static string EffectiveSyntaxPathFor(EditorTabBuffer buf)
        {
            string p = SyntaxPathFor(buf);
            if (buf != null && buf.LanguageModeOverridden && !string.IsNullOrEmpty(buf.Language))
            {
                string lang = UntitledFileDefaults.NormalizeLanguageExtension(buf.Language);
                if (!string.IsNullOrEmpty(lang)) return p + "." + lang;
            }
            return p;
        }

        /// <summary>Resolves the grammar / profile for the buffer's file and syncs the caches with its lines.</summary>
        public void Prepare(EditorTabBuffer buf)
        {
            if (buf == null) return;
            string p = EffectiveSyntaxPathFor(buf);
            if (!pathInitialized || !string.Equals(p, path, StringComparison.Ordinal))
            {
                pathInitialized = true;
                path = p;
                brackets = new EditorBracketCache(BracketLexProfile.ForFile(p));
                textMate = null;
                scope = null;
                registryGeneration = -1;
            }
            ResolveGrammar();
            List<string> lines = buf.Lines;
            if (lines == null) return;
            brackets.Sync(lines);
            if (textMate != null) textMate.Sync(lines);
            fallbackSynced = false;
        }

        /// <summary>The fallback tokenizer cache (null until a line was painted without TextMate tokens).</summary>
        public FallbackTokenCache Fallback { get { return fallback; } }

        /// <summary>
        /// Full-line fallback tokens (no grammar, or a line too long for the TextMate tokenizer), with the line-start
        /// state of multi-line strings / comments and a per-line cache keyed by the line's text reference.
        /// </summary>
        public List<CodeToken> GetFallbackTokens(IList<string> lines, int line, string language, long deadlineTicks, out bool isGuess)
        {
            isGuess = false;
            if (lines == null || line < 0 || line >= lines.Count) return null;
            if (fallback == null || !string.Equals(fallback.Language, language ?? "", StringComparison.Ordinal))
            {
                fallback = new FallbackTokenCache(language);
                fallbackSynced = false;
            }
            if (!fallbackSynced) { fallback.Sync(lines); fallbackSynced = true; }
            fallbackUsed = true;
            return fallback.GetTokens(lines, line, deadlineTicks, out isGuess);
        }

        /// <summary>Fallback spans covering exactly [sliceStart, sliceEnd) of the line (only the slice's tokens are styled).</summary>
        public List<StyledSpan> GetFallbackSpans(IList<string> lines, int line, string language, StudioTheme theme, int sliceStart, int sliceEnd, bool colorOperators, long deadlineTicks, out bool isGuess)
        {
            List<CodeToken> toks = GetFallbackTokens(lines, line, language, deadlineTicks, out isGuess);
            if (toks == null) return null;
            return FallbackTokenStyles.SliceSpans(toks, (lines[line] ?? "").Length, sliceStart, sliceEnd, theme, colorOperators);
        }

        /// <summary>True when the TextMate tokenizer skips this line (longer than its limit; VS Code shows it plain).</summary>
        public bool IsTooLongForTextMate(string text)
        {
            int maxLen = textMate != null && textMate.Grammar != null ? textMate.Grammar.MaxLineLength : 20000;
            return text != null && text.Length > maxLen;
        }

        /// <summary>Known fallback line-start state (multi-line string / comment), or -1 when not computed.</summary>
        public int FallbackStartState(int line)
        {
            if (fallback == null || line < 0 || line > fallback.ValidCount) return -1;
            return fallback.StartStateOf(line);
        }

        private void ResolveGrammar()
        {
            if (!EditorNicetyOptions.TextMateHighlighting)
            {
                textMate = null;
                grammarPending = false;
                registryGeneration = -1;
                return;
            }
            TextMateGrammarRegistry reg = TextMateGrammarRegistry.Shared;
            if (registryGeneration == reg.Generation) return;
            registryGeneration = reg.Generation;
            string sc = reg.GetScopeForFile(path);
            if (sc == null) { textMate = null; scope = null; grammarPending = false; return; }
            TmCompiledGrammar g = reg.GetCompiled(sc, false);
            if (g == null)
            {
                grammarPending = reg.IsLoading(sc);
                if (!grammarPending) { textMate = null; scope = null; }
                return;
            }
            grammarPending = false;
            if (textMate == null || !ReferenceEquals(textMate.Grammar, g) || scope != sc)
            {
                scope = sc;
                textMate = new TextMateDocument(g);
            }
        }

        public static long DeadlineFromNow(int ms)
        {
            if (ms <= 0) return 0;
            return Stopwatch.GetTimestamp() + (long)(ms * (double)Stopwatch.Frequency / 1000.0);
        }

        /// <summary>True while a grammar is loading or lines still need a background tokenization pass.</summary>
        public bool NeedsBackgroundWork
        {
            get
            {
                if (grammarPending) return true;
                if (textMate != null && textMate.ValidCount < textMate.LineCount) return true;
                if (fallbackUsed && fallback != null && fallback.ValidCount < fallback.LineCount) return true;
                return false;
            }
        }

        /// <summary>
        /// Advances background tokenization within <paramref name="budgetMs"/>. Returns true when the result can change
        /// what is on screen (grammar became ready, or lines up to <paramref name="visibleBottom"/> became exact).
        /// </summary>
        public bool AdvanceBackground(EditorTabBuffer buf, int visibleTop, int visibleBottom, int budgetMs)
        {
            if (buf == null || buf.Lines == null) return false;
            bool hadGrammar = textMate != null;
            if (grammarPending)
            {
                registryGeneration = -1;
                ResolveGrammar();
                if (textMate != null) { textMate.Sync(buf.Lines); return true; }
                if (grammarPending) return false;
                return hadGrammar != (textMate != null);
            }
            bool fbRepaint = false;
            if (fallbackUsed && fallback != null && fallback.ValidCount < buf.Lines.Count)
            {
                fallback.Sync(buf.Lines);
                int fb0 = fallback.ValidCount;
                fallback.EnsureValid(buf.Lines, buf.Lines.Count - 1, DeadlineFromNow(budgetMs));
                int fb1 = fallback.ValidCount;
                fbRepaint = fb0 <= visibleBottom && fb1 > fb0 && fb1 > visibleTop;
            }
            if (textMate == null) return fbRepaint;
            textMate.Sync(buf.Lines);
            int before = textMate.ValidCount;
            textMate.EnsureValid(buf.Lines, buf.Lines.Count - 1, DeadlineFromNow(budgetMs));
            int after = textMate.ValidCount;
            if (after >= textMate.LineCount && textMate.LineCount > 6000)
                textMate.EvictDataOutside(Math.Max(0, visibleTop - 1500), visibleBottom + 1500);
            return fbRepaint || (before <= visibleBottom && after > before && after > visibleTop);
        }

        /// <summary>
        /// TextMate-colored spans for a line, or null when no grammar applies (caller uses its built-in tokenizer).
        /// <paramref name="isGuess"/> is true when the line's real start state is not known yet.
        /// </summary>
        public List<StyledSpan> GetTextMateSpans(IList<string> lines, int line, int firstVisibleLine, StudioTheme theme, long deadlineTicks, out bool isGuess)
        {
            return GetTextMateSpans(lines, line, firstVisibleLine, theme, deadlineTicks, out isGuess, 0, int.MaxValue);
        }

        /// <summary>
        /// As above, limited to the tokens overlapping [sliceStart, sliceEnd) (a horizontally windowed long line only
        /// resolves the tokens on screen instead of thousands per frame). Spans keep their full extent.
        /// </summary>
        public List<StyledSpan> GetTextMateSpans(IList<string> lines, int line, int firstVisibleLine, StudioTheme theme, long deadlineTicks, out bool isGuess, int sliceStart, int sliceEnd)
        {
            isGuess = false;
            if (textMate == null || lines == null || line < 0 || line >= lines.Count) return null;
            TmLineTokens toks = textMate.GetTokens(lines, line, firstVisibleLine, deadlineTicks, out isGuess);
            if (toks == null) return null;
            TextMateTheme tm = TextMateTheme.For(theme);
            int len = (lines[line] ?? "").Length;
            int first = 0;
            if (sliceStart > 0 && toks.Count > 0)
            {
                // last token starting at or before sliceStart (Starts is ascending)
                int lo = 0, hi = toks.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (toks.Starts[mid] <= sliceStart) lo = mid; else hi = mid - 1;
                }
                first = lo;
            }
            List<StyledSpan> spans = new List<StyledSpan>(sliceStart > 0 || sliceEnd < len ? 64 : toks.Count);
            for (int i = first; i < toks.Count; i++)
            {
                int s = toks.Starts[i];
                if (s >= sliceEnd) break;
                int e = Math.Min(len, toks.EndOf(i));
                if (e <= s) continue;
                TmTokenStyle st = tm.Resolve(toks.Scopes[i]);
                if (spans.Count > 0)
                {
                    StyledSpan prev = spans[spans.Count - 1];
                    if (prev.Color == st.Foreground && prev.Bold == st.Bold && prev.Italic == st.Italic && prev.Start + prev.Length == s)
                    {
                        prev.Length = e - prev.Start;
                        spans[spans.Count - 1] = prev;
                        continue;
                    }
                }
                spans.Add(new StyledSpan(s, e - s, st.Foreground, st.Bold, st.Italic));
            }
            return spans;
        }

        /// <summary>Recolors bracket characters with their rotating pair color (spans must cover the line in order).</summary>
        public static List<StyledSpan> ApplyBracketColors(List<StyledSpan> spans, BracketLineData brackets, StudioTheme theme)
        {
            if (spans == null || brackets == null || brackets.Count == 0) return spans;
            List<StyledSpan> res = new List<StyledSpan>(spans.Count + brackets.Count * 2);
            int b = 0;
            foreach (StyledSpan sp in spans)
            {
                int s = sp.Start, e = sp.Start + sp.Length;
                while (b < brackets.Count && brackets.Cols[b] < s) b++;
                int cur = s;
                while (b < brackets.Count && brackets.Cols[b] < e)
                {
                    int c = brackets.Cols[b];
                    if (c > cur) res.Add(new StyledSpan(cur, c - cur, sp.Color, sp.Bold, sp.Italic));
                    int lvl = brackets.Levels[b];
                    Color bc = lvl < 0 ? UiTokens.BracketUnexpected(theme) : UiTokens.BracketPairColor(theme, lvl);
                    res.Add(new StyledSpan(c, 1, bc, sp.Bold, sp.Italic));
                    cur = c + 1;
                    b++;
                }
                if (cur < e) res.Add(new StyledSpan(cur, e - cur, sp.Color, sp.Bold, sp.Italic));
            }
            return res;
        }
    }
}
