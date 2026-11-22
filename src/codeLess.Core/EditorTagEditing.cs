using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    public enum TagLanguageMode { None, Html, Xml, Jsx }

    /// <summary>A start or end tag found by <see cref="EditorTagEditing.ScanTags"/> (0-based positions).</summary>
    public struct TagInfo
    {
        public bool IsClose;
        public bool SelfClosing;
        public string Name;
        public int Line;
        public int LtCol;
        public int NameStart;
        public int NameEnd;
        /// <summary>Position just after the closing '&gt;' (EndLine / EndCol); EndLine = -1 when the tag is unterminated.</summary>
        public int EndLine;
        public int EndCol;
        public bool IsValid { get { return Line >= 0; } }
        public static TagInfo None { get { TagInfo t = new TagInfo(); t.Line = -1; t.EndLine = -1; t.Name = ""; return t; } }
    }

    /// <summary>
    /// HTML / XML / JSX tag helpers: auto-close on '&gt;', close-tag completion on '&lt;/', and linked tag rename
    /// (editing a start tag's name updates its end tag and vice versa). The scanner skips comments, CDATA, processing
    /// instructions, quoted attribute values, JSX {expressions} and HTML script/style contents; all scans are bounded.
    /// STRICT C# 5.
    /// </summary>
    public static class EditorTagEditing
    {
        public const int BackScanLines = 400;
        public const int PairScanLines = 5000;

        private static readonly HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
            "keygen", "command", "basefont", "frame", "isindex", "menuitem"
        };

        public static TagLanguageMode ModeForFile(string filePath)
        {
            string ext = "";
            try { ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant(); } catch { }
            switch (ext)
            {
                case ".html": case ".htm": case ".xhtml": case ".vue": case ".svelte": case ".razor": case ".cshtml": case ".php": case ".hbs": case ".handlebars": case ".ejs":
                    return TagLanguageMode.Html;
                case ".xml": case ".xaml": case ".axaml": case ".svg": case ".csproj": case ".vbproj": case ".fsproj": case ".props": case ".targets":
                case ".nuspec": case ".resx": case ".plist": case ".config": case ".xsd": case ".xsl": case ".xslt": case ".wxs": case ".storyboard": case ".xib":
                    return TagLanguageMode.Xml;
                case ".jsx": case ".tsx":
                    return TagLanguageMode.Jsx;
            }
            return TagLanguageMode.None;
        }

        public static bool IsVoidElement(string name) { return name != null && VoidElements.Contains(name); }

        public static bool IsNameStartChar(char c) { return char.IsLetter(c) || c == '_' || c == ':'; }
        public static bool IsNameChar(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '-' || c == '.' || c == '$'; }

        public static string ReadTagName(string line, int nameStart)
        {
            if (line == null || nameStart < 0 || nameStart > line.Length) return "";
            int e = nameStart;
            while (e < line.Length && IsNameChar(line[e])) e++;
            return line.Substring(nameStart, e - nameStart);
        }

        private static readonly HashSet<string> JsxKeywordsBeforeTag = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "yield", "await", "default", "else", "case", "in", "of", "typeof", "void", "do"
        };

        /// <summary>JSX: is the '&lt;' at (line, col) a tag start rather than a comparison / generic?</summary>
        private static bool JsxTagContext(IList<string> lines, int line, int col)
        {
            int l = line, c = col - 1;
            for (int guard = 0; guard < 3 && l >= 0; guard++)
            {
                string s = lines[l] ?? "";
                if (c >= s.Length) c = s.Length - 1;
                while (c >= 0 && char.IsWhiteSpace(s[c])) c--;
                if (c >= 0)
                {
                    char p = s[c];
                    if (p == ')' || p == ']' || p == '.') return false;
                    if (char.IsLetterOrDigit(p) || p == '_' || p == '$')
                    {
                        int e = c + 1;
                        while (c >= 0 && (char.IsLetterOrDigit(s[c]) || s[c] == '_' || s[c] == '$')) c--;
                        string word = s.Substring(c + 1, e - c - 1);
                        return JsxKeywordsBeforeTag.Contains(word);
                    }
                    return true; // ( = , : ? { } > & | ; etc.
                }
                l--;
                c = int.MaxValue;
            }
            return true; // start of file
        }

        /// <summary>Scans tags whose '&lt;' lies in [fromLine, toLine] (tags may end after toLine).</summary>
        public static List<TagInfo> ScanTags(IList<string> lines, int fromLine, int toLine, TagLanguageMode mode)
        {
            List<TagInfo> res = new List<TagInfo>();
            if (lines == null || lines.Count == 0 || mode == TagLanguageMode.None) return res;
            int l = Math.Max(0, fromLine);
            int last = Math.Min(lines.Count - 1, toLine);
            int c = 0;
            string rawTextEnd = null; // "</script" while inside raw text
            while (l <= last)
            {
                string s = lines[l] ?? "";
                if (c >= s.Length) { l++; c = 0; continue; }
                if (rawTextEnd != null)
                {
                    int e = s.IndexOf(rawTextEnd, c, StringComparison.OrdinalIgnoreCase);
                    if (e < 0) { l++; c = 0; continue; }
                    rawTextEnd = null;
                    c = e; // let the end tag be scanned normally
                    continue;
                }
                int lt = s.IndexOf('<', c);
                if (lt < 0) { l++; c = 0; continue; }
                // comments / CDATA / declarations / processing instructions
                if (mode != TagLanguageMode.Jsx)
                {
                    if (string.CompareOrdinal(s, lt, "<!--", 0, 4) == 0) { SkipTo(lines, ref l, ref c, lt + 4, "-->", last + PairScanLines); continue; }
                    if (string.CompareOrdinal(s, lt, "<![CDATA[", 0, 9) == 0) { SkipTo(lines, ref l, ref c, lt + 9, "]]>", last + PairScanLines); continue; }
                    if (lt + 1 < s.Length && (s[lt + 1] == '!' || s[lt + 1] == '?')) { SkipTo(lines, ref l, ref c, lt + 2, ">", last + PairScanLines); continue; }
                }
                bool isClose = lt + 1 < s.Length && s[lt + 1] == '/';
                int ns = lt + (isClose ? 2 : 1);
                bool fragment = ns < s.Length && s[ns] == '>';
                if (!fragment && (ns >= s.Length || !IsNameStartChar(s[ns])))
                {
                    c = lt + 1;
                    continue;
                }
                if (mode == TagLanguageMode.Jsx && !isClose && !JsxTagContext(lines, l, lt)) { c = lt + 1; continue; }
                int ne = ns;
                while (ne < s.Length && IsNameChar(s[ne])) ne++;
                TagInfo t = new TagInfo();
                t.IsClose = isClose;
                t.Name = s.Substring(ns, ne - ns);
                t.Line = l;
                t.LtCol = lt;
                t.NameStart = ns;
                t.NameEnd = ne;
                t.EndLine = -1;
                // attributes up to '>' (may span lines)
                int al = l, ac = ne;
                bool selfClosing = false, closed = false;
                int braceDepth = 0;
                char quote = '\0';
                int guardLines = 0;
                while (al < lines.Count && guardLines < 200)
                {
                    string a = lines[al] ?? "";
                    while (ac < a.Length)
                    {
                        char ch = a[ac];
                        if (quote != '\0') { if (ch == quote) quote = '\0'; ac++; continue; }
                        if (braceDepth > 0)
                        {
                            if (ch == '{') braceDepth++;
                            else if (ch == '}') braceDepth--;
                            else if (ch == '"' || ch == '\'' || ch == '`') quote = ch;
                            ac++;
                            continue;
                        }
                        if (ch == '"' || ch == '\'') { quote = ch; ac++; continue; }
                        if (ch == '{' && mode == TagLanguageMode.Jsx) { braceDepth = 1; ac++; continue; }
                        if (ch == '<' && mode != TagLanguageMode.Jsx) { closed = false; break; } // malformed: a new tag starts
                        if (ch == '>')
                        {
                            selfClosing = ac > 0 && a[ac - 1] == '/';
                            closed = true;
                            ac++;
                            break;
                        }
                        ac++;
                    }
                    if (closed || (ac < a.Length && a[ac] == '<')) break;
                    al++;
                    ac = 0;
                    guardLines++;
                }
                if (closed)
                {
                    t.SelfClosing = selfClosing;
                    t.EndLine = al;
                    t.EndCol = ac;
                    res.Add(t);
                    if (!isClose && !selfClosing && mode == TagLanguageMode.Html &&
                        (string.Equals(t.Name, "script", StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, "style", StringComparison.OrdinalIgnoreCase)))
                        rawTextEnd = "</" + t.Name;
                    l = al;
                    c = ac;
                }
                else
                {
                    res.Add(t); // unterminated: still useful for the tag under the caret
                    c = ne;
                }
            }
            return res;
        }

        private static void SkipTo(IList<string> lines, ref int l, ref int c, int from, string end, int maxLine)
        {
            int ll = l, cc = from;
            int lim = Math.Min(lines.Count - 1, maxLine);
            while (ll <= lim)
            {
                string s = lines[ll] ?? "";
                int e = cc <= s.Length ? s.IndexOf(end, Math.Min(cc, s.Length), StringComparison.Ordinal) : -1;
                if (e >= 0) { l = ll; c = e + end.Length; return; }
                ll++;
                cc = 0;
            }
            l = lim + 1;
            c = 0;
        }

        private static bool NamesEqual(string a, string b, TagLanguageMode mode)
        {
            return string.Equals(a, b, mode == TagLanguageMode.Html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool OpensElement(TagInfo t, TagLanguageMode mode)
        {
            return !t.IsClose && !t.SelfClosing && t.EndLine >= 0 && !(mode == TagLanguageMode.Html && IsVoidElement(t.Name));
        }

        /// <summary>
        /// Called right after '&gt;' was typed (the caret is after it). Returns the end tag to insert after the caret
        /// ("&lt;/div&gt;"), or null.
        /// </summary>
        public static string GetAutoCloseText(IList<string> lines, int line, int caretCol, TagLanguageMode mode)
        {
            if (lines == null || line < 0 || line >= lines.Count || mode == TagLanguageMode.None) return null;
            string s = lines[line] ?? "";
            if (caretCol <= 0 || caretCol > s.Length || s[caretCol - 1] != '>') return null;
            if (caretCol >= 2 && s[caretCol - 2] == '/') return null; // self-closing
            List<TagInfo> tags = ScanTags(lines, Math.Max(0, line - BackScanLines), line, mode);
            for (int i = tags.Count - 1; i >= 0; i--)
            {
                TagInfo t = tags[i];
                if (t.EndLine != line || t.EndCol != caretCol) continue;
                if (t.IsClose || t.SelfClosing) return null;
                if (t.Name.Length == 0 && mode != TagLanguageMode.Jsx) return null;
                if (mode == TagLanguageMode.Html && IsVoidElement(t.Name)) return null;
                string after = s.Substring(caretCol).TrimStart();
                if (after.StartsWith("</" + t.Name + ">", StringComparison.Ordinal)) return null; // already closed right here
                return "</" + t.Name + ">";
            }
            return null;
        }

        /// <summary>
        /// Called right after '/' was typed following '&lt;'. Returns the rest of the end tag of the innermost open
        /// element ("div&gt;"), or null.
        /// </summary>
        public static string GetCloseTagCompletion(IList<string> lines, int line, int caretCol, TagLanguageMode mode)
        {
            if (lines == null || line < 0 || line >= lines.Count || mode == TagLanguageMode.None) return null;
            string s = lines[line] ?? "";
            if (caretCol < 2 || caretCol > s.Length || s[caretCol - 1] != '/' || s[caretCol - 2] != '<') return null;
            if (caretCol < s.Length && IsNameChar(s[caretCol])) return null;
            int ltCol = caretCol - 2;
            List<TagInfo> tags = ScanTags(lines, Math.Max(0, line - PairScanLines), line, mode);
            List<TagInfo> stack = new List<TagInfo>();
            foreach (TagInfo t in tags)
            {
                if (t.Line > line || (t.Line == line && t.LtCol >= ltCol)) break;
                if (OpensElement(t, mode)) stack.Add(t);
                else if (t.IsClose)
                {
                    for (int k = stack.Count - 1; k >= 0; k--)
                        if (NamesEqual(stack[k].Name, t.Name, mode)) { stack.RemoveRange(k, stack.Count - k); break; }
                }
            }
            if (stack.Count == 0) return null;
            string name = stack[stack.Count - 1].Name;
            if (name.Length == 0 && mode != TagLanguageMode.Jsx) return null;
            string rest = s.Substring(caretCol);
            return rest.StartsWith(">", StringComparison.Ordinal) ? name : name + ">";
        }

        /// <summary>Finds the tag whose name contains (line, col) (inclusive end) and its matching partner.</summary>
        public static bool TryFindLinkedTag(IList<string> lines, int line, int col, TagLanguageMode mode, out TagInfo self, out TagInfo partner)
        {
            self = TagInfo.None;
            partner = TagInfo.None;
            if (lines == null || line < 0 || line >= lines.Count || mode == TagLanguageMode.None) return false;
            List<TagInfo> before = ScanTags(lines, Math.Max(0, line - BackScanLines), line, mode);
            int idx = -1;
            for (int i = before.Count - 1; i >= 0; i--)
            {
                TagInfo t = before[i];
                if (t.Line == line && col >= t.NameStart && col <= t.NameEnd) { idx = i; break; }
                if (t.Line < line) break;
            }
            if (idx < 0) return false;
            self = before[idx];
            if (self.SelfClosing || self.EndLine < 0) return false;
            if (!self.IsClose)
            {
                if (mode == TagLanguageMode.Html && IsVoidElement(self.Name)) return false;
                List<TagInfo> after = ScanTags(lines, self.Line, Math.Min(lines.Count - 1, self.Line + PairScanLines), mode);
                int depth = 0;
                bool started = false;
                foreach (TagInfo t in after)
                {
                    if (!started)
                    {
                        if (t.Line == self.Line && t.LtCol == self.LtCol) started = true;
                        continue;
                    }
                    if (!NamesEqual(t.Name, self.Name, mode)) continue;
                    if (t.IsClose)
                    {
                        if (depth == 0) { partner = t; return true; }
                        depth--;
                    }
                    else if (OpensElement(t, mode)) depth++;
                }
                return false;
            }
            else
            {
                List<TagInfo> all = ScanTags(lines, Math.Max(0, line - PairScanLines), line, mode);
                int me = -1;
                for (int i = all.Count - 1; i >= 0; i--) if (all[i].Line == self.Line && all[i].LtCol == self.LtCol) { me = i; break; }
                if (me < 0) return false;
                int depth = 0;
                for (int i = me - 1; i >= 0; i--)
                {
                    TagInfo t = all[i];
                    if (!NamesEqual(t.Name, self.Name, mode)) continue;
                    if (t.IsClose) depth++;
                    else if (OpensElement(t, mode))
                    {
                        if (depth == 0) { partner = t; return true; }
                        depth--;
                    }
                }
                return false;
            }
        }

        /// <summary>Snapshot taken before an edit so the partner tag can be renamed after it.</summary>
        public sealed class LinkedEdit
        {
            public TagInfo Self;
            public TagInfo Partner;
            public int LineCountBefore;
            public string SelfLineBefore;
        }

        public static LinkedEdit BeginLinkedEdit(IList<string> lines, int caretLine, int caretCol, TagLanguageMode mode)
        {
            TagInfo self, partner;
            if (!TryFindLinkedTag(lines, caretLine, caretCol, mode, out self, out partner)) return null;
            LinkedEdit le = new LinkedEdit();
            le.Self = self;
            le.Partner = partner;
            le.LineCountBefore = lines.Count;
            le.SelfLineBefore = lines[self.Line];
            return le;
        }

        /// <summary>
        /// After a single-line edit inside the tag name, renames the partner tag. Returns the column delta applied on the
        /// caret line when the partner sits before the caret on the same line (the caller shifts its caret), or
        /// int.MinValue when nothing was changed.
        /// </summary>
        public static int CompleteLinkedEdit(IList<string> lines, LinkedEdit le, int caretLine, int caretCol)
        {
            if (le == null || lines == null || lines.Count != le.LineCountBefore) return int.MinValue;
            if (caretLine != le.Self.Line) return int.MinValue;
            string now = lines[le.Self.Line] ?? "";
            if (string.Equals(now, le.SelfLineBefore, StringComparison.Ordinal)) return int.MinValue;
            int nameStart = le.Self.NameStart;
            // the text before the name must be untouched ("<" or "</")
            if (nameStart > now.Length || string.CompareOrdinal(now, 0, le.SelfLineBefore, 0, nameStart) != 0) return int.MinValue;
            string newName = ReadTagName(now, nameStart);
            if (caretCol < nameStart || caretCol > nameStart + newName.Length) return int.MinValue;
            if (newName.Length > 0 && !IsNameStartChar(newName[0])) return int.MinValue;
            string oldName = le.Self.Name;
            if (string.Equals(newName, oldName, StringComparison.Ordinal)) return int.MinValue;
            int lenDelta = now.Length - le.SelfLineBefore.Length;
            // only edits inside the name: the text after the name must be unchanged
            string tailBefore = le.SelfLineBefore.Substring(le.Self.NameEnd);
            string tailNow = now.Substring(nameStart + newName.Length);
            if (!string.Equals(tailBefore, tailNow, StringComparison.Ordinal)) return int.MinValue;

            TagInfo p = le.Partner;
            int pStart = p.NameStart;
            if (p.Line == le.Self.Line && p.NameStart > le.Self.NameStart) pStart += lenDelta;
            string pl = lines[p.Line] ?? "";
            if (pStart < 0 || pStart + p.Name.Length > pl.Length || string.CompareOrdinal(pl, pStart, p.Name, 0, p.Name.Length) != 0) return int.MinValue;
            if (pStart + p.Name.Length < pl.Length && IsNameChar(pl[pStart + p.Name.Length])) return int.MinValue;
            lines[p.Line] = pl.Substring(0, pStart) + newName + pl.Substring(pStart + p.Name.Length);
            if (p.Line == caretLine && pStart < caretCol) return newName.Length - p.Name.Length;
            return 0;
        }
    }
}
