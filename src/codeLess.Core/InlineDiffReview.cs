using System;
using System.Collections.Generic;
using System.Linq;

namespace codeLess.Studio
{
    /// <summary>
    /// In-editor review of a proposed full-file text (agent proposals, inline AI edits, code-block apply), shared by the
    /// WinForms and Avalonia editors.
    ///
    /// Model: the buffer holds the proposal (what the user sees and edits), <see cref="EditorTabBuffer.PreDiffContent"/>
    /// holds the base it is compared with. Pending hunks are always <b>derived</b> from (base, buffer) — they are
    /// recomputed after every decision and every edit, so a user edit during review never leaves stale line indices.
    /// <list type="bullet">
    /// <item>Accept hunk: the hunk is folded into the base (the buffer doesn't change).</item>
    /// <item>Reject hunk: the hunk's added lines in the buffer are replaced by all of its removed lines.</item>
    /// <item>Accept all: base := buffer. Reject all: buffer := base (hunks accepted earlier stay accepted).</item>
    /// </list>
    /// Text is compared with LF line breaks; the file's own EOL/encoding/final newline are kept by the buffer on save.
    /// Accepting never writes to disk here: agent change sets are saved by the agent review layer, every other inline
    /// proposal stays in the buffer (dirty) exactly like VS Code's inline chat — saved by Ctrl+S or auto save.
    /// </summary>
    public static class InlineDiffReview
    {
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.IndexOf('\r') >= 0 ? text.Replace("\r\n", "\n").Replace("\r", "\n") : text;
        }

        public static bool HasPending(EditorTabBuffer buf)
        {
            return buf != null && buf.PreDiffContent != null && buf.DiffHunks != null && buf.DiffHunks.Any(h => h.IsPending);
        }

        /// <summary>Stages <paramref name="proposed"/> in the buffer for review (undoable). Returns the number of hunks.</summary>
        public static int Stage(EditorTabBuffer buf, string proposed)
        {
            if (buf == null || proposed == null) return 0;
            if (buf.Lines != null) buf.SyncContentFromLines(false);
            string current = Normalize(buf.Content);
            if (buf.PreDiffContent == null) buf.PreDiffContent = current;
            string baseText = Normalize(buf.PreDiffContent);
            string norm = MatchFinalNewline(baseText, Normalize(proposed), buf.IsReadOnlyDiffPreview);
            if (!string.Equals(norm, current, StringComparison.Ordinal))
            {
                buf.PushUndoSnapshot();
                buf.Content = norm;
                buf.SyncLinesFromContent();
            }
            int n = Refresh(buf);
            UpdateDirty(buf);
            return n;
        }

        /// <summary>
        /// A proposal that only differs from the base by a dropped / added final newline would show a spurious hunk on
        /// the last line (and the file's final newline is restored on save anyway): follow the base.
        /// </summary>
        public static string MatchFinalNewline(string baseText, string proposed, bool readOnlyPreview)
        {
            if (readOnlyPreview || string.IsNullOrEmpty(baseText) || string.IsNullOrEmpty(proposed)) return proposed;
            bool baseNl = baseText.EndsWith("\n", StringComparison.Ordinal);
            bool propNl = proposed.EndsWith("\n", StringComparison.Ordinal);
            if (baseNl && !propNl) return proposed + "\n";
            if (!baseNl && propNl) return proposed.Substring(0, proposed.Length - 1);
            return proposed;
        }

        /// <summary>Recomputes the pending hunks from (base, buffer). Ends the review when nothing differs.</summary>
        public static int Refresh(EditorTabBuffer buf)
        {
            if (buf == null) return 0;
            if (buf.DiffHunks == null) buf.DiffHunks = new List<EditorDiffHunk>();
            if (buf.PreDiffContent == null)
            {
                buf.DiffHunks.Clear();
                return 0;
            }
            string current = buf.Lines != null ? string.Join("\n", buf.Lines.ToArray()) : Normalize(buf.Content);
            buf.DiffHunks = LlmHarnessAndAgentEngine.ComputeLineDiffHunks(buf.PreDiffContent, current);
            if (buf.DiffHunks.Count == 0) buf.PreDiffContent = null;
            return buf.DiffHunks.Count;
        }

        /// <summary>Line in the buffer where a hunk's actions are shown (pure deletions anchor on the following line).</summary>
        public static int AnchorLine(EditorTabBuffer buf, EditorDiffHunk h)
        {
            int count = buf != null && buf.Lines != null ? buf.Lines.Count : 1;
            return Math.Max(0, Math.Min(Math.Max(0, count - 1), h.StartLine));
        }

        /// <summary>The pending hunk shown at <paramref name="line"/> (inside its added lines, or its anchor line).</summary>
        public static EditorDiffHunk FindHunkAtLine(EditorTabBuffer buf, int line)
        {
            if (buf == null || buf.DiffHunks == null) return null;
            foreach (EditorDiffHunk h in buf.DiffHunks)
                if (h.IsPending && h.AddedLines.Count > 0 && line >= h.StartLine && line < h.StartLine + h.AddedLines.Count) return h;
            foreach (EditorDiffHunk h in buf.DiffHunks)
                if (h.IsPending && h.AddedLines.Count == 0 && AnchorLine(buf, h) == line) return h;
            return null;
        }

        /// <summary>Finds the fresh hunk that corresponds to a (possibly stale) hunk object: same text, nearest position.</summary>
        public static EditorDiffHunk Match(EditorTabBuffer buf, EditorDiffHunk stale)
        {
            if (buf == null || stale == null || buf.DiffHunks == null) return null;
            EditorDiffHunk best = null;
            int bestDist = int.MaxValue;
            foreach (EditorDiffHunk h in buf.DiffHunks)
            {
                if (!h.IsPending || !SameLines(h.AddedLines, stale.AddedLines) || !SameLines(h.RemovedLines, stale.RemovedLines)) continue;
                int d = Math.Abs(h.StartLine - stale.StartLine);
                if (d < bestDist) { best = h; bestDist = d; }
            }
            return best ?? FindHunkAtLine(buf, stale.StartLine);
        }

        public static bool AcceptHunkAtLine(EditorTabBuffer buf, int line)
        {
            Refresh(buf);
            return AcceptFresh(buf, FindHunkAtLine(buf, line));
        }

        public static bool AcceptHunk(EditorTabBuffer buf, EditorDiffHunk hunk)
        {
            Refresh(buf);
            return AcceptFresh(buf, Match(buf, hunk));
        }

        public static bool RejectHunkAtLine(EditorTabBuffer buf, int line)
        {
            Refresh(buf);
            return RejectFresh(buf, FindHunkAtLine(buf, line));
        }

        public static bool RejectHunk(EditorTabBuffer buf, EditorDiffHunk hunk)
        {
            Refresh(buf);
            return RejectFresh(buf, Match(buf, hunk));
        }

        /// <summary>Keeps the buffer as it is and ends the review.</summary>
        public static void AcceptAll(EditorTabBuffer buf)
        {
            if (buf == null) return;
            buf.PreDiffContent = null;
            if (buf.DiffHunks != null) buf.DiffHunks.Clear();
            UpdateDirty(buf);
            buf.RefreshGutterDiff();
        }

        /// <summary>Restores the base (hunks accepted earlier stay) and ends the review. Undoable.</summary>
        public static void RejectAll(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (buf.PreDiffContent != null)
            {
                string baseText = Normalize(buf.PreDiffContent);
                if (buf.Lines != null) buf.SyncContentFromLines(false);
                if (!string.Equals(baseText, Normalize(buf.Content), StringComparison.Ordinal))
                {
                    buf.PushUndoSnapshot();
                    buf.Content = baseText;
                    buf.SyncLinesFromContent();
                }
            }
            buf.PreDiffContent = null;
            if (buf.DiffHunks != null) buf.DiffHunks.Clear();
            UpdateDirty(buf);
            buf.RefreshGutterDiff();
        }

        /// <summary>Start of the hunk in the base text (the hunk's <c>StartLine</c> is in the buffer).</summary>
        public static int BaseStartLine(List<EditorDiffHunk> hunks, EditorDiffHunk target)
        {
            int delta = 0;
            foreach (EditorDiffHunk h in hunks.OrderBy(x => x.StartLine))
            {
                if (ReferenceEquals(h, target)) return h.StartLine - delta;
                delta += h.AddedLines.Count - h.RemovedLines.Count;
            }
            return -1;
        }

        private static bool AcceptFresh(EditorTabBuffer buf, EditorDiffHunk h)
        {
            if (buf == null || h == null || buf.PreDiffContent == null) return false;
            int oldStart = BaseStartLine(buf.DiffHunks, h);
            List<string> baseLines = new List<string>(Normalize(buf.PreDiffContent).Split('\n'));
            if (oldStart < 0 || oldStart + h.RemovedLines.Count > baseLines.Count) return false;
            for (int i = 0; i < h.RemovedLines.Count; i++)
                if (!string.Equals(baseLines[oldStart + i], h.RemovedLines[i], StringComparison.Ordinal)) return false;
            baseLines.RemoveRange(oldStart, h.RemovedLines.Count);
            baseLines.InsertRange(oldStart, h.AddedLines);
            buf.PreDiffContent = string.Join("\n", baseLines.ToArray());
            Refresh(buf);
            buf.RefreshGutterDiff();
            return true;
        }

        private static bool RejectFresh(EditorTabBuffer buf, EditorDiffHunk h)
        {
            if (buf == null || h == null || buf.Lines == null) return false;
            int start = h.StartLine;
            if (start < 0 || start + h.AddedLines.Count > buf.Lines.Count) return false;
            for (int i = 0; i < h.AddedLines.Count; i++)
                if (!string.Equals(buf.Lines[start + i], h.AddedLines[i], StringComparison.Ordinal)) return false;
            buf.PushUndoSnapshot();
            buf.Lines.RemoveRange(start, h.AddedLines.Count);
            buf.Lines.InsertRange(start, h.RemovedLines);
            if (buf.Lines.Count == 0) buf.Lines.Add("");
            buf.SyncContentFromLines(false);
            Refresh(buf);
            UpdateDirty(buf);
            buf.RefreshGutterDiff();
            buf.ClampCursor();
            return true;
        }

        private static void UpdateDirty(EditorTabBuffer buf)
        {
            string saved = Normalize(buf.SavedContent);
            string cur = Normalize(buf.Content);
            // Same rule as the save path: the final newline is the file's, not part of the comparison.
            buf.IsDirty = !string.Equals(cur, saved, StringComparison.Ordinal)
                && !string.Equals(buf.GetNormalizedContentRespectingFinalNewline(), saved, StringComparison.Ordinal);
            if (buf.IsUntitled && cur.Length > 0) buf.IsDirty = true;
        }

        private static bool SameLines(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
