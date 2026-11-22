using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Language-aware line tokenizer for files highlighted without a TextMate grammar (no grammar, grammar disabled or
    /// large files) — QA #19. The old fallback treated every '#' (and every "//") as a comment in every language, so C#
    /// #region / #if lines, CSS #id selectors and colors, Python floor division and URLs turned into comments.
    /// Comment syntax per language: '#' (Python, shell, YAML, Ruby, ...), "//" and "/* */" (C family, JS/TS, Go, Rust,
    /// Java, ...), "/* */" only (CSS), "--" (SQL, Lua, Haskell), "&lt;!-- --&gt;" (HTML / XML / Markdown); none for plain
    /// text. C-family '#' directives are keywords; CSS '#' starts an id selector or a hex color. Strict C# 5.
    /// </summary>
    public static class FallbackTokenizer
    {
        private enum Family { Plain, Hash, CLike, CLikeHash, Css, ScssLess, DashDash, Markup }

        private static Family FamilyOf(string language)
        {
            string l = SnippetEngine.NormalizeLanguage(language);
            switch (l)
            {
                case "csharp": case "javascript": case "javascriptreact": case "typescript": case "typescriptreact":
                case "java": case "kotlin": case "go": case "rust": case "swift": case "dart": case "scala": case "groovy":
                case "c": case "cpp": case "objective-c": case "fsharp": case "json": case "proto": case "zig":
                    return Family.CLike;
                case "php":
                    return Family.CLikeHash;
                case "css":
                    return Family.Css;
                case "scss": case "less":
                    return Family.ScssLess;
                case "sql": case "lua": case "haskell": case "ada": case "elm":
                    return Family.DashDash;
                case "html": case "xml": case "markdown": case "vue": case "svelte":
                    return Family.Markup;
                case "plaintext": case "":
                    return Family.Plain;
            }
            bool hash = l == "shellscript" || l == "powershell" || l == "dockerfile" || l == "makefile"
                || SimpleCodeTokenizer.UsesHashComments(l) || SimpleCodeTokenizer.UsesHashComments((language ?? "").Trim().TrimStart('.'));
            return hash ? Family.Hash : Family.Plain;
        }

        // ---- line-start states (multi-line constructs); 0 = plain code
        public const int StateCode = 0;
        public const int StateBlockComment = 1;   // /* ... */ (C family, CSS, SQL)
        public const int StateMarkupComment = 2;  // <!-- ... -->
        public const int StateTripleDouble = 3;   // Python """ ... """
        public const int StateTripleSingle = 4;   // Python ''' ... '''
        public const int StateBacktick = 5;       // JS/TS template literal, Go raw string
        public const int StateVerbatim = 6;       // C# @"..." (doubled "" escapes)
        public const int StateRawBase = 100;      // C# raw string literal: StateRawBase + opening quote count (>= 3)

        /// <summary>Per-language switches, computed once per language id.</summary>
        private sealed class Lang
        {
            public Family F;
            public bool Triple;          // Python / CoffeeScript """ and '''
            public bool CSharp;          // @"..." verbatim and """ raw strings
            public bool BacktickMulti;   // ` strings continue over lines (JS/TS templates, Go raw strings)
            public bool BacktickEscapes; // JS templates honour backslash escapes, Go raw strings do not
            public bool BlockComments;   // /* */
            public bool Slash, Hash, Dash, Markup, TrackQuotes, Directives;
        }

        private static readonly Dictionary<string, Lang> langCache = new Dictionary<string, Lang>(StringComparer.Ordinal);

        private static Lang LangOf(string language)
        {
            string key = language ?? "";
            Lang L;
            lock (langCache)
            {
                if (langCache.TryGetValue(key, out L)) return L;
            }
            L = new Lang();
            Family f = FamilyOf(language);
            string id = SnippetEngine.NormalizeLanguage(language);
            L.F = f;
            L.BlockComments = f == Family.CLike || f == Family.CLikeHash || f == Family.Css || f == Family.ScssLess || id == "sql";
            L.Slash = f == Family.CLike || f == Family.CLikeHash || f == Family.ScssLess;
            L.Hash = f == Family.Hash || f == Family.CLikeHash;
            L.Dash = f == Family.DashDash;
            L.Markup = f == Family.Markup;
            L.TrackQuotes = !L.Markup && f != Family.Plain;
            L.Directives = f == Family.CLike;
            L.Triple = id == "python" || id == "py" || id == "coffeescript" || id == "coffee";
            L.CSharp = id == "csharp" || id == "cs";
            L.BacktickMulti = id == "javascript" || id == "javascriptreact" || id == "typescript" || id == "typescriptreact"
                || id == "js" || id == "ts" || id == "jsx" || id == "tsx" || id == "go" || id == "vue" || id == "svelte";
            L.BacktickEscapes = id != "go";
            lock (langCache) { langCache[key] = L; }
            return L;
        }

        public static List<CodeToken> Tokenize(string line, string language)
        {
            int end;
            return Tokenize(line, language, StateCode, out end);
        }

        /// <summary>
        /// Tokenizes a whole logical line that starts in <paramref name="startState"/> (a multi-line string or comment
        /// still open from the previous line, or <see cref="StateCode"/>); <paramref name="endState"/> is the state the
        /// next line starts in. Always tokenize from the start of the line: a slice that begins inside a string flips the
        /// quote parity (QA: JSON text in .py files painted as identifiers when scrolled horizontally / wrapped).
        /// </summary>
        public static List<CodeToken> Tokenize(string line, string language, int startState, out int endState)
        {
            List<CodeToken> result = new List<CodeToken>();
            endState = Run(line, LangOf(language), startState, result);
            return result;
        }

        /// <summary>State after the line without producing tokens (fast, allocation-free pass for line-start states).</summary>
        public static int ScanEndState(string line, string language, int startState)
        {
            return Run(line, LangOf(language), startState, null);
        }

        /// <summary>True for line-start states inside a comment (minimap colouring).</summary>
        public static bool IsCommentState(int state)
        {
            return state == StateBlockComment || state == StateMarkupComment;
        }

        /// <summary>Index of the first token that ends after <paramref name="col"/> (tokens sorted by start; binary search).</summary>
        public static int FirstTokenEndingAfter(List<CodeToken> tokens, int col)
        {
            int lo = 0, hi = tokens == null ? 0 : tokens.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                CodeToken t = tokens[mid];
                if (t.Start + t.Length <= col) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private static void Emit(List<CodeToken> into, int start, int end, CodeTokenKind kind)
        {
            if (into != null && end > start) into.Add(new CodeToken(start, end - start, kind));
        }

        /// <summary>
        /// End of a multi-line construct that is open at <paramref name="from"/> (index just past its closing
        /// delimiter), or -1 when it continues past the end of the line.
        /// </summary>
        private static int FindClose(string line, int from, int state, Lang L)
        {
            int n = line.Length;
            switch (state)
            {
                case StateBlockComment:
                {
                    int c = line.IndexOf("*/", Math.Min(from, n), StringComparison.Ordinal);
                    return c < 0 ? -1 : c + 2;
                }
                case StateMarkupComment:
                {
                    int c = line.IndexOf("-->", Math.Min(from, n), StringComparison.Ordinal);
                    return c < 0 ? -1 : c + 3;
                }
                case StateTripleDouble:
                case StateTripleSingle:
                {
                    char q = state == StateTripleDouble ? '"' : '\'';
                    for (int i = from; i < n; i++)
                    {
                        char ch = line[i];
                        if (ch == '\\') { i++; continue; }
                        if (ch == q && i + 2 < n && line[i + 1] == q && line[i + 2] == q) return i + 3;
                    }
                    return -1;
                }
                case StateBacktick:
                    for (int i = from; i < n; i++)
                    {
                        char ch = line[i];
                        if (ch == '\\' && L.BacktickEscapes) { i++; continue; }
                        if (ch == '`') return i + 1;
                    }
                    return -1;
                case StateVerbatim:
                    for (int i = from; i < n; i++)
                    {
                        if (line[i] != '"') continue;
                        if (i + 1 < n && line[i + 1] == '"') { i++; continue; }
                        return i + 1;
                    }
                    return -1;
                default:
                    if (state > StateRawBase)
                    {
                        int k = state - StateRawBase;
                        for (int i = from; i < n; i++)
                        {
                            if (line[i] != '"') continue;
                            int r = i;
                            while (r < n && line[r] == '"') r++;
                            if (r - i >= k) return i + k;
                            i = r - 1;
                        }
                        return -1;
                    }
                    return from;
            }
        }

        /// <summary>The tokenizer proper: emits tokens into <paramref name="result"/> (null = state pass only).</summary>
        private static int Run(string line, Lang L, int state, List<CodeToken> result)
        {
            // multi-line constructs stay open over empty lines
            if (string.IsNullOrEmpty(line)) return state;
            Family f = L.F;
            int n = line.Length;
            int segStart = 0;
            int i = 0;

            if (state != StateCode)
            {
                int close = FindClose(line, 0, state, L);
                CodeTokenKind k = IsCommentState(state) ? CodeTokenKind.Comment : CodeTokenKind.String;
                if (close < 0) { Emit(result, 0, n, k); return state; }
                Emit(result, 0, close, k);
                segStart = i = close;
                state = StateCode;
            }
            else if (L.Directives)
            {
                // C-family preprocessor directives (#region, #if, #include, #pragma ...) are keywords, not comments.
                int ws = 0;
                while (ws < n && (line[ws] == ' ' || line[ws] == '\t')) ws++;
                if (ws < n && line[ws] == '#')
                {
                    int e = ws + 1;
                    while (e < n && (char.IsLetter(line[e]) || line[e] == '_')) e++;
                    if (e > ws + 1)
                    {
                        Emit(result, ws, e, CodeTokenKind.Keyword);
                        segStart = i = e;
                    }
                }
            }

            while (i < n)
            {
                char c = line[i];
                if (L.TrackQuotes && (c == '"' || c == '\'' || c == '`'))
                {
                    int start = i;
                    int open = StateCode;
                    int end;
                    if (L.Triple && c != '`' && i + 2 < n && line[i + 1] == c && line[i + 2] == c)
                    {
                        open = c == '"' ? StateTripleDouble : StateTripleSingle;
                        end = FindClose(line, i + 3, open, L);
                    }
                    else if (L.CSharp && c == '"' && i + 2 < n && line[i + 1] == '"' && line[i + 2] == '"')
                    {
                        int r = i;
                        while (r < n && line[r] == '"') r++;
                        open = StateRawBase + (r - i);
                        end = FindClose(line, r, open, L);
                        while (start > segStart && line[start - 1] == '$') start--;
                    }
                    else if (L.CSharp && c == '"' && i > segStart && (line[i - 1] == '@' || (line[i - 1] == '$' && i - 2 >= segStart && line[i - 2] == '@')))
                    {
                        open = StateVerbatim;
                        end = FindClose(line, i + 1, open, L);
                        while (start > segStart && (line[start - 1] == '@' || line[start - 1] == '$')) start--;
                    }
                    else if (c == '`' && L.BacktickMulti)
                    {
                        open = StateBacktick;
                        end = FindClose(line, i + 1, open, L);
                    }
                    else
                    {
                        // single-line string: ends at the matching quote or at the end of the line
                        end = n;
                        for (int k = i + 1; k < n; k++)
                        {
                            char ch = line[k];
                            if (ch == '\\') { k++; continue; }
                            if (ch == c) { end = k + 1; break; }
                        }
                        if (L.CSharp && c == '"' && start > segStart && line[start - 1] == '$') start--;
                    }
                    if (result != null) AddCode(result, line, segStart, start, f);
                    if (end < 0)
                    {
                        Emit(result, start, n, CodeTokenKind.String);
                        return open;
                    }
                    end = Math.Min(n, end);
                    Emit(result, start, end, CodeTokenKind.String);
                    segStart = i = end;
                    continue;
                }

                int commentEnd = -1;
                int openComment = StateCode;
                if (L.BlockComments && c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    openComment = StateBlockComment;
                    commentEnd = FindClose(line, i + 2, StateBlockComment, L);
                    if (commentEnd < 0) commentEnd = -2;
                }
                else if (L.Markup && c == '<' && string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
                {
                    openComment = StateMarkupComment;
                    commentEnd = FindClose(line, i + 4, StateMarkupComment, L);
                    if (commentEnd < 0) commentEnd = -2;
                }
                else if ((L.Slash && c == '/' && i + 1 < n && line[i + 1] == '/' && !(i > 0 && line[i - 1] == ':'))
                    || (L.Hash && c == '#')
                    || (L.Dash && c == '-' && i + 1 < n && line[i + 1] == '-'))
                {
                    if (result != null) AddCode(result, line, segStart, i, f);
                    Emit(result, i, n, CodeTokenKind.Comment);
                    return StateCode;
                }
                if (commentEnd != -1)
                {
                    if (result != null) AddCode(result, line, segStart, i, f);
                    if (commentEnd == -2)
                    {
                        Emit(result, i, n, CodeTokenKind.Comment);
                        return openComment;
                    }
                    Emit(result, i, commentEnd, CodeTokenKind.Comment);
                    segStart = i = commentEnd;
                    continue;
                }
                i++;
            }
            if (result != null) AddCode(result, line, segStart, n, f);
            return StateCode;
        }
        /// <summary>True when a (left-trimmed) line starts with a comment in this language (minimap colouring).</summary>
        public static bool StartsWithLineComment(string trimmedLine, string language)
        {
            return StartsWithLineComment(trimmedLine, 0, language);
        }

        /// <summary>Same, for the text starting at <paramref name="start"/> (no allocation; per minimap row).</summary>
        public static bool StartsWithLineComment(string line, int start, string language)
        {
            if (string.IsNullOrEmpty(line) || start < 0 || start >= line.Length) return false;
            switch (FamilyOf(language))
            {
                case Family.Hash: return line[start] == '#';
                case Family.CLike: return At(line, start, "//") || At(line, start, "/*");
                case Family.CLikeHash: return line[start] == '#' || At(line, start, "//") || At(line, start, "/*");
                case Family.Css: return At(line, start, "/*");
                case Family.ScssLess: return At(line, start, "//") || At(line, start, "/*");
                case Family.DashDash: return At(line, start, "--");
                case Family.Markup: return At(line, start, "<!--");
                default: return false;
            }
        }

        private static bool At(string s, int i, string token)
        {
            return i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;
        }

        /// <summary>Tokenizes a comment-free code segment [from, to).</summary>
        private static void AddCode(List<CodeToken> into, string line, int from, int to, Family f)
        {
            if (to <= from) return;
            if (f != Family.Css && f != Family.ScssLess)
            {
                // no substring: multi-MB minified lines are tokenized in place
                SimpleCodeTokenizer.TokenizeRange(line, from, to, false, false, into);
                return;
            }
            string seg = line.Substring(from, to - from);
            List<CodeToken> toks = SimpleCodeTokenizer.Tokenize(seg, false, false);
            toks = CssHashTokens(seg, toks);
            foreach (CodeToken t in toks) into.Add(new CodeToken(t.Start + from, t.Length, t.Kind));
        }

        /// <summary>CSS: "#fff" / "#1e1e1e" are colors (number), "#main" an id selector (type).</summary>
        private static List<CodeToken> CssHashTokens(string seg, List<CodeToken> toks)
        {
            List<CodeToken> r = new List<CodeToken>();
            for (int k = 0; k < toks.Count; k++)
            {
                CodeToken t = toks[k];
                bool endsWithHash = t.Kind == CodeTokenKind.Operator && seg[t.Start + t.Length - 1] == '#';
                CodeToken next = k + 1 < toks.Count ? toks[k + 1] : null;
                if (endsWithHash && next != null && next.Start == t.Start + t.Length && next.Kind != CodeTokenKind.String && next.Kind != CodeTokenKind.Comment)
                {
                    if (t.Length > 1) r.Add(new CodeToken(t.Start, t.Length - 1, CodeTokenKind.Operator));
                    int end = next.Start + next.Length;
                    while (end < seg.Length && (char.IsLetterOrDigit(seg[end]) || seg[end] == '-' || seg[end] == '_')) end++;
                    string name = seg.Substring(next.Start, end - next.Start);
                    bool hex = (name.Length == 3 || name.Length == 4 || name.Length == 6 || name.Length == 8) && IsHex(name);
                    r.Add(new CodeToken(t.Start + t.Length - 1, end - (t.Start + t.Length - 1), hex ? CodeTokenKind.Number : CodeTokenKind.Type));
                    while (k + 1 < toks.Count && toks[k + 1].Start < end) k++;
                    continue;
                }
                r.Add(t);
            }
            return r;
        }

        private static bool IsHex(string s)
        {
            foreach (char ch in s)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))) return false;
            return true;
        }
    }
}
