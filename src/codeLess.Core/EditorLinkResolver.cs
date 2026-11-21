using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>What a Cmd/Ctrl+Click (or F12) at a position in the editor should navigate to.</summary>
    public class EditorLinkTarget
    {
        public const string KindUrl = "url";
        public const string KindFile = "file";     // an import / include / path that resolved to a file
        public const string KindSymbol = "symbol"; // an identifier: go to its definition

        public string Kind = KindSymbol;
        /// <summary>Span on the clicked line (0-based, end exclusive) to underline while the modifier is held.</summary>
        public int StartCol;
        public int EndCol;
        public string Url;
        /// <summary>Existing file to open (null for a symbol that must be looked up in the workspace).</summary>
        public string FilePath;
        /// <summary>1-based line / column inside <see cref="FilePath"/>, 0 when unknown.</summary>
        public int Line;
        public int Column;
        /// <summary>Identifier to locate (inside <see cref="FilePath"/> when set, otherwise across the workspace).</summary>
        public string SymbolName;

        public bool SameSpan(EditorLinkTarget other)
        {
            return other != null && other.StartCol == StartCol && other.EndCol == EndCol && other.Kind == Kind;
        }
    }

    /// <summary>
    /// VS Code-style "follow link" resolution shared by the Windows (WinForms) and macOS/Linux (Avalonia) editors:
    /// URLs, JS/TS/CSS imports and require(), Python imports (absolute and relative), C/C++ #include, Rust `mod`,
    /// Markdown links, quoted or bare file paths (with :line:col), and imported identifiers (jumps to the
    /// definition inside the imported module). Pure file-system logic; compiled by legacy csc.exe, so C# 5 only.
    /// </summary>
    public static class EditorLinkResolver
    {
        private static readonly Regex UrlRegex = new Regex(@"\b(?:https?|file)://[^\s""'<>`)\]}]+", RegexOptions.Compiled);
        private static readonly Regex PyFromImport = new Regex(@"^\s*from\s+(\.*[\w\.]*)\s+import\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex PyImport = new Regex(@"^\s*import\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex CInclude = new Regex(@"^\s*#\s*(?:include|import)\s*([""<])([^"">]+)["">]", RegexOptions.Compiled);
        private static readonly Regex RustMod = new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?mod\s+([A-Za-z_]\w*)\s*;", RegexOptions.Compiled);
        private static readonly Regex MdLink = new Regex(@"\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
        private static readonly Regex LineColSuffix = new Regex(@"^(.*?)(?::(\d+)(?::(\d+))?|\((\d+)(?:,\s*(\d+))?\)|#L(\d+))$", RegexOptions.Compiled);
        private static readonly Regex JsFromSpec = new Regex(@"\bfrom\s*([""'])([^""']+)\1", RegexOptions.Compiled);
        private static readonly Regex JsRequireSpec = new Regex(@"\b(?:require|import)\s*\(\s*([""'])([^""']+)\1\s*\)", RegexOptions.Compiled);
        private static readonly Regex JsSideEffectImport = new Regex(@"^\s*import\s*([""'])([^""']+)\1", RegexOptions.Compiled);

        private static readonly string[] ScriptExtensions = new string[]
        {
            ".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts", ".json", ".vue", ".svelte",
            ".css", ".scss", ".sass", ".less", ".py", ".pyi", ".md", ".html"
        };

        private static readonly HashSet<string> NonNavigableWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "while", "do", "return", "break", "continue", "switch", "case", "default", "try", "catch",
            "finally", "throw", "new", "this", "self", "super", "base", "null", "nil", "None", "true", "false", "True", "False",
            "import", "from", "as", "export", "require", "using", "namespace", "package", "include", "public", "private",
            "protected", "internal", "static", "readonly", "const", "let", "var", "val", "def", "class", "struct", "enum",
            "interface", "function", "func", "fn", "async", "await", "yield", "void", "int", "string", "bool", "float",
            "double", "char", "long", "and", "or", "not", "in", "is", "with", "pass", "lambda", "elif", "end", "then"
        };

        /// <summary>Resolve what lies under (<paramref name="lineIdx"/>, <paramref name="col"/>). Returns null for whitespace / punctuation.</summary>
        public static EditorLinkTarget Resolve(IList<string> lines, int lineIdx, int col, string currentFilePath, string workspaceRoot)
        {
            if (lines == null || lineIdx < 0 || lineIdx >= lines.Count) return null;
            string line = lines[lineIdx] ?? "";
            if (line.Length == 0) return null;
            if (col < 0) col = 0;
            if (col >= line.Length) return null;
            string fileDir = SafeDirName(currentFilePath);

            // 1. URLs anywhere (comments, strings, Markdown).
            foreach (Match m in UrlRegex.Matches(line))
            {
                if (col >= m.Index && col < m.Index + m.Length)
                {
                    string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                    if (col >= m.Index + url.Length) break;
                    EditorLinkTarget t = new EditorLinkTarget();
                    t.Kind = EditorLinkTarget.KindUrl;
                    t.Url = url;
                    t.StartCol = m.Index;
                    t.EndCol = m.Index + url.Length;
                    return t;
                }
            }

            // 2. C / C++ / Objective-C #include / #import.
            Match inc = CInclude.Match(line);
            if (inc.Success && col >= inc.Groups[2].Index - 1 && col <= inc.Groups[2].Index + inc.Groups[2].Length)
            {
                string spec = inc.Groups[2].Value;
                string found = ResolveInclude(spec, inc.Groups[1].Value == "<", fileDir, workspaceRoot);
                if (found != null) return FileTarget(found, inc.Groups[2].Index, inc.Groups[2].Index + spec.Length, 0, 0);
            }

            // 3. Rust `mod foo;`.
            Match rm = RustMod.Match(line);
            if (rm.Success && col >= rm.Groups[1].Index && col < rm.Groups[1].Index + rm.Groups[1].Length)
            {
                string found = ResolveRustMod(rm.Groups[1].Value, currentFilePath);
                if (found != null) return FileTarget(found, rm.Groups[1].Index, rm.Groups[1].Index + rm.Groups[1].Length, 0, 0);
            }

            // 4. Python `from x.y import a, b` / `import x.y as z`.
            EditorLinkTarget py = ResolvePythonImportLine(line, col, currentFilePath, workspaceRoot);
            if (py != null) return py;

            // 5. Quoted string under the caret: JS/TS/CSS module specifiers, HTML src/href, any path literal.
            int qs, qe;
            if (TryGetQuotedSpan(line, col, out qs, out qe))
            {
                string spec = line.Substring(qs, qe - qs);
                EditorLinkTarget st = ResolvePathLike(spec, fileDir, workspaceRoot, true);
                if (st != null)
                {
                    st.StartCol = qs;
                    st.EndCol = qe;
                    return st;
                }
            }

            // 6. Markdown link target `[text](./path.md)`.
            foreach (Match m in MdLink.Matches(line))
            {
                Group g = m.Groups[1];
                if (col >= g.Index && col < g.Index + g.Length)
                {
                    EditorLinkTarget mt = ResolvePathLike(g.Value, fileDir, workspaceRoot, false);
                    if (mt != null)
                    {
                        mt.StartCol = g.Index;
                        mt.EndCol = g.Index + g.Length;
                        return mt;
                    }
                }
            }

            // 7. Identifier: imported names jump into the imported module, everything else is a definition lookup.
            int ws, we;
            if (TryGetIdentifierSpan(line, col, out ws, out we))
            {
                string word = line.Substring(ws, we - ws);
                if (char.IsDigit(word[0])) return null;

                // A bare path token such as src/app/main.ts:12 in a comment or README.
                int ts, te;
                if (TryGetPathTokenSpan(line, col, out ts, out te) && te - ts > word.Length)
                {
                    string tok = line.Substring(ts, te - ts);
                    if (tok.IndexOf('/') >= 0 || tok.IndexOf('\\') >= 0 || LineColSuffix.IsMatch(tok))
                    {
                        EditorLinkTarget pt = ResolvePathLike(tok, fileDir, workspaceRoot, false);
                        if (pt != null)
                        {
                            pt.StartCol = ts;
                            pt.EndCol = te;
                            return pt;
                        }
                    }
                }

                if (NonNavigableWords.Contains(word)) return null;
                EditorLinkTarget sym = new EditorLinkTarget();
                sym.Kind = EditorLinkTarget.KindSymbol;
                sym.SymbolName = word;
                sym.StartCol = ws;
                sym.EndCol = we;
                string modulePath = FindImportedModuleForName(lines, word, currentFilePath, workspaceRoot);
                if (modulePath != null) sym.FilePath = modulePath;
                return sym;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Imports of an identifier (so a usage of an imported name jumps into the module that defines it)
        // ---------------------------------------------------------------------------------------------------

        /// <summary>Finds the file of the module that imports <paramref name="name"/> into this file (JS/TS or Python), or null.</summary>
        public static string FindImportedModuleForName(IList<string> lines, string name, string currentFilePath, string workspaceRoot)
        {
            if (lines == null || string.IsNullOrEmpty(name)) return null;
            string fileDir = SafeDirName(currentFilePath);
            Regex word = new Regex(@"(?<![\w$])" + Regex.Escape(name) + @"(?![\w$])");
            int limit = Math.Min(lines.Count, 4000);
            for (int i = 0; i < limit; i++)
            {
                string ln = lines[i];
                if (string.IsNullOrEmpty(ln)) continue;
                string trimmed = ln.TrimStart();

                // Python (also the parenthesised multi-line form: from x import (\n a,\n b\n))
                Match pf = PyFromImport.Match(ln);
                if (pf.Success && trimmed.StartsWith("from "))
                {
                    string names = pf.Groups[2].Value;
                    int j = i;
                    if (names.IndexOf('(') >= 0)
                    {
                        while (names.IndexOf(')') < 0 && j + 1 < lines.Count && j - i < 60) { j++; names += " " + lines[j]; }
                    }
                    else
                    {
                        while (names.TrimEnd().EndsWith("\\") && j + 1 < lines.Count && j - i < 60) { j++; names = names.TrimEnd().TrimEnd('\\') + " " + lines[j]; }
                    }
                    if (word.IsMatch(names))
                    {
                        string module = pf.Groups[1].Value;
                        string asName = OriginalNameForAlias(names, name);
                        string sub = ResolvePythonModule(module.EndsWith(".") ? module + asName : module + "." + asName, currentFilePath, workspaceRoot);
                        if (sub != null) return sub;
                        string mod = ResolvePythonModule(module, currentFilePath, workspaceRoot);
                        if (mod != null) return mod;
                    }
                    i = j;
                    continue;
                }

                // JS / TS: the statement may span several lines (import {\n a,\n b\n} from './x').
                bool isStmt = trimmed.StartsWith("import") || trimmed.StartsWith("export");
                if (isStmt || (ln.IndexOf("require(", StringComparison.Ordinal) >= 0 && ln.IndexOf(name, StringComparison.Ordinal) >= 0))
                {
                    StringBuilder stmt = new StringBuilder(ln);
                    int j = i;
                    while (isStmt && !JsFromSpec.IsMatch(stmt.ToString()) && !JsRequireSpec.IsMatch(stmt.ToString()) &&
                           !JsSideEffectImport.IsMatch(stmt.ToString()) && j + 1 < lines.Count && j - i < 60 && stmt.ToString().IndexOf(';') < 0)
                    {
                        j++;
                        stmt.Append(' ').Append(lines[j]);
                    }
                    string s = stmt.ToString();
                    Match spec = JsFromSpec.Match(s);
                    if (!spec.Success) spec = JsRequireSpec.Match(s);
                    if (spec.Success)
                    {
                        string bindings = s.Substring(0, spec.Index);
                        if (word.IsMatch(bindings))
                        {
                            string resolved = ResolveModuleSpecifier(spec.Groups[2].Value, fileDir, workspaceRoot);
                            if (resolved != null) return resolved;
                        }
                    }
                    if (isStmt) i = Math.Max(i, j);
                }
            }
            return null;
        }

        private static string OriginalNameForAlias(string importedNames, string name)
        {
            // "a as b, c" -> clicking b means original a
            foreach (string part in importedNames.Replace("(", " ").Replace(")", " ").Split(','))
            {
                string[] bits = part.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length == 3 && bits[1] == "as" && bits[2] == name) return bits[0];
            }
            return name;
        }

        // ---------------------------------------------------------------------------------------------------
        // Python
        // ---------------------------------------------------------------------------------------------------

        private static EditorLinkTarget ResolvePythonImportLine(string line, int col, string currentFilePath, string workspaceRoot)
        {
            Match m = PyFromImport.Match(line);
            if (m.Success)
            {
                Group modG = m.Groups[1];
                string module = modG.Value;
                if (col >= modG.Index && col < modG.Index + modG.Length)
                {
                    // Open the module up to the clicked dotted segment (clicking `os` in `os.path` opens os).
                    int end = col;
                    while (end < modG.Index + modG.Length && line[end] != '.') end++;
                    string prefix = line.Substring(modG.Index, end - modG.Index);
                    string found = ResolvePythonModule(prefix, currentFilePath, workspaceRoot);
                    if (found == null) found = ResolvePythonModule(module, currentFilePath, workspaceRoot);
                    if (found != null) return FileTarget(found, modG.Index, end, 0, 0);
                    return null;
                }
                Group namesG = m.Groups[2];
                if (col >= namesG.Index)
                {
                    int ws, we;
                    if (!TryGetIdentifierSpan(line, col, out ws, out we)) return null;
                    string name = line.Substring(ws, we - ws);
                    if (name == "as" || name == "import") return null;
                    string original = OriginalNameForAlias(namesG.Value, name);
                    string sub = ResolvePythonModule(module.EndsWith(".") ? module + original : module + "." + original, currentFilePath, workspaceRoot);
                    if (sub != null) return FileTarget(sub, ws, we, 0, 0);
                    string mod = ResolvePythonModule(module, currentFilePath, workspaceRoot);
                    if (mod == null) return null;
                    EditorLinkTarget t = FileTarget(mod, ws, we, 0, 0);
                    t.SymbolName = original;
                    return t;
                }
                return null;
            }

            m = PyImport.Match(line);
            if (m.Success && line.TrimStart().StartsWith("import ") && line.IndexOf(" from ", StringComparison.Ordinal) < 0 &&
                line.IndexOf('{') < 0 && line.IndexOf('"') < 0 && line.IndexOf('\'') < 0)
            {
                // import a.b.c as d, e
                Group g = m.Groups[1];
                if (col < g.Index) return null;
                int s = col, e = col;
                while (s > g.Index && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] == '_' || line[s - 1] == '.')) s--;
                while (e < line.Length && (char.IsLetterOrDigit(line[e]) || line[e] == '_')) e++;
                if (e <= s) return null;
                string dotted = line.Substring(s, e - s);
                if (dotted == "as") return null;
                string found = ResolvePythonModule(dotted, currentFilePath, workspaceRoot);
                if (found != null)
                {
                    int segStart = col;
                    while (segStart > s && line[segStart - 1] != '.') segStart--;
                    return FileTarget(found, segStart, e, 0, 0);
                }
            }
            return null;
        }

        /// <summary>Resolve a dotted Python module ("pkg.mod", ".sibling", "..parent.mod") to a .py / __init__.py file.</summary>
        public static string ResolvePythonModule(string dotted, string currentFilePath, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(dotted)) return null;
            string fileDir = SafeDirName(currentFilePath);
            List<string> roots = new List<string>();
            string rel = dotted;
            if (dotted.StartsWith("."))
            {
                int dots = 0;
                while (dots < dotted.Length && dotted[dots] == '.') dots++;
                string baseDir = fileDir;
                for (int i = 1; i < dots && baseDir != null; i++) baseDir = SafeDirName(baseDir);
                if (baseDir == null) return null;
                roots.Add(baseDir);
                rel = dotted.Substring(dots);
                if (rel.Length == 0) return FirstExisting(Path.Combine(baseDir, "__init__.py"), Path.Combine(baseDir, "__init__.pyi"));
            }
            else
            {
                // The file's directory and its ancestors up to the workspace root, then common source roots.
                string d = fileDir;
                int guard = 0;
                while (d != null && guard++ < 32)
                {
                    roots.Add(d);
                    if (!string.IsNullOrEmpty(workspaceRoot) && PathEquals(d, workspaceRoot)) break;
                    d = SafeDirName(d);
                }
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    roots.Add(workspaceRoot);
                    roots.Add(Path.Combine(workspaceRoot, "src"));
                    roots.Add(Path.Combine(workspaceRoot, "lib"));
                    roots.AddRange(FindVenvSitePackages(workspaceRoot));
                }
            }

            string relPath = rel.Replace('.', Path.DirectorySeparatorChar);
            foreach (string r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string baseP = Path.Combine(r, relPath);
                string f = FirstExisting(baseP + ".py", baseP + ".pyi", Path.Combine(baseP, "__init__.py"), Path.Combine(baseP, "__init__.pyi"));
                if (f != null) return f;
            }
            return null;
        }

        private static List<string> FindVenvSitePackages(string workspaceRoot)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string venvName in new string[] { ".venv", "venv", "env" })
                {
                    string venv = Path.Combine(workspaceRoot, venvName);
                    if (!File.Exists(Path.Combine(venv, "pyvenv.cfg"))) continue;
                    string winSite = Path.Combine(Path.Combine(venv, "Lib"), "site-packages");
                    if (Directory.Exists(winSite)) result.Add(winSite);
                    string lib = Path.Combine(venv, "lib");
                    if (Directory.Exists(lib))
                    {
                        foreach (string py in Directory.GetDirectories(lib, "python*"))
                        {
                            string sp = Path.Combine(py, "site-packages");
                            if (Directory.Exists(sp)) result.Add(sp);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        // ---------------------------------------------------------------------------------------------------
        // JS / TS / CSS module specifiers and plain paths
        // ---------------------------------------------------------------------------------------------------

        private static EditorLinkTarget ResolvePathLike(string text, string fileDir, string workspaceRoot, bool allowModuleSpecifier)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 400) return null;
            string spec = text.Trim();
            if (spec.Length == 0 || spec.IndexOf('\n') >= 0) return null;
            if (UrlRegex.IsMatch(spec) && spec.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                EditorLinkTarget u = new EditorLinkTarget();
                u.Kind = EditorLinkTarget.KindUrl;
                u.Url = spec;
                return u;
            }

            int line = 0, column = 0;
            string pathPart = spec;
            Match lc = LineColSuffix.Match(spec);
            if (lc.Success && lc.Groups[1].Value.Length > 0)
            {
                string l = lc.Groups[2].Success ? lc.Groups[2].Value : (lc.Groups[4].Success ? lc.Groups[4].Value : lc.Groups[6].Value);
                string c = lc.Groups[3].Success ? lc.Groups[3].Value : lc.Groups[5].Value;
                int li, ci;
                if (int.TryParse(l, out li)) { line = li; pathPart = lc.Groups[1].Value; }
                if (int.TryParse(c, out ci)) column = ci;
            }

            string found = allowModuleSpecifier
                ? ResolveModuleSpecifier(pathPart, fileDir, workspaceRoot)
                : ResolvePlainPath(pathPart, fileDir, workspaceRoot);
            if (found == null && !ReferenceEquals(pathPart, spec))
            {
                found = allowModuleSpecifier ? ResolveModuleSpecifier(spec, fileDir, workspaceRoot) : ResolvePlainPath(spec, fileDir, workspaceRoot);
                if (found != null) { line = 0; column = 0; }
            }
            if (found == null) return null;
            return FileTarget(found, 0, 0, line, column);
        }

        /// <summary>Resolve an import / require / CSS @import specifier the way Node, TypeScript, bundlers and Sass do.</summary>
        public static string ResolveModuleSpecifier(string spec, string fileDir, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            int cut = spec.IndexOfAny(new char[] { '?', '#' });
            if (cut > 0) spec = spec.Substring(0, cut);
            if (spec.StartsWith("node:") || spec.StartsWith("data:")) return null;

            string plain = ResolvePlainPath(spec, fileDir, workspaceRoot);
            if (plain != null) return plain;

            List<string> candidates = new List<string>();
            bool relative = spec.StartsWith("./") || spec.StartsWith("../") || spec == "." || spec == ".." ||
                            spec.StartsWith(".\\") || spec.StartsWith("..\\");
            if (relative)
            {
                if (fileDir != null) candidates.Add(CombineSafe(fileDir, spec));
            }
            else if (IsRootedPath(spec))
            {
                candidates.Add(spec);
                if (!string.IsNullOrEmpty(workspaceRoot)) candidates.Add(CombineSafe(workspaceRoot, spec.TrimStart('/', '\\')));
            }
            else
            {
                // Common path aliases (@/x, ~/x, src/x), then the workspace root, then node_modules packages.
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    if (spec.StartsWith("@/") || spec.StartsWith("~/") || spec.StartsWith("#/"))
                    {
                        string rest = spec.Substring(2);
                        candidates.Add(CombineSafe(Path.Combine(workspaceRoot, "src"), rest));
                        candidates.Add(CombineSafe(workspaceRoot, rest));
                        candidates.Add(CombineSafe(Path.Combine(workspaceRoot, "app"), rest));
                    }
                    candidates.Add(CombineSafe(workspaceRoot, spec));
                    candidates.Add(CombineSafe(Path.Combine(workspaceRoot, "src"), spec));
                }
                if (fileDir != null) candidates.Add(CombineSafe(fileDir, spec)); // CSS / Sass / HTML resolve bare names relative to the file
            }

            foreach (string c in candidates)
            {
                string f = ResolveFileWithExtensions(c);
                if (f != null) return f;
            }

            if (!relative && !IsRootedPath(spec)) return ResolveNodeModule(spec, fileDir, workspaceRoot);
            return null;
        }

        private static string ResolveNodeModule(string spec, string fileDir, string workspaceRoot)
        {
            string pkgName = spec;
            string subPath = "";
            string[] parts = spec.Split('/');
            if (spec.StartsWith("@") && parts.Length >= 2)
            {
                pkgName = parts[0] + "/" + parts[1];
                subPath = parts.Length > 2 ? string.Join("/", parts, 2, parts.Length - 2) : "";
            }
            else if (parts.Length >= 1)
            {
                pkgName = parts[0];
                subPath = parts.Length > 1 ? string.Join("/", parts, 1, parts.Length - 1) : "";
            }
            if (pkgName.Length == 0) return null;

            string dir = fileDir ?? workspaceRoot;
            int guard = 0;
            while (!string.IsNullOrEmpty(dir) && guard++ < 40)
            {
                string pkgDir = CombineSafe(Path.Combine(dir, "node_modules"), pkgName);
                if (pkgDir != null && Directory.Exists(pkgDir))
                {
                    if (subPath.Length > 0)
                    {
                        string f = ResolveFileWithExtensions(CombineSafe(pkgDir, subPath));
                        if (f != null) return f;
                    }
                    else
                    {
                        string f = ResolveFileWithExtensions(pkgDir);
                        if (f != null) return f;
                        string pj = Path.Combine(pkgDir, "package.json");
                        if (File.Exists(pj)) return pj;
                    }
                }
                // @types fallback for packages that ship no typings
                string typesDir = CombineSafe(Path.Combine(Path.Combine(dir, "node_modules"), "@types"), pkgName.StartsWith("@") ? pkgName.Substring(1).Replace("/", "__") : pkgName);
                if (typesDir != null && Directory.Exists(typesDir))
                {
                    string f = FirstExisting(Path.Combine(typesDir, "index.d.ts"));
                    if (f != null) return f;
                }
                if (!string.IsNullOrEmpty(workspaceRoot) && PathEquals(dir, workspaceRoot)) break;
                dir = SafeDirName(dir);
            }
            return null;
        }

        /// <summary>Existing file for a path, trying the common extensions, directory index files and package.json entry points.</summary>
        public static string ResolveFileWithExtensions(string basePath)
        {
            if (string.IsNullOrEmpty(basePath)) return null;
            try
            {
                if (File.Exists(basePath)) return Path.GetFullPath(basePath);
                foreach (string ext in ScriptExtensions)
                {
                    if (File.Exists(basePath + ext)) return Path.GetFullPath(basePath + ext);
                }
                // TypeScript ESM: import './x.js' compiles from x.ts
                string ext0 = Path.GetExtension(basePath).ToLowerInvariant();
                if (ext0 == ".js" || ext0 == ".jsx" || ext0 == ".mjs" || ext0 == ".cjs")
                {
                    string stem = basePath.Substring(0, basePath.Length - ext0.Length);
                    string ts = FirstExisting(stem + ".ts", stem + ".tsx", stem + ".mts", stem + ".cts");
                    if (ts != null) return ts;
                }
                // Sass partials: @use 'base' -> _base.scss
                string dir = Path.GetDirectoryName(basePath);
                string name = Path.GetFileName(basePath);
                if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name) && !name.StartsWith("_"))
                {
                    string partial = Path.Combine(dir, "_" + name);
                    string pf = FirstExisting(partial, partial + ".scss", partial + ".sass", partial + ".css");
                    if (pf != null) return pf;
                }
                if (Directory.Exists(basePath))
                {
                    string pj = Path.Combine(basePath, "package.json");
                    if (File.Exists(pj))
                    {
                        string entry = ReadPackageEntry(pj);
                        if (!string.IsNullOrEmpty(entry))
                        {
                            string ef = CombineSafe(basePath, entry);
                            if (ef != null)
                            {
                                if (File.Exists(ef)) return Path.GetFullPath(ef);
                                foreach (string ext in ScriptExtensions)
                                    if (File.Exists(ef + ext)) return Path.GetFullPath(ef + ext);
                            }
                        }
                    }
                    foreach (string idx in new string[] { "index.ts", "index.tsx", "index.d.ts", "index.js", "index.jsx", "index.mjs", "index.cjs", "__init__.py", "mod.rs", "index.scss", "_index.scss", "index.css" })
                    {
                        string f = Path.Combine(basePath, idx);
                        if (File.Exists(f)) return Path.GetFullPath(f);
                    }
                }
            }
            catch { }
            return null;
        }

        private static readonly Regex PackageEntryRegex = new Regex(@"""(types|typings|module|main)""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

        private static string ReadPackageEntry(string packageJsonPath)
        {
            try
            {
                FileInfo fi = new FileInfo(packageJsonPath);
                if (fi.Length > 512 * 1024) return null;
                string json = File.ReadAllText(packageJsonPath);
                string best = null;
                int bestRank = 99;
                foreach (Match m in PackageEntryRegex.Matches(json))
                {
                    string key = m.Groups[1].Value;
                    int rank = key == "types" || key == "typings" ? 0 : (key == "module" ? 1 : 2);
                    if (rank < bestRank) { bestRank = rank; best = m.Groups[2].Value; }
                }
                return best;
            }
            catch { return null; }
        }

        private static string ResolvePlainPath(string path, string fileDir, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Trim();
            if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { p = new Uri(p).LocalPath; } catch { return null; }
            }
            if (p.StartsWith("~/") || p.StartsWith("~\\"))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) return null;
                p = Path.Combine(home, p.Substring(2));
            }
            if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            try
            {
                if (IsRootedPath(p))
                {
                    if (File.Exists(p)) return Path.GetFullPath(p);
                    if (!string.IsNullOrEmpty(workspaceRoot))
                    {
                        string wr = CombineSafe(workspaceRoot, p.TrimStart('/', '\\'));
                        if (wr != null && File.Exists(wr)) return Path.GetFullPath(wr);
                    }
                    return null;
                }
                if (fileDir != null)
                {
                    string f = CombineSafe(fileDir, p);
                    if (f != null && File.Exists(f)) return Path.GetFullPath(f);
                }
                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    string f = CombineSafe(workspaceRoot, p);
                    if (f != null && File.Exists(f)) return Path.GetFullPath(f);
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------
        // C / Rust
        // ---------------------------------------------------------------------------------------------------

        private static string ResolveInclude(string spec, bool angle, string fileDir, string workspaceRoot)
        {
            List<string> dirs = new List<string>();
            if (!angle && fileDir != null) dirs.Add(fileDir);
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                dirs.Add(workspaceRoot);
                dirs.Add(Path.Combine(workspaceRoot, "include"));
                dirs.Add(Path.Combine(workspaceRoot, "src"));
                dirs.Add(Path.Combine(workspaceRoot, "inc"));
            }
            if (angle && fileDir != null) dirs.Add(fileDir);
            if (Path.DirectorySeparatorChar == '/')
            {
                dirs.Add("/usr/local/include");
                dirs.Add("/usr/include");
                dirs.Add("/opt/homebrew/include");
            }
            foreach (string d in dirs)
            {
                string f = CombineSafe(d, spec);
                if (f != null && File.Exists(f)) return Path.GetFullPath(f);
            }
            return null;
        }

        private static string ResolveRustMod(string name, string currentFilePath)
        {
            string dir = SafeDirName(currentFilePath);
            if (dir == null) return null;
            string stem = Path.GetFileNameWithoutExtension(currentFilePath);
            List<string> bases = new List<string>();
            if (stem == "mod" || stem == "lib" || stem == "main") bases.Add(dir);
            else { bases.Add(Path.Combine(dir, stem)); bases.Add(dir); }
            foreach (string b in bases)
            {
                string f = FirstExisting(Path.Combine(b, name + ".rs"), Path.Combine(Path.Combine(b, name), "mod.rs"));
                if (f != null) return f;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Definition lookup inside a file
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Best-effort 0-based line / column of the definition of <paramref name="name"/> in <paramref name="lines"/>:
        /// class / function / variable / type declarations across common languages first, then the first whole-word
        /// occurrence that is not an import. Returns false when the name does not occur.
        /// </summary>
        public static bool FindDefinitionInLines(IList<string> lines, string name, out int line, out int col)
        {
            return FindDefinitionInLines(lines, name, true, out line, out col);
        }

        /// <summary>As above; with <paramref name="allowPlainOccurrence"/> false only declaration patterns count.</summary>
        public static bool FindDefinitionInLines(IList<string> lines, string name, bool allowPlainOccurrence, out int line, out int col)
        {
            line = -1; col = -1;
            if (lines == null || string.IsNullOrEmpty(name)) return false;
            string n = Regex.Escape(name);
            Regex[] patterns = new Regex[]
            {
                new Regex(@"\b(?:class|interface|enum|struct|trait|type|typedef|record|object|protocol|module|namespace|union|impl)\s+" + n + @"\b"),
                new Regex(@"\b(?:def|function|func|fn|sub|proc|macro_rules!)\s*\*?\s*" + n + @"\b"),
                new Regex(@"\b(?:const|let|var|val|static)\s+(?:mut\s+)?" + n + @"\b"),
                new Regex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?" + n + @"\s*(?:=|:\s*[\w<\[]|\()"),
                new Regex(@"^\s*" + n + @"\s*=(?!=)"),
                new Regex(@"^\s*(?:[\w<>\[\],\.?]+\s+)+" + n + @"\s*\("),
                new Regex(@"^\s*#\s*define\s+" + n + @"\b")
            };
            int limit = Math.Min(lines.Count, 200000);
            foreach (Regex rx in patterns)
            {
                for (int i = 0; i < limit; i++)
                {
                    string ln = lines[i];
                    if (string.IsNullOrEmpty(ln) || ln.IndexOf(name, StringComparison.Ordinal) < 0) continue;
                    Match m = rx.Match(ln);
                    if (!m.Success) continue;
                    string t = ln.TrimStart();
                    if (t.StartsWith("import ") || t.StartsWith("from ") || t.StartsWith("return ") || t.StartsWith("//") || t.StartsWith("#include")) continue;
                    int at = IndexOfWord(ln, name, m.Index);
                    if (at < 0) continue;
                    line = i; col = at;
                    return true;
                }
            }
            if (!allowPlainOccurrence) return false;
            for (int i = 0; i < limit; i++)
            {
                string ln = lines[i];
                if (string.IsNullOrEmpty(ln)) continue;
                string t = ln.TrimStart();
                if (t.StartsWith("import ") || t.StartsWith("from ") || t.StartsWith("using ")) continue;
                int at = IndexOfWord(ln, name, 0);
                if (at >= 0) { line = i; col = at; return true; }
            }
            return false;
        }

        private static int IndexOfWord(string s, string word, int from)
        {
            int i = Math.Max(0, from);
            while (i <= s.Length - word.Length)
            {
                int at = s.IndexOf(word, i, StringComparison.Ordinal);
                if (at < 0) return -1;
                bool leftOk = at == 0 || !(char.IsLetterOrDigit(s[at - 1]) || s[at - 1] == '_' || s[at - 1] == '$');
                int end = at + word.Length;
                bool rightOk = end >= s.Length || !(char.IsLetterOrDigit(s[end]) || s[end] == '_' || s[end] == '$');
                if (leftOk && rightOk) return at;
                i = at + 1;
            }
            return -1;
        }

        /// <summary>Opens an http(s) URL in the default browser (other schemes are refused). Returns true when launched.</summary>
        public static bool OpenExternalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            string safe = parsed.AbsoluteUri;
            try
            {
                System.Diagnostics.ProcessStartInfo psi;
                if (Path.DirectorySeparatorChar == '\\')
                {
                    psi = new System.Diagnostics.ProcessStartInfo(safe);
                    psi.UseShellExecute = true;
                }
                else
                {
                    bool mac = Directory.Exists("/System/Library/CoreServices") && File.Exists("/usr/bin/open");
                    psi = new System.Diagnostics.ProcessStartInfo(mac ? "/usr/bin/open" : "xdg-open");
                    psi.UseShellExecute = false;
                    psi.Arguments = "\"" + safe.Replace("\"", "%22") + "\"";
                }
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------------------------------------------
        // Span helpers
        // ---------------------------------------------------------------------------------------------------

        public static bool TryGetIdentifierSpan(string line, int col, out int start, out int end)
        {
            start = end = col;
            if (string.IsNullOrEmpty(line) || col < 0 || col >= line.Length) return false;
            if (!IsIdentChar(line[col]))
            {
                // Clicking just after the last character of a word still targets that word.
                if (col > 0 && IsIdentChar(line[col - 1])) col--;
                else return false;
            }
            start = col; end = col;
            while (start > 0 && IsIdentChar(line[start - 1])) start--;
            while (end < line.Length && IsIdentChar(line[end])) end++;
            return end > start;
        }

        private static bool IsIdentChar(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '$'; }

        private static bool TryGetQuotedSpan(string line, int col, out int start, out int end)
        {
            start = end = -1;
            // Walk the line tracking string state so escaped quotes and quotes inside other strings are respected.
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"' || c == '\'' || c == '`')
                {
                    int s = i + 1;
                    int j = s;
                    while (j < line.Length && line[j] != c)
                    {
                        if (line[j] == '\\') j++;
                        j++;
                    }
                    if (j >= line.Length) return false;
                    if (col >= s && col < j && j > s)
                    {
                        start = s; end = j;
                        return true;
                    }
                    if (col < s) return false;
                    i = j + 1;
                    continue;
                }
                i++;
            }
            return false;
        }

        private static bool TryGetPathTokenSpan(string line, int col, out int start, out int end)
        {
            start = end = col;
            if (col < 0 || col >= line.Length || IsPathBreak(line[col])) return false;
            start = col; end = col;
            while (start > 0 && !IsPathBreak(line[start - 1])) start--;
            while (end < line.Length && !IsPathBreak(line[end])) end++;
            while (end > start && ".,;:!?".IndexOf(line[end - 1]) >= 0) end--;
            return end > start;
        }

        private static bool IsPathBreak(char c)
        {
            return char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '`' || c == '<' || c == '>' || c == '|' ||
                   c == '[' || c == ']' || c == '{' || c == '}' || c == '=' || c == ',' || c == ';';
        }

        private static EditorLinkTarget FileTarget(string path, int start, int end, int line, int col)
        {
            EditorLinkTarget t = new EditorLinkTarget();
            t.Kind = EditorLinkTarget.KindFile;
            t.FilePath = path;
            t.StartCol = start;
            t.EndCol = end;
            t.Line = line;
            t.Column = col;
            return t;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return Path.GetFullPath(p); }
                catch { }
            }
            return null;
        }

        private static string SafeDirName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string d = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(d) ? null : d;
            }
            catch { return null; }
        }

        private static string CombineSafe(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return null;
            try
            {
                string bb = Path.DirectorySeparatorChar == '/' ? b.Replace('\\', '/') : b.Replace('/', '\\');
                return Path.GetFullPath(Path.Combine(a, bb));
            }
            catch { return null; }
        }

        private static bool IsRootedPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) return true;
            if (Path.DirectorySeparatorChar == '/' && p[0] == '/') return true;
            return p.StartsWith("\\\\");
        }

        private static bool PathEquals(string a, string b)
        {
            try
            {
                string fa = Path.GetFullPath(a).TrimEnd('/', '\\');
                string fb = Path.GetFullPath(b).TrimEnd('/', '\\');
                return string.Equals(fa, fb, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }
    }
}
