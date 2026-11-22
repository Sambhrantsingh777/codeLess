using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>Options for <see cref="SaveActions.Apply"/> (from settings).</summary>
    public sealed class SaveActionOptions
    {
        public bool TrimTrailingWhitespace;
        public bool InsertFinalNewline;
        public bool TrimFinalNewlines;
        /// <summary>Auto save (afterDelay): like VS Code, the caret line keeps its trailing whitespace so typing isn't disturbed.</summary>
        public bool IsAutoSave;
        /// <summary>0-based caret lines to protect during auto save.</summary>
        public List<int> ProtectedLines;

        public bool Any { get { return TrimTrailingWhitespace || InsertFinalNewline || TrimFinalNewlines; } }

        public static SaveActionOptions FromSettings(IdePersistentSettings s, bool isAutoSave)
        {
            SaveActionOptions o = new SaveActionOptions();
            if (s == null) return o;
            o.TrimTrailingWhitespace = s.TrimTrailingWhitespaceOnSave;
            o.InsertFinalNewline = s.InsertFinalNewlineOnSave;
            o.TrimFinalNewlines = s.TrimFinalNewlinesOnSave;
            o.IsAutoSave = isAutoSave;
            return o;
        }
    }

    /// <summary>
    /// Save participants shared by both editors (C# 5): files.trimTrailingWhitespace (skipping lines that end inside a
    /// multi-line string and Markdown hard line breaks), files.insertFinalNewline and files.trimFinalNewlines.
    /// Works on the editor's line list (no terminators). In the buffer model a trailing newline is represented by a
    /// final empty line, so "insert final newline" appends "" when the last line has text.
    /// </summary>
    public static class SaveActions
    {
        /// <summary>Applies the actions to a copy of <paramref name="lines"/>; returns null when nothing changes.</summary>
        public static List<string> Apply(IList<string> lines, string filePath, SaveActionOptions o)
        {
            if (lines == null || o == null || !o.Any) return null;
            List<string> res = new List<string>(lines);
            bool changed = false;
            if (o.TrimTrailingWhitespace)
            {
                bool markdown = IsMarkdown(filePath);
                bool[] inString = markdown ? null : LinesEndingInsideMultilineString(res, LanguageFamily(filePath));
                for (int i = 0; i < res.Count; i++)
                {
                    string l = res[i];
                    if (l.Length == 0) continue;
                    char last = l[l.Length - 1];
                    if (last != ' ' && last != '\t') continue;
                    if (o.IsAutoSave && o.ProtectedLines != null && o.ProtectedLines.Contains(i)) continue;
                    if (inString != null && inString[i]) continue;
                    string t;
                    if (markdown) t = TrimMarkdownLine(l, i + 1 < res.Count ? res[i + 1] : null);
                    else t = l.TrimEnd(' ', '\t');
                    if (!string.Equals(t, l, StringComparison.Ordinal)) { res[i] = t; changed = true; }
                }
            }
            if (o.TrimFinalNewlines)
            {
                // Keep exactly one trailing empty line (the newline itself) when the file had one.
                int n = res.Count;
                while (n >= 3 && res[n - 1].Length == 0 && res[n - 2].Length == 0) { res.RemoveAt(n - 1); n--; changed = true; }
                if (n == 2 && res[0].Length == 0 && res[1].Length == 0) { res.RemoveAt(1); changed = true; }
            }
            if (o.InsertFinalNewline)
            {
                if (res.Count > 0 && res[res.Count - 1].Length > 0) { res.Add(""); changed = true; }
            }
            return changed ? res : null;
        }

        /// <summary>
        /// Applies the save actions to an editor buffer as one undoable step. Returns true when the buffer changed
        /// (lines edited or the final-newline flag turned on).
        /// </summary>
        public static bool ApplyToBuffer(EditorTabBuffer buf, SaveActionOptions o)
        {
            if (buf == null || o == null || !o.Any || buf.Lines == null || buf.IsLargeFile && buf.Lines.Count > 200000) return false;
            SaveActionOptions lineOpts = new SaveActionOptions();
            lineOpts.TrimTrailingWhitespace = o.TrimTrailingWhitespace;
            lineOpts.TrimFinalNewlines = o.TrimFinalNewlines && buf.HasFinalNewline;
            lineOpts.IsAutoSave = o.IsAutoSave;
            lineOpts.ProtectedLines = o.ProtectedLines;
            if (o.IsAutoSave && lineOpts.ProtectedLines == null)
            {
                lineOpts.ProtectedLines = new List<int>();
                lineOpts.ProtectedLines.Add(buf.CursorLine);
            }
            bool changed = false;
            List<string> r = lineOpts.Any ? Apply(buf.Lines, buf.FilePath, lineOpts) : null;
            if (r != null)
            {
                buf.PushUndoSnapshot();
                buf.Lines = r;
                buf.ClampCursor();
                buf.SyncContentFromLines(false);
                changed = true;
            }
            if (o.InsertFinalNewline && !buf.HasFinalNewline)
            {
                bool hasText = false;
                foreach (string l in buf.Lines) if (!string.IsNullOrEmpty(l)) { hasText = true; break; }
                if (hasText) { buf.HasFinalNewline = true; changed = true; }
            }
            return changed;
        }

        /// <summary>Convenience for whole texts ("\n" line ends); returns the input when nothing changes.</summary>
        public static string ApplyToText(string text, string filePath, SaveActionOptions o)
        {
            string t = (text ?? "").Replace("\r\n", "\n");
            List<string> lines = new List<string>(t.Split('\n'));
            List<string> r = Apply(lines, filePath, o);
            return r == null ? t : string.Join("\n", r.ToArray());
        }

        public static bool IsMarkdown(string path)
        {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return ext == ".md" || ext == ".markdown" || ext == ".mdx" || ext == ".mkd";
        }

        /// <summary>Markdown: two or more trailing spaces before a non-blank next line are a hard break and stay (normalized to two).</summary>
        public static string TrimMarkdownLine(string line, string nextLine)
        {
            string trimmed = line.TrimEnd(' ', '\t');
            if (trimmed.Length == 0) return "";
            int spaces = 0;
            for (int i = line.Length - 1; i >= 0 && line[i] == ' '; i--) spaces++;
            bool nextHasText = nextLine != null && nextLine.Trim().Length > 0;
            if (spaces >= 2 && spaces == line.Length - trimmed.Length && nextHasText) return trimmed + "  ";
            return trimmed;
        }

        public static string LanguageFamily(string path)
        {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".py": case ".pyw": case ".pyi": return "python";
                case ".js": case ".jsx": case ".mjs": case ".cjs": case ".ts": case ".tsx": case ".mts": case ".cts": case ".vue": case ".svelte": return "js";
                case ".go": return "go";
                case ".cs": return "csharp";
                case ".kt": case ".kts": case ".java": case ".scala": case ".swift": return "triple";
                case ".rs": return "rust";
                default: return "";
            }
        }

        /// <summary>
        /// Cheap lexer: marks lines whose end lies inside a multi-line string literal (Python / Kotlin / Swift triple
        /// quotes, JS template literals, Go raw strings, C# verbatim and raw strings, Rust strings). Comments and
        /// ordinary single-line strings are skipped so quotes inside them don't confuse the scan.
        /// </summary>
        public static bool[] LinesEndingInsideMultilineString(IList<string> lines, string family)
        {
            bool[] res = new bool[lines.Count];
            if (string.IsNullOrEmpty(family)) return res;
            string open = null; // active multi-line delimiter
            bool inBlockComment = false;
            for (int li = 0; li < lines.Count; li++)
            {
                string s = lines[li] ?? "";
                int i = 0;
                while (i < s.Length)
                {
                    if (inBlockComment)
                    {
                        int e = s.IndexOf("*/", i, StringComparison.Ordinal);
                        if (e < 0) { i = s.Length; break; }
                        inBlockComment = false; i = e + 2; continue;
                    }
                    if (open != null)
                    {
                        if (open == "`" || open == "\"")
                        {
                            // Template literal / Rust string: backslash escapes.
                            char d = open[0];
                            while (i < s.Length && s[i] != d) { if (s[i] == '\\' && family != "go") i++; i++; }
                            if (i < s.Length) { open = null; i++; }
                            continue;
                        }
                        if (open == "@\"")
                        {
                            while (i < s.Length)
                            {
                                if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; } open = null; i++; break; }
                                i++;
                            }
                            continue;
                        }
                        int end = s.IndexOf(open, i, StringComparison.Ordinal);
                        if (end < 0) { i = s.Length; break; }
                        i = end + open.Length; open = null; continue;
                    }
                    char c = s[i];
                    if (family == "python" && c == '#') break;
                    if (family != "python" && c == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
                    if (family != "python" && c == '/' && i + 1 < s.Length && s[i + 1] == '*') { inBlockComment = true; i += 2; continue; }
                    if ((family == "python" || family == "triple" || family == "csharp") && i + 2 < s.Length && (c == '"' || (c == '\'' && family == "python")) && s[i + 1] == c && s[i + 2] == c)
                    {
                        int q = 3;
                        while (family == "csharp" && i + q < s.Length && s[i + q] == '"') q++;
                        open = new string(c, q); i += q; continue;
                    }
                    if (family == "js" && c == '`') { open = "`"; i++; continue; }
                    if (family == "go" && c == '`') { open = "`"; i++; continue; }
                    if (family == "csharp" && c == '@' && i + 1 < s.Length && s[i + 1] == '"') { open = "@\""; i += 2; continue; }
                    if (family == "csharp" && c == '$' && i + 2 < s.Length && s[i + 1] == '@' && s[i + 2] == '"') { open = "@\""; i += 3; continue; }
                    if (family == "rust" && c == '"') { open = "\""; i++; continue; }
                    if (c == '"' || c == '\'')
                    {
                        if (family == "rust" && c == '\'') { i++; continue; } // lifetimes / chars
                        i++;
                        while (i < s.Length && s[i] != c) { if (s[i] == '\\') i++; i++; }
                        i++;
                        continue;
                    }
                    i++;
                }
                res[li] = open != null;
            }
            return res;
        }
    }
}
