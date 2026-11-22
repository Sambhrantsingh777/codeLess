using System;
using System.Collections.Generic;
using System.Drawing;

namespace codeLess.Studio
{
    /// <summary>Line-start state of the fallback tokenizer (boxed int; shared instances for the common values).</summary>
    public sealed class FallbackLineState
    {
        public readonly int Value;
        private static readonly FallbackLineState[] common = CreateCommon();

        private FallbackLineState(int value) { Value = value; }

        private static FallbackLineState[] CreateCommon()
        {
            FallbackLineState[] a = new FallbackLineState[FallbackTokenizer.StateRawBase + 16];
            for (int i = 0; i < a.Length; i++) a[i] = new FallbackLineState(i);
            return a;
        }

        public static FallbackLineState Of(int value)
        {
            if (value >= 0 && value < common.Length) return common[value];
            return new FallbackLineState(value);
        }
    }

    /// <summary>
    /// Incremental, per-buffer cache of the fallback tokenizer (files without a TextMate grammar, and lines too long for
    /// the TextMate tokenizer). Line-start states are computed with an allocation-free scan (so multi-line strings and
    /// comments color every line they span), and full-line tokens are computed lazily for painted lines only and kept
    /// until the line's text reference changes: a multi-MB minified JSON line is tokenized once, not on every frame,
    /// and each frame only converts the tokens of the rendered slice. STRICT C# 5.
    /// </summary>
    public sealed class FallbackTokenCache : LineStateCache<FallbackLineState, List<CodeToken>>
    {
        private readonly string language;
        private bool produceTokens;
        private int tokenizedSinceEvict;
        private readonly Dictionary<int, List<CodeToken>> guesses = new Dictionary<int, List<CodeToken>>();
        private readonly Dictionary<int, string> guessText = new Dictionary<int, string>();

        public FallbackTokenCache(string language) { this.language = language ?? ""; }

        public string Language { get { return language; } }

        protected override FallbackLineState InitialState { get { return FallbackLineState.Of(FallbackTokenizer.StateCode); } }

        protected override bool StateEquals(FallbackLineState a, FallbackLineState b)
        {
            return a != null && b != null && a.Value == b.Value;
        }

        protected override FallbackLineState ProcessLine(string text, int lineIndex, FallbackLineState start, out List<CodeToken> data)
        {
            int s = start == null ? 0 : start.Value;
            if (!produceTokens)
            {
                data = null;
                return FallbackLineState.Of(FallbackTokenizer.ScanEndState(text, language, s));
            }
            int end;
            data = FallbackTokenizer.Tokenize(text, language, s, out end);
            tokenizedSinceEvict++;
            return FallbackLineState.Of(end);
        }

        protected override void OnInvalidated(int firstChangedLine)
        {
            guesses.Clear();
            guessText.Clear();
        }

        /// <summary>
        /// Full-line tokens of <paramref name="line"/>: exact when its start state is known (computing states up to the
        /// deadline), otherwise tokenized from the code state (<paramref name="isGuess"/>) until the background pass
        /// catches up. Call <see cref="LineStateCache{TState,TData}.Sync"/> first.
        /// </summary>
        public List<CodeToken> GetTokens(IList<string> lines, int line, long deadlineTicks, out bool isGuess)
        {
            isGuess = false;
            if (lines == null || line < 0 || line >= lines.Count) return null;
            if (tokenizedSinceEvict > 3000)
            {
                // tokens are kept for painted lines only; bound memory on long scrolls through big files
                tokenizedSinceEvict = 0;
                EvictDataOutside(line - 1500, line + 1500);
            }
            if (line < ValidCount || EnsureValid(lines, line, deadlineTicks))
            {
                produceTokens = true;
                try { return GetData(line); }
                finally { produceTokens = false; }
            }
            isGuess = true;
            List<CodeToken> t;
            string text = lines[line] ?? "";
            string cachedText;
            if (guesses.TryGetValue(line, out t) && guessText.TryGetValue(line, out cachedText) && ReferenceEquals(cachedText, text)) return t;
            int unused;
            t = FallbackTokenizer.Tokenize(text, language, FallbackTokenizer.StateCode, out unused);
            if (guesses.Count > 512) { guesses.Clear(); guessText.Clear(); }
            guesses[line] = t;
            guessText[line] = text;
            return t;
        }

        /// <summary>Line-start state when known (minimap), else <see cref="FallbackTokenizer.StateCode"/>.</summary>
        public int StartStateOf(int line)
        {
            FallbackLineState s = GetStartState(line);
            return s == null ? FallbackTokenizer.StateCode : s.Value;
        }
    }

    /// <summary>Theme colors for fallback token kinds, and slicing of full-line tokens to the drawn range.</summary>
    public static class FallbackTokenStyles
    {
        public static void StyleOf(CodeTokenKind kind, StudioTheme th, bool colorOperators, out Color color, out bool bold, out bool italic)
        {
            color = th.FgPrimary;
            bold = false;
            italic = false;
            switch (kind)
            {
                case CodeTokenKind.Comment: color = th.TokenComment; italic = true; break;
                case CodeTokenKind.String: color = th.TokenString; break;
                case CodeTokenKind.Number: color = th.TokenNumber; break;
                case CodeTokenKind.Control: color = th.TokenControl; bold = true; break;
                case CodeTokenKind.Keyword: color = th.TokenKeyword; bold = true; break;
                case CodeTokenKind.Type: color = th.TokenType; break;
                case CodeTokenKind.Function: color = th.TokenFunction; break;
                case CodeTokenKind.Operator: if (colorOperators) color = th.TokenOperator; break;
            }
        }

        /// <summary>
        /// Styled spans covering exactly [sliceStart, sliceEnd) of a line from its full-line tokens (gaps get the text
        /// color). Only the tokens overlapping the slice are visited (binary search), so the per-frame cost does not grow
        /// with the line length.
        /// </summary>
        public static List<StyledSpan> SliceSpans(List<CodeToken> tokens, int lineLength, int sliceStart, int sliceEnd, StudioTheme th, bool colorOperators)
        {
            List<StyledSpan> res = new List<StyledSpan>();
            sliceStart = Math.Max(0, sliceStart);
            sliceEnd = Math.Min(lineLength, sliceEnd);
            if (sliceEnd <= sliceStart) return res;
            int pos = sliceStart;
            int k = FallbackTokenizer.FirstTokenEndingAfter(tokens, sliceStart);
            int count = tokens == null ? 0 : tokens.Count;
            for (; k < count; k++)
            {
                CodeToken t = tokens[k];
                if (t.Start >= sliceEnd) break;
                int s = Math.Max(t.Start, sliceStart), e = Math.Min(t.Start + t.Length, sliceEnd);
                if (e <= s) continue;
                if (s > pos) res.Add(new StyledSpan(pos, s - pos, th.FgPrimary, false, false));
                Color c; bool b, it;
                StyleOf(t.Kind, th, colorOperators, out c, out b, out it);
                res.Add(new StyledSpan(s, e - s, c, b, it));
                pos = e;
            }
            if (pos < sliceEnd) res.Add(new StyledSpan(pos, sliceEnd - pos, th.FgPrimary, false, false));
            return res;
        }
    }
}
