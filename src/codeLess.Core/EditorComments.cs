using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>Comment syntax of a language (null = not available).</summary>
    public sealed class CommentTokens
    {
        public string Line;
        public string BlockStart;
        public string BlockEnd;
    }

    /// <summary>One text change made by a comment toggle: <see cref="Delta"/> characters inserted (&gt; 0) or removed (&lt; 0) at <see cref="Col"/>.</summary>
    public sealed class CommentEdit
    {
        public int Line;
        public int Col;
        public int Delta;
    }

    /// <summary>
    /// Toggle Line Comment (Cmd/Ctrl+/) and Toggle Block Comment (Shift+Alt+A) shared by both editors, following VS Code:
    /// language-specific tokens; line comments go at the smallest indentation of the selected non-blank lines (blank
    /// lines are skipped) and are removed together with one following space; languages without line comments (HTML,
    /// XML, CSS, Markdown) wrap the selection / line in a block comment; carets and selections follow the text. STRICT C# 5.
    /// </summary>
    public static class EditorComments
    {
        public static CommentTokens For(string filePath)
        {
            string ext = "", name = "";
            try
            {
                ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant();
                name = (Path.GetFileName(filePath ?? "") ?? "").ToLowerInvariant();
            }
            catch { }
            CommentTokens t = new CommentTokens();
            switch (ext)
            {
                case ".html": case ".htm": case ".xhtml": case ".xml": case ".xaml": case ".axaml": case ".svg": case ".csproj": case ".vbproj":
                case ".fsproj": case ".props": case ".targets": case ".config": case ".plist": case ".resx": case ".nuspec": case ".md":
                case ".markdown": case ".mdx": case ".vue": case ".svelte": case ".xsd": case ".xsl": case ".xslt":
                    t.BlockStart = "<!--"; t.BlockEnd = "-->"; break;
                case ".css":
                    t.BlockStart = "/*"; t.BlockEnd = "*/"; break;
                case ".razor": case ".cshtml":
                    t.BlockStart = "@*"; t.BlockEnd = "*@"; break;
                case ".py": case ".pyw": case ".pyi": case ".sh": case ".bash": case ".zsh": case ".fish": case ".ksh": case ".yaml": case ".yml":
                case ".toml": case ".pl": case ".pm": case ".r": case ".conf": case ".properties": case ".cmake": case ".ex": case ".exs":
                case ".nim": case ".tcl": case ".gitignore": case ".dockerignore": case ".env": case ".mk": case ".jl": case ".tf": case ".hcl":
                    t.Line = "#"; break;
                case ".rb": case ".rake": case ".gemspec":
                    t.Line = "#"; t.BlockStart = "=begin"; t.BlockEnd = "=end"; break;
                case ".ps1": case ".psm1": case ".psd1":
                    t.Line = "#"; t.BlockStart = "<#"; t.BlockEnd = "#>"; break;
                case ".coffee":
                    t.Line = "#"; t.BlockStart = "###"; t.BlockEnd = "###"; break;
                case ".sql":
                    t.Line = "--"; t.BlockStart = "/*"; t.BlockEnd = "*/"; break;
                case ".lua":
                    t.Line = "--"; t.BlockStart = "--[["; t.BlockEnd = "]]"; break;
                case ".hs":
                    t.Line = "--"; t.BlockStart = "{-"; t.BlockEnd = "-}"; break;
                case ".ini": case ".asm": case ".s": case ".lisp": case ".el": case ".clj": case ".cljs": case ".scm":
                    t.Line = ";"; break;
                case ".tex": case ".erl": case ".m":
                    t.Line = "%"; break;
                case ".vb": case ".vbs":
                    t.Line = "'"; break;
                case ".bat": case ".cmd":
                    t.Line = "@REM"; break;
                case ".fs": case ".fsx": case ".fsi":
                    t.Line = "//"; t.BlockStart = "(*"; t.BlockEnd = "*)"; break;
                case ".ml": case ".mli":
                    t.BlockStart = "(*"; t.BlockEnd = "*)"; break;
                case ".txt": case ".log": case ".csv": case ".tsv":
                    return t;
                default:
                    if (name == "makefile" || name == "gnumakefile" || name == "dockerfile" || name == "gemfile" || name == "rakefile") { t.Line = "#"; break; }
                    t.Line = "//"; t.BlockStart = "/*"; t.BlockEnd = "*/"; break;
            }
            return t;
        }

        private static int FirstNonWs(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] != ' ' && s[i] != '\t') return i;
            return -1;
        }

        private static bool At(string s, int i, string tok)
        {
            return tok != null && i >= 0 && i + tok.Length <= s.Length && string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0;
        }

        /// <summary>
        /// Toggles line comments on the union of the inclusive line ranges (each int[] { first, last }). Edits
        /// <see cref="EditorTabBuffer.Lines"/>, moves the primary caret / anchor, and returns the edits so the caller can
        /// move other carets with <see cref="AdjustColumn"/>. Returns null when the language has no comment syntax.
        /// </summary>
        public static List<CommentEdit> ToggleLineComments(EditorTabBuffer buf, IList<int[]> lineRanges)
        {
            if (buf == null || buf.Lines == null || lineRanges == null) return null;
            CommentTokens t = For(buf.FilePath ?? buf.FileName ?? ("x." + (buf.Language ?? "")));
            SortedDictionary<int, bool> set = new SortedDictionary<int, bool>();
            foreach (int[] r in lineRanges)
            {
                int a = Math.Max(0, Math.Min(r[0], r[1])), b = Math.Min(buf.Lines.Count - 1, Math.Max(r[0], r[1]));
                for (int l = a; l <= b; l++) set[l] = true;
            }
            if (set.Count == 0) return new List<CommentEdit>();
            List<CommentEdit> edits = new List<CommentEdit>();
            if (t.Line == null)
            {
                if (t.BlockStart == null) return null;
                // no line comments: wrap / unwrap the lines' content in one block comment per contiguous range
                int first = -1, last = -1;
                foreach (int l in set.Keys) { if (first < 0) first = l; last = l; }
                ToggleBlockOnLines(buf, first, last, t, edits);
                ApplyEditsToPrimary(buf, edits);
                return edits;
            }
            string tok = t.Line;
            bool allCommented = true, anyText = false;
            int minIndent = int.MaxValue;
            foreach (int l in set.Keys)
            {
                string s = buf.Lines[l] ?? "";
                int f = FirstNonWs(s);
                if (f < 0) continue;
                anyText = true;
                if (!At(s, f, tok)) allCommented = false;
                if (f < minIndent) minIndent = f;
            }
            if (!anyText) { allCommented = false; minIndent = 0; }
            foreach (int l in set.Keys)
            {
                string s = buf.Lines[l] ?? "";
                int f = FirstNonWs(s);
                if (allCommented)
                {
                    if (f < 0 || !At(s, f, tok)) continue;
                    int n = tok.Length + (f + tok.Length < s.Length && s[f + tok.Length] == ' ' ? 1 : 0);
                    buf.Lines[l] = s.Remove(f, n);
                    CommentEdit e = new CommentEdit(); e.Line = l; e.Col = f; e.Delta = -n; edits.Add(e);
                }
                else
                {
                    if (f < 0 && anyText) continue; // blank lines are left alone
                    int at = Math.Min(minIndent, s.Length);
                    string ins = tok + " ";
                    buf.Lines[l] = s.Insert(at, ins);
                    CommentEdit e = new CommentEdit(); e.Line = l; e.Col = at; e.Delta = ins.Length; edits.Add(e);
                }
            }
            ApplyEditsToPrimary(buf, edits);
            return edits;
        }

        private static void ToggleBlockOnLines(EditorTabBuffer buf, int first, int last, CommentTokens t, List<CommentEdit> edits)
        {
            string a = buf.Lines[first] ?? "", b = buf.Lines[last] ?? "";
            int fs = FirstNonWs(a);
            string bt = b.TrimEnd(' ', '\t');
            if (fs >= 0 && At(a, fs, t.BlockStart) && bt.EndsWith(t.BlockEnd, StringComparison.Ordinal) && !(first == last && bt.Length - fs < t.BlockStart.Length + t.BlockEnd.Length))
            {
                // remove the end token (and one space before it) first, then the start token (and one space after it)
                int endAt = bt.Length - t.BlockEnd.Length;
                int endN = t.BlockEnd.Length;
                if (endAt > 0 && b[endAt - 1] == ' ' && !(first == last && endAt - 1 < fs + t.BlockStart.Length)) { endAt--; endN++; }
                buf.Lines[last] = b.Remove(endAt, endN);
                CommentEdit e1 = new CommentEdit(); e1.Line = last; e1.Col = endAt; e1.Delta = -endN; edits.Add(e1);
                a = buf.Lines[first];
                int n = t.BlockStart.Length + (fs + t.BlockStart.Length < a.Length && a[fs + t.BlockStart.Length] == ' ' ? 1 : 0);
                buf.Lines[first] = a.Remove(fs, n);
                CommentEdit e2 = new CommentEdit(); e2.Line = first; e2.Col = fs; e2.Delta = -n; edits.Add(e2);
                return;
            }
            int at = fs < 0 ? a.Length : fs;
            string endText = buf.Lines[last] ?? "";
            int endCol = endText.TrimEnd(' ', '\t').Length;
            buf.Lines[last] = endText.Insert(endCol, " " + t.BlockEnd);
            CommentEdit e3 = new CommentEdit(); e3.Line = last; e3.Col = endCol; e3.Delta = t.BlockEnd.Length + 1; edits.Add(e3);
            buf.Lines[first] = buf.Lines[first].Insert(at, t.BlockStart + " ");
            CommentEdit e4 = new CommentEdit(); e4.Line = first; e4.Col = at; e4.Delta = t.BlockStart.Length + 1; edits.Add(e4);
        }

        /// <summary>Toggle Block Comment on the primary selection (or the caret: an empty block with the caret inside).</summary>
        public static bool ToggleBlockComment(EditorTabBuffer buf)
        {
            if (buf == null || buf.Lines == null) return false;
            CommentTokens t = For(buf.FilePath ?? buf.FileName ?? ("x." + (buf.Language ?? "")));
            if (t.BlockStart == null)
            {
                // no block comments (Python, shell, ...): VS Code falls back to line comments
                int a = buf.HasSelection() ? Math.Min(buf.SelectionAnchorLine, buf.CursorLine) : buf.CursorLine;
                int b = buf.HasSelection() ? Math.Max(buf.SelectionAnchorLine, buf.CursorLine) : buf.CursorLine;
                List<int[]> r = new List<int[]>();
                r.Add(new int[] { a, b });
                return ToggleLineComments(buf, r) != null;
            }
            string open = t.BlockStart, close = t.BlockEnd;
            if (!buf.HasSelection())
            {
                string ln = buf.Lines[buf.CursorLine] ?? "";
                int c = Math.Min(ln.Length, buf.CursorCol);
                buf.Lines[buf.CursorLine] = ln.Substring(0, c) + open + "  " + close + ln.Substring(c);
                buf.CursorCol = c + open.Length + 1;
                return true;
            }
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            string sLine = buf.Lines[sL], eLine = buf.Lines[eL];
            // selection inside the tokens ("/* |text| */") or including them ("|/* text */|")
            int openAt = -1, closeAt = -1;
            if (At(sLine, sC, open) && eC - close.Length >= 0 && At(eLine, eC - close.Length, close) && !(sL == eL && eC - close.Length < sC + open.Length)) { openAt = sC; closeAt = eC - close.Length; }
            else
            {
                int o = sC - open.Length;
                while (o >= 0 && o + open.Length <= sC && !At(sLine, o, open) && sC - o <= open.Length + 1) o--;
                if (o >= 0 && At(sLine, o, open) && sLine.Substring(o + open.Length, sC - o - open.Length).Trim().Length == 0)
                {
                    int cpos = eC;
                    while (cpos < eLine.Length && eLine[cpos] == ' ') cpos++;
                    if (At(eLine, cpos, close)) { openAt = o; closeAt = cpos; }
                }
            }
            bool fwd = buf.CursorLine > buf.SelectionAnchorLine || (buf.CursorLine == buf.SelectionAnchorLine && buf.CursorCol >= buf.SelectionAnchorCol);
            int nsL = sL, nsC, neL = eL, neC;
            if (openAt >= 0)
            {
                int closeEnd = closeAt + close.Length;
                int closeStart = closeAt;
                if (closeStart > 0 && eLine[closeStart - 1] == ' ' && !(sL == eL && closeStart - 1 < openAt + open.Length)) closeStart--;
                buf.Lines[eL] = eLine.Remove(closeStart, closeEnd - closeStart);
                string sl2 = buf.Lines[sL];
                int openN = open.Length + (openAt + open.Length < sl2.Length && sl2[openAt + open.Length] == ' ' ? 1 : 0);
                buf.Lines[sL] = sl2.Remove(openAt, openN);
                nsC = sC <= openAt ? openAt : Math.Max(openAt, sC - openN);
                neC = Math.Min(eC, closeStart) - (sL == eL ? openN : 0);
                if (neC < nsC && sL == eL) neC = nsC;
            }
            else
            {
                buf.Lines[eL] = eLine.Insert(eC, " " + close);
                buf.Lines[sL] = buf.Lines[sL].Insert(sC, open + " ");
                nsC = sC + open.Length + 1;
                neC = eC + (sL == eL ? open.Length + 1 : 0);
            }
            if (fwd) { buf.SelectionAnchorLine = nsL; buf.SelectionAnchorCol = nsC; buf.CursorLine = neL; buf.CursorCol = neC; }
            else { buf.SelectionAnchorLine = neL; buf.SelectionAnchorCol = neC; buf.CursorLine = nsL; buf.CursorCol = nsC; }
            return true;
        }

        /// <summary>New column of a caret at (line, col) after the edits.</summary>
        public static int AdjustColumn(List<CommentEdit> edits, int line, int col)
        {
            if (edits == null) return col;
            int res = col;
            foreach (CommentEdit e in edits)
            {
                if (e.Line != line) continue;
                if (e.Delta > 0) { if (col >= e.Col) res += e.Delta; }
                else
                {
                    int n = -e.Delta;
                    if (col >= e.Col + n) res -= n;
                    else if (col > e.Col) res -= col - e.Col;
                }
            }
            return Math.Max(0, res);
        }

        private static void ApplyEditsToPrimary(EditorTabBuffer buf, List<CommentEdit> edits)
        {
            bool sel = buf.HasSelection();
            int ac = sel ? AdjustColumn(edits, buf.SelectionAnchorLine, buf.SelectionAnchorCol) : -1;
            buf.CursorCol = AdjustColumn(edits, buf.CursorLine, buf.CursorCol);
            if (sel) buf.SelectionAnchorCol = ac;
            buf.ClampCursor();
        }
    }
}
