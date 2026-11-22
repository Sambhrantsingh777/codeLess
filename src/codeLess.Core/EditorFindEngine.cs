using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>Find-in-selection range of the editor find widget (inclusive start, exclusive end column).</summary>
    public sealed class EditorFindScope
    {
        public int StartLine, StartCol, EndLine, EndCol;

        public EditorFindScope(int startLine, int startCol, int endLine, int endCol)
        {
            StartLine = startLine; StartCol = startCol; EndLine = endLine; EndCol = endCol;
        }

        public bool Contains(int line, int col, int len)
        {
            if (line < StartLine || line > EndLine) return false;
            if (line == StartLine && col < StartCol) return false;
            if (line == EndLine && col + len > EndCol) return false;
            return true;
        }
    }

    /// <summary>One find-widget match.</summary>
    public struct EditorFindMatch
    {
        public int Line;
        public int Col;
        public int Length;

        public EditorFindMatch(int line, int col, int length) { Line = line; Col = col; Length = length; }
    }

    /// <summary>
    /// Editor find widget matching shared by both UIs (VS Code semantics): Match Case, Match Whole Word (a match must not
    /// touch a word character on either side, same rule as Find in Files), Use Regular Expression (whole word wraps the
    /// regex too; invalid patterns report the parser message; matching has a timeout), Find in Selection, and Preserve
    /// Case replacement. STRICT C# 5.
    /// </summary>
    public static class EditorFindEngine
    {
        /// <summary>Upper bound on reported matches (VS Code stops counting at 19,999).</summary>
        public const int MaxMatches = 19999;

        /// <summary>Line regex for whole-word / regex searches; null for plain substring searches (or on error).</summary>
        public static Regex Compile(string query, bool matchCase, bool wholeWord, bool useRegex, out string error)
        {
            WorkspaceSearchOptions o = new WorkspaceSearchOptions();
            o.CaseSensitive = matchCase;
            o.WholeWord = wholeWord;
            o.UseRegex = useRegex;
            return WorkspaceSearchEngine.CompileSearchRegex(query, o, out error);
        }

        /// <summary>
        /// All matches of <paramref name="query"/> in <paramref name="lines"/> (optionally inside <paramref name="scope"/>).
        /// <paramref name="error"/> is the regex parser message for an invalid pattern (no matches then).
        /// </summary>
        public static List<EditorFindMatch> FindAll(IList<string> lines, string query, bool matchCase, bool wholeWord, bool useRegex, EditorFindScope scope, out string error)
        {
            List<EditorFindMatch> res = new List<EditorFindMatch>();
            error = null;
            if (lines == null || string.IsNullOrEmpty(query)) return res;
            Regex rx = Compile(query, matchCase, wholeWord, useRegex, out error);
            if (error != null) return res;
            StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int first = scope != null ? Math.Max(0, scope.StartLine) : 0;
            int last = scope != null ? Math.Min(lines.Count - 1, scope.EndLine) : lines.Count - 1;
            try
            {
                for (int i = first; i <= last && res.Count < MaxMatches; i++)
                {
                    string line = lines[i] ?? "";
                    if (line.Length == 0) continue;
                    if (rx != null)
                    {
                        for (Match m = rx.Match(line); m.Success; m = m.NextMatch())
                        {
                            if (m.Length == 0) continue;
                            if (scope != null && !scope.Contains(i, m.Index, m.Length)) continue;
                            res.Add(new EditorFindMatch(i, m.Index, m.Length));
                            if (res.Count >= MaxMatches) break;
                        }
                    }
                    else
                    {
                        int pos = 0;
                        while (pos <= line.Length - query.Length)
                        {
                            int idx = line.IndexOf(query, pos, cmp);
                            if (idx < 0) break;
                            if (scope == null || scope.Contains(i, idx, query.Length)) res.Add(new EditorFindMatch(i, idx, query.Length));
                            pos = idx + Math.Max(1, query.Length);
                            if (res.Count >= MaxMatches) break;
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                error = "The regular expression took too long to evaluate";
            }
            return res;
        }

        /// <summary>Short widget message for an invalid pattern (the full parser text goes into the tooltip).</summary>
        public static string ShortError(string error)
        {
            if (string.IsNullOrEmpty(error)) return null;
            return error.IndexOf("too long", StringComparison.OrdinalIgnoreCase) >= 0 ? "Regex timed out" : "Invalid regex";
        }

        /// <summary>
        /// VS Code "Preserve Case" (Alt+P / Cmd+Alt+P): the replacement takes the case shape of the matched text —
        /// ALL CAPS, all lower, Capitalized, and per-part shapes for '-' / '_' separated words.
        /// </summary>
        public static string ApplyPreserveCase(string matched, string replacement)
        {
            if (string.IsNullOrEmpty(matched) || string.IsNullOrEmpty(replacement)) return replacement ?? "";
            if (IsAllUpper(matched) && HasLetter(matched)) return replacement.ToUpperInvariant();
            if (IsAllLower(matched) && HasLetter(matched)) return replacement.ToLowerInvariant();
            foreach (char sep in new[] { '-', '_' })
            {
                if (matched.IndexOf(sep) > 0 && replacement.IndexOf(sep) > 0)
                {
                    string[] mp = matched.Split(sep), rp = replacement.Split(sep);
                    if (mp.Length == rp.Length)
                    {
                        for (int k = 0; k < rp.Length; k++) rp[k] = ApplyPreserveCase(mp[k], rp[k]);
                        return string.Join(sep.ToString(), rp);
                    }
                }
            }
            if (char.IsUpper(matched[0]) && char.IsLetter(replacement[0]))
                return char.ToUpperInvariant(replacement[0]) + replacement.Substring(1);
            if (char.IsLower(matched[0]) && char.IsLetter(replacement[0]))
                return char.ToLowerInvariant(replacement[0]) + replacement.Substring(1);
            return replacement;
        }

        private static bool HasLetter(string s) { foreach (char c in s) if (char.IsLetter(c)) return true; return false; }
        private static bool IsAllUpper(string s) { foreach (char c in s) if (char.IsLetter(c) && !char.IsUpper(c)) return false; return true; }
        private static bool IsAllLower(string s) { foreach (char c in s) if (char.IsLetter(c) && !char.IsLower(c)) return false; return true; }

        /// <summary>Count label text: "No results", "3 of 12", or "19999+" style capping like VS Code.</summary>
        public static string CountLabel(int current, int total)
        {
            if (total <= 0) return "No results";
            string t = total >= MaxMatches ? MaxMatches + "+" : total.ToString();
            return current >= 0 ? (current + 1) + " of " + t : t + " results";
        }
    }
}
