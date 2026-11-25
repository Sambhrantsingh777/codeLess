using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace codeLess.Studio
{
    public enum FoldRangeKind { Code, Region, Comment, Imports, Markup, Heading }

    /// <summary>A foldable range: folding hides lines (Start, End], the header line Start stays visible.</summary>
    public struct FoldRange
    {
        public int Start;
        public int End;
        public FoldRangeKind Kind;

        public FoldRange(int start, int end, FoldRangeKind kind)
        {
            Start = start;
            End = end;
            Kind = kind;
        }
    }

    /// <summary>
    /// Folding ranges of a document, shared by both editors (QA #13 / #16). Computed per language like VS Code:
    /// brace languages from the string/comment-aware bracket lexer (with Allman headers: "void F()" folds the "{"
    /// block below it), indentation for Python / YAML / plain text, tags for HTML / XML, headings and code fences for
    /// Markdown, plus region markers (#region, // #region, #pragma region, &lt;!-- #region --&gt;), multi-line comments
    /// and import / using groups. The closing line of a block stays visible ("function f() {⋯" / "}"), as in VS Code.
    /// Results are cached per buffer edit version; the bracket analysis is incremental. STRICT C# 5.
    /// </summary>
    public sealed class EditorFoldingModel
    {
        private static readonly ConditionalWeakTable<EditorTabBuffer, EditorFoldingModel> table = new ConditionalWeakTable<EditorTabBuffer, EditorFoldingModel>();

        public static EditorFoldingModel For(EditorTabBuffer buf)
        {
            if (buf == null) return null;
            return table.GetValue(buf, delegate (EditorTabBuffer b) { return new EditorFoldingModel(); });
        }

        /// <summary>Brings the buffer's model up to date (unbounded) and returns the end line of the range starting at <paramref name="line"/> (-1: none).</summary>
        public static int GetFoldEnd(EditorTabBuffer buf, int line, int tabSize)
        {
            EditorFoldingModel m = For(buf);
            if (m == null) return -1;
            m.Update(buf, tabSize, 0);
            return m.EndOf(line);
        }

        private int version = -1;
        private string path;
        private int tab = -1;
        private bool complete;
        private List<FoldRange> ranges = new List<FoldRange>();
        private Dictionary<int, int> endByStart = new Dictionary<int, int>();
        private long computations;

        public IList<FoldRange> Ranges { get { return ranges; } }
        /// <summary>Full recomputations so far (tests: proves the per-version cache).</summary>
        public long ComputationCount { get { return computations; } }
        public bool IsComplete { get { return complete; } }

        public bool IsUpToDate(EditorTabBuffer buf, int tabSize)
        {
            return buf != null && complete && version == buf.EditVersion && tab == tabSize &&
                   string.Equals(path, EditorSyntaxSession.EffectiveSyntaxPathFor(buf), StringComparison.Ordinal);
        }

        /// <summary>End line of the foldable range whose header is <paramref name="line"/>, or -1.</summary>
        public int EndOf(int line)
        {
            int e;
            return endByStart.TryGetValue(line, out e) ? e : -1;
        }

        public bool IsFoldable(int line) { return endByStart.ContainsKey(line); }

        /// <summary>
        /// Recomputes when the buffer changed. <paramref name="budgetMs"/> bounds the (incremental) bracket analysis;
        /// returns false when it ran out of time (the previous ranges, shifted by the edits, stay in use).
        /// </summary>
        public bool Update(EditorTabBuffer buf, int tabSize, int budgetMs)
        {
            if (buf == null || buf.Lines == null) return true;
            tabSize = Math.Max(1, tabSize);
            if (IsUpToDate(buf, tabSize)) return true;
            string p = EditorSyntaxSession.EffectiveSyntaxPathFor(buf);
            List<FoldRange> res = new List<FoldRange>();
            bool ok = Compute(buf, p, tabSize, budgetMs, res);
            if (!ok) { complete = false; return false; }
            Normalize(res);
            ranges = res;
            endByStart = new Dictionary<int, int>(res.Count);
            for (int i = 0; i < res.Count; i++) endByStart[res[i].Start] = res[i].End;
            version = buf.EditVersion;
            path = p;
            tab = tabSize;
            complete = true;
            computations++;
            EditorFoldState.Validate(buf, this);
            return true;
        }

        /// <summary>Keeps stale ranges aligned with an edit until the next recomputation (large documents).</summary>
        internal void ShiftForEdit(int first, int removed, int inserted)
        {
            if (ranges.Count == 0) return;
            int delta = inserted - removed;
            List<FoldRange> res = new List<FoldRange>(ranges.Count);
            foreach (FoldRange r in ranges)
            {
                if (r.End < first) { res.Add(r); continue; }
                if (r.Start >= first + removed) { res.Add(new FoldRange(r.Start + delta, r.End + delta, r.Kind)); continue; }
                if (r.Start < first)
                {
                    int e = r.End >= first + removed ? r.End + delta : Math.Max(r.Start, first + inserted - 1);
                    if (e > r.Start) res.Add(new FoldRange(r.Start, e, r.Kind));
                }
            }
            ranges = res;
            endByStart = new Dictionary<int, int>(res.Count);
            for (int i = 0; i < res.Count; i++) endByStart[res[i].Start] = res[i].End;
        }

        // ------------------------------------------------------------------ computation

        internal enum Strategy { Brace, Indent, Markup, Markdown }

        internal static Strategy StrategyFor(string filePath, BracketLexProfile profile)
        {
            string ext = "", name = "";
            try
            {
                ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant();
                name = (Path.GetFileName(filePath ?? "") ?? "").ToLowerInvariant();
            }
            catch { }
            switch (ext)
            {
                case ".html": case ".htm": case ".xhtml": case ".xml": case ".xaml": case ".axaml": case ".svg": case ".csproj":
                case ".vbproj": case ".fsproj": case ".props": case ".targets": case ".config": case ".plist": case ".resx":
                case ".xsd": case ".xsl": case ".xslt": case ".nuspec": case ".manifest": case ".vue": case ".svelte": case ".razor": case ".cshtml":
                    return Strategy.Markup;
                case ".md": case ".markdown": case ".mdx": case ".mkd":
                    return Strategy.Markdown;
                case ".py": case ".pyw": case ".pyi": case ".yaml": case ".yml": case ".coffee": case ".rb": case ".rake": case ".gemspec":
                case ".lua": case ".sh": case ".bash": case ".zsh": case ".ksh": case ".fish": case ".toml": case ".ini": case ".cfg":
                case ".conf": case ".properties": case ".env": case ".sql": case ".txt": case ".log": case ".csv": case ".tsv":
                case ".pug": case ".jade": case ".nim": case ".fs": case ".fsx": case ".fsi": case ".ex": case ".exs": case ".elm":
                case ".hs": case ".clj": case ".cljs": case ".r": case ".jl": case ".gitignore": case ".dockerignore": case ".mk": case "":
                    return Strategy.Indent;
            }
            if (name == "makefile" || name == "gnumakefile" || name == "dockerfile") return Strategy.Indent;
            if (profile == null || !profile.Enabled || profile.IndentBased) return Strategy.Indent;
            return Strategy.Brace;
        }

        private bool Compute(EditorTabBuffer buf, string p, int tabSize, int budgetMs, List<FoldRange> res)
        {
            List<string> lines = buf.Lines;
            BracketLexProfile profile = BracketLexProfile.ForFile(p);
            Strategy st = StrategyFor(p, profile);
            ComputeRegionMarkers(lines, res);
            switch (st)
            {
                case Strategy.Brace:
                    {
                        EditorSyntaxSession session = EditorSyntaxSession.For(buf);
                        session.Prepare(buf);
                        long deadline = budgetMs > 0 ? EditorSyntaxSession.DeadlineFromNow(budgetMs) : 0;
                        if (!ComputeBraceRanges(lines, session.Brackets, deadline, res)) return false;
                        ComputeLineCommentGroups(lines, session.Brackets.Profile, res);
                        ComputeImportGroups(lines, p, res);
                        break;
                    }
                case Strategy.Markup:
                    ComputeMarkupRanges(lines, p, res);
                    break;
                case Strategy.Markdown:
                    ComputeMarkdownRanges(lines, res);
                    break;
                default:
                    ComputeIndentRanges(lines, tabSize, profile, res);
                    if (profile.LineComment != null) ComputeLineCommentGroups(lines, profile, res);
                    break;
            }
            return true;
        }

        /// <summary>One range per header line (the outermost wins), sorted by start; drops empty ranges.</summary>
        private static void Normalize(List<FoldRange> res)
        {
            res.RemoveAll(delegate (FoldRange r) { return r.End <= r.Start || r.Start < 0; });
            res.Sort(delegate (FoldRange a, FoldRange b)
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : b.End.CompareTo(a.End);
            });
            int w = 0;
            for (int i = 0; i < res.Count; i++)
            {
                if (w > 0 && res[w - 1].Start == res[i].Start) continue;
                res[w++] = res[i];
            }
            if (w < res.Count) res.RemoveRange(w, res.Count - w);
        }

        /// <summary>Pure computation for a list of lines (tests / tools). Brace languages use a private bracket analysis.</summary>
        public static List<FoldRange> ComputeFor(IList<string> lines, string filePath, int tabSize)
        {
            List<FoldRange> res = new List<FoldRange>();
            if (lines == null) return res;
            BracketLexProfile profile = BracketLexProfile.ForFile(filePath);
            Strategy st = StrategyFor(filePath, profile);
            ComputeRegionMarkers(lines, res);
            if (st == Strategy.Brace)
            {
                EditorBracketCache c = new EditorBracketCache(profile);
                c.Sync(lines);
                ComputeBraceRanges(lines, c, 0, res);
                ComputeLineCommentGroups(lines, profile, res);
                ComputeImportGroups(lines, filePath, res);
            }
            else if (st == Strategy.Markup) ComputeMarkupRanges(lines, filePath, res);
            else if (st == Strategy.Markdown) ComputeMarkdownRanges(lines, res);
            else
            {
                ComputeIndentRanges(lines, Math.Max(1, tabSize), profile, res);
                if (profile.LineComment != null) ComputeLineCommentGroups(lines, profile, res);
            }
            Normalize(res);
            return res;
        }

        private static int FirstNonWs(string s)
        {
            if (s == null) return -1;
            for (int i = 0; i < s.Length; i++) if (s[i] != ' ' && s[i] != '\t') return i;
            return -1;
        }

        private static bool StartsWithAt(string s, int i, string tok)
        {
            return i >= 0 && i + tok.Length <= s.Length && string.CompareOrdinal(s, i, tok, 0, tok.Length) == 0;
        }

        // ---- brackets ------------------------------------------------------------------------------------------
        private static bool ComputeBraceRanges(IList<string> lines, EditorBracketCache cache, long deadline, List<FoldRange> res)
        {
            int n = lines.Count;
            if (n == 0) return true;
            if (!cache.EnsureValid(lines, n - 1, deadline)) return false;
            int commentStart = -1;
            for (int l = 0; l < n; l++)
            {
                BracketLexState s = cache.GetStartState(l), e = cache.GetEndState(l);
                if (s == null || e == null) break;
                bool startsInComment = s.Mode == BracketLexState.ModeBlockComment;
                bool endsInComment = e.Mode == BracketLexState.ModeBlockComment;
                if (!startsInComment && endsInComment) commentStart = l;
                else if (startsInComment && !endsInComment && commentStart >= 0)
                {
                    if (l > commentStart) res.Add(new FoldRange(commentStart, l, FoldRangeKind.Comment));
                    commentStart = -1;
                }
                if (ReferenceEquals(s.Stack, e.Stack)) continue;
                BracketFrame common = e.Stack;
                while (common != null && common.Line >= l) common = common.Parent;
                for (BracketFrame f = s.Stack; f != null && !ReferenceEquals(f, common); f = f.Parent)
                {
                    if ((f.Open != '{' && f.Open != '[') || f.Line >= l) continue;
                    AddBracketRange(lines, f.Line, f.Col, l, res);
                }
            }
            return true;
        }

        private static void AddBracketRange(IList<string> lines, int openLine, int openCol, int closeLine, List<FoldRange> res)
        {
            string closeText = lines[closeLine] ?? "";
            int closeFirst = FirstNonWs(closeText);
            // VS Code keeps the closing line visible when it starts with the closing bracket ("}" / "} else {").
            int end = (closeFirst >= 0 && (closeText[closeFirst] == '}' || closeText[closeFirst] == ']')) ? closeLine - 1 : closeLine;
            int start = openLine;
            string openText = lines[openLine] ?? "";
            if (FirstNonWs(openText) == openCol)
            {
                // Allman: "{" alone on its line belongs to the header above it ("void F()" / "{").
                int h = openLine - 1;
                while (h >= 0 && FirstNonWs(lines[h]) < 0) h--;
                if (h >= 0 && IsAllmanHeader(lines[h], openText, openCol)) start = h;
            }
            if (end > start) res.Add(new FoldRange(start, end, FoldRangeKind.Code));
        }

        private static bool IsAllmanHeader(string header, string braceLine, int braceCol)
        {
            int hf = FirstNonWs(header);
            if (hf < 0) return false;
            string t = header.TrimEnd();
            char last = t[t.Length - 1];
            if (last == '{' || last == '}' || last == ';' || last == ',' || last == '[' || last == '(' || last == ']') return false;
            if (StartsWithAt(header, hf, "//") || StartsWithAt(header, hf, "/*") || StartsWithAt(header, hf, "*") || header[hf] == '#') return false;
            // the header is not indented deeper than its brace
            return EditorTextLayout.VisualColumn(header, hf, 4) <= EditorTextLayout.VisualColumn(braceLine, braceCol, 4);
        }

        // ---- region markers ------------------------------------------------------------------------------------
        /// <summary>1 = region start, -1 = region end, 0 = neither ("#region", "// #region", "//region", "#pragma region", "/* #region */", "&lt;!-- #region --&gt;", "-- #region").</summary>
        public static int RegionMarkerKind(string line)
        {
            int i = FirstNonWs(line);
            if (i < 0) return 0;
            if (StartsWithAt(line, i, "//")) i += 2;
            else if (StartsWithAt(line, i, "/*")) i += 2;
            else if (StartsWithAt(line, i, "<!--")) i += 4;
            else if (StartsWithAt(line, i, "--")) i += 2;
            else if (line[i] == ';' || line[i] == '\'') i += 1;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            bool hash = i < line.Length && line[i] == '#';
            if (hash) i++;
            if (StartsWithAt(line, i, "pragma"))
            {
                i += 6;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            }
            int kind = 0;
            if (StartsWithAt(line, i, "region")) { kind = 1; i += 6; }
            else if (StartsWithAt(line, i, "endregion")) { kind = -1; i += 9; }
            else if (StartsWithAt(line, i, "Region")) { kind = 1; i += 6; }
            else if (StartsWithAt(line, i, "EndRegion")) { kind = -1; i += 9; }
            else if (StartsWithAt(line, i, "<editor-fold")) return 1;
            else if (StartsWithAt(line, i, "</editor-fold")) return -1;
            if (kind == 0) return 0;
            if (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) return 0;
            return kind;
        }

        private static void ComputeRegionMarkers(IList<string> lines, List<FoldRange> res)
        {
            List<int> stack = null;
            for (int l = 0; l < lines.Count; l++)
            {
                string s = lines[l];
                if (string.IsNullOrEmpty(s) || s.Length > 2000) continue;
                int k = RegionMarkerKind(s);
                if (k == 1) { if (stack == null) stack = new List<int>(); stack.Add(l); }
                else if (k == -1 && stack != null && stack.Count > 0)
                {
                    int start = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (l > start) res.Add(new FoldRange(start, l, FoldRangeKind.Region));
                }
            }
        }

        // ---- comment / import groups ---------------------------------------------------------------------------
        private static void ComputeLineCommentGroups(IList<string> lines, BracketLexProfile p, List<FoldRange> res)
        {
            string tok = p != null ? p.LineComment : null;
            if (string.IsNullOrEmpty(tok)) return;
            int start = -1, last = -1;
            for (int l = 0; l <= lines.Count; l++)
            {
                bool isComment = false;
                if (l < lines.Count)
                {
                    string s = lines[l] ?? "";
                    int f = FirstNonWs(s);
                    isComment = f >= 0 && StartsWithAt(s, f, tok) && RegionMarkerKind(s) == 0 && !(tok == "#" && StartsWithAt(s, f, "#!"));
                }
                if (isComment)
                {
                    if (start < 0) start = l;
                    last = l;
                    continue;
                }
                if (start >= 0 && last > start) res.Add(new FoldRange(start, last, FoldRangeKind.Comment));
                start = -1;
            }
        }

        private static void ComputeImportGroups(IList<string> lines, string filePath, List<FoldRange> res)
        {
            string ext = "";
            try { ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant(); } catch { }
            string kw1, kw2 = null;
            switch (ext)
            {
                case ".cs": case ".csx": kw1 = "using "; break;
                case ".java": case ".kt": case ".kts": case ".scala": case ".go": case ".dart": case ".swift": kw1 = "import "; break;
                case ".js": case ".jsx": case ".mjs": case ".cjs": case ".ts": case ".tsx": case ".mts": case ".cts": kw1 = "import "; kw2 = "import{"; break;
                case ".rs": kw1 = "use "; break;
                case ".c": case ".h": case ".cpp": case ".cc": case ".cxx": case ".hpp": case ".hh": case ".m": case ".mm": kw1 = "#include"; kw2 = "#import"; break;
                default: return;
            }
            int start = -1, last = -1;
            for (int l = 0; l <= lines.Count; l++)
            {
                string s = l < lines.Count ? (lines[l] ?? "") : null;
                int f = s == null ? -1 : FirstNonWs(s);
                bool imp = f >= 0 && (StartsWithAt(s, f, kw1) || (kw2 != null && StartsWithAt(s, f, kw2))) &&
                           !(kw1 == "using " && s.IndexOf('(') >= 0);
                if (imp)
                {
                    if (start < 0) start = l;
                    last = l;
                    continue;
                }
                if (s != null && f < 0 && start >= 0) continue; // blank lines inside the group
                if (start >= 0 && last > start) res.Add(new FoldRange(start, last, FoldRangeKind.Imports));
                start = -1;
            }
        }

        // ---- indentation ---------------------------------------------------------------------------------------
        private static void ComputeIndentRanges(IList<string> lines, int tabSize, BracketLexProfile profile, List<FoldRange> res)
        {
            int n = lines.Count;
            EditorBracketCache lexCache = null;
            if (profile != null && profile.Enabled && (profile.TripleQuotes || profile.BlockStart != null))
            {
                lexCache = new EditorBracketCache(profile);
                lexCache.Sync(lines);
                lexCache.EnsureValid(lines, n - 1, 0);
            }
            List<int> stackIndent = new List<int>();
            List<int> stackLine = new List<int>();
            int lastNonBlank = -1;
            for (int l = 0; l < n; l++)
            {
                string s = lines[l] ?? "";
                int f = FirstNonWs(s);
                if (f < 0) continue;
                if (lexCache != null)
                {
                    BracketLexState st = lexCache.GetStartState(l);
                    if (st != null && (st.Mode == BracketLexState.ModeTriple || st.Mode == BracketLexState.ModeBlockComment))
                    {
                        lastNonBlank = l;
                        continue;
                    }
                }
                int ind = EditorTextLayout.VisualColumn(s, f, tabSize);
                while (stackIndent.Count > 0 && stackIndent[stackIndent.Count - 1] >= ind)
                {
                    int start = stackLine[stackLine.Count - 1];
                    stackIndent.RemoveAt(stackIndent.Count - 1);
                    stackLine.RemoveAt(stackLine.Count - 1);
                    if (lastNonBlank > start) res.Add(new FoldRange(start, lastNonBlank, FoldRangeKind.Code));
                }
                stackIndent.Add(ind);
                stackLine.Add(l);
                lastNonBlank = l;
            }
            for (int k = stackLine.Count - 1; k >= 0; k--)
                if (lastNonBlank > stackLine[k]) res.Add(new FoldRange(stackLine[k], lastNonBlank, FoldRangeKind.Code));
        }

        // ---- markup (HTML / XML) -------------------------------------------------------------------------------
        private static readonly HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "!doctype"
        };

        private static void ComputeMarkupRanges(IList<string> lines, string filePath, List<FoldRange> res)
        {
            string ext = "";
            try { ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant(); } catch { }
            bool html = ext == ".html" || ext == ".htm" || ext == ".xhtml" || ext == ".vue" || ext == ".svelte" || ext == ".razor" || ext == ".cshtml";
            List<string> names = new List<string>();
            List<int> openLines = new List<int>();
            int commentStart = -1;
            string rawTextEnd = null; // inside <script> / <style>: skip to the closing tag
            int rawTextLine = -1;
            for (int l = 0; l < lines.Count; l++)
            {
                string s = lines[l] ?? "";
                if (s.Length > 20000) continue;
                int i = 0;
                while (i < s.Length)
                {
                    if (commentStart >= 0)
                    {
                        int endIdx = s.IndexOf("-->", i, StringComparison.Ordinal);
                        if (endIdx < 0) { i = s.Length; break; }
                        if (l > commentStart) res.Add(new FoldRange(commentStart, l, FoldRangeKind.Comment));
                        commentStart = -1;
                        i = endIdx + 3;
                        continue;
                    }
                    if (rawTextEnd != null)
                    {
                        int endIdx = s.IndexOf(rawTextEnd, i, StringComparison.OrdinalIgnoreCase);
                        if (endIdx < 0) { i = s.Length; break; }
                        int fnw = FirstNonWs(s);
                        int end = fnw == endIdx ? l - 1 : l;
                        if (end > rawTextLine) res.Add(new FoldRange(rawTextLine, end, FoldRangeKind.Markup));
                        rawTextEnd = null;
                        i = endIdx + 2;
                        continue;
                    }
                    int lt = s.IndexOf('<', i);
                    if (lt < 0) break;
                    if (StartsWithAt(s, lt, "<!--"))
                    {
                        int endIdx = s.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                        if (endIdx < 0) { commentStart = l; i = s.Length; break; }
                        i = endIdx + 3;
                        continue;
                    }
                    if (StartsWithAt(s, lt, "<![CDATA[") || StartsWithAt(s, lt, "<?") || StartsWithAt(s, lt, "<!"))
                    {
                        int gt0 = s.IndexOf('>', lt + 2);
                        i = gt0 < 0 ? s.Length : gt0 + 1;
                        continue;
                    }
                    bool closing = lt + 1 < s.Length && s[lt + 1] == '/';
                    int ns = lt + (closing ? 2 : 1);
                    int ne = ns;
                    while (ne < s.Length && (char.IsLetterOrDigit(s[ne]) || s[ne] == '-' || s[ne] == '_' || s[ne] == ':' || s[ne] == '.')) ne++;
                    if (ne == ns) { i = lt + 1; continue; }
                    string name = s.Substring(ns, ne - ns);
                    // find the end of the tag (quotes may contain '>'; the tag may span lines: only same-line tags tracked)
                    int k = ne;
                    char q = '\0';
                    while (k < s.Length)
                    {
                        char c = s[k];
                        if (q != '\0') { if (c == q) q = '\0'; }
                        else if (c == '"' || c == '\'') q = c;
                        else if (c == '>') break;
                        k++;
                    }
                    bool selfClosing = k < s.Length && k > 0 && s[k - 1] == '/';
                    if (closing)
                    {
                        for (int d = names.Count - 1; d >= 0; d--)
                        {
                            if (!string.Equals(names[d], name, html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                            int open = openLines[d];
                            int end = FirstNonWs(s) == lt ? l - 1 : l;
                            if (end > open) res.Add(new FoldRange(open, end, FoldRangeKind.Markup));
                            names.RemoveRange(d, names.Count - d);
                            openLines.RemoveRange(d, openLines.Count - d);
                            break;
                        }
                    }
                    else if (!selfClosing && !(html && VoidElements.Contains(name)))
                    {
                        if (html && (string.Equals(name, "script", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "style", StringComparison.OrdinalIgnoreCase)))
                        {
                            rawTextEnd = "</" + name;
                            rawTextLine = l;
                        }
                        else
                        {
                            names.Add(name);
                            openLines.Add(l);
                        }
                    }
                    i = k < s.Length ? k + 1 : s.Length;
                    if (k >= s.Length && !closing && !selfClosing)
                    {
                        // a start tag continued on the next lines: its attributes are part of the element
                        break;
                    }
                }
            }
        }

        // ---- markdown ------------------------------------------------------------------------------------------
        private static void ComputeMarkdownRanges(IList<string> lines, List<FoldRange> res)
        {
            int n = lines.Count;
            List<int> headLines = new List<int>();
            List<int> headLevels = new List<int>();
            string fence = null;
            int fenceStart = -1;
            for (int l = 0; l < n; l++)
            {
                string s = lines[l] ?? "";
                int f = FirstNonWs(s);
                if (f >= 0 && f <= 3 && (StartsWithAt(s, f, "```") || StartsWithAt(s, f, "~~~")))
                {
                    string tok = s.Substring(f, 3);
                    if (fence == null) { fence = tok; fenceStart = l; }
                    else if (tok == fence)
                    {
                        if (l > fenceStart) res.Add(new FoldRange(fenceStart, l, FoldRangeKind.Code));
                        fence = null;
                    }
                    continue;
                }
                if (fence != null || f != 0 || s[0] != '#') continue;
                int level = 0;
                while (level < s.Length && s[level] == '#') level++;
                if (level > 6 || (level < s.Length && s[level] != ' ' && s[level] != '\t')) continue;
                headLines.Add(l);
                headLevels.Add(level);
            }
            for (int h = 0; h < headLines.Count; h++)
            {
                int end = n - 1;
                for (int k = h + 1; k < headLines.Count; k++)
                    if (headLevels[k] <= headLevels[h]) { end = headLines[k] - 1; break; }
                while (end > headLines[h] && FirstNonWs(lines[end]) < 0) end--;
                if (end > headLines[h]) res.Add(new FoldRange(headLines[h], end, FoldRangeKind.Heading));
            }
        }
    }

    /// <summary>
    /// Keeps folded regions attached to their lines while the document is edited (QA #13 "fold markers stable after
    /// edits"): every change of the buffer's lines is located by comparing line references with the previous version,
    /// folded headers / ends below it shift, folds whose header was replaced are dropped. Works on both editors'
    /// fold stores (<see cref="EditorTabBuffer.FoldedBlockRanges"/> and <see cref="EditorTabBuffer.FoldedLines"/>).
    /// </summary>
    public static class EditorFoldState
    {
        private sealed class Tracker
        {
            public List<string> Snapshot;
        }

        private static readonly ConditionalWeakTable<EditorTabBuffer, Tracker> trackers = new ConditionalWeakTable<EditorTabBuffer, Tracker>();

        private static Tracker TrackerFor(EditorTabBuffer buf)
        {
            return trackers.GetValue(buf, delegate (EditorTabBuffer b) { return new Tracker(); });
        }

        internal static void OnLinesReplaced(EditorTabBuffer buf)
        {
            OnLinesChanged(buf);
        }

        internal static void OnLinesChanged(EditorTabBuffer buf)
        {
            if (buf == null || buf.Lines == null) return;
            Tracker t = TrackerFor(buf);
            List<string> cur = buf.Lines;
            List<string> old = t.Snapshot;
            if (old == null)
            {
                t.Snapshot = new List<string>(cur);
                return;
            }
            int n = cur.Count, m = old.Count, min = Math.Min(n, m);
            int p = 0;
            while (p < min && (ReferenceEquals(cur[p], old[p]) || string.Equals(cur[p], old[p], StringComparison.Ordinal))) p++;
            if (p == n && p == m) return;
            int s = 0;
            while (s < min - p && (ReferenceEquals(cur[n - 1 - s], old[m - 1 - s]) || string.Equals(cur[n - 1 - s], old[m - 1 - s], StringComparison.Ordinal))) s++;
            int removed = m - s - p, inserted = n - s - p;
            // splice the snapshot
            int common = Math.Min(removed, inserted);
            for (int k = 0; k < common; k++) old[p + k] = cur[p + k];
            if (removed > common) old.RemoveRange(p + common, removed - common);
            else if (inserted > common) old.InsertRange(p + common, cur.GetRange(p + common, inserted - common));
            ShiftFolds(buf, p, removed, inserted);
            EditorFoldingModel model = EditorFoldingModel.For(buf);
            if (model != null && removed != inserted) model.ShiftForEdit(p, removed, inserted);
        }

        /// <summary>Applies an edit (lines [first, first + removed) replaced by <paramref name="inserted"/> lines) to the fold stores.</summary>
        public static void ShiftFolds(EditorTabBuffer buf, int first, int removed, int inserted)
        {
            int delta = inserted - removed;
            Dictionary<int, int> ranges = buf.FoldedBlockRanges;
            if (ranges != null && ranges.Count > 0)
            {
                List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>(ranges);
                ranges.Clear();
                foreach (KeyValuePair<int, int> kv in list)
                {
                    int h = kv.Key, e = kv.Value;
                    if (h < first)
                    {
                        if (e >= first + removed) e += delta;
                        else if (e >= first) e = Math.Max(h + 1, first + inserted - 1);
                        ranges[h] = e;
                    }
                    else if (h >= first + removed) ranges[h + delta] = e + delta;
                    else if (removed == inserted) ranges[h] = e + delta; // the header line itself was edited in place
                }
            }
            HashSet<int> heads = buf.FoldedLines;
            if (heads != null && heads.Count > 0 && delta != 0)
            {
                List<int> list = new List<int>(heads);
                heads.Clear();
                foreach (int h in list)
                {
                    if (h < first) heads.Add(h);
                    else if (h >= first + removed) heads.Add(h + delta);
                    else if (removed == inserted) heads.Add(h);
                }
            }
        }

        /// <summary>After a recomputation: folds whose range disappeared are expanded, ends follow the new ranges.</summary>
        internal static void Validate(EditorTabBuffer buf, EditorFoldingModel model)
        {
            Dictionary<int, int> ranges = buf.FoldedBlockRanges;
            if (ranges != null && ranges.Count > 0)
            {
                List<int> keys = new List<int>(ranges.Keys);
                foreach (int h in keys)
                {
                    int e = model.EndOf(h);
                    if (e < 0) ranges.Remove(h);
                    else ranges[h] = e;
                }
            }
            HashSet<int> heads = buf.FoldedLines;
            if (heads != null && heads.Count > 0)
                heads.RemoveWhere(delegate (int h) { return model.EndOf(h) < 0; });
        }

        /// <summary>The folded region hiding <paramref name="line"/> (header index), or -1 when the line is visible.</summary>
        public static int HiddenBy(EditorTabBuffer buf, int line)
        {
            if (buf == null || buf.FoldedBlockRanges == null) return -1;
            int best = -1;
            foreach (KeyValuePair<int, int> kv in buf.FoldedBlockRanges)
                if (kv.Key < line && kv.Value >= line && (best < 0 || kv.Key < best)) best = kv.Key;
            return best;
        }
    }

    public partial class EditorTabBuffer
    {
        private readonly Dictionary<int, int> foldedBlockRanges = new Dictionary<int, int>();

        /// <summary>Folded regions: header line -> last hidden line (0-based).</summary>
        public Dictionary<int, int> FoldedBlockRanges { get { return foldedBlockRanges; } }
    }
}
