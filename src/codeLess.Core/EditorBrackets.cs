using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// How the bracket lexer recognises comments and strings for a language (brackets inside them are ignored, like
    /// VS Code's bracket pair colorizer which skips string / comment / regex tokens).
    /// </summary>
    public sealed class BracketLexProfile
    {
        public string Id = "plain";
        public string LineComment;      // "//", "#", "--", ";"
        public string LineComment2;
        public bool HashCommentNeedsSpace; // shell / yaml: '#' only at line start or after whitespace
        public string BlockStart;       // "/*", "<!--", "--[[", "{-"
        public string BlockEnd;
        public bool NestedBlockComments; // Rust, Swift, Kotlin, Haskell
        public bool DoubleQuote = true;
        public bool SingleQuote = true;
        public bool SingleQuoteIsChar;  // C-family char literal ('{' is a literal, not a string start for lifetimes)
        public bool RustLifetimes;
        public bool Backtick;           // multi-line strings
        public bool BacktickTemplates;  // JS/TS `${ ... }` expressions are code
        public bool TripleQuotes;       // Python """ / ''', C# raw """
        public bool CSharpVerbatim;     // @"..." with "" escapes, multi-line
        public bool IndentBased;        // Python / YAML / CoffeeScript: sticky scroll by indentation
        public bool Markdown;
        public bool Enabled = true;

        public static BracketLexProfile ForFile(string filePath)
        {
            string ext = "";
            string name = "";
            try
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();
                    name = (Path.GetFileName(filePath) ?? "").ToLowerInvariant();
                }
            }
            catch { }
            return ForExtension(ext, name);
        }

        public static BracketLexProfile ForExtension(string ext, string fileName)
        {
            BracketLexProfile p = new BracketLexProfile();
            p.Id = string.IsNullOrEmpty(ext) ? (fileName ?? "") : ext.TrimStart('.');
            switch (ext)
            {
                case ".js": case ".mjs": case ".cjs": case ".jsx": case ".ts": case ".mts": case ".cts": case ".tsx":
                case ".vue": case ".svelte":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.Backtick = true; p.BacktickTemplates = true;
                    break;
                case ".cs": case ".csx":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.SingleQuoteIsChar = true; p.CSharpVerbatim = true; p.TripleQuotes = true;
                    break;
                case ".c": case ".h": case ".cpp": case ".cc": case ".cxx": case ".hpp": case ".hh": case ".hxx": case ".m": case ".mm":
                case ".java": case ".scala": case ".dart": case ".php": case ".groovy": case ".gradle": case ".proto": case ".hlsl": case ".glsl":
                case ".jsonc": case ".json5":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.SingleQuoteIsChar = ext != ".php" && ext != ".dart" && ext != ".jsonc" && ext != ".json5";
                    break;
                case ".kt": case ".kts": case ".swift":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.NestedBlockComments = true; p.SingleQuoteIsChar = true; p.TripleQuotes = true;
                    break;
                case ".go":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.SingleQuoteIsChar = true; p.Backtick = true;
                    break;
                case ".rs":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.NestedBlockComments = true; p.SingleQuoteIsChar = true; p.RustLifetimes = true;
                    break;
                case ".json":
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.SingleQuote = false;
                    break;
                case ".py": case ".pyw": case ".pyi":
                    p.LineComment = "#"; p.TripleQuotes = true; p.IndentBased = true;
                    break;
                case ".rb": case ".rake": case ".gemspec":
                    p.LineComment = "#";
                    break;
                case ".sh": case ".bash": case ".zsh": case ".ksh": case ".fish": case ".ps1": case ".psm1":
                    p.LineComment = "#"; p.HashCommentNeedsSpace = true;
                    if (ext == ".ps1" || ext == ".psm1") { p.BlockStart = "<#"; p.BlockEnd = "#>"; }
                    break;
                case ".yaml": case ".yml":
                    p.LineComment = "#"; p.HashCommentNeedsSpace = true; p.IndentBased = true;
                    break;
                case ".toml": case ".ini": case ".cfg": case ".conf": case ".properties": case ".env":
                    p.LineComment = "#"; p.LineComment2 = ";"; p.HashCommentNeedsSpace = true;
                    break;
                case ".coffee":
                    p.LineComment = "#"; p.TripleQuotes = true; p.IndentBased = true;
                    break;
                case ".sql":
                    p.LineComment = "--"; p.BlockStart = "/*"; p.BlockEnd = "*/"; p.DoubleQuote = true;
                    break;
                case ".lua":
                    p.LineComment = "--"; p.BlockStart = "--[["; p.BlockEnd = "]]";
                    break;
                case ".hs":
                    p.LineComment = "--"; p.BlockStart = "{-"; p.BlockEnd = "-}"; p.NestedBlockComments = true; p.SingleQuoteIsChar = true;
                    break;
                case ".css": case ".scss": case ".less":
                    p.BlockStart = "/*"; p.BlockEnd = "*/";
                    if (ext != ".css") p.LineComment = "//";
                    break;
                case ".html": case ".htm": case ".xhtml": case ".xml": case ".xaml": case ".axaml": case ".svg": case ".csproj":
                case ".props": case ".targets": case ".config": case ".plist": case ".razor": case ".cshtml":
                    p.BlockStart = "<!--"; p.BlockEnd = "-->"; p.DoubleQuote = false; p.SingleQuote = false;
                    break;
                case ".md": case ".markdown": case ".mdx":
                    p.Markdown = true; p.DoubleQuote = false; p.SingleQuote = false; p.Enabled = false;
                    break;
                case ".txt": case ".log": case ".csv": case ".tsv": case "":
                    if (fileName == "makefile" || fileName == "gnumakefile" || fileName == "dockerfile")
                    {
                        p.LineComment = "#"; p.HashCommentNeedsSpace = false;
                        break;
                    }
                    p.Enabled = false; p.DoubleQuote = false; p.SingleQuote = false;
                    break;
                default:
                    p.LineComment = "//"; p.BlockStart = "/*"; p.BlockEnd = "*/";
                    break;
            }
            return p;
        }
    }

    /// <summary>One open bracket on the lexer stack (immutable, shared between line states).</summary>
    public sealed class BracketFrame
    {
        public readonly BracketFrame Parent;
        /// <summary>'(' '[' '{', or '$' for a JS template expression "${".</summary>
        public readonly char Open;
        public readonly int Line;
        public readonly int Col;
        /// <summary>1-based nesting level of real brackets (template frames do not count).</summary>
        public readonly int Level;
        public readonly int Depth;

        public BracketFrame(BracketFrame parent, char open, int line, int col)
        {
            Parent = parent;
            Open = open;
            Line = line;
            Col = col;
            Depth = parent == null ? 1 : parent.Depth + 1;
            int pl = parent == null ? 0 : parent.Level;
            Level = open == '$' ? pl : pl + 1;
        }

        public bool Contains(BracketFrame f)
        {
            if (f == null) return true;
            BracketFrame x = this;
            while (x != null && x.Depth > f.Depth) x = x.Parent;
            return ReferenceEquals(x, f);
        }
    }

    public sealed class BracketLexState
    {
        public const int ModeCode = 0, ModeBlockComment = 1, ModeString = 2, ModeTriple = 3, ModeVerbatim = 4;
        public readonly int Mode;
        public readonly char Quote;
        public readonly int CommentDepth;
        public readonly BracketFrame Stack;

        public BracketLexState(int mode, char quote, int commentDepth, BracketFrame stack)
        {
            Mode = mode;
            Quote = quote;
            CommentDepth = commentDepth;
            Stack = stack;
        }

        public static bool Same(BracketLexState a, BracketLexState b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Mode != b.Mode || a.Quote != b.Quote || a.CommentDepth != b.CommentDepth) return false;
            BracketFrame x = a.Stack, y = b.Stack;
            while (true)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                if (x.Open != y.Open || x.Line != y.Line || x.Col != y.Col || x.Depth != y.Depth) return false;
                x = x.Parent;
                y = y.Parent;
            }
        }
    }

    /// <summary>Brackets found on one line (columns are character indices).</summary>
    public sealed class BracketLineData
    {
        public static readonly BracketLineData Empty = new BracketLineData();
        public int Count;
        public int[] Cols = new int[0];
        public char[] Chars = new char[0];
        /// <summary>0-based color level (nesting level - 1); -1 = unexpected closing bracket.</summary>
        public int[] Levels = new int[0];
        /// <summary>For an opener: its frame. For a closer: the frame it closed (null when unexpected).</summary>
        public BracketFrame[] Frames = new BracketFrame[0];
        public bool IsOpener(int i) { char c = Chars[i]; return c == '(' || c == '[' || c == '{'; }

        internal void Add(int col, char ch, int level, BracketFrame f)
        {
            if (Count == Cols.Length)
            {
                int cap = Math.Max(4, Count * 2);
                Array.Resize(ref Cols, cap);
                Array.Resize(ref Chars, cap);
                Array.Resize(ref Levels, cap);
                Array.Resize(ref Frames, cap);
            }
            Cols[Count] = col;
            Chars[Count] = ch;
            Levels[Count] = level;
            Frames[Count] = f;
            Count++;
        }
    }

    /// <summary>A matched bracket pair (positions are 0-based line / character index).</summary>
    public struct BracketPair
    {
        public int OpenLine, OpenCol, CloseLine, CloseCol, Level;
        public char OpenChar;
        public bool IsValid { get { return OpenLine >= 0 && CloseLine >= 0; } }
        public static BracketPair None { get { BracketPair p = new BracketPair(); p.OpenLine = -1; p.CloseLine = -1; return p; } }
    }

    /// <summary>
    /// Per-document bracket analysis: nesting levels for colorization (3 rotating colors), the bracket pair enclosing
    /// the caret (active bracket pair guide), string/comment-aware bracket matching, and the open-brace stack used by
    /// sticky scroll. Incremental (see <see cref="LineStateCache{TState,TData}"/>).
    /// </summary>
    public sealed class EditorBracketCache : LineStateCache<BracketLexState, BracketLineData>
    {
        public readonly BracketLexProfile Profile;
        private static readonly BracketLexState Initial = new BracketLexState(BracketLexState.ModeCode, '\0', 0, null);
        /// <summary>Longest line that is lexed (longer lines keep their start state and report no brackets).</summary>
        public int MaxLineLength = 20000;
        /// <summary>How far (lines) pair searches may look below the caret.</summary>
        public int PairSearchLimit = 4000;

        private int activeVersion = -1, activeLine = -1, activeCol = -1;
        private BracketPair activePair = BracketPair.None;

        public EditorBracketCache(BracketLexProfile profile)
        {
            Profile = profile ?? new BracketLexProfile();
        }

        protected override BracketLexState InitialState { get { return Initial; } }
        protected override bool StateEquals(BracketLexState a, BracketLexState b) { return BracketLexState.Same(a, b); }

        private static bool At(string s, int i, string tok)
        {
            return tok != null && i + tok.Length <= s.Length && string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0;
        }

        protected override BracketLexState ProcessLine(string text, int lineIndex, BracketLexState start, out BracketLineData data)
        {
            BracketLexProfile p = Profile;
            string s = text ?? "";
            if (!p.Enabled || s.Length > MaxLineLength)
            {
                data = BracketLineData.Empty;
                // single-line strings never continue past the end of a line
                if (start.Mode == BracketLexState.ModeString && start.Quote != '`') return new BracketLexState(BracketLexState.ModeCode, '\0', 0, start.Stack);
                return start;
            }
            BracketLineData d = null;
            int mode = start.Mode;
            char quote = start.Quote;
            int cdepth = start.CommentDepth;
            BracketFrame stack = start.Stack;
            int n = s.Length;
            int i = 0;
            while (i < n)
            {
                char c = s[i];
                if (mode == BracketLexState.ModeBlockComment)
                {
                    if (p.NestedBlockComments && At(s, i, p.BlockStart)) { cdepth++; i += p.BlockStart.Length; continue; }
                    if (At(s, i, p.BlockEnd))
                    {
                        i += p.BlockEnd.Length;
                        if (p.NestedBlockComments && cdepth > 1) { cdepth--; continue; }
                        cdepth = 0;
                        mode = BracketLexState.ModeCode;
                        continue;
                    }
                    i++;
                    continue;
                }
                if (mode == BracketLexState.ModeString)
                {
                    if (c == '\\') { i += 2; continue; }
                    if (quote == '`' && p.BacktickTemplates && c == '$' && i + 1 < n && s[i + 1] == '{')
                    {
                        stack = new BracketFrame(stack, '$', lineIndex, i);
                        mode = BracketLexState.ModeCode;
                        quote = '\0';
                        i += 2;
                        continue;
                    }
                    if (c == quote) { mode = BracketLexState.ModeCode; quote = '\0'; }
                    i++;
                    continue;
                }
                if (mode == BracketLexState.ModeTriple)
                {
                    if (c == '\\' && quote != '"') { i += 2; continue; }
                    if (c == quote && i + 2 < n && s[i + 1] == quote && s[i + 2] == quote) { mode = BracketLexState.ModeCode; quote = '\0'; i += 3; continue; }
                    i++;
                    continue;
                }
                if (mode == BracketLexState.ModeVerbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; }
                        mode = BracketLexState.ModeCode;
                        i++;
                        continue;
                    }
                    i++;
                    continue;
                }

                // ---- code
                if (p.BlockStart != null && At(s, i, p.BlockStart))
                {
                    mode = BracketLexState.ModeBlockComment;
                    cdepth = 1;
                    i += p.BlockStart.Length;
                    continue;
                }
                if (p.LineComment != null && At(s, i, p.LineComment))
                {
                    if (!(p.HashCommentNeedsSpace && p.LineComment == "#" && i > 0 && !char.IsWhiteSpace(s[i - 1])))
                        break;
                }
                if (p.LineComment2 != null && At(s, i, p.LineComment2) && (i == 0 || char.IsWhiteSpace(s[i - 1]))) break;
                if (p.TripleQuotes && (c == '"' || c == '\'') && i + 2 < n && s[i + 1] == c && s[i + 2] == c)
                {
                    mode = BracketLexState.ModeTriple;
                    quote = c;
                    i += 3;
                    continue;
                }
                if (p.CSharpVerbatim && c == '@' && i + 1 < n && s[i + 1] == '"')
                {
                    mode = BracketLexState.ModeVerbatim;
                    i += 2;
                    continue;
                }
                if (p.CSharpVerbatim && c == '$' && i + 2 < n && s[i + 1] == '@' && s[i + 2] == '"')
                {
                    mode = BracketLexState.ModeVerbatim;
                    i += 3;
                    continue;
                }
                if ((c == '"' && p.DoubleQuote) || (c == '`' && p.Backtick))
                {
                    mode = BracketLexState.ModeString;
                    quote = c;
                    i++;
                    continue;
                }
                if (c == '\'' && p.SingleQuote)
                {
                    if (p.SingleQuoteIsChar)
                    {
                        // char literal: 'x', '\n', '\u{1F600}', ... ; Rust lifetimes ('a) are not strings
                        int close = -1;
                        if (i + 1 < n && s[i + 1] == '\\')
                        {
                            int lim = Math.Min(n, i + 14);
                            for (int k = i + 2; k < lim; k++) if (s[k] == '\'') { close = k; break; }
                        }
                        else if (i + 2 < n && s[i + 2] == '\'') close = i + 2;
                        else if (i + 3 < n && char.IsSurrogatePair(s[i + 1], s[i + 2]) && s[i + 3] == '\'') close = i + 3;
                        if (close > 0) { i = close + 1; continue; }
                        i++; // lifetime / stray quote
                        continue;
                    }
                    mode = BracketLexState.ModeString;
                    quote = '\'';
                    i++;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{')
                {
                    if (d == null) d = new BracketLineData();
                    stack = new BracketFrame(stack, c, lineIndex, i);
                    d.Add(i, c, stack.Level - 1, stack);
                    i++;
                    continue;
                }
                if (c == ')' || c == ']' || c == '}')
                {
                    if (c == '}' && stack != null && stack.Open == '$')
                    {
                        // end of a template expression: back into the template string
                        stack = stack.Parent;
                        mode = BracketLexState.ModeString;
                        quote = '`';
                        i++;
                        continue;
                    }
                    char want = c == ')' ? '(' : (c == ']' ? '[' : '{');
                    if (d == null) d = new BracketLineData();
                    BracketFrame match = null;
                    int skipped = 0;
                    for (BracketFrame f = stack; f != null && skipped <= 3; f = f.Parent, skipped++)
                    {
                        if (f.Open == '$') break;
                        if (f.Open == want) { match = f; break; }
                    }
                    if (match != null)
                    {
                        d.Add(i, c, match.Level - 1, match);
                        stack = match.Parent;
                    }
                    else d.Add(i, c, -1, null);
                    i++;
                    continue;
                }
                i++;
            }
            if (mode == BracketLexState.ModeString && quote != '`') { mode = BracketLexState.ModeCode; quote = '\0'; }
            data = d ?? BracketLineData.Empty;
            if (mode == start.Mode && quote == start.Quote && cdepth == start.CommentDepth && ReferenceEquals(stack, start.Stack)) return start;
            return new BracketLexState(mode, quote, cdepth, stack);
        }

        /// <summary>Brackets of a line (null when the line is beyond <paramref name="deadlineTicks"/>' reach).</summary>
        public BracketLineData GetLine(IList<string> lines, int line, long deadlineTicks)
        {
            if (line < 0 || lines == null || line >= lines.Count) return null;
            if (!EnsureValid(lines, line, deadlineTicks)) return null;
            return GetData(line);
        }

        /// <summary>Open bracket frames enclosing the start of <paramref name="line"/> (innermost first via Parent).</summary>
        public BracketFrame GetStackAtLineStart(IList<string> lines, int line)
        {
            if (lines == null || line <= 0 || line >= lines.Count) return null;
            if (!EnsureValid(lines, line - 1, 0)) return null;
            BracketLexState st = GetEndState(line - 1);
            return st == null ? null : st.Stack;
        }

        /// <summary>The innermost bracket pair enclosing the position (VS Code's active bracket pair).</summary>
        public BracketPair FindEnclosingPair(IList<string> lines, int line, int col)
        {
            if (lines == null || line < 0 || line >= lines.Count) return BracketPair.None;
            if (activeVersion == Version && activeLine == line && activeCol == col) return activePair;
            BracketPair res = BracketPair.None;
            if (EnsureValid(lines, line, 0))
            {
                BracketLexState st = line == 0 ? InitialState : GetEndState(line - 1);
                BracketFrame stack = st == null ? null : st.Stack;
                BracketLineData d = GetData(line);
                for (int k = 0; d != null && k < d.Count && d.Cols[k] < col; k++)
                {
                    if (d.IsOpener(k)) stack = d.Frames[k];
                    else if (d.Frames[k] != null) stack = d.Frames[k].Parent;
                }
                while (stack != null && stack.Open == '$') stack = stack.Parent;
                if (stack != null) res = FindCloser(lines, stack, line, col);
            }
            activeVersion = Version; activeLine = line; activeCol = col; activePair = res;
            return res;
        }

        /// <summary>Frames are compared by position: a line reused after an edit may hold an equal, older frame object.</summary>
        public static bool SameFrame(BracketFrame a, BracketFrame b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a != null && b != null && a.Line == b.Line && a.Col == b.Col && a.Open == b.Open;
        }

        private static bool StackContains(BracketFrame stack, BracketFrame f)
        {
            for (BracketFrame x = stack; x != null && x.Depth >= f.Depth; x = x.Parent)
                if (x.Depth == f.Depth) return SameFrame(x, f);
            return false;
        }

        private BracketPair FindCloser(IList<string> lines, BracketFrame opener, int fromLine, int fromCol)
        {
            int last = Math.Min(lines.Count - 1, fromLine + PairSearchLimit);
            for (int L = fromLine; L <= last; L++)
            {
                if (!EnsureValid(lines, L, 0)) break;
                BracketLineData d = GetData(L);
                if (d != null)
                {
                    for (int k = 0; k < d.Count; k++)
                    {
                        if (L == fromLine && d.Cols[k] < fromCol) continue;
                        if (!d.IsOpener(k) && SameFrame(d.Frames[k], opener))
                        {
                            BracketPair bp = new BracketPair();
                            bp.OpenLine = opener.Line; bp.OpenCol = opener.Col; bp.OpenChar = opener.Open;
                            bp.CloseLine = L; bp.CloseCol = d.Cols[k]; bp.Level = opener.Level - 1;
                            return bp;
                        }
                    }
                }
                // the opener is no longer on the stack at the end of this line without a closer => unmatched
                BracketLexState end = GetEndState(L);
                if (end != null && !StackContains(end.Stack, opener)) break;
            }
            BracketPair open = BracketPair.None;
            open.OpenLine = opener.Line; open.OpenCol = opener.Col; open.OpenChar = opener.Open; open.Level = opener.Level - 1;
            return open;
        }

        /// <summary>
        /// The bracket pair of the bracket at / just before the caret (string- and comment-aware replacement for a naive
        /// character scan). Returns false when there is no bracket or no partner.
        /// </summary>
        public bool TryFindMatchAt(IList<string> lines, int line, int col, out BracketPair pair)
        {
            pair = BracketPair.None;
            if (lines == null || line < 0 || line >= lines.Count) return false;
            if (!EnsureValid(lines, line, 0)) return false;
            BracketLineData d = GetData(line);
            if (d == null || d.Count == 0) return false;
            int hit = -1;
            for (int k = 0; k < d.Count; k++) if (d.Cols[k] == col) { hit = k; break; }
            if (hit < 0) for (int k = 0; k < d.Count; k++) if (d.Cols[k] == col - 1) { hit = k; break; }
            if (hit < 0) return false;
            BracketFrame f = d.Frames[hit];
            if (f == null) return false;
            if (d.IsOpener(hit))
            {
                BracketPair bp = FindCloser(lines, f, line, d.Cols[hit] + 1);
                if (!bp.IsValid) return false;
                pair = bp;
                return true;
            }
            pair.OpenLine = f.Line; pair.OpenCol = f.Col; pair.OpenChar = f.Open; pair.Level = f.Level - 1;
            pair.CloseLine = line; pair.CloseCol = d.Cols[hit];
            return true;
        }
    }
}
