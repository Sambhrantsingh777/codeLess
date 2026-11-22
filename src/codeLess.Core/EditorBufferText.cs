using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Thresholds for the normal editor's "large file" mode (lighter analysis: no synchronous AST pass / gutter diff
    /// on every edit). Files at or above <see cref="LargeFilePolicy"/>'s viewer thresholds never reach the editor.
    /// Shared by both editors (QA #17: the old 2,500 lines / 180 KB cut-off turned off bracket matching and git
    /// change markers on ordinary source files).
    /// </summary>
    public static class EditorLargeFileThresholds
    {
        /// <summary>
        /// Lines above which an open document is treated as large. Measured (headless, M-series Mac): the debounced AST
        /// pass costs ~57 ms at 50,000 lines and ~125 ms at 100,000; the git gutter diff stays under 1 ms.
        /// </summary>
        public static int LineThreshold = 100000;
        /// <summary>Characters above which an open document is treated as large (8 MB of text).</summary>
        public static long CharThreshold = 8L * 1024 * 1024;
    }

    /// <summary>
    /// Lazy document text (QA #15). <see cref="Lines"/> is the source of truth while editing; <see cref="Content"/>
    /// is materialized only when something reads it (save, AI context, search, LSP sync), so a keystroke on a
    /// 50,000-line file no longer joins and compares the whole text. STRICT C# 5.
    /// </summary>
    public partial class EditorTabBuffer
    {
        /// <summary>Soft-wrap row of <see cref="ScrollTopLine"/> shown at the top of the viewport (0 when not wrapping).</summary>
        public int ScrollTopWrapRow;

        private string contentText = "";
        private bool contentStale;
        private int editVersion;
        private string savedLinesRef;
        private string[] savedLinesCache;

        /// <summary>Full text with "\n" line ends. Assigning it does not update <see cref="Lines"/> (call SyncLinesFromContent).</summary>
        public string Content
        {
            get
            {
                if (contentStale)
                {
                    List<string> l = Lines;
                    contentText = (l == null || l.Count == 0) ? "" : string.Join("\n", l);
                    contentStale = false;
                }
                return contentText;
            }
            set
            {
                contentText = value ?? "";
                contentStale = false;
            }
        }

        /// <summary>True when <see cref="Content"/> is up to date without a rebuild (tests / diagnostics).</summary>
        public bool IsContentMaterialized { get { return !contentStale; } }

        /// <summary>Incremented on every change of <see cref="Lines"/> reported through SyncContentFromLines / undo.</summary>
        public int EditVersion { get { return editVersion; } }

        /// <summary>Marks the text as changed (Content becomes stale, caches keyed by <see cref="EditVersion"/> refresh).</summary>
        public void MarkLinesChanged()
        {
            contentStale = true;
            editVersion++;
            EditorFoldState.OnLinesChanged(this);
        }

        /// <summary>Lines were replaced wholesale (file load / revert): content is current, folds and caches reset.</summary>
        private void OnLinesReplaced()
        {
            editVersion++;
            EditorFoldState.OnLinesReplaced(this);
        }

        /// <summary>Total characters of the document including "\n" separators, without materializing it.</summary>
        public long TextLength
        {
            get
            {
                if (!contentStale) return contentText.Length;
                List<string> l = Lines;
                if (l == null || l.Count == 0) return 0;
                long t = l.Count - 1;
                for (int i = 0; i < l.Count; i++) t += (l[i] ?? "").Length;
                return t;
            }
        }

        private bool ComputeIsLargeFile(long knownLength)
        {
            int n = Lines != null ? Lines.Count : 0;
            if (n > EditorLargeFileThresholds.LineThreshold) return true;
            long len = knownLength >= 0 ? knownLength : TextLength;
            return len > EditorLargeFileThresholds.CharThreshold;
        }

        /// <summary>Splits normalized text ("\n" only) into lines; "" gives one empty line.</summary>
        public static string[] SplitNormalizedLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[] { "" };
            int count = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') count++;
            string[] res = new string[count];
            int start = 0, k = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                res[k++] = text.Substring(start, i - start);
                start = i + 1;
            }
            res[k] = text.Substring(start);
            return res;
        }

        private string[] GetSavedLines()
        {
            string raw = SavedContent ?? "";
            if (!ReferenceEquals(raw, savedLinesRef) || savedLinesCache == null)
            {
                savedLinesRef = raw;
                string norm = raw.IndexOf('\r') >= 0 ? raw.Replace("\r\n", "\n").Replace("\r", "\n") : raw;
                savedLinesCache = SplitNormalizedLines(norm);
            }
            return savedLinesCache;
        }

        /// <summary>
        /// Whether the lines differ from the saved text. O(1) when the line count changed or the hinted (edited) line
        /// differs; otherwise a reference-first line comparison (no text is built).
        /// </summary>
        public bool ComputeDirtyFromLines(int hintLine)
        {
            List<string> l = Lines;
            string[] saved = GetSavedLines();
            if (l == null) return saved.Length != 1 || saved[0].Length != 0;
            if (saved.Length != l.Count) return true;
            if (hintLine >= 0 && hintLine < l.Count && !ReferenceEquals(saved[hintLine], l[hintLine]) &&
                !string.Equals(saved[hintLine], l[hintLine], StringComparison.Ordinal))
                return true;
            for (int i = 0; i < saved.Length; i++)
            {
                if (ReferenceEquals(saved[i], l[i])) continue;
                if (!string.Equals(saved[i], l[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
