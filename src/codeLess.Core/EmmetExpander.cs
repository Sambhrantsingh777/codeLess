using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    public enum EmmetSyntax { None, Html, Jsx, Css }

    /// <summary>
    /// Basic Emmet expansion on Tab (VS Code's emmet.triggerExpansionOnTab):
    /// HTML / JSX: elements, #id, .class, [attr=value], {text}, child &gt;, sibling +, climb-up ^, (groups), *N
    /// multiplication with $ numbering ($$ padding, $@- reverse, $@N start), implicit tag names, default attributes,
    /// "!" (HTML5 document). JSX uses className / htmlFor and self-closing void elements.
    /// CSS / SCSS / LESS: property abbreviations with values (m10 → margin: 10px;, m10-20, w100p, lh1.5, c#fff,
    /// pos:a, df, jcsb, ...). Anything that does not look like a deliberate abbreviation is left alone so Tab keeps
    /// inserting indentation. STRICT C# 5.
    /// </summary>
    public static class EmmetExpander
    {
        private const char Stop = '\u0001';

        public static EmmetSyntax SyntaxForFile(string filePath)
        {
            string ext = "";
            try { ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant(); } catch { }
            switch (ext)
            {
                case ".html": case ".htm": case ".xhtml": case ".vue": case ".svelte": case ".php": case ".razor": case ".cshtml": case ".hbs": case ".ejs":
                    return EmmetSyntax.Html;
                case ".jsx": case ".tsx":
                    return EmmetSyntax.Jsx;
                case ".css": case ".scss": case ".less": case ".sass":
                    return EmmetSyntax.Css;
            }
            return EmmetSyntax.None;
        }

        private static readonly HashSet<string> HtmlTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "a","abbr","address","area","article","aside","audio","b","base","bdi","bdo","blockquote","body","br","button","canvas",
            "caption","cite","code","col","colgroup","data","datalist","dd","del","details","dfn","dialog","div","dl","dt","em","embed",
            "fieldset","figcaption","figure","footer","form","h1","h2","h3","h4","h5","h6","head","header","hgroup","hr","html","i",
            "iframe","img","input","ins","kbd","label","legend","li","link","main","map","mark","menu","meta","meter","nav","noscript",
            "object","ol","optgroup","option","output","p","param","picture","pre","progress","q","rp","rt","ruby","s","samp","script",
            "search","section","select","slot","small","source","span","strong","style","sub","summary","sup","table","tbody","td",
            "template","textarea","tfoot","th","thead","time","title","tr","track","u","ul","var","video","wbr","svg","path","circle",
            "rect","line","polyline","polygon","g","defs","use","symbol","text","tspan","main"
        };

        private static readonly HashSet<string> InlineParents = new HashSet<string>(StringComparer.Ordinal)
        {
            "a","abbr","b","bdi","bdo","cite","code","data","dfn","em","i","kbd","label","mark","p","q","s","samp","small","span","strong","sub","sup","time","u","var","button","h1","h2","h3","h4","h5","h6"
        };

        private sealed class Attr
        {
            public string Name;
            public string Value;
            public bool HasValue;
        }

        private sealed class Node
        {
            public string Name = "";
            public string Id;
            public List<string> Classes = new List<string>();
            public List<Attr> Attrs = new List<Attr>();
            public string Text;
            public int Repeat = 1;
            public bool Repeated;
            public bool SelfClose;
            public bool IsGroup;
            public bool ExplicitName;
            public List<Node> Children = new List<Node>();
            public Node Parent;
        }

        // ------------------------------------------------------------------ abbreviation extraction
        private static bool IsAbbrChar(char c)
        {
            return char.IsLetterOrDigit(c) || ".#>+^*$@-_:!()%/".IndexOf(c) >= 0;
        }

        /// <summary>Abbreviation ending at the caret, or null.</summary>
        public static string ExtractAbbreviation(string line, int caretCol, out int start)
        {
            start = -1;
            if (string.IsNullOrEmpty(line) || caretCol <= 0 || caretCol > line.Length) return null;
            int i = caretCol;
            int sq = 0, cu = 0;
            while (i > 0)
            {
                char ch = line[i - 1];
                if (cu > 0) { if (ch == '}') cu++; else if (ch == '{') cu--; i--; continue; }
                if (sq > 0) { if (ch == ']') sq++; else if (ch == '[') sq--; i--; continue; }
                if (ch == '}') { cu = 1; i--; continue; }
                if (ch == ']') { sq = 1; i--; continue; }
                if (IsAbbrChar(ch)) { i--; continue; }
                break;
            }
            if (sq > 0 || cu > 0 || i >= caretCol) return null;
            string ab = line.Substring(i, caretCol - i);
            // balanced parentheses only; trim unmatched leading '(' (e.g. "foo(div")
            while (ab.Length > 0 && ab[0] == '(' && Balance(ab) > 0) { ab = ab.Substring(1); i++; }
            if (ab.Length == 0 || Balance(ab) != 0) return null;
            char f = ab[0];
            if (f == '>' || f == '+' || f == '^' || f == '*' || f == '-' || f == '$' || f == '@' || f == ':' || f == '/' || char.IsDigit(f)) return null;
            start = i;
            return ab;
        }

        private static int Balance(string s)
        {
            int b = 0;
            int sq = 0, cu = 0;
            foreach (char c in s)
            {
                if (c == '[') sq++; else if (c == ']') sq--;
                else if (c == '{') cu++; else if (c == '}') cu--;
                else if (sq == 0 && cu == 0) { if (c == '(') b++; else if (c == ')') b--; }
            }
            return b;
        }

        /// <summary>
        /// Expands the abbreviation before the caret. On success <paramref name="replaceStart"/>..caret is replaced by
        /// <paramref name="expansion"/> (lines after the first already carry <paramref name="baseIndent"/>) and the caret
        /// goes to <paramref name="caretOffset"/> within the expansion.
        /// </summary>
        public static bool TryExpand(string line, int caretCol, EmmetSyntax syntax, string indentUnit, bool insideCssRule,
            out int replaceStart, out string expansion, out int caretOffset)
        {
            replaceStart = -1;
            expansion = null;
            caretOffset = 0;
            if (syntax == EmmetSyntax.None || string.IsNullOrEmpty(line)) return false;
            if (caretCol < line.Length && (char.IsLetterOrDigit(line[caretCol]) || line[caretCol] == '_')) return false; // caret inside a word
            int start;
            string ab = ExtractAbbreviation(line, caretCol, out start);
            if (ab == null) return false;
            string before = line.Substring(0, start);
            if (ab == "!" && before.Trim().Length > 0) return false;
            int ws = 0;
            while (ws < line.Length && (line[ws] == ' ' || line[ws] == '\t')) ws++;
            string baseIndent = line.Substring(0, Math.Min(ws, start));
            string unit = string.IsNullOrEmpty(indentUnit) ? "\t" : indentUnit;
            string result;
            if (syntax == EmmetSyntax.Css)
            {
                if (!insideCssRule || before.Trim().Length > 0) return false;
                result = ExpandCss(ab);
                if (result == null) return false;
            }
            else
            {
                if (!HtmlContextOk(line, start, syntax)) return false;
                result = ExpandMarkup(ab, syntax, unit);
                if (result == null) return false;
                if (baseIndent.Length > 0) result = result.Replace("\n", "\n" + baseIndent);
            }
            int stop = result.IndexOf(Stop);
            caretOffset = stop >= 0 ? stop : result.Replace(Stop.ToString(), "").Length;
            expansion = result.Replace(Stop.ToString(), "");
            replaceStart = start;
            return true;
        }

        private static readonly HashSet<string> JsxKeywordsBefore = new HashSet<string>(StringComparer.Ordinal) { "return", "yield", "default", "else" };

        private static bool HtmlContextOk(string line, int start, EmmetSyntax syntax)
        {
            if (start > 0)
            {
                char p = line[start - 1];
                if (p == '<' || p == '&' || p == '/' || p == '"' || p == '\'' || p == '=' || p == '.' || p == '`' || p == '\\') return false;
            }
            // not inside a tag on this line
            int lastLt = line.LastIndexOf('<', Math.Max(0, start - 1));
            int lastGt = line.LastIndexOf('>', Math.Max(0, start - 1));
            if (start > 0 && lastLt >= 0 && lastLt > lastGt) return false;
            if (syntax == EmmetSyntax.Jsx)
            {
                int k = start - 1;
                while (k >= 0 && char.IsWhiteSpace(line[k])) k--;
                if (k < 0) return true;
                char q = line[k];
                if ("(>{?:&|,".IndexOf(q) >= 0) return true;
                if (char.IsLetter(q))
                {
                    int e = k + 1;
                    while (k >= 0 && char.IsLetter(line[k])) k--;
                    return JsxKeywordsBefore.Contains(line.Substring(k + 1, e - k - 1));
                }
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ markup parsing
        private sealed class Parser
        {
            private readonly string s;
            private int i;
            public bool Failed;

            public Parser(string text) { s = text; }

            public List<Node> ParseAll()
            {
                Node root = new Node();
                root.IsGroup = true;
                ParseSequence(root, false);
                if (i != s.Length) Failed = true;
                return root.Children;
            }

            private char Peek { get { return i < s.Length ? s[i] : '\0'; } }

            // Parses "a>b+c^d" into container; stops at ')' (consumed by caller) or end.
            private void ParseSequence(Node container, bool inGroup)
            {
                Node parent = container;
                Node last = null;
                while (i < s.Length && !Failed)
                {
                    Node n;
                    if (Peek == '(')
                    {
                        i++;
                        Node g = new Node();
                        g.IsGroup = true;
                        ParseSequence(g, true);
                        if (Peek != ')') { Failed = true; return; }
                        i++;
                        ParseMultiplier(g);
                        n = g;
                    }
                    else
                    {
                        n = ParseElement();
                        if (n == null) { Failed = true; return; }
                    }
                    n.Parent = parent;
                    parent.Children.Add(n);
                    last = n;
                    if (i >= s.Length) return;
                    char op = Peek;
                    if (op == ')') { if (!inGroup) Failed = true; return; }
                    if (op == '>') { i++; parent = last; continue; }
                    if (op == '+') { i++; continue; }
                    if (op == '^')
                    {
                        while (Peek == '^') { i++; parent = parent.Parent ?? container; if (parent == null) parent = container; }
                        if (IsAncestorOrSelf(container, parent) == false) parent = container;
                        continue;
                    }
                    Failed = true;
                    return;
                }
            }

            private static bool IsAncestorOrSelf(Node container, Node n)
            {
                for (Node x = n; x != null; x = x.Parent) if (ReferenceEquals(x, container)) return true;
                return false;
            }

            private void ParseMultiplier(Node n)
            {
                if (Peek != '*') return;
                i++;
                int st = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                int count;
                if (i == st || !int.TryParse(s.Substring(st, i - st), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1 || count > 200)
                {
                    Failed = true;
                    return;
                }
                n.Repeat = count;
                n.Repeated = true;
            }

            private string ReadName()
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_' || s[i] == ':' || s[i] == '$' || s[i] == '@' || s[i] == '!' || s[i] == '%'))
                {
                    if (s[i] == '@' && i + 1 < s.Length && (s[i + 1] == '-' || char.IsDigit(s[i + 1]))) { i++; while (i < s.Length && (s[i] == '-' || char.IsDigit(s[i]))) i++; continue; }
                    i++;
                }
                return s.Substring(st, i - st);
            }

            private Node ParseElement()
            {
                Node n = new Node();
                if (i < s.Length && (char.IsLetter(s[i]) || s[i] == '!'))
                {
                    n.Name = ReadName();
                    n.ExplicitName = true;
                }
                bool any = n.Name.Length > 0;
                while (i < s.Length && !Failed)
                {
                    char c = s[i];
                    if (c == '.') { i++; string cl = ReadName(); if (cl.Length == 0) { Failed = true; return null; } n.Classes.Add(cl); any = true; continue; }
                    if (c == '#') { i++; string id = ReadName(); if (id.Length == 0) { Failed = true; return null; } n.Id = id; any = true; continue; }
                    if (c == '[') { i++; if (!ParseAttributes(n)) { Failed = true; return null; } any = true; continue; }
                    if (c == '{') { i++; int close = FindClose('{', '}'); if (close < 0) { Failed = true; return null; } n.Text = s.Substring(i, close - i); i = close + 1; any = true; continue; }
                    if (c == '*') { ParseMultiplier(n); continue; }
                    if (c == '/') { i++; n.SelfClose = true; continue; }
                    break;
                }
                return any ? n : null;
            }

            private int FindClose(char open, char close)
            {
                int depth = 1;
                for (int k = i; k < s.Length; k++)
                {
                    if (s[k] == open) depth++;
                    else if (s[k] == close) { depth--; if (depth == 0) return k; }
                }
                return -1;
            }

            private bool ParseAttributes(Node n)
            {
                while (i < s.Length)
                {
                    while (i < s.Length && s[i] == ' ') i++;
                    if (i >= s.Length) return false;
                    if (s[i] == ']') { i++; return true; }
                    int st = i;
                    while (i < s.Length && s[i] != '=' && s[i] != ' ' && s[i] != ']') i++;
                    string name = s.Substring(st, i - st);
                    if (name.Length == 0) return false;
                    Attr a = new Attr();
                    a.Name = name.TrimEnd('.');
                    if (i < s.Length && s[i] == '=')
                    {
                        i++;
                        a.HasValue = true;
                        if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                        {
                            char q = s[i];
                            int e = s.IndexOf(q, i + 1);
                            if (e < 0) return false;
                            a.Value = s.Substring(i + 1, e - i - 1);
                            i = e + 1;
                        }
                        else
                        {
                            int vs = i;
                            while (i < s.Length && s[i] != ' ' && s[i] != ']') i++;
                            a.Value = s.Substring(vs, i - vs);
                        }
                    }
                    n.Attrs.Add(a);
                }
                return false;
            }
        }

        private static bool IsKnownName(string name, EmmetSyntax syntax)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (HtmlTags.Contains(name)) return true;
            if (name.IndexOf('-') > 0 && char.IsLower(name[0])) return true; // custom element
            if (syntax == EmmetSyntax.Jsx && char.IsUpper(name[0])) return true; // component
            return false;
        }

        private static bool AllNamesKnown(List<Node> nodes, EmmetSyntax syntax)
        {
            foreach (Node n in nodes)
            {
                if (!n.IsGroup && n.ExplicitName && !IsKnownName(Number(n.Name, 1, 1), syntax)) return false;
                if (!AllNamesKnown(n.Children, syntax)) return false;
            }
            return true;
        }

        private static int CountNodes(List<Node> nodes)
        {
            int c = 0;
            foreach (Node n in nodes) c += Math.Max(1, n.Repeat) * (1 + CountNodes(n.Children));
            return c;
        }

        public static string ExpandMarkup(string ab, EmmetSyntax syntax, string unit)
        {
            if (ab == "!" || ab == "html:5")
            {
                if (syntax != EmmetSyntax.Html) return null;
                return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n" + unit + "<meta charset=\"UTF-8\">\n" + unit +
                       "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" + unit + "<title>Document</title>\n</head>\n<body>\n" +
                       unit + Stop + "\n</body>\n</html>";
            }
            Parser p = new Parser(ab);
            List<Node> nodes = p.ParseAll();
            if (p.Failed || nodes.Count == 0) return null;
            if (!AllNamesKnown(nodes, syntax)) return null;
            if (CountNodes(nodes) > 1000) return null;
            StringBuilder sb = new StringBuilder();
            RenderList(nodes, null, syntax, unit, 0, sb, 1, 1);
            string res = sb.ToString();
            return res.EndsWith("\n", StringComparison.Ordinal) ? res.Substring(0, res.Length - 1) : res;
        }

        private static string ResolveName(Node n, Node parent)
        {
            if (n.Name.Length > 0) return n.Name;
            string pn = parent != null ? parent.Name : "";
            switch (pn)
            {
                case "ul": case "ol": case "menu": return "li";
                case "table": case "tbody": case "thead": case "tfoot": return "tr";
                case "tr": return "td";
                case "select": case "optgroup": case "datalist": return "option";
                case "colgroup": return "col";
                case "audio": case "video": case "picture": return "source";
                case "map": return "area";
            }
            return InlineParents.Contains(pn) ? "span" : "div";
        }

        private static string Number(string text, int index, int total)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('$') < 0) return text;
            StringBuilder sb = new StringBuilder();
            int k = 0;
            while (k < text.Length)
            {
                if (text[k] == '\\' && k + 1 < text.Length && text[k + 1] == '$') { sb.Append('$'); k += 2; continue; }
                if (text[k] != '$') { sb.Append(text[k]); k++; continue; }
                int pad = 0;
                while (k < text.Length && text[k] == '$') { pad++; k++; }
                int value = index;
                if (k < text.Length && text[k] == '@')
                {
                    k++;
                    bool reverse = false;
                    if (k < text.Length && text[k] == '-') { reverse = true; k++; }
                    int st = k;
                    while (k < text.Length && char.IsDigit(text[k])) k++;
                    int startAt = 1;
                    if (k > st) int.TryParse(text.Substring(st, k - st), NumberStyles.Integer, CultureInfo.InvariantCulture, out startAt);
                    value = reverse ? (startAt + total - index) : (startAt + index - 1);
                }
                sb.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0'));
            }
            return sb.ToString();
        }

        private static void RenderList(List<Node> nodes, Node parent, EmmetSyntax syntax, string unit, int depth, StringBuilder sb, int idx, int total)
        {
            foreach (Node n in nodes)
            {
                for (int r = 1; r <= n.Repeat; r++)
                {
                    int ci = n.Repeated ? r : idx;
                    int ct = n.Repeated ? n.Repeat : total;
                    if (n.IsGroup) { RenderList(n.Children, parent, syntax, unit, depth, sb, ci, ct); continue; }
                    RenderElement(n, parent, syntax, unit, depth, sb, ci, ct);
                }
            }
        }

        private static void RenderElement(Node n, Node parent, EmmetSyntax syntax, string unit, int depth, StringBuilder sb, int idx, int total)
        {
            string indent = Repeat(unit, depth);
            string name = Number(ResolveName(n, parent), idx, total);
            bool jsx = syntax == EmmetSyntax.Jsx;
            StringBuilder tag = new StringBuilder();
            tag.Append('<').Append(name);
            if (n.Id != null) tag.Append(" id=\"").Append(Number(n.Id, idx, total)).Append('"');
            List<string> classes = new List<string>();
            foreach (string c in n.Classes) classes.Add(Number(c, idx, total));
            bool classInAttrs = false;
            foreach (Attr a in n.Attrs) if (a.Name == "class" || a.Name == "className") classInAttrs = true;
            if (classes.Count > 0 && !classInAttrs) tag.Append(jsx ? " className=\"" : " class=\"").Append(string.Join(" ", classes.ToArray())).Append('"');
            List<Attr> attrs = new List<Attr>(n.Attrs);
            if (attrs.Count == 0) AddDefaultAttrs(name, attrs, n);
            foreach (Attr a in attrs)
            {
                string an = a.Name;
                if (jsx && an == "class") an = "className";
                if (jsx && an == "for") an = "htmlFor";
                string val = a.HasValue ? Number(a.Value, idx, total) : "";
                if (an == "class" && classes.Count > 0) val = (string.Join(" ", classes.ToArray()) + " " + val).Trim();
                tag.Append(' ').Append(an).Append("=\"").Append(val.Length == 0 ? Stop.ToString() : val).Append('"');
            }
            bool isVoid = EditorTagEditing.IsVoidElement(name) || n.SelfClose;
            if (isVoid)
            {
                tag.Append(jsx ? " />" : ">");
                sb.Append(indent).Append(tag).Append('\n');
                return;
            }
            tag.Append('>');
            string text = n.Text != null ? Number(n.Text, idx, total) : null;
            if (n.Children.Count == 0)
            {
                sb.Append(indent).Append(tag).Append(string.IsNullOrEmpty(text) ? Stop.ToString() : text).Append("</").Append(name).Append(">\n");
                return;
            }
            sb.Append(indent).Append(tag);
            if (!string.IsNullOrEmpty(text)) sb.Append(text);
            sb.Append('\n');
            Node self = new Node();
            self.Name = name;
            RenderList(n.Children, self, syntax, unit, depth + 1, sb, idx, total);
            sb.Append(indent).Append("</").Append(name).Append(">\n");
        }

        private static void AddDefaultAttrs(string name, List<Attr> attrs, Node n)
        {
            switch (name)
            {
                case "a": attrs.Add(EmptyAttr("href")); break;
                case "img": attrs.Add(EmptyAttr("src")); attrs.Add(EmptyAttr("alt")); break;
                case "link": attrs.Add(ValueAttr("rel", "stylesheet")); attrs.Add(EmptyAttr("href")); break;
                case "form": attrs.Add(EmptyAttr("action")); break;
                case "label": attrs.Add(EmptyAttr("for")); break;
                case "input": attrs.Add(ValueAttr("type", "text")); break;
                case "option": attrs.Add(EmptyAttr("value")); break;
                case "abbr": case "acronym": attrs.Add(EmptyAttr("title")); break;
                case "iframe": attrs.Add(EmptyAttr("src")); break;
                case "script": break;
                case "source": attrs.Add(EmptyAttr("src")); break;
            }
        }

        private static Attr EmptyAttr(string n) { Attr a = new Attr(); a.Name = n; a.HasValue = true; a.Value = ""; return a; }
        private static Attr ValueAttr(string n, string v) { Attr a = new Attr(); a.Name = n; a.HasValue = true; a.Value = v; return a; }

        private static string Repeat(string unit, int n)
        {
            if (n <= 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int k = 0; k < n; k++) sb.Append(unit);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ CSS
        private static readonly Dictionary<string, string> CssProps = BuildCssProps();
        private static readonly Dictionary<string, string> CssKeywords = BuildCssKeywords();
        private static readonly HashSet<string> Unitless = new HashSet<string>(StringComparer.Ordinal)
        {
            "z-index", "opacity", "flex", "flex-grow", "flex-shrink", "font-weight", "line-height", "order", "zoom", "orphans", "widows", "fill-opacity", "stroke-opacity", "columns", "column-count", "tab-size"
        };

        private static Dictionary<string, string> BuildCssProps()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] pairs = {
                "m","margin", "mt","margin-top", "mr","margin-right", "mb","margin-bottom", "ml","margin-left", "mx","margin-inline", "my","margin-block",
                "p","padding", "pt","padding-top", "pr","padding-right", "pb","padding-bottom", "pl","padding-left", "px","padding-inline", "py","padding-block",
                "w","width", "h","height", "maw","max-width", "mah","max-height", "miw","min-width", "mih","min-height",
                "t","top", "r","right", "b","bottom", "l","left", "z","z-index", "op","opacity", "g","gap", "rg","row-gap", "cg","column-gap",
                "fz","font-size", "fw","font-weight", "ff","font-family", "fs","font-style", "lh","line-height", "ls","letter-spacing",
                "ta","text-align", "td","text-decoration", "tt","text-transform", "ti","text-indent", "ws","white-space", "va","vertical-align",
                "c","color", "bg","background", "bgc","background-color", "bgi","background-image", "bgs","background-size", "bgp","background-position", "bgr","background-repeat",
                "bd","border", "bdt","border-top", "bdr","border-right", "bdb","border-bottom", "bdl","border-left", "bdc","border-color", "bdw","border-width", "bds","border-style",
                "br","border-radius", "bxsh","box-shadow", "bxz","box-sizing", "ol","outline", "d","display", "pos","position", "fl","float", "cl","clear",
                "ov","overflow", "ovx","overflow-x", "ovy","overflow-y", "v","visibility", "cur","cursor", "fx","flex", "fxd","flex-direction", "fxw","flex-wrap",
                "fxg","flex-grow", "fxs","flex-shrink", "fxb","flex-basis", "jc","justify-content", "ji","justify-items", "ai","align-items", "ac","align-content", "as","align-self",
                "ord","order", "gtc","grid-template-columns", "gtr","grid-template-rows", "gc","grid-column", "gr","grid-row", "ga","grid-area",
                "trf","transform", "trs","transition", "anim","animation", "con","content", "ct","content", "lis","list-style", "us","user-select", "pe","pointer-events", "obf","object-fit", "ar","aspect-ratio", "inset","inset"
            };
            for (int k = 0; k + 1 < pairs.Length; k += 2) d[pairs[k]] = pairs[k + 1];
            return d;
        }

        private static Dictionary<string, string> BuildCssKeywords()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] pairs = {
                "df","display: flex;", "dif","display: inline-flex;", "db","display: block;", "di","display: inline;", "dib","display: inline-block;",
                "dn","display: none;", "dg","display: grid;", "dig","display: inline-grid;", "dt","display: table;", "dc","display: contents;",
                "posa","position: absolute;", "posr","position: relative;", "posf","position: fixed;", "poss","position: sticky;", "posst","position: static;",
                "tac","text-align: center;", "tal","text-align: left;", "tar","text-align: right;", "taj","text-align: justify;",
                "fwb","font-weight: bold;", "fwn","font-weight: normal;", "fsi","font-style: italic;", "fsn","font-style: normal;",
                "tdn","text-decoration: none;", "tdu","text-decoration: underline;", "ttu","text-transform: uppercase;", "ttl","text-transform: lowercase;", "ttc","text-transform: capitalize;",
                "jcc","justify-content: center;", "jcsb","justify-content: space-between;", "jcsa","justify-content: space-around;", "jcse","justify-content: space-evenly;",
                "jcfs","justify-content: flex-start;", "jcfe","justify-content: flex-end;", "aic","align-items: center;", "aifs","align-items: flex-start;",
                "aife","align-items: flex-end;", "ais","align-items: stretch;", "aib","align-items: baseline;", "asc","align-self: center;",
                "fdc","flex-direction: column;", "fdr","flex-direction: row;", "fxdc","flex-direction: column;", "fxdr","flex-direction: row;", "fxww","flex-wrap: wrap;", "fww","flex-wrap: wrap;",
                "curp","cursor: pointer;", "curd","cursor: default;", "ovh","overflow: hidden;", "ova","overflow: auto;", "ovs","overflow: scroll;", "ovv","overflow: visible;",
                "bxzbb","box-sizing: border-box;", "bxzcb","box-sizing: content-box;", "vh","visibility: hidden;", "vv","visibility: visible;",
                "fll","float: left;", "flr","float: right;", "fln","float: none;", "clb","clear: both;", "wsnw","white-space: nowrap;",
                "bdn","border: none;", "bgn","background: none;", "lisn","list-style: none;", "usn","user-select: none;", "pen","pointer-events: none;",
                "obfc","object-fit: cover;", "obfcn","object-fit: contain;", "m0","margin: 0;", "p0","padding: 0;", "ma","margin: auto;", "mauto","margin: auto;"
            };
            for (int k = 0; k + 1 < pairs.Length; k += 2) d[pairs[k]] = pairs[k + 1];
            return d;
        }

        private static readonly Dictionary<string, string> CssValueAliases = BuildValueAliases();

        private static Dictionary<string, string> BuildValueAliases()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] pairs = { "a","auto", "n","none", "f","flex", "b","block", "i","inline", "ib","inline-block", "g","grid", "c","center", "l","left", "r","right",
                               "s","static", "rel","relative", "abs","absolute", "fix","fixed", "st","sticky", "h","hidden", "v","visible", "p","pointer", "bb","border-box", "w","wrap", "nw","nowrap", "co","column" };
            for (int k = 0; k + 1 < pairs.Length; k += 2) d[pairs[k]] = pairs[k + 1];
            return d;
        }

        /// <summary>Expands a CSS abbreviation to "property: value;" (Stop marks the caret), or null.</summary>
        public static string ExpandCss(string ab)
        {
            if (string.IsNullOrEmpty(ab)) return null;
            string kw;
            if (CssKeywords.TryGetValue(ab, out kw)) return kw + Stop;
            int colon = ab.IndexOf(':');
            if (colon > 0)
            {
                string pk = ab.Substring(0, colon), vk = ab.Substring(colon + 1);
                string prop;
                if (!CssProps.TryGetValue(pk, out prop) || vk.Length == 0) return null;
                if (prop == "position" && vk == "a") vk = "abs";
                if (prop == "position" && vk == "r") vk = "rel";
                if (prop == "position" && vk == "f") vk = "fix";
                string val;
                if (!CssValueAliases.TryGetValue(vk, out val)) val = vk;
                return prop + ": " + val + ";" + Stop;
            }
            int li = 0;
            while (li < ab.Length && char.IsLetter(ab[li]) && char.IsLower(ab[li])) li++;
            string letters = ab.Substring(0, li);
            string rest = ab.Substring(li);
            string property;
            // longest property prefix (e.g. "bgc" before "bg")
            while (letters.Length > 0 && !CssProps.TryGetValue(letters, out property))
            {
                if (rest.Length > 0 && !(char.IsDigit(rest[0]) || rest[0] == '-' || rest[0] == '.' || rest[0] == '#')) return null;
                rest = letters.Substring(letters.Length - 1) + rest;
                letters = letters.Substring(0, letters.Length - 1);
            }
            if (letters.Length == 0 || !CssProps.TryGetValue(letters, out property)) return null;
            if (rest.Length == 0) return property + ": " + Stop + ";";
            string value = CssValue(property, rest);
            if (value == null) return null;
            return property + ": " + value + ";" + Stop;
        }

        private static string CssValue(string property, string rest)
        {
            if (rest[0] == '#')
            {
                string hex = rest.Substring(1);
                foreach (char ch in hex) if (!Uri.IsHexDigit(ch)) return null;
                if (hex.Length == 1) hex = new string(hex[0], 3);
                else if (hex.Length == 2) hex = hex + hex + hex;
                if (hex.Length != 3 && hex.Length != 6 && hex.Length != 8) return null;
                return "#" + hex;
            }
            if (rest == "a" || rest == "auto") return "auto";
            List<string> values = new List<string>();
            int k = 0;
            while (k < rest.Length)
            {
                bool neg = false;
                if (rest[k] == '-') { neg = true; k++; }
                int ns = k;
                while (k < rest.Length && (char.IsDigit(rest[k]) || rest[k] == '.')) k++;
                if (k == ns) return null;
                string num = rest.Substring(ns, k - ns);
                int us = k;
                while (k < rest.Length && (char.IsLetter(rest[k]) || rest[k] == '%')) k++;
                string unit = rest.Substring(us, k - us);
                double dv;
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return null;
                string u;
                if (unit.Length == 0) u = (Unitless.Contains(property) || dv == 0) ? "" : (num.IndexOf('.') >= 0 && property == "line-height" ? "" : "px");
                else if (unit == "p") u = "%";
                else if (unit == "e") u = "em";
                else if (unit == "r") u = "rem";
                else if (unit == "x") u = "ex";
                else u = unit;
                if (dv == 0 && unit.Length == 0) u = "";
                values.Add((neg ? "-" : "") + num + u);
                if (k < rest.Length)
                {
                    if (rest[k] == '-' && k + 1 < rest.Length && rest[k + 1] == '-') { k++; continue; } // "10--5" = 10px -5px
                    if (rest[k] == '-') { k++; continue; }
                    return null;
                }
                if (values.Count > 4) return null;
            }
            return values.Count == 0 ? null : string.Join(" ", values.ToArray());
        }

        /// <summary>True when (line, col) is inside a { } rule block (bounded scan, comments and strings skipped).</summary>
        public static bool IsInsideCssRule(IList<string> lines, int line, int col)
        {
            if (lines == null || line < 0 || line >= lines.Count) return false;
            int depth = 0;
            int from = Math.Max(0, line - 3000);
            bool inComment = false;
            char quote = '\0';
            for (int l = from; l <= line; l++)
            {
                string s = lines[l] ?? "";
                int end = l == line ? Math.Min(col, s.Length) : s.Length;
                for (int i = 0; i < end; i++)
                {
                    char c = s[i];
                    if (inComment) { if (c == '*' && i + 1 < s.Length && s[i + 1] == '/') { inComment = false; i++; } continue; }
                    if (quote != '\0') { if (c == '\\') { i++; continue; } if (c == quote) quote = '\0'; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { inComment = true; i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
                    if (c == '"' || c == '\'') { quote = c; continue; }
                    if (c == '{') depth++;
                    else if (c == '}') depth = Math.Max(0, depth - 1);
                }
                quote = '\0';
            }
            return depth > 0;
        }
    }
}
