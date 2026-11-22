using System;
using System.Collections.Generic;
using System.Linq;

namespace codeLess.Studio
{
    /// <summary>Language server preferences persisted in settings.json (both UIs share IdePersistentSettings).</summary>
    public partial class IdePersistentSettings
    {
        /// <summary>When true no language server is ever started (LSP features fall back to the built-in heuristics).</summary>
        public bool DisableLanguageServers { get; set; }

        /// <summary>User overrides, one "language = command args" (or "language = off") per line. See LanguageServerUserSettings.</summary>
        public string LanguageServerOverrides { get; set; }

        /// <summary>Comma-separated language ids whose "install a language server" hint was dismissed for good.</summary>
        public string LanguageServerHintsDismissed { get; set; }

        public LanguageServerUserSettings GetLanguageServerUserSettings()
        {
            return LanguageServerUserSettings.Parse(LanguageServerOverrides, DisableLanguageServers);
        }

        public bool IsLanguageServerHintDismissed(string languageId)
        {
            if (string.IsNullOrEmpty(LanguageServerHintsDismissed) || string.IsNullOrEmpty(languageId)) return false;
            foreach (string s in LanguageServerHintsDismissed.Split(','))
                if (string.Equals(s.Trim(), languageId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void DismissLanguageServerHint(string languageId)
        {
            if (string.IsNullOrEmpty(languageId) || IsLanguageServerHintDismissed(languageId)) return;
            LanguageServerHintsDismissed = string.IsNullOrEmpty(LanguageServerHintsDismissed) ? languageId : LanguageServerHintsDismissed + "," + languageId;
        }

        /// <summary>Copies the language server preferences (used by the Avalonia StudioSettings loader).</summary>
        public void CopyLanguageServerSettingsFrom(IdePersistentSettings other)
        {
            if (other == null) return;
            DisableLanguageServers = other.DisableLanguageServers;
            LanguageServerOverrides = other.LanguageServerOverrides;
            LanguageServerHintsDismissed = other.LanguageServerHintsDismissed;
        }
    }

    /// <summary>Extra diagnostic data for items that come from a language server.</summary>
    public partial class DiagnosticItem
    {
        public bool IsLanguageServer;
        /// <summary>1-based end position (exclusive column) of the exact range, 0 when unknown.</summary>
        public int EndLine;
        public int EndColumn;
        public string Code;
        /// <summary>The original LSP Diagnostic object (sent back in textDocument/codeAction context).</summary>
        public object LspRaw;
    }

    /// <summary>
    /// Process-wide store of language-server diagnostics keyed by file. The shared heuristic analyzer
    /// (LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics) rebuilds buf.Diagnostics on every edit; it calls
    /// <see cref="MergeInto"/> so LSP results survive and are shown next to the built-in syntax-gate diagnostics.
    /// </summary>
    public static class LspDiagnosticsStore
    {
        private static readonly object sync = new object();
        private static readonly Dictionary<string, List<DiagnosticItem>> byFile = new Dictionary<string, List<DiagnosticItem>>(StringComparer.Ordinal);

        public static void Set(string filePath, List<DiagnosticItem> items)
        {
            string key = LspText.PathKey(filePath);
            if (key.Length == 0) return;
            lock (sync)
            {
                if (items == null || items.Count == 0) byFile.Remove(key);
                else byFile[key] = new List<DiagnosticItem>(items);
            }
        }

        public static List<DiagnosticItem> Get(string filePath)
        {
            string key = LspText.PathKey(filePath);
            lock (sync)
            {
                List<DiagnosticItem> l;
                return byFile.TryGetValue(key, out l) ? new List<DiagnosticItem>(l) : new List<DiagnosticItem>();
            }
        }

        public static int Count(string filePath)
        {
            string key = LspText.PathKey(filePath);
            lock (sync)
            {
                List<DiagnosticItem> l;
                return byFile.TryGetValue(key, out l) ? l.Count : 0;
            }
        }

        public static void Clear(string filePath)
        {
            Set(filePath, null);
        }

        /// <summary>Replaces the language-server items in <paramref name="buf"/>.Diagnostics with the current store contents (sorted by line, stable).</summary>
        public static void MergeInto(EditorTabBuffer buf)
        {
            if (buf == null || buf.Diagnostics == null) return;
            List<DiagnosticItem> lsp = string.IsNullOrEmpty(buf.FilePath) ? new List<DiagnosticItem>() : Get(buf.FilePath);
            // Task problem-matcher results (Run Task / Run Build Task) are external diagnostics too.
            if (!string.IsNullOrEmpty(buf.FilePath)) lsp.AddRange(TaskProblemStore.Get(buf.FilePath));
            bool hadLsp = false;
            foreach (DiagnosticItem d in buf.Diagnostics) if (d.IsLanguageServer || d.IsTaskProblem) { hadLsp = true; break; }
            if (!hadLsp && lsp.Count == 0) return;
            List<DiagnosticItem> merged = buf.Diagnostics.Where(d => !d.IsLanguageServer && !d.IsTaskProblem).ToList();
            int lineCount = buf.Lines != null ? buf.Lines.Count : int.MaxValue;
            foreach (DiagnosticItem d in lsp)
            {
                if (d.Line > lineCount) continue; // stale result for a since-shortened document
                d.RelativePath = buf.RelativePath ?? d.RelativePath;
                merged.Add(d);
            }
            List<KeyValuePair<int, DiagnosticItem>> indexed = new List<KeyValuePair<int, DiagnosticItem>>();
            for (int i = 0; i < merged.Count; i++) indexed.Add(new KeyValuePair<int, DiagnosticItem>(i, merged[i]));
            indexed.Sort(delegate (KeyValuePair<int, DiagnosticItem> a, KeyValuePair<int, DiagnosticItem> b)
            {
                int c = a.Value.Line.CompareTo(b.Value.Line);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            buf.Diagnostics.Clear();
            foreach (KeyValuePair<int, DiagnosticItem> kv in indexed) buf.Diagnostics.Add(kv.Value);
        }
    }

    /// <summary>
    /// Language-server document symbols per file. The heuristic analyzer rebuilds buf.Symbols on every edit and then calls
    /// <see cref="ApplyTo"/>, so the outline keeps showing the (more precise) server symbols when a server provides them.
    /// </summary>
    public static class LspSymbolsStore
    {
        private static readonly object sync = new object();
        private static readonly Dictionary<string, List<AstSymbolItem>> byFile = new Dictionary<string, List<AstSymbolItem>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<StickySymbolRange>> scopesByFile = new Dictionary<string, List<StickySymbolRange>>(StringComparer.Ordinal);

        public static void Set(string filePath, List<LspDocumentSymbol> symbols)
        {
            string key = LspText.PathKey(filePath);
            if (key.Length == 0) return;
            lock (sync)
            {
                if (symbols == null) { byFile.Remove(key); scopesByFile.Remove(key); return; }
                List<AstSymbolItem> list = new List<AstSymbolItem>();
                List<StickySymbolRange> scopes = new List<StickySymbolRange>();
                foreach (LspDocumentSymbol d in symbols)
                {
                    if (d == null || d.Range == null) continue;
                    AstSymbolItem it = new AstSymbolItem();
                    it.Name = d.Depth > 0 ? new string(' ', Math.Min(8, d.Depth * 2)) + d.Name : d.Name;
                    it.Kind = LspDocumentSymbol.ToOutlineKind(d.Kind);
                    LspRange sel = d.SelectionRange ?? d.Range;
                    it.Line = sel.StartLine + 1;
                    it.LineSpan = Math.Max(1, d.Range.EndLine - d.Range.StartLine + 1);
                    it.Signature = d.Detail ?? "";
                    list.Add(it);
                    // container symbols (namespace, class, method, function, ...) become sticky-scroll scopes
                    if (d.Range.EndLine > d.Range.StartLine && IsScopeKind(d.Kind))
                    {
                        StickySymbolRange sr = new StickySymbolRange();
                        sr.StartLine = d.Range.StartLine;
                        sr.EndLine = d.Range.EndLine;
                        sr.NameLine = sel.StartLine;
                        sr.Name = d.Name;
                        scopes.Add(sr);
                    }
                }
                byFile[key] = list;
                scopesByFile[key] = scopes;
            }
        }

        private static bool IsScopeKind(int kind)
        {
            // LSP SymbolKind: Module 2, Namespace 3, Package 4, Class 5, Method 6, Property 7, Constructor 9, Enum 10,
            // Interface 11, Function 12, Object 19, Struct 23, Operator 25
            return kind == 2 || kind == 3 || kind == 4 || kind == 5 || kind == 6 || kind == 7 || kind == 9 || kind == 10 ||
                   kind == 11 || kind == 12 || kind == 19 || kind == 23 || kind == 25;
        }

        /// <summary>Scope ranges of the last document-symbol response for a file (null when none).</summary>
        public static List<StickySymbolRange> GetScopeRanges(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            lock (sync)
            {
                List<StickySymbolRange> l;
                return scopesByFile.TryGetValue(LspText.PathKey(filePath), out l) ? l : null;
            }
        }

        public static bool Has(string filePath)
        {
            lock (sync) { return byFile.ContainsKey(LspText.PathKey(filePath)); }
        }

        public static void Clear(string filePath)
        {
            Set(filePath, null);
        }

        /// <summary>Replaces buf.Symbols with the stored server symbols (no-op when none are stored).</summary>
        public static void ApplyTo(EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath) || buf.Symbols == null) return;
            List<AstSymbolItem> l;
            lock (sync)
            {
                if (!byFile.TryGetValue(LspText.PathKey(buf.FilePath), out l)) return;
                l = new List<AstSymbolItem>(l);
            }
            buf.Symbols.Clear();
            buf.Symbols.AddRange(l);
        }
    }
}
