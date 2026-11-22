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

        public static List<CodeToken> Tokenize(string line, string language)
        {
            List<CodeToken> result = new List<CodeToken>();
            if (string.IsNullOrEmpty(line)) return result;
            Family f = FamilyOf(language);
            bool cStyleBlock = f == Family.CLike || f == Family.CLikeHash || f == Family.Css || f == Family.ScssLess;
            bool slash = f == Family.CLike || f == Family.CLikeHash || f == Family.ScssLess;
            bool hash = f == Family.Hash || f == Family.CLikeHash;
            bool dash = f == Family.DashDash;
            bool markup = f == Family.Markup;

            int n = line.Length;
            int segStart = 0;
            int i = 0;

            // C-family preprocessor directives (#region, #if, #include, #pragma ...) are keywords, not comments.
            if (f == Family.CLike)
            {
                int ws = 0;
                while (ws < n && (line[ws] == ' ' || line[ws] == '\t')) ws++;
                if (ws < n && line[ws] == '#')
                {
                    int e = ws + 1;
                    while (e < n && (char.IsLetter(line[e]) || line[e] == '_')) e++;
                    if (e > ws + 1)
                    {
                        result.Add(new CodeToken(ws, e - ws, CodeTokenKind.Keyword));
                        segStart = i = e;
                    }
                }
            }

            bool trackQuotes = !markup && f != Family.Plain;
            char quote = '\0';
            while (i < n)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i += 2; continue; }
                    if (c == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (trackQuotes && (c == '"' || c == '\'' || c == '`')) { quote = c; i++; continue; }

                int commentLen = 0;
                if (cStyleBlock && c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    commentLen = close < 0 ? n - i : close + 2 - i;
                }
                else if (markup && c == '<' && string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
                {
                    int close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    commentLen = close < 0 ? n - i : close + 3 - i;
                }
                else if ((slash && c == '/' && i + 1 < n && line[i + 1] == '/' && !(i > 0 && line[i - 1] == ':'))
                    || (hash && c == '#')
                    || (dash && c == '-' && i + 1 < n && line[i + 1] == '-'))
                {
                    commentLen = n - i;
                }
                if (commentLen > 0)
                {
                    AddCode(result, line, segStart, i, f);
                    result.Add(new CodeToken(i, commentLen, CodeTokenKind.Comment));
                    i += commentLen;
                    segStart = i;
                    continue;
                }
                i++;
            }
            AddCode(result, line, segStart, n, f);
            return result;
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
            string seg = line.Substring(from, to - from);
            List<CodeToken> toks = SimpleCodeTokenizer.Tokenize(seg, false, false);
            if (f == Family.Css || f == Family.ScssLess) toks = CssHashTokens(seg, toks);
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
