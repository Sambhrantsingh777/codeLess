using System;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// Auto-closing brackets / quotes, overtype, auto-surround and pair deletion rules shared by both editors (VS Code's
    /// "languageDefined" defaults): an opener is closed only before whitespace, the end of the line or one of
    /// ";:.,=}])>"; a quote is not closed after a word character or inside a string; Backspace between an empty pair
    /// deletes both; typing a bracket / quote with a selection surrounds it. STRICT C# 5.
    /// </summary>
    public static class EditorAutoClosing
    {
        public static bool AutoCloseBrackets = true;
        public static bool AutoCloseQuotes = true;
        public static bool AutoSurround = true;

        private const string AutoCloseBefore = ";:.,=}])>";

        private static string Ext(string filePath)
        {
            try { return (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant(); } catch { return ""; }
        }

        private static bool IsPlainText(string ext)
        {
            return ext == ".txt" || ext == ".log" || ext == ".csv" || ext == ".tsv";
        }

        /// <summary>The closing character for an opener in the file's language, or '\0'.</summary>
        public static char CloserFor(char c, string filePath)
        {
            string ext = Ext(filePath);
            switch (c)
            {
                case '(': return ')';
                case '[': return ']';
                case '{': return '}';
                case '"': return IsPlainText(ext) ? '\0' : '"';
                case '\'':
                    if (IsPlainText(ext) || ext == ".md" || ext == ".markdown" || ext == ".mdx" || ext == ".rs" || ext == ".lisp" || ext == ".clj" || ext == ".el") return '\0';
                    return '\'';
                case '`':
                    if (ext == ".js" || ext == ".jsx" || ext == ".mjs" || ext == ".cjs" || ext == ".ts" || ext == ".tsx" || ext == ".mts" || ext == ".cts" ||
                        ext == ".md" || ext == ".markdown" || ext == ".mdx" || ext == ".go" || ext == ".sh" || ext == ".bash" || ext == ".zsh" ||
                        ext == ".kt" || ext == ".sql" || ext == ".vue" || ext == ".svelte" || ext == ".py")
                        return '`';
                    return '\0';
            }
            return '\0';
        }

        public static bool IsQuote(char c) { return c == '"' || c == '\'' || c == '`'; }

        public static bool IsCloser(char c) { return c == ')' || c == ']' || c == '}'; }

        private static bool IsWordChar(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        /// <summary>True when an unterminated <paramref name="quote"/> string is open before <paramref name="col"/> on the line.</summary>
        public static bool InsideString(string line, int col, char quote)
        {
            bool inside = false;
            int n = Math.Min(col, line == null ? 0 : line.Length);
            for (int i = 0; i < n; i++)
            {
                char c = line[i];
                if (c == '\\' && inside) { i++; continue; }
                if (c == quote) inside = !inside;
            }
            return inside;
        }

        /// <summary>Should typing <paramref name="opener"/> at <paramref name="col"/> also insert its closer?</summary>
        public static bool ShouldAutoClose(string line, int col, char opener, string filePath)
        {
            char closer = CloserFor(opener, filePath);
            if (closer == '\0') return false;
            bool quote = IsQuote(opener);
            if (quote ? !AutoCloseQuotes : !AutoCloseBrackets) return false;
            line = line ?? "";
            if (col < line.Length)
            {
                char next = line[col];
                if (!char.IsWhiteSpace(next) && AutoCloseBefore.IndexOf(next) < 0) return false;
            }
            if (quote)
            {
                if (col > 0 && (IsWordChar(line[col - 1]) || line[col - 1] == '\\' || line[col - 1] == opener)) return false;
                if (InsideString(line, col, opener)) return false;
                // inside another kind of string ("it's") a quote is plain text
                if ((opener != '"' && InsideString(line, col, '"')) || (opener != '\'' && opener != '`' && InsideString(line, col, '\''))) return false;
            }
            return true;
        }

        /// <summary>Should typing <paramref name="typed"/> just step over the identical character after the caret?</summary>
        public static bool ShouldOvertype(string line, int col, char typed, bool autoInserted)
        {
            line = line ?? "";
            if (col >= line.Length || line[col] != typed) return false;
            if (IsQuote(typed))
            {
                // closing the string that is open here
                return autoInserted || InsideString(line, col, typed);
            }
            return IsCloser(typed) && autoInserted;
        }

        /// <summary>Backspace between an empty pair "(|)" deletes both characters.</summary>
        public static bool ShouldDeletePair(string line, int col, string filePath)
        {
            line = line ?? "";
            if (col <= 0 || col >= line.Length) return false;
            char o = line[col - 1], c = line[col];
            char expect = CloserFor(o, filePath);
            if (expect == '\0' || expect != c) return false;
            if (IsQuote(o)) return !InsideString(line, col - 1, o);
            return true;
        }

        /// <summary>Opening / closing text for auto-surround (brackets and quotes; also "&lt;" in markup files).</summary>
        public static bool TryGetSurroundPair(char typed, string filePath, out string open, out string close)
        {
            open = close = null;
            if (!AutoSurround) return false;
            char closer = CloserFor(typed, filePath);
            if (closer == '\0' && typed == '`') closer = '`';
            if (closer == '\0' && (typed == '"' || typed == '\'')) closer = typed;
            if (closer == '\0') return false;
            open = typed.ToString();
            close = closer.ToString();
            return true;
        }
    }
}
