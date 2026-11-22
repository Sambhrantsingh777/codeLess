using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public enum ImportCompletionKind
    {
        None = 0,
        /// <summary>A file / folder path inside quotes (JS/TS import/require/export-from, CSS @import/url(), HTML src/href, C #include "", Markdown links).</summary>
        Path = 1,
        /// <summary>Python module position: after "import " / "from ".</summary>
        PythonModule = 2,
        /// <summary>Python names: after "from module import ".</summary>
        PythonName = 3
    }

    /// <summary>Where the caret is in an import-like construct (see <see cref="ImportCompletionEngine.DetectContext(string, IList{string}, int, int)"/>).</summary>
    public sealed class ImportCompletionContext
    {
        public ImportCompletionKind Kind;
        /// <summary>"js", "css", "html", "c", "md" or "py".</summary>
        public string Family = "";
        /// <summary>Everything typed between the opening quote (or the module position) and the caret.</summary>
        public string Specifier = "";
        /// <summary>Column where the segment being completed starts (after the last '/' or '.').</summary>
        public int SegmentStart;
        /// <summary>Caret column at detection time.</summary>
        public int Caret;
        public int Line;
        /// <summary>Opening quote ('\0' for unquoted url( / Markdown links / Python).</summary>
        public char Quote;
        /// <summary>JS/TS module specifiers drop script extensions like VS Code (.ts/.tsx/.js/.jsx/.mjs/.cjs/.mts/.cts/.d.ts).</summary>
        public bool OmitScriptExtensions;
        /// <summary>Python: the module of "from X import" (with its leading dots).</summary>
        public string PythonModule = "";

        /// <summary>The text of the current segment before the caret (what the popup filters by).</summary>
        public string Segment
        {
            get
            {
                int cut = Specifier.Length - (Caret - SegmentStart);
                return cut >= 0 && cut <= Specifier.Length ? Specifier.Substring(cut) : "";
            }
        }
    }

    /// <summary>Client-side payload of a path / module completion item.</summary>
    public sealed class ImportCompletionTag
    {
        public bool IsFolder;
        /// <summary>Accepting re-opens the popup (folders: their contents).</summary>
        public bool Retrigger;
        public string FullPath;
        public string Source; // "workspace", "node_modules", "alias", "site-packages", "stdlib"
    }

    /// <summary>
    /// VS Code-style path and module completion shared by both UIs (strict C# 5). Detection is line based plus the
    /// buffer's comment/string context; item building only reads the file system (never runs anything, so it is fine in
    /// Restricted Mode), is thread-safe and cached (directory listings are revalidated by their timestamp, so
    /// created / deleted files show up immediately).
    /// </summary>
    public static class ImportCompletionEngine
    {
        public const int MaxEntriesPerDirectory = 1500;
        public const int MaxItems = 2000;

        private const RegexOptions Opt = RegexOptions.CultureInvariant;

        private static readonly Regex JsImport = new Regex(
            @"(?:^|[^\w$.])(?:import\s*(?:type\s+)?(?:[\w$*{}\s,]+?\s*from\s*)?|export\s*(?:type\s+)?(?:[\w$*{}\s,]+?\s*)?from\s*|from\s*|require(?:\.resolve)?\s*\(\s*|import\s*\(\s*|(?:jest|vi)\.(?:mock|requireActual|importActual)\s*\(\s*)(['""`])([^'""`\r\n]*)$", Opt);
        private static readonly Regex CssImport = new Regex(@"@import\s+(?:url\(\s*)?(['""])([^'""]*)$", Opt | RegexOptions.IgnoreCase);
        private static readonly Regex CssUrl = new Regex(@"\burl\(\s*(['""]?)([^'""()\s]*)$", Opt | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlAttr = new Regex(@"<[^<>]*\s(?:src|href|poster|data|action|formaction)\s*=\s*(['""])([^'""<>]*)$", Opt | RegexOptions.IgnoreCase);
        private static readonly Regex CInclude = new Regex(@"^\s*#\s*(?:include|import|include_next)\s*("")([^""]*)$", Opt);
        private static readonly Regex MdLink = new Regex(@"!?\[[^\]]*\]\(\s*(<?)([^()\s>]*)$", Opt);
        private static readonly Regex MdRef = new Regex(@"^\s{0,3}\[[^\]]+\]:\s*(<?)(\S*)$", Opt);
        private static readonly Regex PyImport = new Regex(@"^\s*import\s+((?:[\w.]+(?:\s+as\s+\w+)?\s*,\s*)*)([\w.]*)$", Opt);
        private static readonly Regex PyFrom = new Regex(@"^\s*from\s+(\.*)([\w.]*)$", Opt);
        private static readonly Regex PyFromImport = new Regex(@"^\s*from\s+(\.*[\w.]*)\s+import\s+(?:\(\s*)?(?:\w+(?:\s+as\s+\w+)?\s*,\s*)*(\w*)$", Opt);
        private static readonly Regex UrlScheme = new Regex(@"^[A-Za-z][A-Za-z0-9+.-]*:", Opt);

        private static readonly HashSet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts" };

        private static readonly HashSet<string> AlwaysHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", ".DS_Store", "Thumbs.db", ".hg", ".svn", "__pycache__" };

        /// <summary>Language family of a file (by extension / language id), or "" when path completion does not apply.</summary>
        public static string FamilyOf(string filePath, string language)
        {
            string ext = "";
            try { ext = Path.GetExtension(filePath ?? "").TrimStart('.').ToLowerInvariant(); } catch { }
            string l = ext.Length > 0 ? ext : (language ?? "").Trim().TrimStart('.').ToLowerInvariant();
            switch (l)
            {
                case "js": case "jsx": case "ts": case "tsx": case "mjs": case "cjs": case "mts": case "cts":
                case "javascript": case "typescript": case "javascriptreact": case "typescriptreact":
                    return "js";
                case "vue": case "svelte": case "astro":
                    return "web"; // script imports + src/href
                case "css": case "scss": case "sass": case "less":
                    return "css";
                case "html": case "htm": case "xhtml": case "php": case "cshtml": case "razor":
                    return "html";
                case "c": case "h": case "cpp": case "cc": case "cxx": case "hpp": case "hh": case "hxx": case "m": case "mm": case "ino":
                    return "c";
                case "md": case "markdown": case "mdx":
                    return "md";
                case "py": case "pyi": case "pyw": case "python":
                    return "py";
            }
            return "";
        }

        /// <summary>Context at the caret using the buffer's syntax state: never inside comments (or, for Python, strings).</summary>
        public static ImportCompletionContext DetectContext(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null || buf.Lines == null || line < 0 || line >= buf.Lines.Count) return null;
            ImportCompletionContext ctx = DetectContext(buf.FilePath ?? buf.FileName, buf.Lines, line, col, buf.Language);
            if (ctx == null) return null;
            try
            {
                EditorSyntaxSession session = EditorSyntaxSession.For(buf);
                if (session != null)
                {
                    session.Prepare(buf);
                    if (session.Brackets != null && session.Brackets.Profile.Enabled)
                    {
                        int c = session.Brackets.ContextAt(buf.Lines, line, col);
                        if (c == EditorBracketCache.ContextComment) return null;
                        if (ctx.Kind != ImportCompletionKind.Path && c == EditorBracketCache.ContextString) return null;
                    }
                }
            }
            catch { }
            return ctx;
        }

        public static ImportCompletionContext DetectContext(string filePath, IList<string> lines, int line, int col)
        {
            return DetectContext(filePath, lines, line, col, null);
        }

        public static ImportCompletionContext DetectContext(string filePath, IList<string> lines, int line, int col, string language)
        {
            if (lines == null || line < 0 || line >= lines.Count) return null;
            string text = lines[line] ?? "";
            if (col < 0 || col > text.Length) return null;
            string pre = text.Substring(0, col);
            string fam = FamilyOf(filePath, language);
            if (fam.Length == 0) return null;
            if (fam == "py") return DetectPython(pre, line, col);

            Match m = null;
            string family = fam;
            if (fam == "js" || fam == "web")
            {
                m = JsImport.Match(pre);
                if (m.Success && !LooksCommentedOut(pre, m.Index)) family = "js";
                else m = null;
            }
            if (m == null && (fam == "css" || fam == "html" || fam == "web" || fam == "md"))
            {
                Match c = CssImport.Match(pre);
                if (!c.Success) c = CssUrl.Match(pre);
                if (c.Success && (fam == "css" || pre.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0 || pre.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0) && fam != "md")
                {
                    m = c; family = "css";
                }
            }
            if (m == null && (fam == "html" || fam == "web" || fam == "md"))
            {
                Match h = HtmlAttr.Match(pre);
                if (h.Success) { m = h; family = "html"; }
            }
            if (m == null && fam == "c")
            {
                Match h = CInclude.Match(pre);
                if (h.Success) { m = h; family = "c"; }
            }
            if (m == null && fam == "md")
            {
                Match h = MdLink.Match(pre);
                if (!h.Success) h = MdRef.Match(pre);
                if (h.Success) { m = h; family = "md"; }
            }
            if (m == null) return null;

            string quote = m.Groups[1].Value;
            string spec = m.Groups[2].Value;
            if (UrlScheme.IsMatch(spec) || spec.StartsWith("//", StringComparison.Ordinal) || spec.IndexOf('#') >= 0 || spec.IndexOf('?') >= 0) return null;
            if (family == "js" && quote == "`" && spec.IndexOf("${", StringComparison.Ordinal) >= 0) return null;
            ImportCompletionContext ctx = new ImportCompletionContext();
            ctx.Kind = ImportCompletionKind.Path;
            ctx.Family = family;
            ctx.Specifier = spec;
            ctx.Caret = col;
            ctx.Line = line;
            ctx.Quote = quote.Length > 0 && quote != "<" ? quote[0] : '\0';
            ctx.OmitScriptExtensions = family == "js";
            int slash = Math.Max(spec.LastIndexOf('/'), spec.LastIndexOf('\\'));
            ctx.SegmentStart = col - (spec.Length - (slash + 1));
            return ctx;
        }

        private static bool LooksCommentedOut(string pre, int matchIndex)
        {
            string head = pre.Substring(0, Math.Min(matchIndex + 1, pre.Length));
            int sl = head.IndexOf("//", StringComparison.Ordinal);
            if (sl >= 0)
            {
                // "//" inside a string before the match ("http://") is not a comment: count quotes before it
                int q = 0;
                for (int i = 0; i < sl; i++) if (head[i] == '\'' || head[i] == '"' || head[i] == '`') q++;
                if (q % 2 == 0) return true;
            }
            string t = head.TrimStart();
            return t.StartsWith("*", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal);
        }

        private static ImportCompletionContext DetectPython(string pre, int line, int col)
        {
            if (pre.TrimStart().StartsWith("#", StringComparison.Ordinal)) return null;
            ImportCompletionContext ctx = new ImportCompletionContext();
            ctx.Family = "py";
            ctx.Caret = col;
            ctx.Line = line;
            Match m = PyFromImport.Match(pre);
            if (m.Success)
            {
                ctx.Kind = ImportCompletionKind.PythonName;
                ctx.PythonModule = m.Groups[1].Value;
                ctx.Specifier = m.Groups[2].Value;
                ctx.SegmentStart = col - ctx.Specifier.Length;
                return ctx;
            }
            m = PyFrom.Match(pre);
            if (m.Success)
            {
                ctx.Kind = ImportCompletionKind.PythonModule;
                ctx.Specifier = m.Groups[1].Value + m.Groups[2].Value;
            }
            else
            {
                m = PyImport.Match(pre);
                if (!m.Success) return null;
                ctx.Kind = ImportCompletionKind.PythonModule;
                ctx.Specifier = m.Groups[2].Value;
            }
            int dot = ctx.Specifier.LastIndexOf('.');
            ctx.SegmentStart = col - (ctx.Specifier.Length - (dot + 1));
            return ctx;
        }

        /// <summary>Characters that belong to the segment being completed (typing them keeps filtering the open list).</summary>
        public static bool IsSegmentChar(ImportCompletionContext ctx, char c)
        {
            if (ctx == null) return LspEditing.IsWordChar(c);
            if (ctx.Kind != ImportCompletionKind.Path) return LspEditing.IsWordChar(c);
            if (c == '/' || c == '\\' || c == '\r' || c == '\n' || c == '\'' || c == '"' || c == '`') return false;
            if (ctx.Quote == '\0' && (char.IsWhiteSpace(c) || c == ')' || c == '(' || c == '>')) return false;
            return true;
        }

        /// <summary>Whether a typed string should (re)open path / module completion in this context.</summary>
        public static bool ShouldTrigger(ImportCompletionContext ctx, string typed)
        {
            if (ctx == null || string.IsNullOrEmpty(typed)) return false;
            char last = typed[typed.Length - 1];
            if (ctx.Kind == ImportCompletionKind.Path)
                return last == '/' || last == '\\' || last == '.' || last == '\'' || last == '"' || last == '`' || last == '(' || last == '<' || IsSegmentChar(ctx, last);
            if (ctx.Kind == ImportCompletionKind.PythonModule) return last == ' ' || last == '.' || LspEditing.IsWordChar(last);
            if (ctx.Kind == ImportCompletionKind.PythonName) return last == ' ' || last == ',' || last == '(' || LspEditing.IsWordChar(last);
            return false;
        }

        /// <summary>
        /// End column of the segment under the caret (the text accepting an item replaces: up to the next '/', quote or
        /// closing bracket), like VS Code's replace range.
        /// </summary>
        public static int SegmentEnd(ImportCompletionContext ctx, string lineText, int caret)
        {
            int e = Math.Min(caret, lineText.Length);
            while (e < lineText.Length && IsSegmentChar(ctx, lineText[e])) e++;
            return e;
        }

        /// <summary>Gives a path / module item a text edit replacing the whole segment around the caret.</summary>
        public static void PrepareAccept(ImportCompletionContext ctx, LspCompletionItem item, string lineText, int line, int startCol, int caret)
        {
            if (item == null || lineText == null) return;
            int s = Math.Max(0, Math.Min(startCol, lineText.Length));
            int e = SegmentEnd(ctx, lineText, Math.Max(s, caret));
            string text = item.InsertText ?? item.Label ?? "";
            // A folder's trailing '/' must not be doubled when the text after the caret already starts with one.
            if (text.EndsWith("/", StringComparison.Ordinal) && e < lineText.Length && lineText[e] == '/') text = text.Substring(0, text.Length - 1);
            item.TextEdit = new LspTextEdit(new LspRange(line, s, line, e), text);
            item.IsSnippet = false;
        }

        // ------------------------------------------------------------------ items

        /// <summary>Builds the completion items for a context (file system only; call off the UI thread for big folders).</summary>
        public static List<LspCompletionItem> BuildItems(ImportCompletionContext ctx, string filePath, string workspaceRoot)
        {
            List<LspCompletionItem> items = new List<LspCompletionItem>();
            if (ctx == null) return items;
            try
            {
                if (ctx.Kind == ImportCompletionKind.Path) items = BuildPathItems(ctx, filePath, workspaceRoot);
                else if (ctx.Kind == ImportCompletionKind.PythonModule) items = PythonModuleCompletion.BuildModuleItems(ctx, filePath, workspaceRoot);
                else if (ctx.Kind == ImportCompletionKind.PythonName) items = PythonModuleCompletion.BuildNameItems(ctx, filePath, workspaceRoot);
            }
            catch { }
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);
            foreach (LspCompletionItem it in items) it.Resolved = true; // client-side items: nothing to resolve on a server
            return items;
        }

        private static string DirOf(string filePath)
        {
            try { return string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch { return null; }
        }

        private static List<LspCompletionItem> BuildPathItems(ImportCompletionContext ctx, string filePath, string workspaceRoot)
        {
            List<LspCompletionItem> items = new List<LspCompletionItem>();
            string spec = ctx.Specifier.Replace('\\', '/');
            int slash = spec.LastIndexOf('/');
            string dirPart = slash >= 0 ? spec.Substring(0, slash + 1) : "";
            string segment = slash >= 0 ? spec.Substring(slash + 1) : spec;
            string fileDir = DirOf(filePath);
            string root = string.IsNullOrEmpty(workspaceRoot) ? fileDir : workspaceRoot;
            bool relative = spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec == "." || spec == "..";
            bool rooted = spec.StartsWith("/", StringComparison.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            if (relative || (ctx.Family != "js" && !rooted))
            {
                if (fileDir == null) return items;
                if (spec == "." || spec == "..") return items; // "./" / "../" come next
                AddDirectoryItems(items, seen, CombineSpec(fileDir, dirPart), ctx, filePath, segment, "workspace", root);
                return items;
            }
            if (rooted)
            {
                if (ctx.Family == "c" || string.IsNullOrEmpty(root)) return items;
                AddDirectoryItems(items, seen, CombineSpec(root, dirPart.TrimStart('/')), ctx, filePath, segment, "workspace", root);
                return items;
            }

            // JS/TS bare specifier: tsconfig/jsconfig path aliases, baseUrl, node_modules packages.
            TsPathConfig cfg = TsPathConfig.Find(fileDir, root);
            bool aliased = false;
            if (cfg != null)
            {
                foreach (KeyValuePair<string, List<string>> kv in cfg.Paths)
                {
                    string key = kv.Key;
                    int star = key.IndexOf('*');
                    if (star < 0) continue;
                    string keyPrefix = key.Substring(0, star);
                    if (keyPrefix.Length == 0 || !spec.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                    string rest = dirPart.Length >= keyPrefix.Length ? dirPart.Substring(keyPrefix.Length) : null;
                    if (rest == null) continue;
                    foreach (string target in kv.Value)
                    {
                        int tstar = target.IndexOf('*');
                        string tPrefix = tstar >= 0 ? target.Substring(0, tstar) : target;
                        string baseDir = CombineSpec(cfg.PathsBase, tPrefix);
                        AddDirectoryItems(items, seen, CombineSpec(baseDir, rest), ctx, filePath, segment, "alias", root);
                        aliased = true;
                    }
                }
            }
            if (aliased) return items;

            if (dirPart.Length == 0)
            {
                if (cfg != null)
                {
                    foreach (KeyValuePair<string, List<string>> kv in cfg.Paths)
                    {
                        string key = kv.Key;
                        int star = key.IndexOf('*');
                        string label = star >= 0 ? key.Substring(0, star) : key;
                        if (label.Length == 0 || !seen.Add(label)) continue;
                        LspCompletionItem it = new LspCompletionItem();
                        it.Label = label;
                        it.InsertText = label;
                        it.Kind = star >= 0 ? 19 : 9;
                        it.Detail = "path alias";
                        it.SortText = "0" + label;
                        ImportCompletionTag tag = new ImportCompletionTag();
                        tag.IsFolder = star >= 0;
                        tag.Retrigger = star >= 0 && label.EndsWith("/", StringComparison.Ordinal);
                        tag.Source = "alias";
                        it.Tag = tag;
                        items.Add(it);
                    }
                    if (!string.IsNullOrEmpty(cfg.BaseUrl)) AddDirectoryItems(items, seen, cfg.BaseUrl, ctx, filePath, segment, "baseUrl", root);
                }
                foreach (string pkg in NodePackages.List(fileDir, root))
                {
                    if (!seen.Add(pkg)) continue;
                    LspCompletionItem it = new LspCompletionItem();
                    it.Label = pkg;
                    it.InsertText = pkg;
                    it.Kind = 9;
                    it.Detail = "node_modules";
                    it.SortText = "2" + pkg;
                    ImportCompletionTag tag = new ImportCompletionTag();
                    tag.Source = "node_modules";
                    it.Tag = tag;
                    items.Add(it);
                    if (items.Count >= MaxItems) break;
                }
                return items;
            }

            // "pkg/sub/" or "@scope/" inside a package (or the baseUrl).
            if (dirPart.StartsWith("@", StringComparison.Ordinal) && dirPart.IndexOf('/') == dirPart.Length - 1)
            {
                string scope = dirPart.TrimEnd('/');
                foreach (string pkg in NodePackages.List(fileDir, root))
                {
                    if (!pkg.StartsWith(scope + "/", StringComparison.Ordinal)) continue;
                    string name = pkg.Substring(scope.Length + 1);
                    if (!seen.Add(name)) continue;
                    LspCompletionItem it = new LspCompletionItem();
                    it.Label = name;
                    it.InsertText = name;
                    it.Kind = 9;
                    it.Detail = "node_modules";
                    it.SortText = "2" + name;
                    ImportCompletionTag tag = new ImportCompletionTag();
                    tag.Source = "node_modules";
                    it.Tag = tag;
                    items.Add(it);
                }
                return items;
            }
            foreach (string nm in NodePackages.NodeModulesDirs(fileDir, root))
                AddDirectoryItems(items, seen, CombineSpec(nm, dirPart), ctx, filePath, segment, "node_modules", root);
            if (cfg != null && !string.IsNullOrEmpty(cfg.BaseUrl))
                AddDirectoryItems(items, seen, CombineSpec(cfg.BaseUrl, dirPart), ctx, filePath, segment, "baseUrl", root);
            return items;
        }

        internal static string CombineSpec(string baseDir, string rel)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            string p = baseDir;
            foreach (string part in (rel ?? "").Replace('\\', '/').Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                {
                    string up = null;
                    try { up = Path.GetDirectoryName(p.TrimEnd('/', '\\')); } catch { }
                    if (string.IsNullOrEmpty(up)) return null;
                    p = up;
                }
                else p = Path.Combine(p, part);
            }
            return p;
        }

        private static GitIgnoreRulesCache ignoreCache = new GitIgnoreRulesCache();

        private static void AddDirectoryItems(List<LspCompletionItem> items, HashSet<string> seen, string dir, ImportCompletionContext ctx,
            string currentFile, string segment, string source, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(dir)) return;
            List<DirectoryListingCache.Entry> entries = DirectoryListingCache.List(dir);
            if (entries == null) return;
            bool showDot = segment.StartsWith(".", StringComparison.Ordinal);
            string currentFull = null;
            try { currentFull = string.IsNullOrEmpty(currentFile) ? null : Path.GetFullPath(currentFile); } catch { }
            WorkspaceIgnoreRules rules = source == "node_modules" ? null : ignoreCache.For(workspaceRoot);
            foreach (DirectoryListingCache.Entry e in entries)
            {
                if (AlwaysHidden.Contains(e.Name)) continue;
                if (e.Name.StartsWith(".", StringComparison.Ordinal) && !showDot) continue;
                string full = Path.Combine(dir, e.Name);
                if (!e.IsDirectory && currentFull != null && string.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (rules != null && IsIgnored(rules, full, e.IsDirectory)) continue;
                string label = e.Name;
                if (!e.IsDirectory && ctx.OmitScriptExtensions)
                {
                    if (label.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || label.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || label.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase))
                        label = label.Substring(0, label.LastIndexOf(".d.", StringComparison.OrdinalIgnoreCase));
                    else if (ScriptExtensions.Contains(Path.GetExtension(label)))
                        label = Path.GetFileNameWithoutExtension(label);
                }
                string shown = e.IsDirectory ? label + "/" : label;
                if (!seen.Add(shown)) continue;
                LspCompletionItem it = new LspCompletionItem();
                it.Label = shown;
                it.FilterText = label;
                it.InsertText = shown;
                it.Kind = e.IsDirectory ? 19 : 17;
                it.SortText = (e.IsDirectory ? "0" : "1") + label.ToLowerInvariant();
                ImportCompletionTag tag = new ImportCompletionTag();
                tag.IsFolder = e.IsDirectory;
                tag.Retrigger = e.IsDirectory;
                tag.FullPath = full;
                tag.Source = source;
                it.Tag = tag;
                items.Add(it);
                if (items.Count >= MaxItems) return;
            }
        }

        private static bool IsIgnored(WorkspaceIgnoreRules rules, string full, bool isDir)
        {
            try
            {
                if (string.IsNullOrEmpty(rules.Root) || !full.StartsWith(rules.Root, StringComparison.OrdinalIgnoreCase)) return false;
                return isDir ? rules.IsExcludedDirectory(full) : rules.IsGitIgnoredEntry(full, false);
            }
            catch { return false; }
        }

        /// <summary>Drops every cached listing / config (also revalidated automatically by timestamps).</summary>
        public static void InvalidateCaches()
        {
            DirectoryListingCache.Clear();
            TsPathConfig.ClearCache();
            NodePackages.ClearCache();
            PythonModuleCompletion.ClearCache();
            ignoreCache = new GitIgnoreRulesCache();
        }

        /// <summary>Merges server items into path / module items: same label = one entry (the server's wins).</summary>
        public static List<LspCompletionItem> Merge(List<LspCompletionItem> ownItems, List<LspCompletionItem> serverItems)
        {
            List<LspCompletionItem> result = new List<LspCompletionItem>();
            HashSet<string> labels = new HashSet<string>(StringComparer.Ordinal);
            if (serverItems != null)
                foreach (LspCompletionItem s in serverItems)
                {
                    if (s == null) continue;
                    result.Add(s);
                    labels.Add(NormalizeLabel(s.Label));
                }
            if (ownItems != null)
                foreach (LspCompletionItem o in ownItems)
                    if (o != null && labels.Add(NormalizeLabel(o.Label))) result.Add(o);
            return result;
        }

        private static string NormalizeLabel(string l)
        {
            return (l ?? "").TrimEnd('/');
        }
    }

    /// <summary>One <see cref="WorkspaceIgnoreRules"/> per workspace root for path completion (git ignore + a narrow exclude list:
    /// build folders such as "packages", "dist" or "bin" stay importable).</summary>
    internal sealed class GitIgnoreRulesCache
    {
        private readonly object gate = new object();
        private string root;
        private WorkspaceIgnoreRules rules;
        private static readonly string[] Excluded = { ".git", "node_modules", "__pycache__", ".venv", ".tox", ".mypy_cache", ".pytest_cache" };

        public WorkspaceIgnoreRules For(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            lock (gate)
            {
                if (rules != null && string.Equals(root, workspaceRoot, StringComparison.OrdinalIgnoreCase)) return rules;
                try { rules = new WorkspaceIgnoreRules(workspaceRoot, Excluded, WorkspaceSearchEngine.RespectGitIgnore); }
                catch { rules = null; }
                root = workspaceRoot;
                return rules;
            }
        }
    }

    /// <summary>Thread-safe directory listing cache, revalidated by the directory's last-write time (changes when entries are added / removed / renamed).</summary>
    public static class DirectoryListingCache
    {
        public sealed class Entry
        {
            public string Name;
            public bool IsDirectory;
        }

        private sealed class Listing
        {
            public DateTime Stamp;
            public List<Entry> Entries;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Listing> cache = new Dictionary<string, Listing>(StringComparer.Ordinal);
        public static int Enumerations; // tests: how many real directory reads happened

        public static List<Entry> List(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            DateTime stamp;
            try
            {
                if (!Directory.Exists(dir)) return null;
                stamp = Directory.GetLastWriteTimeUtc(dir);
            }
            catch { return null; }
            lock (gate)
            {
                Listing l;
                if (cache.TryGetValue(dir, out l) && l.Stamp == stamp) return l.Entries;
            }
            List<Entry> entries = new List<Entry>();
            try
            {
                System.Threading.Interlocked.Increment(ref Enumerations);
                DirectoryInfo di = new DirectoryInfo(dir);
                foreach (FileSystemInfo fi in di.EnumerateFileSystemInfos())
                {
                    Entry e = new Entry();
                    e.Name = fi.Name;
                    e.IsDirectory = (fi.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(e);
                    if (entries.Count >= ImportCompletionEngine.MaxEntriesPerDirectory) break;
                }
            }
            catch { }
            entries.Sort(delegate (Entry a, Entry b)
            {
                if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            lock (gate)
            {
                if (cache.Count > 512) cache.Clear();
                Listing nl = new Listing();
                nl.Stamp = stamp;
                nl.Entries = entries;
                cache[dir] = nl;
            }
            return entries;
        }

        public static void Invalidate(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            lock (gate) { cache.Remove(dir); }
        }

        public static void Clear()
        {
            lock (gate) { cache.Clear(); }
        }
    }

    /// <summary>compilerOptions.baseUrl / paths of the nearest tsconfig.json or jsconfig.json (JSONC, one "extends" chain).</summary>
    public sealed class TsPathConfig
    {
        public string ConfigPath;
        /// <summary>Absolute baseUrl (null when not set).</summary>
        public string BaseUrl;
        /// <summary>Directory "paths" targets are relative to (baseUrl, else the config's folder).</summary>
        public string PathsBase;
        public List<KeyValuePair<string, List<string>>> Paths = new List<KeyValuePair<string, List<string>>>();

        private sealed class Cached
        {
            public DateTime Stamp;
            public TsPathConfig Config;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.Ordinal);

        public static void ClearCache() { lock (gate) { cache.Clear(); } }

        /// <summary>Nearest tsconfig.json / jsconfig.json from <paramref name="startDir"/> up to (and including) the workspace root.</summary>
        public static TsPathConfig Find(string startDir, string workspaceRoot)
        {
            string d = startDir;
            int guard = 0;
            while (!string.IsNullOrEmpty(d) && guard++ < 32)
            {
                foreach (string name in new[] { "tsconfig.json", "jsconfig.json" })
                {
                    string p = Path.Combine(d, name);
                    if (File.Exists(p)) return Load(p);
                }
                if (!string.IsNullOrEmpty(workspaceRoot) && string.Equals(d.TrimEnd('/', '\\'), workspaceRoot.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) break;
                string up;
                try { up = Path.GetDirectoryName(d); } catch { up = null; }
                if (up == d) break;
                d = up;
            }
            return null;
        }

        public static TsPathConfig Load(string configPath)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(configPath); } catch { return null; }
            lock (gate)
            {
                Cached c;
                if (cache.TryGetValue(configPath, out c) && c.Stamp == stamp) return c.Config;
            }
            TsPathConfig cfg = new TsPathConfig();
            cfg.ConfigPath = configPath;
            try { Apply(cfg, configPath, 0); } catch { }
            if (cfg.PathsBase == null) cfg.PathsBase = cfg.BaseUrl ?? Path.GetDirectoryName(configPath);
            lock (gate)
            {
                Cached nc = new Cached();
                nc.Stamp = stamp;
                nc.Config = cfg;
                cache[configPath] = nc;
            }
            return cfg;
        }

        private static void Apply(TsPathConfig cfg, string path, int depth)
        {
            if (depth > 4 || !File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            IDictionary<string, object> root = ser.DeserializeObject(HttpEnvironmentSet.StripJsonComments(File.ReadAllText(path))) as IDictionary<string, object>;
            if (root == null) return;
            object ext;
            if (root.TryGetValue("extends", out ext) && ext is string)
            {
                string e = (string)ext;
                if (e.StartsWith(".", StringComparison.Ordinal))
                {
                    string ep = Path.GetFullPath(Path.Combine(dir, e));
                    if (!ep.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !File.Exists(ep)) ep += ".json";
                    Apply(cfg, ep, depth + 1); // base first, then this file overrides
                }
            }
            object co;
            if (!root.TryGetValue("compilerOptions", out co)) return;
            IDictionary<string, object> opts = co as IDictionary<string, object>;
            if (opts == null) return;
            object bu;
            if (opts.TryGetValue("baseUrl", out bu) && bu is string)
            {
                try { cfg.BaseUrl = Path.GetFullPath(Path.Combine(dir, (string)bu)); } catch { }
                cfg.PathsBase = cfg.BaseUrl;
            }
            object paths;
            if (opts.TryGetValue("paths", out paths) && paths is IDictionary<string, object>)
            {
                if (cfg.BaseUrl == null) cfg.PathsBase = dir; // TS 4.1+: paths without baseUrl resolve from the config's folder
                cfg.Paths.Clear();
                foreach (KeyValuePair<string, object> kv in (IDictionary<string, object>)paths)
                {
                    List<string> targets = new List<string>();
                    System.Collections.IEnumerable arr = kv.Value as System.Collections.IEnumerable;
                    if (kv.Value is string) targets.Add((string)kv.Value);
                    else if (arr != null) foreach (object t in arr) if (t is string) targets.Add((string)t);
                    if (targets.Count > 0) cfg.Paths.Add(new KeyValuePair<string, List<string>>(kv.Key, targets));
                }
            }
        }
    }

    /// <summary>Package names for bare JS/TS specifiers: node_modules folders up the tree (scopes expanded) + package.json dependencies.</summary>
    public static class NodePackages
    {
        private sealed class Cached
        {
            public DateTime Stamp;
            public List<string> Names;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.Ordinal);
        public const int MaxPackages = 4000;

        public static void ClearCache() { lock (gate) { cache.Clear(); } }

        /// <summary>node_modules folders from <paramref name="startDir"/> upwards (stops above the workspace root's parent chain after 12 levels).</summary>
        public static List<string> NodeModulesDirs(string startDir, string workspaceRoot)
        {
            List<string> dirs = new List<string>();
            string d = startDir;
            int guard = 0;
            while (!string.IsNullOrEmpty(d) && guard++ < 12)
            {
                string nm = Path.Combine(d, "node_modules");
                if (Directory.Exists(nm)) dirs.Add(nm);
                string up;
                try { up = Path.GetDirectoryName(d); } catch { up = null; }
                if (up == d) break;
                d = up;
            }
            return dirs;
        }

        public static List<string> List(string startDir, string workspaceRoot)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string nm in NodeModulesDirs(startDir, workspaceRoot))
                foreach (string n in ListNodeModules(nm))
                    if (seen.Add(n)) { result.Add(n); if (result.Count >= MaxPackages) return result; }
            string pj = FindPackageJson(startDir, workspaceRoot);
            if (pj != null)
                foreach (string n in PackageJsonDependencies(pj))
                    if (seen.Add(n)) result.Add(n);
            return result;
        }

        private static string FindPackageJson(string startDir, string workspaceRoot)
        {
            string d = startDir;
            int guard = 0;
            while (!string.IsNullOrEmpty(d) && guard++ < 12)
            {
                string p = Path.Combine(d, "package.json");
                if (File.Exists(p)) return p;
                if (!string.IsNullOrEmpty(workspaceRoot) && string.Equals(d.TrimEnd('/', '\\'), workspaceRoot.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) break;
                string up;
                try { up = Path.GetDirectoryName(d); } catch { up = null; }
                if (up == d) break;
                d = up;
            }
            return null;
        }

        public static List<string> ListNodeModules(string nodeModulesDir)
        {
            DateTime stamp;
            try { stamp = Directory.GetLastWriteTimeUtc(nodeModulesDir); } catch { return new List<string>(); }
            lock (gate)
            {
                Cached c;
                if (cache.TryGetValue(nodeModulesDir, out c) && c.Stamp == stamp) return c.Names;
            }
            List<string> names = new List<string>();
            List<DirectoryListingCache.Entry> entries = DirectoryListingCache.List(nodeModulesDir);
            if (entries != null)
            {
                foreach (DirectoryListingCache.Entry e in entries)
                {
                    if (!e.IsDirectory || e.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (e.Name.StartsWith("@", StringComparison.Ordinal))
                    {
                        List<DirectoryListingCache.Entry> scoped = DirectoryListingCache.List(Path.Combine(nodeModulesDir, e.Name));
                        if (scoped != null)
                            foreach (DirectoryListingCache.Entry s in scoped)
                                if (s.IsDirectory && !s.Name.StartsWith(".", StringComparison.Ordinal)) names.Add(e.Name + "/" + s.Name);
                    }
                    else names.Add(e.Name);
                    if (names.Count >= MaxPackages) break;
                }
            }
            lock (gate)
            {
                Cached nc = new Cached();
                nc.Stamp = stamp;
                nc.Names = names;
                cache[nodeModulesDir] = nc;
            }
            return names;
        }

        public static List<string> PackageJsonDependencies(string packageJsonPath)
        {
            List<string> names = new List<string>();
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                IDictionary<string, object> root = ser.DeserializeObject(File.ReadAllText(packageJsonPath)) as IDictionary<string, object>;
                if (root == null) return names;
                foreach (string key in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
                {
                    object deps;
                    if (root.TryGetValue(key, out deps) && deps is IDictionary<string, object>)
                        foreach (string n in ((IDictionary<string, object>)deps).Keys) if (!names.Contains(n)) names.Add(n);
                }
            }
            catch { }
            return names;
        }
    }
}
