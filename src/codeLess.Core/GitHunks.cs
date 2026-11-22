using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One line of a hunk: ' ' context (old line), '-' removed (old line), '+' added (new text).</summary>
    public struct GitHunkLine
    {
        public char Op;
        /// <summary>0-based line in the base text for ' ' and '-', -1 for '+'.</summary>
        public int OldIndex;
        /// <summary>0-based line in the new text for ' ' and '+', -1 for '-'.</summary>
        public int NewIndex;
        public string Text;
    }

    /// <summary>A contiguous change between a base (index / HEAD) text and the editor text. 0-based line numbers.</summary>
    public sealed class GitDiffHunk
    {
        public int OldStart;
        public int OldCount;
        public int NewStart;
        public int NewCount;
        /// <summary>Only '-' and '+' lines, removed lines first (unified diff order).</summary>
        public List<GitHunkLine> Lines = new List<GitHunkLine>();

        public bool IsPureAddition { get { return OldCount == 0; } }
        public bool IsPureDeletion { get { return NewCount == 0; } }

        /// <summary>True when the gutter bar for <paramref name="newLine"/> belongs to this hunk (deletions sit between lines).</summary>
        public bool TouchesNewLine(int newLine)
        {
            if (NewCount == 0) return newLine == NewStart || newLine == NewStart - 1;
            return newLine >= NewStart && newLine < NewStart + NewCount;
        }

        public List<string> OldTexts()
        {
            List<string> r = new List<string>();
            foreach (GitHunkLine l in Lines) if (l.Op == '-') r.Add(GitHunkDiff.StripMarker(l.Text));
            return r;
        }

        public List<string> NewTexts()
        {
            List<string> r = new List<string>();
            foreach (GitHunkLine l in Lines) if (l.Op == '+') r.Add(GitHunkDiff.StripMarker(l.Text));
            return r;
        }

        public string Summary
        {
            get
            {
                int add = 0, del = 0;
                foreach (GitHunkLine l in Lines) { if (l.Op == '+') add++; else if (l.Op == '-') del++; }
                return "+" + add.ToString(CultureInfo.InvariantCulture) + " -" + del.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Hunk computation and patch building for "Stage Hunk", "Revert Hunk" and "Stage Selected Ranges" (shared, C# 5).
    /// The patch is built against the index content and applied with "git apply --cached", so only the chosen change
    /// reaches the index; the working file and the editor buffer are untouched.
    /// </summary>
    public static class GitHunkDiff
    {
        /// <summary>Appended to the last line when a text has no final newline, so "\ No newline at end of file" diffs are exact.</summary>
        public const string NoEolMarker = "\u0001\u0002noeol";

        public static string StripMarker(string s)
        {
            if (s != null && s.EndsWith(NoEolMarker, StringComparison.Ordinal)) return s.Substring(0, s.Length - NoEolMarker.Length);
            return s ?? "";
        }

        public static bool HasMarker(string s) { return s != null && s.EndsWith(NoEolMarker, StringComparison.Ordinal); }

        /// <summary>Splits into lines without terminators; the last line carries <see cref="NoEolMarker"/> when there is no final newline.</summary>
        public static string[] SplitForDiff(string text)
        {
            string t = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (t.Length == 0) return new string[0];
            bool finalNl = t.EndsWith("\n", StringComparison.Ordinal);
            string[] lines = LineDiff.SplitLines(t);
            if (!finalNl && lines.Length > 0) lines[lines.Length - 1] = lines[lines.Length - 1] + NoEolMarker;
            return lines;
        }

        /// <summary>Editor lines (no terminators) to diff lines; <paramref name="hasFinalNewline"/> mirrors the buffer's flag.</summary>
        public static string[] FromEditorLines(IList<string> editorLines, bool hasFinalNewline)
        {
            if (editorLines == null) return new string[0];
            List<string> l = new List<string>(editorLines);
            // A buffer ending with "" represents a trailing newline in the saved file.
            if (l.Count > 0 && l[l.Count - 1].Length == 0) { l.RemoveAt(l.Count - 1); hasFinalNewline = true; }
            if (l.Count == 0) return new string[0];
            if (!hasFinalNewline) l[l.Count - 1] = l[l.Count - 1] + NoEolMarker;
            return l.ToArray();
        }

        public static List<GitDiffHunk> ComputeHunks(string[] oldLines, string[] newLines)
        {
            List<GitDiffHunk> hunks = new List<GitDiffHunk>();
            List<int[]> ops = LineDiff.ComputeEditScript(oldLines, newLines);
            int i = 0;
            int oldPos = 0, newPos = 0;
            while (i < ops.Count)
            {
                if (ops[i][0] == 0) { oldPos = ops[i][1] + 1; newPos = ops[i][2] + 1; i++; continue; }
                GitDiffHunk h = new GitDiffHunk();
                h.OldStart = oldPos;
                h.NewStart = newPos;
                List<GitHunkLine> dels = new List<GitHunkLine>();
                List<GitHunkLine> adds = new List<GitHunkLine>();
                while (i < ops.Count && ops[i][0] != 0)
                {
                    GitHunkLine hl = new GitHunkLine();
                    if (ops[i][0] == 1)
                    {
                        hl.Op = '-'; hl.OldIndex = ops[i][1]; hl.NewIndex = -1; hl.Text = oldLines[hl.OldIndex];
                        dels.Add(hl);
                        oldPos = hl.OldIndex + 1;
                    }
                    else
                    {
                        hl.Op = '+'; hl.OldIndex = -1; hl.NewIndex = ops[i][2]; hl.Text = newLines[hl.NewIndex];
                        adds.Add(hl);
                        newPos = hl.NewIndex + 1;
                    }
                    i++;
                }
                h.OldCount = dels.Count;
                h.NewCount = adds.Count;
                h.Lines.AddRange(dels);
                h.Lines.AddRange(adds);
                hunks.Add(h);
            }
            return hunks;
        }

        public static List<GitDiffHunk> ComputeHunks(string baseText, IList<string> editorLines, bool hasFinalNewline)
        {
            return ComputeHunks(SplitForDiff(baseText), FromEditorLines(editorLines, hasFinalNewline));
        }

        public static GitDiffHunk HunkAtLine(List<GitDiffHunk> hunks, int newLine)
        {
            if (hunks == null) return null;
            foreach (GitDiffHunk h in hunks) if (h.NewCount > 0 && h.TouchesNewLine(newLine)) return h;
            foreach (GitDiffHunk h in hunks) if (h.TouchesNewLine(newLine)) return h;
            return null;
        }

        /// <summary>
        /// Restricts hunks to a selected range of new-text lines (inclusive): added lines outside the range are dropped,
        /// removed lines outside it stay as they are (become context). Returns only hunks that still change something.
        /// </summary>
        public static List<GitDiffHunk> SelectLines(List<GitDiffHunk> hunks, int fromNewLine, int toNewLine)
        {
            List<GitDiffHunk> res = new List<GitDiffHunk>();
            if (hunks == null) return res;
            if (toNewLine < fromNewLine) { int t = fromNewLine; fromNewLine = toNewLine; toNewLine = t; }
            foreach (GitDiffHunk h in hunks)
            {
                // Removed lines are anchored where they disappeared: the block's first new line (a modified line), or the
                // gap between the neighbouring lines for pure deletions.
                int anchorFrom = h.NewCount > 0 ? h.NewStart : h.NewStart - 1;
                int anchorTo = h.NewStart;
                bool delsSelected = anchorTo >= fromNewLine && anchorFrom <= toNewLine;
                GitDiffHunk sub = new GitDiffHunk();
                sub.OldStart = h.OldStart;
                sub.NewStart = h.NewStart;
                bool changes = false;
                foreach (GitHunkLine l in h.Lines)
                {
                    if (l.Op == '+')
                    {
                        if (l.NewIndex >= fromNewLine && l.NewIndex <= toNewLine) { sub.Lines.Add(l); changes = true; }
                    }
                    else if (l.Op == '-')
                    {
                        if (delsSelected) { sub.Lines.Add(l); changes = true; }
                        else
                        {
                            GitHunkLine ctx = l;
                            ctx.Op = ' ';
                            sub.Lines.Add(ctx);
                        }
                    }
                }
                if (!changes) continue;
                // Keep unified-diff order: context/removed (old order) first, then additions.
                List<GitHunkLine> olds = new List<GitHunkLine>();
                List<GitHunkLine> news = new List<GitHunkLine>();
                foreach (GitHunkLine l in sub.Lines) { if (l.Op == '+') news.Add(l); else olds.Add(l); }
                sub.Lines.Clear();
                // Context lines that were removed-but-unselected keep their position among the old lines.
                sub.Lines.AddRange(olds);
                sub.Lines.AddRange(news);
                sub.OldCount = olds.Count;
                int newCount = news.Count;
                foreach (GitHunkLine l in olds) if (l.Op == ' ') newCount++;
                sub.NewCount = newCount;
                res.Add(sub);
            }
            return res;
        }

        /// <summary>
        /// Builds a unified diff (with <paramref name="context"/> lines of context taken from <paramref name="oldLines"/>)
        /// that applies <paramref name="hunks"/> to the base text. Hunks whose contexts touch are merged into one.
        /// <paramref name="crlf"/> writes lines with CRLF to match an index that stores CRLF.
        /// </summary>
        public static string BuildPatch(string repoRelPath, string[] oldLines, List<GitDiffHunk> hunks, int context, bool crlf)
        {
            return BuildPatch(repoRelPath, oldLines, hunks, context, crlf, false);
        }

        /// <param name="useHunkNewStart">Use the hunks' own new-side positions (all hunks between old and new were computed
        /// together, e.g. HEAD to index for "Unstage"); otherwise the new side is derived from the included hunks only.</param>
        public static string BuildPatch(string repoRelPath, string[] oldLines, List<GitDiffHunk> hunks, int context, bool crlf, bool useHunkNewStart)
        {
            if (hunks == null || hunks.Count == 0) return "";
            List<GitDiffHunk> sorted = new List<GitDiffHunk>(hunks);
            sorted.Sort(delegate (GitDiffHunk a, GitDiffHunk b) { return a.OldStart.CompareTo(b.OldStart); });
            string eol = crlf ? "\r\n" : "\n";
            string path = (repoRelPath ?? "file").Replace('\\', '/');
            StringBuilder sb = new StringBuilder();
            sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
            sb.Append("--- a/").Append(path).Append('\n');
            sb.Append("+++ b/").Append(path).Append('\n');

            int delta = 0; // new-side line shift caused by groups already written
            int gi = 0;
            while (gi < sorted.Count)
            {
                // Group hunks whose context windows overlap.
                int gj = gi;
                while (gj + 1 < sorted.Count && sorted[gj + 1].OldStart - (sorted[gj].OldStart + OldSpan(sorted[gj])) <= 2 * context) gj++;
                int firstOld = sorted[gi].OldStart;
                int lastOldEnd = sorted[gj].OldStart + OldSpan(sorted[gj]); // exclusive
                int ctxStart = Math.Max(0, firstOld - context);
                int ctxEnd = Math.Min(oldLines.Length, lastOldEnd + context);

                List<KeyValuePair<char, string>> body = new List<KeyValuePair<char, string>>();
                int pos = ctxStart;
                for (int k = gi; k <= gj; k++)
                {
                    GitDiffHunk h = sorted[k];
                    while (pos < h.OldStart && pos < oldLines.Length) { body.Add(new KeyValuePair<char, string>(' ', oldLines[pos])); pos++; }
                    foreach (GitHunkLine l in h.Lines)
                    {
                        if (l.Op == '+') continue;
                        body.Add(new KeyValuePair<char, string>(l.Op, l.Op == ' ' && l.OldIndex >= 0 && l.OldIndex < oldLines.Length ? oldLines[l.OldIndex] : l.Text));
                        pos = Math.Max(pos, l.OldIndex + 1);
                    }
                    foreach (GitHunkLine l in h.Lines) if (l.Op == '+') body.Add(new KeyValuePair<char, string>('+', l.Text));
                    pos = Math.Max(pos, h.OldStart + OldSpan(h));
                }
                while (pos < ctxEnd) { body.Add(new KeyValuePair<char, string>(' ', oldLines[pos])); pos++; }

                int oldCount = 0, newCount = 0;
                foreach (KeyValuePair<char, string> kv in body)
                {
                    if (kv.Key != '+') oldCount++;
                    if (kv.Key != '-') newCount++;
                }
                int oldStartNum = oldCount == 0 ? ctxStart : ctxStart + 1;
                int newStartNum = ctxStart + delta + (newCount == 0 ? 0 : 1);
                if (useHunkNewStart) newStartNum = sorted[gi].NewStart - (firstOld - ctxStart) + (newCount == 0 ? 0 : 1);
                sb.Append("@@ -").Append(oldStartNum.ToString(CultureInfo.InvariantCulture)).Append(',').Append(oldCount.ToString(CultureInfo.InvariantCulture));
                sb.Append(" +").Append(newStartNum.ToString(CultureInfo.InvariantCulture)).Append(',').Append(newCount.ToString(CultureInfo.InvariantCulture)).Append(" @@\n");
                foreach (KeyValuePair<char, string> kv in body)
                {
                    sb.Append(kv.Key).Append(StripMarker(kv.Value));
                    if (HasMarker(kv.Value)) sb.Append('\n').Append("\\ No newline at end of file").Append('\n');
                    else sb.Append(eol);
                }
                delta += newCount - oldCount;
                gi = gj + 1;
            }
            return sb.ToString();
        }

        private static int OldSpan(GitDiffHunk h)
        {
            int n = 0;
            foreach (GitHunkLine l in h.Lines) if (l.Op != '+') n++;
            return n;
        }

        /// <summary>Applies hunks to the base lines (what the index will contain after staging them). Used by tests and as a fallback.</summary>
        public static List<string> ApplyToBase(string[] oldLines, List<GitDiffHunk> hunks)
        {
            List<string> result = new List<string>();
            List<GitDiffHunk> sorted = new List<GitDiffHunk>(hunks ?? new List<GitDiffHunk>());
            sorted.Sort(delegate (GitDiffHunk a, GitDiffHunk b) { return a.OldStart.CompareTo(b.OldStart); });
            int pos = 0;
            foreach (GitDiffHunk h in sorted)
            {
                while (pos < h.OldStart && pos < oldLines.Length) { result.Add(oldLines[pos]); pos++; }
                foreach (GitHunkLine l in h.Lines)
                {
                    if (l.Op == ' ') result.Add(oldLines[l.OldIndex]);
                    if (l.Op != '+') pos = Math.Max(pos, l.OldIndex + 1);
                }
                foreach (GitHunkLine l in h.Lines) if (l.Op == '+') result.Add(l.Text);
            }
            while (pos < oldLines.Length) { result.Add(oldLines[pos]); pos++; }
            return result;
        }

        /// <summary>Joins diff lines back to text (restores the final newline unless the last line carries the marker).</summary>
        public static string JoinDiffLines(IList<string> lines, string eol)
        {
            if (lines == null || lines.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(StripMarker(lines[i]));
                if (!HasMarker(lines[i])) sb.Append(eol ?? "\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>Editor-side hunk operations (no git).</summary>
    public static class GitHunkEditor
    {
        /// <summary>
        /// "Revert Change": replaces the hunk's new lines with the base lines in the buffer (one undo step). The hunk must
        /// have been computed from the buffer's current lines.
        /// </summary>
        public static bool RevertInBuffer(EditorTabBuffer buf, GitDiffHunk h)
        {
            if (buf == null || h == null || buf.Lines == null) return false;
            List<string> lines = new List<string>(buf.Lines);
            bool trailingEmpty = lines.Count > 0 && lines[lines.Count - 1].Length == 0;
            int contentCount = trailingEmpty ? lines.Count - 1 : lines.Count;
            if (h.NewStart < 0 || h.NewStart + h.NewCount > contentCount) return false;
            bool oldHadNoEol = false;
            List<string> olds = new List<string>();
            foreach (GitHunkLine l in h.Lines)
                if (l.Op == '-') { olds.Add(GitHunkDiff.StripMarker(l.Text)); if (GitHunkDiff.HasMarker(l.Text)) oldHadNoEol = true; }
            bool newHadNoEol = false;
            foreach (GitHunkLine l in h.Lines) if (l.Op == '+' && GitHunkDiff.HasMarker(l.Text)) newHadNoEol = true;
            buf.PushUndoSnapshot();
            lines.RemoveRange(h.NewStart, h.NewCount);
            lines.InsertRange(h.NewStart, olds);
            if (oldHadNoEol)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length == 0 && lines.Count > olds.Count + h.NewStart) lines.RemoveAt(lines.Count - 1);
                buf.HasFinalNewline = false;
            }
            else if (newHadNoEol)
            {
                buf.HasFinalNewline = true;
            }
            buf.Lines = lines;
            buf.CursorLine = Math.Min(h.NewStart, Math.Max(0, lines.Count - 1));
            buf.CursorCol = 0;
            buf.ClearSelection();
            buf.ClampCursor();
            buf.SyncContentFromLines();
            return true;
        }
    }

    /// <summary>Index-level operations for hunks (runs git; call from a background thread).</summary>
    public static class GitHunkStaging
    {
        /// <summary>Raw index content of a file (null when the file is not in the index, e.g. untracked).</summary>
        public static string ReadIndexContent(string repo, string repoRelPath, out bool isBinary)
        {
            isBinary = false;
            GitCommandResult r = GitProcess.Run(repo, new string[] { "show", ":" + repoRelPath }, 20000, CancellationToken.None, null, null);
            if (!r.Success) return null;
            if (r.Stdout.IndexOf('\0') >= 0) { isBinary = true; return null; }
            return r.Stdout;
        }

        /// <summary>Applies a patch to the index only ("git apply --cached").</summary>
        public static GitCommandResult ApplyToIndex(string repo, string patch, bool reverse)
        {
            List<string> args = new List<string>();
            args.Add("apply");
            args.Add("--cached");
            args.Add("--whitespace=nowarn");
            if (reverse) args.Add("--reverse");
            args.Add("-");
            return GitProcess.Run(repo, args.ToArray(), 30000, CancellationToken.None, patch, null);
        }

        /// <summary>
        /// Stages the given hunks (computed against the current index content <paramref name="indexContent"/>).
        /// </summary>
        public static GitOperationResult StageHunks(string repo, string repoRelPath, string indexContent, List<GitDiffHunk> hunks)
        {
            if (hunks == null || hunks.Count == 0)
            {
                GitOperationResult none = new GitOperationResult();
                none.Error = GitErrorKind.Other;
                none.Message = "Nothing to stage here.";
                return none;
            }
            bool crlf = (indexContent ?? "").IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            string[] oldLines = GitHunkDiff.SplitForDiff(indexContent);
            string patch = GitHunkDiff.BuildPatch(repoRelPath, oldLines, hunks, 3, crlf);
            GitCommandResult r = ApplyToIndex(repo, patch, false);
            if (!r.Success)
            {
                // Retry without context (handles whitespace-only context drift).
                string patch0 = GitHunkDiff.BuildPatch(repoRelPath, oldLines, hunks, 0, crlf);
                GitCommandResult r0 = GitProcess.Run(repo, new string[] { "apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", "-" }, 30000, CancellationToken.None, patch0, null);
                if (r0.Success) r = r0;
            }
            return GitErrorClassifier.ToResult(r, "Stage", hunks.Count == 1 ? "Staged change (" + hunks[0].Summary + ")." : "Staged " + hunks.Count.ToString(CultureInfo.InvariantCulture) + " changes.");
        }

        /// <summary>
        /// For a gutter click on a change that is already staged: maps the editor line to the index through the
        /// index/editor diff and returns the HEAD-to-index hunk there (null when none).
        /// </summary>
        public static GitDiffHunk FindStagedHunkAtEditorLine(string headContent, string indexContent, IList<string> editorLines, bool hasFinalNewline, int editorLine)
        {
            if (indexContent == null) return null;
            string[] idx = GitHunkDiff.SplitForDiff(indexContent);
            string[] ed = GitHunkDiff.FromEditorLines(editorLines, hasFinalNewline);
            int indexLine = -1;
            foreach (int[] op in LineDiff.ComputeEditScript(idx, ed))
                if (op[0] == 0 && op[2] == editorLine) { indexLine = op[1]; break; }
            if (indexLine < 0) return null;
            List<GitDiffHunk> staged = GitHunkDiff.ComputeHunks(GitHunkDiff.SplitForDiff(headContent ?? ""), idx);
            return GitHunkDiff.HunkAtLine(staged, indexLine);
        }

        /// <summary>"Unstage Change": reverse-applies one HEAD-to-index hunk to the index.</summary>
        public static GitOperationResult UnstageHunk(string repo, string repoRelPath, string headContent, string indexContent, GitDiffHunk h)
        {
            bool crlf = (indexContent ?? "").IndexOf("\r\n", StringComparison.Ordinal) >= 0;
            string[] headLines = GitHunkDiff.SplitForDiff(headContent ?? "");
            List<GitDiffHunk> one = new List<GitDiffHunk>();
            one.Add(h);
            string patch = GitHunkDiff.BuildPatch(repoRelPath, headLines, one, 3, crlf, true);
            GitCommandResult r = ApplyToIndex(repo, patch, true);
            if (!r.Success)
            {
                string patch0 = GitHunkDiff.BuildPatch(repoRelPath, headLines, one, 0, crlf, true);
                GitCommandResult r0 = GitProcess.Run(repo, new string[] { "apply", "--cached", "--reverse", "--unidiff-zero", "--whitespace=nowarn", "-" }, 30000, CancellationToken.None, patch0, null);
                if (r0.Success) r = r0;
            }
            return GitErrorClassifier.ToResult(r, "Unstage", "Unstaged change (" + h.Summary + ").");
        }

        /// <summary>HEAD content of a file ("" when it does not exist in HEAD, e.g. a newly added file).</summary>
        public static string ReadHeadContent(string repo, string repoRelPath)
        {
            GitCommandResult r = GitProcess.Run(repo, new string[] { "show", "HEAD:" + repoRelPath }, 20000, CancellationToken.None, null, null);
            return r.Success ? r.Stdout : "";
        }

        /// <summary>
        /// Stages a hunk computed between the editor content and the index; untracked files (no index entry) are
        /// added as a whole with "git add" like VS Code.
        /// </summary>
        public static GitOperationResult StageFromEditor(string repo, string repoRelPath, IList<string> editorLines, bool hasFinalNewline, int fromLine, int toLine, bool wholeHunkAtFromLine)
        {
            bool bin;
            string index = ReadIndexContent(repo, repoRelPath, out bin);
            if (bin)
            {
                GitOperationResult b = new GitOperationResult();
                b.Error = GitErrorKind.Other;
                b.Message = "Can't stage parts of a binary file.";
                return b;
            }
            if (index == null)
            {
                GitCommandResult add = GitProcess.Run(repo, new string[] { "add", "--", repoRelPath }, 30000, CancellationToken.None, null, null);
                return GitErrorClassifier.ToResult(add, "Stage", "Staged new file.");
            }
            List<GitDiffHunk> hunks = GitHunkDiff.ComputeHunks(index, editorLines, hasFinalNewline);
            List<GitDiffHunk> chosen;
            if (wholeHunkAtFromLine)
            {
                GitDiffHunk h = GitHunkDiff.HunkAtLine(hunks, fromLine);
                chosen = new List<GitDiffHunk>();
                if (h != null) chosen.Add(h);
            }
            else chosen = GitHunkDiff.SelectLines(hunks, fromLine, toLine);
            return StageHunks(repo, repoRelPath, index, chosen);
        }
    }
}
