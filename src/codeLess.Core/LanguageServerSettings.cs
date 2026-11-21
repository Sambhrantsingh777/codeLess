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
            bool hadLsp = false;
            foreach (DiagnosticItem d in buf.Diagnostics) if (d.IsLanguageServer) { hadLsp = true; break; }
            if (!hadLsp && lsp.Count == 0) return;
            List<DiagnosticItem> merged = buf.Diagnostics.Where(d => !d.IsLanguageServer).ToList();
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
}
