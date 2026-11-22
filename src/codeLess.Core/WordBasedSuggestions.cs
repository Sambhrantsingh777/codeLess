using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Completion without a language server (QA #31), shared by both editors, with VS Code's defaults:
    /// editor.wordBasedSuggestions "matchingDocuments" (words of every open document of the same language, the current
    /// one first, the word being typed and numbers excluded, at most 10 000 words), snippets in the same list
    /// (editor.snippetSuggestions "inline") and editor.quickSuggestions { other: on, comments: off, strings: off } —
    /// as-you-type suggestions never open inside comments or strings (Ctrl+Space still works there), and Markdown has
    /// quick suggestions off like VS Code. STRICT C# 5.
    /// </summary>
    public static class WordBasedSuggestions
    {
        public static bool Enabled = true;
        public const int SuggestionsLimit = 10000;
        public const int KindText = 1, KindSnippet = 15;

        private sealed class DocWords
        {
            public object LinesRef;
            public int Version = int.MinValue;
            public int BuiltTick;
            public List<string> Words = new List<string>();
            /// <summary>One completion item per word, created once per scan (typing must not allocate per word).</summary>
            public List<LspCompletionItem> Items;
            public List<string> ItemsSource;
            public bool Refreshing;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, DocWords> cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, DocWords>();

        /// <summary>Documents above <see cref="LargeDocumentLines"/> lines re-scan at most this often while typing (VS Code computes words in a worker).</summary>
        public static int LargeDocumentRescanMs = 1500;
        public static int LargeDocumentLines = 2000;

        public static bool IsWordChar(char c) { return LspEditing.IsWordChar(c); }

        /// <summary>
        /// Whether as-you-type suggestions may open at (line, col): not inside a comment or a string, not in Markdown
        /// (VS Code turns quick suggestions off there).
        /// </summary>
        public static bool QuickSuggestionsAllowed(EditorTabBuffer buf, int line, int col)
        {
            if (!Enabled || buf == null || buf.Lines == null || line < 0 || line >= buf.Lines.Count) return false;
            string lang = (buf.Language ?? "").ToLowerInvariant();
            if (lang == "md" || lang == "markdown" || lang == "mdx") return false;
            EditorSyntaxSession session = EditorSyntaxSession.For(buf);
            if (session == null) return true;
            session.Prepare(buf);
            if (session.Brackets == null || !session.Brackets.Profile.Enabled) return true;
            return session.Brackets.ContextAt(buf.Lines, line, col) == EditorBracketCache.ContextCode;
        }

        private static bool SameLanguage(EditorTabBuffer a, EditorTabBuffer b)
        {
            return string.Equals(a.Language ?? "", b.Language ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> WordsOf(EditorTabBuffer buf, bool isCurrent)
        {
            DocWords dw = cache.GetValue(buf, delegate(EditorTabBuffer b) { return new DocWords(); });
            if (ReferenceEquals(dw.LinesRef, buf.Lines) && dw.Version == buf.EditVersion) return dw.Words;
            List<string> lines = buf.Lines;
            bool large = lines != null && lines.Count > LargeDocumentLines;
            if (large && dw.LinesRef != null)
            {
                // big document: answer with the previous scan and refresh it on the thread pool (no typing hitch)
                if (!dw.Refreshing && Math.Abs(unchecked(Environment.TickCount - dw.BuiltTick)) >= LargeDocumentRescanMs)
                {
                    if (!BackgroundRescan) { Store(dw, buf, ScanWords(lines)); return dw.Words; }
                    dw.Refreshing = true;
                    string[] snapshot = lines.ToArray();
                    object linesRef = lines;
                    int version = buf.EditVersion;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        List<string> w = null;
                        try { w = ScanWords(snapshot); } catch { }
                        lock (dw)
                        {
                            if (w != null) { dw.Words = w; dw.LinesRef = linesRef; dw.Version = version; dw.BuiltTick = Environment.TickCount; }
                            dw.Refreshing = false;
                        }
                    });
                }
                return dw.Words;
            }
            Store(dw, buf, ScanWords(lines));
            return dw.Words;
        }

        /// <summary>Large documents refresh their word list in the background (tests turn this off for determinism).</summary>
        public static bool BackgroundRescan = true;

        private static void Store(DocWords dw, EditorTabBuffer buf, List<string> words)
        {
            lock (dw)
            {
                dw.LinesRef = buf.Lines;
                dw.Version = buf.EditVersion;
                dw.BuiltTick = Environment.TickCount;
                dw.Words = words;
            }
        }

        /// <summary>Unique non-numeric words of the lines in document order (at most <see cref="SuggestionsLimit"/>), thread-safe.</summary>
        public static List<string> ScanWords(IList<string> lines)
        {
            List<string> words = new List<string>();
            // de-duplicate by a hash of the slice first: repeated words never allocate a substring
            Dictionary<int, List<string>> buckets = new Dictionary<int, List<string>>();
            if (lines == null) return words;
            for (int i = 0; i < lines.Count && words.Count < SuggestionsLimit; i++)
            {
                string s = lines[i];
                if (string.IsNullOrEmpty(s) || s.Length > 20000) continue;
                int k = 0;
                while (k < s.Length && words.Count < SuggestionsLimit)
                {
                    if (!IsWordChar(s[k])) { k++; continue; }
                    int st = k;
                    int h = 17;
                    while (k < s.Length && IsWordChar(s[k])) { h = unchecked(h * 31 + s[k]); k++; }
                    if (char.IsDigit(s[st])) continue; // numbers are not suggested
                    int len = k - st;
                    bool letter = false;
                    for (int q = st; q < k && !letter; q++) letter = char.IsLetter(s[q]) || s[q] == '_';
                    if (!letter) continue;             // "$", "$$": punctuation, not words
                    List<string> bucket;
                    if (!buckets.TryGetValue(h, out bucket)) { bucket = new List<string>(1); buckets[h] = bucket; }
                    bool found = false;
                    for (int bi = 0; bi < bucket.Count; bi++)
                    {
                        string c = bucket[bi];
                        if (c.Length == len && string.CompareOrdinal(c, 0, s, st, len) == 0) { found = true; break; }
                    }
                    if (found) continue;
                    string w = s.Substring(st, len);
                    bucket.Add(w);
                    words.Add(w);
                }
            }
            return words;
        }

        private static List<LspCompletionItem> ItemsOf(EditorTabBuffer buf, bool isCurrent)
        {
            List<string> words = WordsOf(buf, isCurrent);
            DocWords dw = cache.GetValue(buf, delegate(EditorTabBuffer b) { return new DocWords(); });
            if (dw.Items != null && ReferenceEquals(dw.ItemsSource, words)) return dw.Items;
            List<LspCompletionItem> items = new List<LspCompletionItem>(words.Count);
            foreach (string w in words)
            {
                LspCompletionItem it = new LspCompletionItem();
                it.Label = w;
                it.InsertText = w;
                it.Kind = KindText;
                it.Resolved = true;
                items.Add(it);
            }
            dw.Items = items;
            dw.ItemsSource = words;
            return items;
        }

        /// <summary>The word at the caret (being typed / completed), excluded from the suggestions like VS Code.</summary>
        public static string WordAt(string line, int col, out int start)
        {
            line = line ?? "";
            col = Math.Max(0, Math.Min(line.Length, col));
            start = col;
            while (start > 0 && IsWordChar(line[start - 1])) start--;
            int end = col;
            while (end < line.Length && IsWordChar(line[end])) end++;
            return line.Substring(start, end - start);
        }

        /// <summary>
        /// Completion items for (line, col) of <paramref name="current"/>: words of the open documents with the same
        /// language (current first) and the language's snippets. Snippet items carry the <see cref="CodeSnippetItem"/>
        /// in <see cref="LspCompletionItem.Tag"/>.
        /// </summary>
        public static List<LspCompletionItem> Build(EditorTabBuffer current, IEnumerable<EditorTabBuffer> openDocuments, int line, int col, IEnumerable<CodeSnippetItem> snippets)
        {
            return Build(current, openDocuments, line, col, snippets, null);
        }

        /// <summary>
        /// As above, keeping only items that fuzzy-match <paramref name="prefixFilter"/> (the characters typed so far):
        /// typing more only narrows the list, so the suggest session refilters this subset.
        /// </summary>
        public static List<LspCompletionItem> Build(EditorTabBuffer current, IEnumerable<EditorTabBuffer> openDocuments, int line, int col, IEnumerable<CodeSnippetItem> snippets, string prefixFilter)
        {
            bool filter = !string.IsNullOrEmpty(prefixFilter);
            List<LspCompletionItem> items = new List<LspCompletionItem>();
            if (current == null || current.Lines == null || line < 0 || line >= current.Lines.Count) return items;
            int ws;
            string wordAt = WordAt(current.Lines[line], col, out ws);
            List<EditorTabBuffer> docs = new List<EditorTabBuffer>();
            docs.Add(current);
            if (openDocuments != null)
                foreach (EditorTabBuffer d in openDocuments)
                    if (d != null && !ReferenceEquals(d, current) && d.Lines != null && !d.IsLargeFileViewer && SameLanguage(d, current)) docs.Add(d);
            HashSet<string> seen = docs.Count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null; // one document: already unique
            int count = 0;
            foreach (EditorTabBuffer d in docs)
            {
                foreach (LspCompletionItem it in ItemsOf(d, ReferenceEquals(d, current)))
                {
                    if (string.Equals(it.Label, wordAt, StringComparison.Ordinal)) continue;
                    if (filter && LspCompletionList.FuzzyScore(it.Label, prefixFilter) < 0) continue;
                    if (seen != null && !seen.Add(it.Label)) continue;
                    items.Add(it);
                    if (++count >= SuggestionsLimit) break;
                }
                if (count >= SuggestionsLimit) break;
            }
            if (snippets != null)
            {
                foreach (CodeSnippetItem sn in snippets)
                {
                    if (sn == null || string.IsNullOrEmpty(sn.Prefix)) continue;
                    if (filter && LspCompletionList.FuzzyScore(sn.Prefix, prefixFilter) < 0) continue;
                    LspCompletionItem it = new LspCompletionItem();
                    it.Label = sn.Prefix;
                    it.Kind = KindSnippet;
                    it.Detail = string.IsNullOrEmpty(sn.Description) ? "Snippet" : sn.Description;
                    it.Documentation = sn.Body;
                    it.InsertText = sn.Body;
                    it.IsSnippet = true;
                    it.Resolved = true;
                    it.Tag = sn;
                    items.Add(it);
                }
            }
            return items;
        }
    }
}
