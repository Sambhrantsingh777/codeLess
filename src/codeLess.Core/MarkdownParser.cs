using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public enum MdBlockKind
    {
        Paragraph,
        Heading,
        CodeBlock,
        List,
        BlockQuote,
        Table,
        HorizontalRule
    }

    public enum MdInlineKind
    {
        Text,
        Strong,
        Emphasis,
        Strikethrough,
        Code,
        Link,
        Image,
        LineBreak
    }

    public enum MdAlign
    {
        None,
        Left,
        Center,
        Right
    }

    public class MdInline
    {
        public MdInlineKind Kind;
        public string Text = "";
        public string Url = "";
        public string Title = "";
        public List<MdInline> Children = new List<MdInline>();

        public MdInline() { }
        public MdInline(MdInlineKind kind, string text) { Kind = kind; Text = text ?? ""; }

        /// <summary>Concatenated visible text (for anchors, alt text and tests).</summary>
        public string PlainText
        {
            get
            {
                if (Kind == MdInlineKind.Text || Kind == MdInlineKind.Code) return Text;
                if (Kind == MdInlineKind.LineBreak) return "\n";
                if (Kind == MdInlineKind.Image) return Text;
                return MarkdownParser.ToPlainText(Children);
            }
        }
    }

    public class MdListItem
    {
        public List<MdBlock> Blocks = new List<MdBlock>();
        public bool IsTask;
        public bool IsChecked;
        public int SourceLine;
    }

    public class MdBlock
    {
        public MdBlockKind Kind;
        /// <summary>0-based source line where the block starts (scroll sync).</summary>
        public int SourceLine;
        public int Level;
        public List<MdInline> Inlines = new List<MdInline>();
        public string Language = "";
        public string Code = "";
        public bool Ordered;
        public int StartNumber = 1;
        public List<MdListItem> Items = new List<MdListItem>();
        public List<MdBlock> Children = new List<MdBlock>();
        public List<List<MdInline>> TableHeader = new List<List<MdInline>>();
        public List<MdAlign> TableAlign = new List<MdAlign>();
        public List<List<List<MdInline>>> TableRows = new List<List<List<MdInline>>>();
        /// <summary>GitHub-style slug for headings ("my-heading").</summary>
        public string Anchor = "";
    }

    public class MdDocument
    {
        public List<MdBlock> Blocks = new List<MdBlock>();

        /// <summary>All headings in document order (for anchors / outline).</summary>
        public List<MdBlock> Headings()
        {
            List<MdBlock> list = new List<MdBlock>();
            Collect(Blocks, list);
            return list;
        }

        private static void Collect(List<MdBlock> blocks, List<MdBlock> list)
        {
            foreach (MdBlock b in blocks)
            {
                if (b.Kind == MdBlockKind.Heading) list.Add(b);
                Collect(b.Children, list);
                foreach (MdListItem it in b.Items) Collect(it.Blocks, list);
            }
        }
    }

    /// <summary>
    /// CommonMark/GFM-subset parser (headings, emphasis, code spans, fenced/indented code, nested and task lists,
    /// blockquotes, tables, links incl. reference links, images, rules, basic inline HTML). Shared by both UIs; C# 5.
    /// </summary>
    public static class MarkdownParser
    {
        private static readonly Regex FenceOpen = new Regex(@"^( {0,3})(`{3,}|~{3,})\s*([^`\s]*)[^`]*$", RegexOptions.Compiled);
        private static readonly Regex AtxHeading = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex HrRegex = new Regex(@"^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$", RegexOptions.Compiled);
        private static readonly Regex ListItemRegex = new Regex(@"^( {0,3})([-+*]|\d{1,9}[.)])([ \t]+|$)(.*)$", RegexOptions.Compiled);
        private static readonly Regex TableDelim = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex RefDef = new Regex(@"^ {0,3}\[([^\]]+)\]:\s*<?([^\s>]+)>?(?:\s+[""'(](.*)[""')])?\s*$", RegexOptions.Compiled);
        private static readonly Regex SetextRegex = new Regex(@"^ {0,3}(=+|-+)\s*$", RegexOptions.Compiled);

        public static MdDocument Parse(string text)
        {
            string norm = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            List<string> lines = new List<string>(norm.Split('\n'));
            Dictionary<string, string[]> refs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            // Pre-pass: link reference definitions (outside fences).
            bool inFence = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (FenceOpen.IsMatch(lines[i])) inFence = !inFence;
                if (inFence) continue;
                Match m = RefDef.Match(lines[i]);
                if (m.Success)
                {
                    refs[m.Groups[1].Value.Trim()] = new string[] { m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : "" };
                    lines[i] = "\u0000REFDEF";
                }
            }
            MdDocument doc = new MdDocument();
            int[] lineMap = new int[lines.Count];
            for (int i = 0; i < lineMap.Length; i++) lineMap[i] = i;
            doc.Blocks = ParseBlocks(lines, lineMap, refs);
            AssignAnchors(doc);
            return doc;
        }

        private static void AssignAnchors(MdDocument doc)
        {
            Dictionary<string, int> used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MdBlock h in doc.Headings())
            {
                string slug = Slugify(ToPlainText(h.Inlines));
                int n;
                if (used.TryGetValue(slug, out n)) { used[slug] = n + 1; slug = slug + "-" + (n + 1).ToString(CultureInfo.InvariantCulture); }
                else used[slug] = 0;
                h.Anchor = slug;
            }
        }

        /// <summary>GitHub heading anchor: lower case, punctuation removed, spaces to hyphens.</summary>
        public static string Slugify(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (text ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.ToString();
        }

        public static string ToPlainText(List<MdInline> inlines)
        {
            StringBuilder sb = new StringBuilder();
            if (inlines != null) foreach (MdInline i in inlines) sb.Append(i.PlainText);
            return sb.ToString();
        }

        private static bool IsBlank(string s) { return s == null || s.Trim().Length == 0 || s == "\u0000REFDEF"; }

        private static int LeadingSpaces(string s)
        {
            int n = 0;
            foreach (char c in s)
            {
                if (c == ' ') n++;
                else if (c == '\t') n += 4 - (n % 4);
                else break;
            }
            return n;
        }

        /// <summary>Removes up to <paramref name="count"/> columns of leading indentation.</summary>
        private static string Dedent(string s, int count)
        {
            int col = 0, i = 0;
            while (i < s.Length && col < count)
            {
                if (s[i] == ' ') col++;
                else if (s[i] == '\t') col += 4 - (col % 4);
                else break;
                i++;
            }
            return s.Substring(i);
        }

        private static bool StartsBlock(string line)
        {
            if (FenceOpen.IsMatch(line) || AtxHeading.IsMatch(line) || HrRegex.IsMatch(line)) return true;
            string t = line.TrimStart();
            if (t.StartsWith(">")) return true;
            Match lm = ListItemRegex.Match(line);
            if (lm.Success && lm.Groups[4].Value.Trim().Length > 0)
            {
                string marker = lm.Groups[2].Value;
                if (!char.IsDigit(marker[0]) || marker.StartsWith("1")) return true;
            }
            return false;
        }

        private static List<MdBlock> ParseBlocks(List<string> lines, int[] lineMap, Dictionary<string, string[]> refs)
        {
            List<MdBlock> blocks = new List<MdBlock>();
            int i = 0;
            int n = lines.Count;
            while (i < n)
            {
                string line = lines[i];
                if (IsBlank(line)) { i++; continue; }
                int src = lineMap[i];

                // Fenced code
                Match fm = FenceOpen.Match(line);
                if (fm.Success)
                {
                    string fence = fm.Groups[2].Value;
                    int fenceIndent = fm.Groups[1].Value.Length;
                    MdBlock cb = new MdBlock();
                    cb.Kind = MdBlockKind.CodeBlock;
                    cb.SourceLine = src;
                    cb.Language = fm.Groups[3].Value.Trim().ToLowerInvariant();
                    StringBuilder code = new StringBuilder();
                    int j = i + 1;
                    bool first = true;
                    for (; j < n; j++)
                    {
                        string l = lines[j];
                        string lt = l.Trim();
                        if (lt.StartsWith(fence.Substring(0, 3)) && lt.TrimStart(fence[0]).Trim().Length == 0 && lt.Length >= fence.Length && LeadingSpaces(l) < 4) break;
                        if (!first) code.Append('\n');
                        code.Append(Dedent(l, fenceIndent));
                        first = false;
                    }
                    cb.Code = code.ToString();
                    blocks.Add(cb);
                    i = j + 1;
                    continue;
                }

                // ATX heading
                Match hm = AtxHeading.Match(line);
                if (hm.Success)
                {
                    MdBlock h = new MdBlock();
                    h.Kind = MdBlockKind.Heading;
                    h.SourceLine = src;
                    h.Level = hm.Groups[1].Value.Length;
                    h.Inlines = ParseInlines(hm.Groups[2].Success ? hm.Groups[2].Value : "", refs);
                    blocks.Add(h);
                    i++;
                    continue;
                }

                // Horizontal rule
                if (HrRegex.IsMatch(line))
                {
                    MdBlock hr = new MdBlock();
                    hr.Kind = MdBlockKind.HorizontalRule;
                    hr.SourceLine = src;
                    blocks.Add(hr);
                    i++;
                    continue;
                }

                // Blockquote
                if (line.TrimStart().StartsWith(">") && LeadingSpaces(line) < 4)
                {
                    List<string> inner = new List<string>();
                    List<int> innerMap = new List<int>();
                    int j = i;
                    while (j < n && !IsBlank(lines[j]))
                    {
                        string l = lines[j];
                        string lt = l.TrimStart();
                        if (lt.StartsWith(">"))
                        {
                            string rest = lt.Substring(1);
                            if (rest.StartsWith(" ")) rest = rest.Substring(1);
                            inner.Add(rest);
                        }
                        else if (j > i && !StartsBlock(l)) inner.Add(l); // lazy continuation
                        else break;
                        innerMap.Add(lineMap[j]);
                        j++;
                    }
                    MdBlock bq = new MdBlock();
                    bq.Kind = MdBlockKind.BlockQuote;
                    bq.SourceLine = src;
                    bq.Children = ParseBlocks(inner, innerMap.ToArray(), refs);
                    blocks.Add(bq);
                    i = j;
                    continue;
                }

                // List
                Match lm = ListItemRegex.Match(line);
                if (lm.Success && LeadingSpaces(line) < 4)
                {
                    int consumed;
                    MdBlock list = ParseList(lines, lineMap, i, refs, out consumed);
                    blocks.Add(list);
                    i += consumed;
                    continue;
                }

                // Table
                if (line.Contains("|") && i + 1 < n && TableDelim.IsMatch(lines[i + 1]) && lines[i + 1].Contains("-"))
                {
                    List<string> header = SplitTableRow(line);
                    List<string> delim = SplitTableRow(lines[i + 1]);
                    if (header.Count == delim.Count || delim.Count > 0)
                    {
                        MdBlock tb = new MdBlock();
                        tb.Kind = MdBlockKind.Table;
                        tb.SourceLine = src;
                        foreach (string d in delim)
                        {
                            string dt = d.Trim();
                            bool l = dt.StartsWith(":"), r = dt.EndsWith(":");
                            tb.TableAlign.Add(l && r ? MdAlign.Center : (r ? MdAlign.Right : (l ? MdAlign.Left : MdAlign.None)));
                        }
                        foreach (string hc in header) tb.TableHeader.Add(ParseInlines(hc.Trim(), refs));
                        int j = i + 2;
                        while (j < n && !IsBlank(lines[j]) && lines[j].Contains("|"))
                        {
                            List<List<MdInline>> row = new List<List<MdInline>>();
                            foreach (string c in SplitTableRow(lines[j])) row.Add(ParseInlines(c.Trim(), refs));
                            tb.TableRows.Add(row);
                            j++;
                        }
                        blocks.Add(tb);
                        i = j;
                        continue;
                    }
                }

                // Indented code
                if (LeadingSpaces(line) >= 4)
                {
                    StringBuilder code = new StringBuilder();
                    int j = i;
                    int lastNonBlank = i;
                    List<string> collected = new List<string>();
                    while (j < n && (IsBlank(lines[j]) || LeadingSpaces(lines[j]) >= 4))
                    {
                        collected.Add(IsBlank(lines[j]) ? "" : Dedent(lines[j], 4));
                        if (!IsBlank(lines[j])) lastNonBlank = j;
                        j++;
                    }
                    int keep = lastNonBlank - i + 1;
                    for (int k = 0; k < keep; k++) { if (k > 0) code.Append('\n'); code.Append(collected[k]); }
                    MdBlock cb = new MdBlock();
                    cb.Kind = MdBlockKind.CodeBlock;
                    cb.SourceLine = src;
                    cb.Code = code.ToString();
                    blocks.Add(cb);
                    i = lastNonBlank + 1;
                    continue;
                }

                // Paragraph (with setext heading detection)
                {
                    StringBuilder para = new StringBuilder();
                    int j = i;
                    int setextLevel = 0;
                    while (j < n && !IsBlank(lines[j]))
                    {
                        if (j > i)
                        {
                            Match sm = SetextRegex.Match(lines[j]);
                            if (sm.Success) { setextLevel = sm.Groups[1].Value[0] == '=' ? 1 : 2; j++; break; }
                            if (StartsBlock(lines[j])) break;
                            if (lines[j].Contains("|") && j + 1 < n && TableDelim.IsMatch(lines[j + 1]) && lines[j + 1].Contains("-")) break;
                            para.Append('\n');
                        }
                        para.Append(j == i ? lines[j].TrimStart() : lines[j].TrimStart());
                        j++;
                    }
                    MdBlock p = new MdBlock();
                    p.Kind = setextLevel > 0 ? MdBlockKind.Heading : MdBlockKind.Paragraph;
                    p.Level = setextLevel;
                    p.SourceLine = src;
                    p.Inlines = ParseInlines(para.ToString(), refs);
                    blocks.Add(p);
                    i = j;
                }
            }
            return blocks;
        }

        private static MdBlock ParseList(List<string> lines, int[] lineMap, int start, Dictionary<string, string[]> refs, out int consumed)
        {
            MdBlock list = new MdBlock();
            list.Kind = MdBlockKind.List;
            list.SourceLine = lineMap[start];
            Match first = ListItemRegex.Match(lines[start]);
            string firstMarker = first.Groups[2].Value;
            list.Ordered = char.IsDigit(firstMarker[0]);
            if (list.Ordered)
            {
                int sn;
                if (int.TryParse(firstMarker.Substring(0, firstMarker.Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out sn)) list.StartNumber = sn;
            }
            char bulletChar = list.Ordered ? firstMarker[firstMarker.Length - 1] : firstMarker[0];
            int baseIndent = first.Groups[1].Value.Length;
            int n = lines.Count;
            int i = start;
            while (i < n)
            {
                Match m = ListItemRegex.Match(lines[i]);
                if (!m.Success) break;
                string marker = m.Groups[2].Value;
                bool ordered = char.IsDigit(marker[0]);
                char mc = ordered ? marker[marker.Length - 1] : marker[0];
                if (ordered != list.Ordered || mc != bulletChar) break;
                int indent = m.Groups[1].Value.Length;
                if (indent > baseIndent + 3) break;
                if (HrRegex.IsMatch(lines[i])) break;
                string spacing = m.Groups[3].Value;
                int contentIndent = indent + marker.Length + (spacing.Length == 0 ? 1 : (spacing.Length > 4 ? 1 : spacing.Length));
                List<string> itemLines = new List<string>();
                List<int> itemMap = new List<int>();
                itemLines.Add(m.Groups[4].Value);
                itemMap.Add(lineMap[i]);
                int j = i + 1;
                while (j < n)
                {
                    string l = lines[j];
                    if (IsBlank(l))
                    {
                        // Blank line: the item continues only if the next non-blank line is indented into it.
                        int k = j + 1;
                        while (k < n && IsBlank(lines[k])) k++;
                        if (k < n && LeadingSpaces(lines[k]) >= contentIndent)
                        {
                            for (int b = j; b < k; b++) { itemLines.Add(""); itemMap.Add(lineMap[b]); }
                            j = k;
                            continue;
                        }
                        break;
                    }
                    int lead = LeadingSpaces(l);
                    if (lead >= contentIndent)
                    {
                        itemLines.Add(Dedent(l, contentIndent));
                        itemMap.Add(lineMap[j]);
                        j++;
                        continue;
                    }
                    // A new item of this list (or a sibling list) ends the item.
                    if (ListItemRegex.IsMatch(l)) break;
                    if (StartsBlock(l)) break;
                    // Lazy paragraph continuation.
                    itemLines.Add(l.TrimStart());
                    itemMap.Add(lineMap[j]);
                    j++;
                }
                MdListItem item = new MdListItem();
                item.SourceLine = lineMap[i];
                string firstText = itemLines[0];
                if (firstText.StartsWith("[ ] ") || firstText == "[ ]" || firstText.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || string.Equals(firstText, "[x]", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsTask = true;
                    item.IsChecked = firstText[1] == 'x' || firstText[1] == 'X';
                    itemLines[0] = firstText.Length > 4 ? firstText.Substring(4) : "";
                }
                item.Blocks = ParseBlocks(itemLines, itemMap.ToArray(), refs);
                list.Items.Add(item);
                i = j;
                // Blank lines between items keep the list going.
                int nb = i;
                while (nb < n && IsBlank(lines[nb])) nb++;
                if (nb > i && nb < n && ListItemRegex.IsMatch(lines[nb]))
                {
                    Match nm = ListItemRegex.Match(lines[nb]);
                    string nmark = nm.Groups[2].Value;
                    bool nord = char.IsDigit(nmark[0]);
                    char nmc = nord ? nmark[nmark.Length - 1] : nmark[0];
                    if (nord == list.Ordered && nmc == bulletChar && nm.Groups[1].Value.Length <= baseIndent + 3) i = nb;
                }
            }
            consumed = Math.Max(1, i - start);
            return list;
        }

        private static List<string> SplitTableRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            List<string> cells = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inCode = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode) { cells.Add(cur.ToString()); cur.Length = 0; continue; }
                cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        // ------------------------------------------------------------------------------------------------
        // Inlines
        // ------------------------------------------------------------------------------------------------

        public static List<MdInline> ParseInlines(string text, Dictionary<string, string[]> refs)
        {
            List<MdInline> result = new List<MdInline>();
            if (string.IsNullOrEmpty(text)) return result;
            StringBuilder buf = new StringBuilder();
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                // Backslash escapes and hard breaks
                if (c == '\\' && i + 1 < n)
                {
                    char nx = text[i + 1];
                    if (nx == '\n') { Flush(buf, result); result.Add(new MdInline(MdInlineKind.LineBreak, "")); i += 2; continue; }
                    if (IsAsciiPunct(nx)) { buf.Append(nx); i += 2; continue; }
                }
                if (c == '\n')
                {
                    // Two trailing spaces = hard break; otherwise soft break (space).
                    int sp = buf.Length;
                    int trailing = 0;
                    while (sp > 0 && buf[sp - 1] == ' ') { sp--; trailing++; }
                    if (trailing >= 2) { buf.Length = sp; Flush(buf, result); result.Add(new MdInline(MdInlineKind.LineBreak, "")); }
                    else { buf.Length = sp; buf.Append(' '); }
                    i++;
                    continue;
                }
                if (c == '`')
                {
                    int run = CountRun(text, i, '`');
                    int close = FindBacktickClose(text, i + run, run);
                    if (close >= 0)
                    {
                        string code = text.Substring(i + run, close - (i + run)).Replace('\n', ' ');
                        if (code.Length >= 2 && code[0] == ' ' && code[code.Length - 1] == ' ' && code.Trim().Length > 0) code = code.Substring(1, code.Length - 2);
                        Flush(buf, result);
                        result.Add(new MdInline(MdInlineKind.Code, code));
                        i = close + run;
                        continue;
                    }
                    buf.Append(text, i, run);
                    i += run;
                    continue;
                }
                if (c == '!' && i + 1 < n && text[i + 1] == '[')
                {
                    int end;
                    MdInline img = TryParseLink(text, i + 1, refs, true, out end);
                    if (img != null) { Flush(buf, result); result.Add(img); i = end; continue; }
                }
                if (c == '[')
                {
                    int end;
                    MdInline link = TryParseLink(text, i, refs, false, out end);
                    if (link != null) { Flush(buf, result); result.Add(link); i = end; continue; }
                }
                if (c == '<')
                {
                    int end;
                    MdInline html = TryParseAngle(text, i, refs, out end);
                    if (end > i)
                    {
                        Flush(buf, result);
                        if (html != null) result.Add(html);
                        i = end;
                        continue;
                    }
                }
                if (c == '~' && i + 1 < n && text[i + 1] == '~')
                {
                    int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (close > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                    {
                        Flush(buf, result);
                        MdInline st = new MdInline(MdInlineKind.Strikethrough, "");
                        st.Children = ParseInlines(text.Substring(i + 2, close - i - 2), refs);
                        result.Add(st);
                        i = close + 2;
                        continue;
                    }
                }
                if (c == '*' || c == '_')
                {
                    int end;
                    string lead;
                    MdInline emph = TryParseEmphasis(text, i, refs, out end, out lead);
                    if (emph != null) { buf.Append(lead); Flush(buf, result); result.Add(emph); i = end; continue; }
                    int run = CountRun(text, i, c);
                    buf.Append(text, i, run);
                    i += run;
                    continue;
                }
                if (c == '&')
                {
                    int semi = text.IndexOf(';', i);
                    if (semi > i && semi - i <= 10)
                    {
                        string ent = DecodeEntity(text.Substring(i, semi - i + 1));
                        if (ent != null) { buf.Append(ent); i = semi + 1; continue; }
                    }
                }
                // Bare URL autolink (GFM)
                if ((c == 'h' || c == 'w') && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
                {
                    string rest = text.Substring(i, Math.Min(n - i, 8)).ToLowerInvariant();
                    if (rest.StartsWith("https://") || rest.StartsWith("http://") || rest.StartsWith("www."))
                    {
                        int j = i;
                        while (j < n && !char.IsWhiteSpace(text[j]) && text[j] != '<') j++;
                        while (j > i && ".,:;!?)'\"*_".IndexOf(text[j - 1]) >= 0)
                        {
                            if (text[j - 1] == ')' && Count(text, i, j, '(') >= Count(text, i, j, ')')) break;
                            j--;
                        }
                        string url = text.Substring(i, j - i);
                        if (url.Length > 8)
                        {
                            Flush(buf, result);
                            MdInline a = new MdInline(MdInlineKind.Link, "");
                            a.Url = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
                            a.Children.Add(new MdInline(MdInlineKind.Text, url));
                            result.Add(a);
                            i = j;
                            continue;
                        }
                    }
                }
                buf.Append(c);
                i++;
            }
            Flush(buf, result);
            return result;
        }

        private static int Count(string s, int from, int to, char c)
        {
            int k = 0;
            for (int i = from; i < to; i++) if (s[i] == c) k++;
            return k;
        }

        private static void Flush(StringBuilder buf, List<MdInline> result)
        {
            if (buf.Length == 0) return;
            if (result.Count > 0 && result[result.Count - 1].Kind == MdInlineKind.Text) result[result.Count - 1].Text += buf.ToString();
            else result.Add(new MdInline(MdInlineKind.Text, buf.ToString()));
            buf.Length = 0;
        }

        private static bool IsAsciiPunct(char c)
        {
            return "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".IndexOf(c) >= 0;
        }

        private static int CountRun(string s, int i, char c)
        {
            int k = 0;
            while (i + k < s.Length && s[i + k] == c) k++;
            return k;
        }

        private static int FindBacktickClose(string s, int from, int run)
        {
            int i = from;
            while (i < s.Length)
            {
                int idx = s.IndexOf('`', i);
                if (idx < 0) return -1;
                int r = CountRun(s, idx, '`');
                if (r == run) return idx;
                i = idx + r;
            }
            return -1;
        }

        private static MdInline TryParseEmphasis(string text, int i, Dictionary<string, string[]> refs, out int end, out string leadingLiteral)
        {
            end = i;
            leadingLiteral = "";
            char c = text[i];
            int run = CountRun(text, i, c);
            int n = text.Length;
            if (i + run >= n || char.IsWhiteSpace(text[i + run])) return null;
            if (c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1])) return null; // intraword underscore
            int j = i + run;
            while (j < n)
            {
                if (text[j] == '`')
                {
                    int r = CountRun(text, j, '`');
                    int close = FindBacktickClose(text, j + r, r);
                    j = close >= 0 ? close + r : j + r;
                    continue;
                }
                if (text[j] == '\\') { j += 2; continue; }
                if (text[j] == c)
                {
                    int cr = CountRun(text, j, c);
                    bool rightFlank = !char.IsWhiteSpace(text[j - 1]);
                    bool okUnderscore = c != '_' || j + cr >= n || !char.IsLetterOrDigit(text[j + cr]);
                    if (rightFlank && okUnderscore)
                    {
                        int len = Math.Min(3, Math.Min(run, cr));
                        string inner = text.Substring(i + run, j - (i + run));
                        if (inner.Length == 0) { j += cr; continue; }
                        List<MdInline> children = ParseInlines(inner, refs);
                        MdInline node;
                        if (len >= 3)
                        {
                            MdInline em = new MdInline(MdInlineKind.Emphasis, "");
                            em.Children = children;
                            node = new MdInline(MdInlineKind.Strong, "");
                            node.Children.Add(em);
                        }
                        else
                        {
                            node = new MdInline(len == 2 ? MdInlineKind.Strong : MdInlineKind.Emphasis, "");
                            node.Children = children;
                        }
                        // Unmatched opener characters stay literal in front; extra closers are re-scanned by the caller.
                        leadingLiteral = new string(c, run - len);
                        end = j + len;
                        return node;
                    }
                    j += cr;
                    continue;
                }
                j++;
            }
            return null;
        }

        private static MdInline TryParseLink(string text, int i, Dictionary<string, string[]> refs, bool isImage, out int end)
        {
            end = i;
            int n = text.Length;
            // Find matching ']' with nesting.
            int depth = 0;
            int j = i;
            for (; j < n; j++)
            {
                char c = text[j];
                if (c == '\\') { j++; continue; }
                if (c == '`')
                {
                    int r = CountRun(text, j, '`');
                    int close = FindBacktickClose(text, j + r, r);
                    if (close >= 0) { j = close + r - 1; continue; }
                }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) break; }
            }
            if (j >= n) return null;
            string label = text.Substring(i + 1, j - i - 1);
            int after = j + 1;
            string url = null, title = "";
            if (after < n && text[after] == '(')
            {
                int k = after + 1;
                while (k < n && (text[k] == ' ' || text[k] == '\n')) k++;
                int urlStart = k;
                int parenDepth = 0;
                if (k < n && text[k] == '<')
                {
                    int gt = text.IndexOf('>', k);
                    if (gt < 0) return null;
                    url = text.Substring(k + 1, gt - k - 1);
                    k = gt + 1;
                }
                else
                {
                    while (k < n)
                    {
                        char c = text[k];
                        if (c == '\\' && k + 1 < n) { k += 2; continue; }
                        if (c == '(') parenDepth++;
                        else if (c == ')') { if (parenDepth == 0) break; parenDepth--; }
                        else if (c == ' ' || c == '\n') break;
                        k++;
                    }
                    url = text.Substring(urlStart, k - urlStart);
                }
                while (k < n && (text[k] == ' ' || text[k] == '\n')) k++;
                if (k < n && (text[k] == '"' || text[k] == '\'' || text[k] == '('))
                {
                    char q = text[k] == '(' ? ')' : text[k];
                    int qe = text.IndexOf(q, k + 1);
                    if (qe < 0) return null;
                    title = text.Substring(k + 1, qe - k - 1);
                    k = qe + 1;
                    while (k < n && text[k] == ' ') k++;
                }
                if (k >= n || text[k] != ')') return null;
                end = k + 1;
            }
            else
            {
                // Reference link: [text][ref], [text][], or [text]
                string refName = label;
                int e2 = after;
                if (after < n && text[after] == '[')
                {
                    int rb = text.IndexOf(']', after);
                    if (rb > after)
                    {
                        string r = text.Substring(after + 1, rb - after - 1);
                        if (r.Length > 0) refName = r;
                        e2 = rb + 1;
                    }
                }
                string[] def;
                if (refs == null || !refs.TryGetValue(refName.Trim(), out def)) return null;
                url = def[0];
                title = def[1];
                end = e2;
            }
            MdInline node = new MdInline(isImage ? MdInlineKind.Image : MdInlineKind.Link, "");
            node.Url = UnescapeUrl(url);
            node.Title = title;
            if (isImage)
            {
                node.Text = ToPlainText(ParseInlines(label, refs));
                end = Math.Max(end, i + 1);
            }
            else
            {
                node.Children = ParseInlines(label, refs);
            }
            return node;
        }

        private static string UnescapeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < url.Length; i++)
            {
                if (url[i] == '\\' && i + 1 < url.Length && IsAsciiPunct(url[i + 1])) { sb.Append(url[i + 1]); i++; }
                else sb.Append(url[i]);
            }
            return sb.ToString();
        }

        private static readonly Regex AttrRegex = new Regex(@"([a-zA-Z_:][\w:.\-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);

        /// <summary>Autolinks (&lt;https://x&gt;) and a small inline-HTML subset: img, a, br, b/strong, i/em, code, kbd; other tags are dropped.</summary>
        private static MdInline TryParseAngle(string text, int i, Dictionary<string, string[]> refs, out int end)
        {
            end = i;
            int gt = text.IndexOf('>', i);
            if (gt < 0) return null;
            string inner = text.Substring(i + 1, gt - i - 1);
            if (inner.StartsWith("!--"))
            {
                int ce = text.IndexOf("-->", i, StringComparison.Ordinal);
                end = ce < 0 ? text.Length : ce + 3;
                return null;
            }
            if (Regex.IsMatch(inner, @"^[a-zA-Z][a-zA-Z0-9+.\-]{1,31}:[^\s<>]*$"))
            {
                MdInline a = new MdInline(MdInlineKind.Link, "");
                a.Url = inner;
                a.Children.Add(new MdInline(MdInlineKind.Text, inner));
                end = gt + 1;
                return a;
            }
            if (Regex.IsMatch(inner, @"^[^\s@<>]+@[^\s@<>]+\.[a-zA-Z]{2,}$"))
            {
                MdInline a = new MdInline(MdInlineKind.Link, "");
                a.Url = "mailto:" + inner;
                a.Children.Add(new MdInline(MdInlineKind.Text, inner));
                end = gt + 1;
                return a;
            }
            Match tm = Regex.Match(inner, @"^(/?)([a-zA-Z][a-zA-Z0-9]*)\b(.*?)(/?)$", RegexOptions.Singleline);
            if (!tm.Success) return null;
            bool closing = tm.Groups[1].Value == "/";
            string tag = tm.Groups[2].Value.ToLowerInvariant();
            string attrs = tm.Groups[3].Value;
            if (tag == "br" && !closing) { end = gt + 1; return new MdInline(MdInlineKind.LineBreak, ""); }
            if (tag == "img" && !closing)
            {
                MdInline img = new MdInline(MdInlineKind.Image, "");
                foreach (Match am in AttrRegex.Matches(attrs))
                {
                    string name = am.Groups[1].Value.ToLowerInvariant();
                    string val = am.Groups[2].Success ? am.Groups[2].Value : (am.Groups[3].Success ? am.Groups[3].Value : am.Groups[4].Value);
                    if (name == "src") img.Url = val;
                    else if (name == "alt") img.Text = val;
                    else if (name == "title") img.Title = val;
                }
                end = gt + 1;
                return string.IsNullOrEmpty(img.Url) ? null : img;
            }
            if (!closing && (tag == "a" || tag == "b" || tag == "strong" || tag == "i" || tag == "em" || tag == "code" || tag == "kbd" || tag == "s" || tag == "del"))
            {
                string closeTag = "</" + tag + ">";
                int ce = text.IndexOf(closeTag, gt + 1, StringComparison.OrdinalIgnoreCase);
                if (ce > gt)
                {
                    string content = text.Substring(gt + 1, ce - gt - 1);
                    MdInline node;
                    if (tag == "a")
                    {
                        node = new MdInline(MdInlineKind.Link, "");
                        Match hm = Regex.Match(attrs, @"href\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
                        node.Url = hm.Success ? (hm.Groups[1].Success ? hm.Groups[1].Value : hm.Groups[2].Value) : "";
                        node.Children = ParseInlines(content, refs);
                        if (node.Url.Length == 0) node.Kind = MdInlineKind.Emphasis; // <a name="x"> anchors: just text
                    }
                    else if (tag == "code" || tag == "kbd") node = new MdInline(MdInlineKind.Code, DecodeEntities(content));
                    else
                    {
                        MdInlineKind k = (tag == "b" || tag == "strong") ? MdInlineKind.Strong : ((tag == "s" || tag == "del") ? MdInlineKind.Strikethrough : MdInlineKind.Emphasis);
                        node = new MdInline(k, "");
                        node.Children = ParseInlines(content, refs);
                    }
                    end = ce + closeTag.Length;
                    return node;
                }
            }
            // Unknown / structural tag (<p align=center>, </div>, ...): drop it, keep its contents.
            end = gt + 1;
            return null;
        }

        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? "";
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '&')
                {
                    int semi = s.IndexOf(';', i);
                    if (semi > i && semi - i <= 10)
                    {
                        string d = DecodeEntity(s.Substring(i, semi - i + 1));
                        if (d != null) { sb.Append(d); i = semi + 1; continue; }
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        private static string DecodeEntity(string ent)
        {
            switch (ent)
            {
                case "&amp;": return "&";
                case "&lt;": return "<";
                case "&gt;": return ">";
                case "&quot;": return "\"";
                case "&apos;":
                case "&#39;": return "'";
                case "&nbsp;": return " ";
                case "&copy;": return "©";
                case "&reg;": return "®";
                case "&trade;": return "™";
                case "&mdash;": return "—";
                case "&ndash;": return "–";
                case "&hellip;": return "…";
                case "&rarr;": return "→";
                case "&larr;": return "←";
            }
            if (ent.StartsWith("&#") && ent.Length > 3)
            {
                string num = ent.Substring(2, ent.Length - 3);
                int cp;
                bool ok = num.StartsWith("x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(num.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp)
                    : int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out cp);
                if (ok && cp > 0 && cp <= 0x10FFFF) { try { return char.ConvertFromUtf32(cp); } catch { } }
            }
            return null;
        }
    }

    /// <summary>Where a link in the Markdown preview points.</summary>
    public class MdLinkTarget
    {
        public const string KindExternal = "external";
        public const string KindFile = "file";
        public const string KindAnchor = "anchor";
        public const string KindUnsupported = "unsupported";

        public string Kind = KindUnsupported;
        public string Url = "";
        public string FilePath = "";
        public string Anchor = "";
    }

    public static class MarkdownLinkResolver
    {
        public static MdLinkTarget Resolve(string url, string markdownFilePath)
        {
            MdLinkTarget t = new MdLinkTarget();
            t.Url = url ?? "";
            string u = (url ?? "").Trim();
            if (u.Length == 0) return t;
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                t.Kind = MdLinkTarget.KindExternal;
                return t;
            }
            if (u.StartsWith("#"))
            {
                t.Kind = MdLinkTarget.KindAnchor;
                t.Anchor = Uri.UnescapeDataString(u.Substring(1)).ToLowerInvariant();
                return t;
            }
            if (Regex.IsMatch(u, @"^[a-zA-Z][a-zA-Z0-9+.\-]+:") && !Regex.IsMatch(u, @"^[a-zA-Z]:[\\/]")) return t; // mailto:, ftp:, ...
            string pathPart = u;
            int hash = pathPart.IndexOf('#');
            if (hash >= 0) { t.Anchor = pathPart.Substring(hash + 1).ToLowerInvariant(); pathPart = pathPart.Substring(0, hash); }
            int q = pathPart.IndexOf('?');
            if (q >= 0) pathPart = pathPart.Substring(0, q);
            try { pathPart = Uri.UnescapeDataString(pathPart); } catch { }
            try
            {
                string baseDir = string.IsNullOrEmpty(markdownFilePath) ? "" : Path.GetDirectoryName(Path.GetFullPath(markdownFilePath));
                string full = Path.IsPathRooted(pathPart) ? pathPart : Path.GetFullPath(Path.Combine(baseDir, pathPart.Replace('/', Path.DirectorySeparatorChar)));
                t.FilePath = full;
                t.Kind = MdLinkTarget.KindFile;
            }
            catch { }
            return t;
        }

        /// <summary>Local path of an image, or null when it is remote / unsupported.</summary>
        public static string ResolveLocalImage(string src, string markdownFilePath)
        {
            if (string.IsNullOrEmpty(src)) return null;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            MdLinkTarget t = Resolve(src, markdownFilePath);
            return t.Kind == MdLinkTarget.KindFile ? t.FilePath : null;
        }

        public static bool IsRemoteImage(string src)
        {
            return !string.IsNullOrEmpty(src) && (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsMarkdownFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".md" || ext == ".markdown" || ext == ".mdown" || ext == ".mkd" || ext == ".mdx";
        }
    }
}
