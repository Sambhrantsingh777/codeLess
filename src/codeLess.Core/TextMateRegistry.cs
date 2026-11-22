using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>
    /// Knows which TextMate grammar applies to a file: grammars bundled in codeLess.Core (MIT, see
    /// Grammars/THIRD_PARTY_GRAMMARS.txt) plus grammars contributed by installed VSIX extensions
    /// (package.json "contributes.languages" + "contributes.grammars"; VSIX contributions win). Grammars are parsed
    /// lazily on a worker thread; until a grammar is ready the editor keeps its built-in tokenizer. STRICT C# 5.
    /// </summary>
    public sealed class TextMateGrammarRegistry
    {
        private sealed class Entry
        {
            public string ScopeName;
            public string FilePath;      // disk grammar (VSIX)
            public string ResourceName;  // bundled grammar
            public TmRawGrammar Raw;
            public bool Loading;
            public bool Failed;
            public string Error;
            public TmCompiledGrammar Compiled;
            public string Origin;
        }

        private static TextMateGrammarRegistry shared;
        private static readonly object sharedLock = new object();

        public static TextMateGrammarRegistry Shared
        {
            get
            {
                lock (sharedLock)
                {
                    if (shared == null)
                    {
                        shared = new TextMateGrammarRegistry();
                        shared.RegisterBundled();
                        shared.RegisterInstalledExtensions(DefaultExtensionsRoot());
                    }
                    return shared;
                }
            }
        }

        private readonly object sync = new object();
        private readonly Dictionary<string, Entry> byScope = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> scopeByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> scopeByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> scopeByLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int generation;

        /// <summary>Incremented when registrations or loaded grammars change (editors re-resolve their grammar).</summary>
        public int Generation { get { return generation; } }

        public const string BundledResourcePrefix = "codeLess.Grammars.";

        private struct Bundled
        {
            public string File, Scope, Language, Exts, Names;
            public Bundled(string file, string scope, string lang, string exts, string names) { File = file; Scope = scope; Language = lang; Exts = exts; Names = names; }
        }

        private static readonly Bundled[] BundledGrammars = new Bundled[]
        {
            new Bundled("go.tmLanguage.json.gz", "source.go", "go", ".go", ""),
            new Bundled("rust.tmLanguage.json.gz", "source.rust", "rust", ".rs", ""),
            new Bundled("yaml.tmLanguage.json.gz", "source.yaml", "yaml", ".yaml .yml", ".clang-format .clangd"),
            new Bundled("yaml-1.2.tmLanguage.json.gz", "source.yaml.1.2", null, "", ""),
            new Bundled("yaml-embedded.tmLanguage.json.gz", "source.yaml.embedded", null, "", ""),
            new Bundled("shell.tmLanguage.json.gz", "source.shell", "shellscript", ".sh .bash .zsh .ksh .bats .command", ".bashrc .bash_profile .bash_login .bash_logout .zshrc .zprofile .zshenv .profile PKGBUILD"),
            new Bundled("css.tmLanguage.json.gz", "source.css", "css", ".css", ""),
            new Bundled("scss.tmLanguage.json.gz", "source.css.scss", "scss", ".scss", ""),
            new Bundled("markdown.tmLanguage.json.gz", "text.html.markdown", "markdown", ".md .markdown .mdown .mkd .mkdn .mdwn", ""),
            new Bundled("sql.tmLanguage.json.gz", "source.sql", "sql", ".sql .dsql", ""),
            new Bundled("xml.tmLanguage.json.gz", "text.xml", "xml", ".xml .xsd .xsl .xslt .svg .csproj .vbproj .fsproj .props .targets .nuspec .resx .xaml .axaml .plist .config .wxs .storyboard .xib .xlf .manifest", ""),
            new Bundled("typescript.tmLanguage.json.gz", "source.ts", "typescript", ".ts .mts .cts", ""),
            new Bundled("typescriptreact.tmLanguage.json.gz", "source.tsx", "typescriptreact", ".tsx", ""),
            new Bundled("javascript.tmLanguage.json.gz", "source.js", "javascript", ".js .mjs .cjs .es6", "jakefile"),
            new Bundled("javascriptreact.tmLanguage.json.gz", "source.js.jsx", "javascriptreact", ".jsx", ""),
            new Bundled("csharp.tmLanguage.json.gz", "source.cs", "csharp", ".cs .csx .cake", ""),
            new Bundled("ruby.tmLanguage.json.gz", "source.ruby", "ruby", ".rb .rake .gemspec .ru .rbi .podspec", "Gemfile Rakefile Podfile Brewfile Guardfile Vagrantfile"),
            new Bundled("makefile.tmLanguage.json.gz", "source.makefile", "makefile", ".mk .mak", "Makefile makefile GNUmakefile OCamlMakefile"),
        };

        private void RegisterBundled()
        {
            foreach (Bundled b in BundledGrammars)
            {
                Entry e = new Entry();
                e.ScopeName = b.Scope;
                e.ResourceName = BundledResourcePrefix + b.File;
                e.Origin = "bundled";
                byScope[b.Scope] = e;
                if (b.Language != null) scopeByLanguage[b.Language] = b.Scope;
                foreach (string x in b.Exts.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) scopeByExtension[x] = b.Scope;
                foreach (string x in b.Names.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) scopeByFileName[x] = b.Scope;
            }
        }

        /// <summary>~/.codeless/extensions (where VSIX packages are installed by both UIs).</summary>
        public static string DefaultExtensionsRoot()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless", "extensions"); }
            catch { return null; }
        }

        /// <summary>
        /// Registers the grammars of every installed extension (package.json at the folder root or in "extension/").
        /// The Windows VSIX host also calls <see cref="RegisterVsixContributions"/>; registering twice is harmless.
        /// </summary>
        public int RegisterInstalledExtensions(string root)
        {
            int count = 0;
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        string pkg = Path.Combine(dir, "extension", "package.json");
                        if (!File.Exists(pkg)) pkg = Path.Combine(dir, "package.json");
                        if (!File.Exists(pkg)) continue;
                        Dictionary<string, object> d = TmJson.Parse(File.ReadAllText(pkg)) as Dictionary<string, object>;
                        object contrib;
                        if (d == null || !d.TryGetValue("contributes", out contrib)) continue;
                        count += RegisterVsixContributions(Path.GetDirectoryName(pkg), contrib as Dictionary<string, object>);
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        /// <summary>Registers (or replaces) a grammar file for a scope and maps file extensions / names to it.</summary>
        public void RegisterGrammarFile(string scopeName, string path, string languageId, IEnumerable<string> extensions, IEnumerable<string> fileNames)
        {
            if (string.IsNullOrEmpty(scopeName) || string.IsNullOrEmpty(path)) return;
            lock (sync)
            {
                Entry e = new Entry();
                e.ScopeName = scopeName;
                e.FilePath = path;
                e.Origin = "vsix";
                byScope[scopeName] = e;
                if (!string.IsNullOrEmpty(languageId)) scopeByLanguage[languageId] = scopeName;
                if (extensions != null) foreach (string x in extensions) if (!string.IsNullOrEmpty(x)) scopeByExtension[x.StartsWith(".", StringComparison.Ordinal) ? x : "." + x] = scopeName;
                if (fileNames != null) foreach (string x in fileNames) if (!string.IsNullOrEmpty(x)) scopeByFileName[x] = scopeName;
                generation++;
            }
        }

        /// <summary>
        /// Hook for <c>VsixAndPluginHostEngine</c>: reads "languages" and "grammars" from a package.json "contributes"
        /// object. Injection-only grammars (no language) are registered by scope so other grammars can include them.
        /// </summary>
        public int RegisterVsixContributions(string packageDir, Dictionary<string, object> contributes)
        {
            if (contributes == null || string.IsNullOrEmpty(packageDir)) return 0;
            Dictionary<string, List<string>> extsByLang = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> namesByLang = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            object langsObj;
            if (contributes.TryGetValue("languages", out langsObj) && langsObj is IList)
            {
                foreach (object lo in (IList)langsObj)
                {
                    Dictionary<string, object> ld = lo as Dictionary<string, object>;
                    if (ld == null || !ld.ContainsKey("id")) continue;
                    string id = Convert.ToString(ld["id"], CultureInfo.InvariantCulture);
                    List<string> exts;
                    if (!extsByLang.TryGetValue(id, out exts)) { exts = new List<string>(); extsByLang[id] = exts; }
                    List<string> names;
                    if (!namesByLang.TryGetValue(id, out names)) { names = new List<string>(); namesByLang[id] = names; }
                    object eo;
                    if (ld.TryGetValue("extensions", out eo) && eo is IList) foreach (object x in (IList)eo) if (x != null) exts.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                    if (ld.TryGetValue("filenames", out eo) && eo is IList) foreach (object x in (IList)eo) if (x != null) names.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                }
            }
            int count = 0;
            object gramsObj;
            if (contributes.TryGetValue("grammars", out gramsObj) && gramsObj is IList)
            {
                foreach (object go in (IList)gramsObj)
                {
                    Dictionary<string, object> gd = go as Dictionary<string, object>;
                    if (gd == null || !gd.ContainsKey("scopeName") || !gd.ContainsKey("path")) continue;
                    string scope = Convert.ToString(gd["scopeName"], CultureInfo.InvariantCulture);
                    string rel = Convert.ToString(gd["path"], CultureInfo.InvariantCulture).Replace('/', Path.DirectorySeparatorChar);
                    while (rel.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) rel = rel.Substring(2);
                    string full;
                    try { full = Path.GetFullPath(Path.Combine(packageDir, rel)); } catch { continue; }
                    // never follow a grammar path outside the extension folder
                    string root;
                    try { root = Path.GetFullPath(packageDir); } catch { continue; }
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                    string lang = gd.ContainsKey("language") ? Convert.ToString(gd["language"], CultureInfo.InvariantCulture) : null;
                    List<string> exts = null, names = null;
                    if (lang != null) { extsByLang.TryGetValue(lang, out exts); namesByLang.TryGetValue(lang, out names); }
                    RegisterGrammarFile(scope, full, lang, exts, names);
                    count++;
                }
            }
            return count;
        }

        public string GetScopeForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string name, ext;
            try { name = Path.GetFileName(filePath); ext = Path.GetExtension(filePath); }
            catch { return null; }
            lock (sync)
            {
                string scope;
                if (!string.IsNullOrEmpty(name) && scopeByFileName.TryGetValue(name, out scope)) return scope;
                if (!string.IsNullOrEmpty(ext) && scopeByExtension.TryGetValue(ext, out scope)) return scope;
            }
            return null;
        }

        public string GetScopeForLanguage(string languageId)
        {
            if (string.IsNullOrEmpty(languageId)) return null;
            lock (sync)
            {
                string scope;
                return scopeByLanguage.TryGetValue(languageId, out scope) ? scope : null;
            }
        }

        public bool HasGrammar(string scope)
        {
            lock (sync) { return scope != null && byScope.ContainsKey(scope); }
        }

        public string GetLoadError(string scope)
        {
            lock (sync)
            {
                Entry e;
                return scope != null && byScope.TryGetValue(scope, out e) ? e.Error : null;
            }
        }

        /// <summary>Parses a grammar synchronously (used for includes of other grammars and by tests).</summary>
        public TmRawGrammar GetRawGrammar(string scope)
        {
            Entry e;
            lock (sync)
            {
                if (scope == null || !byScope.TryGetValue(scope, out e)) return null;
                if (e.Raw != null || e.Failed) return e.Raw;
            }
            TmRawGrammar g = LoadEntry(e);
            lock (sync)
            {
                if (e.Raw == null && !e.Failed)
                {
                    if (g == null) e.Failed = true;
                    else e.Raw = g;
                }
                return e.Raw;
            }
        }

        private static TmRawGrammar LoadEntry(Entry e)
        {
            try
            {
                if (e.FilePath != null) return TextMateGrammarLoader.LoadFile(e.FilePath);
                if (e.ResourceName != null)
                {
                    Assembly asm = typeof(TextMateGrammarRegistry).Assembly;
                    Stream s = asm.GetManifestResourceStream(e.ResourceName);
                    if (s == null)
                    {
                        // development fallback: Grammars folder next to the binaries
                        string file = e.ResourceName.Substring(BundledResourcePrefix.Length);
                        string disk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "Grammars", file);
                        if (File.Exists(disk)) return TextMateGrammarLoader.LoadFile(disk);
                        e.Error = "bundled grammar resource missing: " + e.ResourceName;
                        return null;
                    }
                    using (s)
                    {
                        string text = TextMateGrammarLoader.ReadGzipText(s);
                        return TextMateGrammarLoader.LoadText(text, e.ResourceName);
                    }
                }
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
            }
            return null;
        }

        /// <summary>
        /// Compiled grammar for a scope, or null while it is still loading (a worker thread parses it; poll again) or
        /// when it failed. Set <paramref name="synchronous"/> to parse on the calling thread (tests, benchmarks).
        /// </summary>
        public TmCompiledGrammar GetCompiled(string scope, bool synchronous)
        {
            Entry e;
            lock (sync)
            {
                if (scope == null || !byScope.TryGetValue(scope, out e)) return null;
                if (e.Compiled != null) return e.Compiled;
                if (e.Failed) return null;
                if (e.Raw == null && !synchronous)
                {
                    if (!e.Loading)
                    {
                        e.Loading = true;
                        Entry captured = e;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            TmRawGrammar g = LoadEntry(captured);
                            lock (sync)
                            {
                                captured.Loading = false;
                                if (captured.Raw == null)
                                {
                                    if (g == null) captured.Failed = true;
                                    else captured.Raw = g;
                                }
                                generation++;
                            }
                        });
                    }
                    return null;
                }
            }
            TmRawGrammar raw = e.Raw ?? GetRawGrammar(scope);
            if (raw == null) return null;
            lock (sync)
            {
                if (e.Compiled == null) e.Compiled = new TmCompiledGrammar(raw, GetRawGrammar);
                return e.Compiled;
            }
        }

        public bool IsLoading(string scope)
        {
            lock (sync)
            {
                Entry e;
                return scope != null && byScope.TryGetValue(scope, out e) && e.Loading;
            }
        }

        public List<string> RegisteredScopes()
        {
            lock (sync) { return new List<string>(byScope.Keys); }
        }
    }

    /// <summary>Per-document incremental TextMate tokenization (states for every line, tokens for recent lines).</summary>
    public sealed class TextMateDocument : LineStateCache<TmStateStack, TmLineTokens>
    {
        public readonly TmCompiledGrammar Grammar;
        private readonly Dictionary<int, TmLineTokens> guesses = new Dictionary<int, TmLineTokens>();
        private readonly Dictionary<int, TmStateStack> guessEnd = new Dictionary<int, TmStateStack>();
        private readonly Dictionary<int, string> guessText = new Dictionary<int, string>();
        private int guessStart = -1;

        public TextMateDocument(TmCompiledGrammar grammar) { Grammar = grammar; }

        protected override TmStateStack InitialState { get { return Grammar.InitialState; } }
        protected override bool StateEquals(TmStateStack a, TmStateStack b) { return TmStateStack.Same(a, b); }

        protected override TmStateStack ProcessLine(string text, int lineIndex, TmStateStack start, out TmLineTokens data)
        {
            TmLineResult r = Grammar.TokenizeLine(text, lineIndex == 0 ? null : start);
            data = r.Tokens;
            return r.EndState;
        }

        protected override void OnInvalidated(int firstChangedLine)
        {
            guesses.Clear();
            guessEnd.Clear();
            guessText.Clear();
            guessStart = -1;
        }

        /// <summary>
        /// Tokens for a line: exact when the line's start state is known (computing up to the deadline), otherwise a
        /// guess tokenized from the first not-yet-valid visible line with the initial state (VS Code shows the
        /// viewport this way while the background pass catches up). <paramref name="isGuess"/> tells which.
        /// </summary>
        public TmLineTokens GetTokens(IList<string> lines, int line, int firstVisibleLine, long deadlineTicks, out bool isGuess)
        {
            isGuess = false;
            if (lines == null || line < 0 || line >= lines.Count) return null;
            if (line < ValidCount || EnsureValid(lines, line, deadlineTicks))
                return GetData(line);
            isGuess = true;
            int start = Math.Max(ValidCount, Math.Min(firstVisibleLine, line));
            if (start != guessStart) { guesses.Clear(); guessEnd.Clear(); guessText.Clear(); guessStart = start; }
            TmLineTokens t;
            if (guesses.TryGetValue(line, out t)) return t;
            TmStateStack st = start == 0 ? null : InitialState;
            for (int i = start; i <= line; i++)
            {
                if (guesses.ContainsKey(i)) { st = guessEnd[i]; continue; }
                TmLineResult r = Grammar.TokenizeLine(lines[i], i == 0 ? null : st);
                guesses[i] = r.Tokens;
                guessEnd[i] = r.EndState;
                guessText[i] = lines[i];
                st = r.EndState;
            }
            return guesses[line];
        }
    }
}
