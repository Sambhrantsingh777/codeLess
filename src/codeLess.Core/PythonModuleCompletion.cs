using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// Python import completion (strict C# 5): module names after "import " / "from " and names after
    /// "from module import ". Sources: workspace modules / packages (also relative imports with leading dots), the
    /// selected interpreter's site-packages and the standard library (its folder when found, otherwise a static list).
    /// Only reads files; never runs the interpreter (Workspace Trust / Restricted Mode safe).
    /// </summary>
    public static class PythonModuleCompletion
    {
        /// <summary>Test hook: overrides the environment layout (site-packages / stdlib folders / version).</summary>
        public static Func<string, PythonEnvironmentLayout> LayoutOverride;

        private static readonly Regex Identifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Assignment = new Regex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*(?::[^=]+)?=(?!=)", RegexOptions.CultureInvariant);
        private static readonly Regex FromImportNames = new Regex(@"^from\s+[\w.]+\s+import\s+\(?([^#)]*)", RegexOptions.CultureInvariant);
        private static readonly Regex AllList = new Regex(@"^__all__\s*(?::[^=]+)?=\s*[\[(]([^\])]*)", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "__pycache__", "node_modules", ".git", ".venv", "venv", ".tox", ".mypy_cache", ".pytest_cache", "site-packages", "dist-packages", ".eggs", "build", "dist" };

        private sealed class NamesCache
        {
            public DateTime Stamp;
            public long Length;
            public List<KeyValuePair<string, int>> Names;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, NamesCache> namesCache = new Dictionary<string, NamesCache>(StringComparer.Ordinal);

        public static void ClearCache()
        {
            lock (gate) { namesCache.Clear(); }
            PythonEnvironmentLayout.ClearCache();
        }

        private static string DirOf(string filePath)
        {
            try { return string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch { return null; }
        }

        private sealed class SearchRoot
        {
            public string Dir;
            public string Source;
            public SearchRoot(string d, string s) { Dir = d; Source = s; }
        }

        /// <summary>Folders absolute imports resolve from: the file's folder (script mode), the workspace root and its src/, site-packages, stdlib.</summary>
        private static List<SearchRoot> AbsoluteRoots(string filePath, string workspaceRoot, PythonEnvironmentLayout layout)
        {
            List<SearchRoot> roots = new List<SearchRoot>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fileDir = DirOf(filePath);
            Action<string, string> add = delegate (string d, string src)
            {
                if (string.IsNullOrEmpty(d)) return;
                string n = d.TrimEnd('/', '\\');
                if (Directory.Exists(n) && seen.Add(n)) roots.Add(new SearchRoot(n, src));
            };
            add(fileDir, "workspace");
            add(workspaceRoot, "workspace");
            if (!string.IsNullOrEmpty(workspaceRoot)) add(Path.Combine(workspaceRoot, "src"), "workspace");
            if (layout != null)
            {
                foreach (string sp in layout.SitePackages) add(sp, "site-packages");
                add(layout.StdlibDir, "stdlib");
            }
            return roots;
        }

        /// <summary>Base folder of a relative import ("." = the file's package, ".." = its parent, ...).</summary>
        private static string RelativeBase(string filePath, int dots)
        {
            string d = DirOf(filePath);
            for (int i = 1; i < dots && !string.IsNullOrEmpty(d); i++)
            {
                try { d = Path.GetDirectoryName(d); } catch { d = null; }
            }
            return d;
        }

        private static int LeadingDots(string s, out string rest)
        {
            int n = 0;
            while (n < s.Length && s[n] == '.') n++;
            rest = s.Substring(n);
            return n;
        }

        public static List<LspCompletionItem> BuildModuleItems(ImportCompletionContext ctx, string filePath, string workspaceRoot)
        {
            List<LspCompletionItem> items = new List<LspCompletionItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string rest;
            int dots = LeadingDots(ctx.Specifier, out rest);
            int lastDot = rest.LastIndexOf('.');
            string package = lastDot >= 0 ? rest.Substring(0, lastDot) : "";
            string[] pkgParts = package.Length > 0 ? package.Split('.') : new string[0];
            if (dots > 0)
            {
                string baseDir = RelativeBase(filePath, dots);
                if (baseDir == null) return items;
                AddModulesIn(items, seen, Combine(baseDir, pkgParts), "relative", filePath);
                return items;
            }
            PythonEnvironmentLayout layout = GetLayout(workspaceRoot);
            foreach (SearchRoot r in AbsoluteRoots(filePath, workspaceRoot, layout))
                AddModulesIn(items, seen, Combine(r.Dir, pkgParts), r.Source, filePath);
            if (pkgParts.Length == 0)
            {
                foreach (string m in PythonStdlibModules.TopLevel(layout != null ? layout.Version : null))
                    if (seen.Add(m)) items.Add(ModuleItem(m, "stdlib", null));
            }
            else if (pkgParts.Length == 1 && (layout == null || layout.StdlibDir == null))
            {
                foreach (string m in PythonStdlibModules.Submodules(pkgParts[0]))
                    if (seen.Add(m)) items.Add(ModuleItem(m, "stdlib", null));
            }
            return items;
        }

        public static List<LspCompletionItem> BuildNameItems(ImportCompletionContext ctx, string filePath, string workspaceRoot)
        {
            List<LspCompletionItem> items = new List<LspCompletionItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string rest;
            int dots = LeadingDots(ctx.PythonModule ?? "", out rest);
            string[] parts = rest.Length > 0 ? rest.Split('.') : new string[0];
            List<SearchRoot> roots;
            if (dots > 0)
            {
                roots = new List<SearchRoot>();
                string b = RelativeBase(filePath, dots);
                if (b != null) roots.Add(new SearchRoot(b, "relative"));
            }
            else roots = AbsoluteRoots(filePath, workspaceRoot, GetLayout(workspaceRoot));
            foreach (SearchRoot r in roots)
            {
                string target = Combine(r.Dir, parts);
                if (target == null) continue;
                bool found = false;
                if (parts.Length == 0 || Directory.Exists(target))
                {
                    // package: its submodules + the names its __init__ defines / re-exports
                    if (Directory.Exists(target))
                    {
                        AddModulesIn(items, seen, target, r.Source, filePath);
                        string init = FirstExisting(Path.Combine(target, "__init__.py"), Path.Combine(target, "__init__.pyi"));
                        if (init != null) AddNames(items, seen, init);
                        found = true;
                    }
                }
                if (!found && parts.Length > 0)
                {
                    string file = FirstExisting(target + ".pyi", target + ".py");
                    if (file != null) { AddNames(items, seen, file); found = true; }
                }
                if (found && r.Source != "workspace") break; // first match on sys.path wins
            }
            if (items.Count == 0 && dots == 0 && parts.Length == 1)
                foreach (string m in PythonStdlibModules.Submodules(parts[0]))
                    if (seen.Add(m)) items.Add(ModuleItem(m, "stdlib", null));
            return items;
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string p in paths) { try { if (File.Exists(p)) return p; } catch { } }
            return null;
        }

        private static string Combine(string baseDir, string[] parts)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            string p = baseDir;
            foreach (string part in parts)
            {
                if (!Identifier.IsMatch(part)) return null;
                p = Path.Combine(p, part);
            }
            return p;
        }

        private static LspCompletionItem ModuleItem(string name, string source, string fullPath)
        {
            LspCompletionItem it = new LspCompletionItem();
            it.Label = name;
            it.InsertText = name;
            it.Kind = 9;
            it.Detail = source;
            it.SortText = (source == "workspace" || source == "relative" ? "0" : source == "site-packages" ? "1" : "2") + name.ToLowerInvariant();
            ImportCompletionTag tag = new ImportCompletionTag();
            tag.Source = source;
            tag.FullPath = fullPath;
            it.Tag = tag;
            return it;
        }

        /// <summary>Modules and packages directly inside <paramref name="dir"/>.</summary>
        private static void AddModulesIn(List<LspCompletionItem> items, HashSet<string> seen, string dir, string source, string currentFile)
        {
            if (string.IsNullOrEmpty(dir)) return;
            List<DirectoryListingCache.Entry> entries = DirectoryListingCache.List(dir);
            if (entries == null) return;
            bool sitePackages = source == "site-packages" || source == "stdlib";
            string currentFull = null;
            try { currentFull = string.IsNullOrEmpty(currentFile) ? null : Path.GetFullPath(currentFile); } catch { }
            foreach (DirectoryListingCache.Entry e in entries)
            {
                string name = null;
                string full = Path.Combine(dir, e.Name);
                if (e.IsDirectory)
                {
                    if (SkipDirs.Contains(e.Name) || !Identifier.IsMatch(e.Name)) continue;
                    // regular packages, or namespace packages (site-packages / stdlib folders; workspace folders holding .py files)
                    if (!sitePackages && !LooksLikePackage(full)) continue;
                    name = e.Name;
                }
                else
                {
                    string ext = Path.GetExtension(e.Name).ToLowerInvariant();
                    if (ext == ".py" || ext == ".pyi" || ext == ".pyw")
                    {
                        name = Path.GetFileNameWithoutExtension(e.Name);
                        if (source == "relative" && currentFull != null && string.Equals(full, currentFull, StringComparison.OrdinalIgnoreCase)) continue; // "from . import" never offers the module itself
                    }
                    else if (ext == ".so" || ext == ".pyd")
                    {
                        int dot = e.Name.IndexOf('.');
                        name = dot > 0 ? e.Name.Substring(0, dot) : null;
                    }
                    if (name == null || name == "__init__" || name == "__main__" || !Identifier.IsMatch(name)) continue;
                }
                if (!seen.Add(name)) continue;
                items.Add(ModuleItem(name, source, full));
                if (items.Count >= ImportCompletionEngine.MaxItems) return;
            }
        }

        private static bool LooksLikePackage(string dir)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "__init__.py")) || File.Exists(Path.Combine(dir, "__init__.pyi"))) return true;
                List<DirectoryListingCache.Entry> entries = DirectoryListingCache.List(dir);
                if (entries == null) return false;
                foreach (DirectoryListingCache.Entry e in entries)
                    if (!e.IsDirectory && (e.Name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase))) return true;
            }
            catch { }
            return false;
        }

        private static void AddNames(List<LspCompletionItem> items, HashSet<string> seen, string file)
        {
            foreach (KeyValuePair<string, int> kv in TopLevelNames(file))
            {
                if (!seen.Add(kv.Key)) continue;
                LspCompletionItem it = new LspCompletionItem();
                it.Label = kv.Key;
                it.InsertText = kv.Key;
                it.Kind = kv.Value;
                it.Detail = Path.GetFileName(file);
                it.SortText = "0" + kv.Key.ToLowerInvariant();
                ImportCompletionTag tag = new ImportCompletionTag();
                tag.Source = "symbol";
                tag.FullPath = file;
                it.Tag = tag;
                items.Add(it);
            }
        }

        /// <summary>
        /// Public top-level names of a module file (name, LSP kind): def / class via <see cref="HeuristicSymbolExtractor"/>,
        /// module-level assignments and re-exports; <c>__all__</c> wins when present. Cached by timestamp + size.
        /// </summary>
        public static List<KeyValuePair<string, int>> TopLevelNames(string file)
        {
            DateTime stamp;
            long len;
            try
            {
                FileInfo fi = new FileInfo(file);
                if (!fi.Exists) return new List<KeyValuePair<string, int>>();
                stamp = fi.LastWriteTimeUtc;
                len = fi.Length;
            }
            catch { return new List<KeyValuePair<string, int>>(); }
            lock (gate)
            {
                NamesCache c;
                if (namesCache.TryGetValue(file, out c) && c.Stamp == stamp && c.Length == len) return c.Names;
            }
            List<KeyValuePair<string, int>> names = new List<KeyValuePair<string, int>>();
            List<string> all = null;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                int n = 0;
                foreach (string raw in File.ReadLines(file))
                {
                    if (++n > 20000) break;
                    if (raw.Length == 0 || raw[0] == ' ' || raw[0] == '\t' || raw[0] == '#') continue;
                    string line = raw.TrimEnd();
                    Match am = AllList.Match(line);
                    if (am.Success)
                    {
                        all = new List<string>();
                        foreach (string part in am.Groups[1].Value.Split(','))
                        {
                            string p = part.Trim().Trim('\'', '"');
                            if (Identifier.IsMatch(p)) all.Add(p);
                        }
                        continue;
                    }
                    string kind, sym;
                    if ((line.StartsWith("def ", StringComparison.Ordinal) || line.StartsWith("async def ", StringComparison.Ordinal) || line.StartsWith("class ", StringComparison.Ordinal)) &&
                        HeuristicSymbolExtractor.TryMatch(line, "py", out kind, out sym))
                    {
                        AddName(names, seen, sym, line.StartsWith("class ", StringComparison.Ordinal) ? 7 : 3);
                        continue;
                    }
                    Match fm = FromImportNames.Match(line);
                    if (fm.Success)
                    {
                        foreach (string part in fm.Groups[1].Value.Split(','))
                        {
                            string p = part.Trim();
                            int asIdx = p.IndexOf(" as ", StringComparison.Ordinal);
                            if (asIdx > 0) p = p.Substring(asIdx + 4).Trim();
                            if (Identifier.IsMatch(p)) AddName(names, seen, p, 6);
                        }
                        continue;
                    }
                    Match m = Assignment.Match(line);
                    if (m.Success) AddName(names, seen, m.Groups[1].Value, IsConstantName(m.Groups[1].Value) ? 21 : 6);
                }
            }
            catch { }
            if (all != null && all.Count > 0)
            {
                Dictionary<string, int> kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, int> kv in names) kinds[kv.Key] = kv.Value;
                names = new List<KeyValuePair<string, int>>();
                foreach (string a in all)
                {
                    int k;
                    names.Add(new KeyValuePair<string, int>(a, kinds.TryGetValue(a, out k) ? k : 6));
                }
            }
            else
            {
                names.RemoveAll(delegate (KeyValuePair<string, int> kv) { return kv.Key.StartsWith("_", StringComparison.Ordinal); });
            }
            lock (gate)
            {
                if (namesCache.Count > 256) namesCache.Clear();
                NamesCache nc = new NamesCache();
                nc.Stamp = stamp;
                nc.Length = len;
                nc.Names = names;
                namesCache[file] = nc;
            }
            return names;
        }

        private static bool IsConstantName(string n)
        {
            bool letter = false;
            foreach (char c in n) { if (char.IsLower(c)) return false; if (char.IsLetter(c)) letter = true; }
            return letter;
        }

        private static void AddName(List<KeyValuePair<string, int>> names, HashSet<string> seen, string name, int kind)
        {
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) return;
            names.Add(new KeyValuePair<string, int>(name, kind));
        }

        private static PythonEnvironmentLayout GetLayout(string workspaceRoot)
        {
            if (LayoutOverride != null) return LayoutOverride(workspaceRoot);
            return PythonEnvironmentLayout.ForWorkspace(workspaceRoot);
        }
    }

    /// <summary>
    /// Where an interpreter's packages and standard library live, derived from paths only (pyvenv.cfg, lib/pythonX.Y,
    /// Lib\site-packages, conda layout, user site) — the interpreter is never executed.
    /// </summary>
    public sealed class PythonEnvironmentLayout
    {
        public string Interpreter;
        public string Version; // "3.12" (may be null)
        public List<string> SitePackages = new List<string>();
        public string StdlibDir;

        private static readonly object gate = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, PythonEnvironmentLayout>> cache = new Dictionary<string, KeyValuePair<DateTime, PythonEnvironmentLayout>>(StringComparer.Ordinal);
        private static readonly Regex PyVerDir = new Regex(@"^python(\d+)\.(\d+)t?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static void ClearCache() { lock (gate) { cache.Clear(); } }

        /// <summary>Layout of the workspace's selected interpreter (PythonInterpreterService), else of a .venv / venv / env folder in the workspace. Cached for 30 s.</summary>
        public static PythonEnvironmentLayout ForWorkspace(string workspaceRoot)
        {
            string key = workspaceRoot ?? "";
            lock (gate)
            {
                KeyValuePair<DateTime, PythonEnvironmentLayout> c;
                if (cache.TryGetValue(key, out c) && (DateTime.UtcNow - c.Key).TotalSeconds < 30) return c.Value;
            }
            PythonEnvironmentLayout layout = null;
            try
            {
                PythonInterpreterInfo sel = PythonInterpreterService.GetSelected(workspaceRoot);
                if (sel != null) layout = FromInterpreter(sel.Path, sel.EnvPath, sel.Version);
                if ((layout == null || layout.SitePackages.Count == 0) && !string.IsNullOrEmpty(workspaceRoot))
                {
                    foreach (string venv in new[] { ".venv", "venv", "env", ".env" })
                    {
                        string d = Path.Combine(workspaceRoot, venv);
                        if (!File.Exists(Path.Combine(d, "pyvenv.cfg"))) continue;
                        PythonEnvironmentLayout v = FromInterpreter(PythonInterpreterDiscovery.VenvPython(d, Path.DirectorySeparatorChar == '\\'), d, null);
                        if (v != null && v.SitePackages.Count > 0) { layout = v; break; }
                    }
                }
            }
            catch { }
            lock (gate) { cache[key] = new KeyValuePair<DateTime, PythonEnvironmentLayout>(DateTime.UtcNow, layout); }
            return layout;
        }

        /// <summary>Builds the layout from an interpreter path and (for venv / conda) its environment folder.</summary>
        public static PythonEnvironmentLayout FromInterpreter(string interpreter, string envPath, string version)
        {
            PythonEnvironmentLayout l = new PythonEnvironmentLayout();
            l.Interpreter = interpreter;
            l.Version = ShortVersion(version);
            bool windows = Path.DirectorySeparatorChar == '\\';
            string prefix = envPath;
            if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(interpreter))
            {
                try
                {
                    string binDir = Path.GetDirectoryName(interpreter);
                    // <prefix>/bin/python3 (Unix) or <prefix>\python.exe / <prefix>\Scripts\python.exe (Windows)
                    if (windows) prefix = string.Equals(Path.GetFileName(binDir), "Scripts", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(binDir) : binDir;
                    else prefix = Path.GetDirectoryName(binDir);
                }
                catch { }
            }
            if (string.IsNullOrEmpty(prefix)) return l;
            string cfgVersion = PythonInterpreterDiscovery.ReadVenvVersion(prefix);
            if (l.Version == null) l.Version = ShortVersion(cfgVersion);
            AddLibDirs(l, prefix, windows, true);
            // venv: the standard library lives in the base installation (pyvenv.cfg "home" = its bin folder)
            string home = PythonInterpreterDiscovery.ReadPyvenvCfg(prefix, "home");
            if (!string.IsNullOrEmpty(home) && l.StdlibDir == null)
            {
                string basePrefix = windows ? home : SafeParent(home);
                if (!string.IsNullOrEmpty(basePrefix)) AddLibDirs(l, basePrefix, windows, false);
            }
            return l;
        }

        private static string SafeParent(string p)
        {
            try { return Path.GetDirectoryName(p.TrimEnd('/', '\\')); } catch { return null; }
        }

        private static void AddLibDirs(PythonEnvironmentLayout l, string prefix, bool windows, bool sitePackages)
        {
            if (windows)
            {
                string lib = Path.Combine(prefix, "Lib");
                if (sitePackages) AddIfDir(l.SitePackages, Path.Combine(lib, "site-packages"));
                if (l.StdlibDir == null && File.Exists(Path.Combine(lib, "os.py"))) l.StdlibDir = lib;
                return;
            }
            string libRoot = Path.Combine(prefix, "lib");
            string verDir = null;
            if (l.Version != null && Directory.Exists(Path.Combine(libRoot, "python" + l.Version))) verDir = Path.Combine(libRoot, "python" + l.Version);
            if (verDir == null)
            {
                // highest lib/python3.X
                List<DirectoryListingCache.Entry> entries = DirectoryListingCache.List(libRoot);
                int best = -1;
                if (entries != null)
                    foreach (DirectoryListingCache.Entry e in entries)
                    {
                        Match m = PyVerDir.Match(e.Name);
                        if (!e.IsDirectory || !m.Success) continue;
                        int v = int.Parse(m.Groups[1].Value) * 100 + int.Parse(m.Groups[2].Value);
                        if (v > best) { best = v; verDir = Path.Combine(libRoot, e.Name); }
                    }
                if (verDir != null && l.Version == null)
                {
                    Match m = PyVerDir.Match(Path.GetFileName(verDir));
                    if (m.Success) l.Version = m.Groups[1].Value + "." + m.Groups[2].Value;
                }
            }
            if (verDir == null) return;
            if (sitePackages)
            {
                AddIfDir(l.SitePackages, Path.Combine(verDir, "site-packages"));
                AddIfDir(l.SitePackages, Path.Combine(verDir, "dist-packages"));
                if (prefix == "/usr" || prefix == "/usr/local") AddIfDir(l.SitePackages, "/usr/lib/python3/dist-packages");
                string homeDir = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(homeDir) && l.Version != null && !File.Exists(Path.Combine(prefix, "pyvenv.cfg")))
                {
                    AddIfDir(l.SitePackages, Path.Combine(homeDir, ".local", "lib", "python" + l.Version, "site-packages"));
                    AddIfDir(l.SitePackages, Path.Combine(homeDir, "Library", "Python", l.Version, "lib", "python", "site-packages"));
                }
            }
            if (l.StdlibDir == null && File.Exists(Path.Combine(verDir, "os.py"))) l.StdlibDir = verDir;
        }

        private static void AddIfDir(List<string> list, string d)
        {
            try { if (Directory.Exists(d) && !list.Contains(d)) list.Add(d); } catch { }
        }

        public static string ShortVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Trim().Split('.');
            int a, b;
            if (parts.Length >= 2 && int.TryParse(parts[0], out a) && int.TryParse(new string(Array.FindAll(parts[1].ToCharArray(), char.IsDigit)), out b))
                return a + "." + b;
            return null;
        }
    }
}
