using System;
using System.Collections.Generic;
using System.Text;

namespace codeLess.Studio
{
    public enum ConflictResolution { Current = 0, Incoming = 1, Both = 2, Base = 3 }

    /// <summary>Per-line role inside a merge conflict (used for editor highlighting).</summary>
    public enum ConflictLineKind : byte
    {
        None = 0,
        CurrentHeader = 1,
        Current = 2,
        BaseHeader = 3,
        Base = 4,
        Separator = 5,
        Incoming = 6,
        IncomingHeader = 7
    }

    /// <summary>
    /// One &lt;&lt;&lt;&lt;&lt;&lt;&lt; / (||||||| base) / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt; block (0-based line numbers).
    /// </summary>
    public sealed class ConflictBlock
    {
        public int StartLine;
        /// <summary>"|||||||" line of diff3 / zdiff3 style conflicts, -1 otherwise.</summary>
        public int BaseLine = -1;
        public int SeparatorLine;
        public int EndLine;
        public string CurrentLabel = "";
        public string BaseLabel = "";
        public string IncomingLabel = "";

        public int CurrentFirst { get { return StartLine + 1; } }
        /// <summary>Exclusive end of the current (ours) content.</summary>
        public int CurrentEndExclusive { get { return BaseLine >= 0 ? BaseLine : SeparatorLine; } }
        public int IncomingFirst { get { return SeparatorLine + 1; } }
        public int IncomingEndExclusive { get { return EndLine; } }
        public int LineCount { get { return EndLine - StartLine + 1; } }

        public bool Contains(int line) { return line >= StartLine && line <= EndLine; }
    }

    /// <summary>
    /// Merge-conflict marker parsing and resolution shared by both editors (C# 5). Markers follow git: exactly seven
    /// characters at the start of a line, followed by end of line or a space and a label.
    /// </summary>
    public static class GitConflictParser
    {
        public const int MarkerLength = 7;

        private static bool IsMarker(string line, char c, bool allowLabel)
        {
            if (line == null || line.Length < MarkerLength) return false;
            for (int i = 0; i < MarkerLength; i++) if (line[i] != c) return false;
            if (line.Length == MarkerLength) return true;
            char next = line[MarkerLength];
            if (next == '\r') return line.Length == MarkerLength + 1 || allowLabel;
            if (!allowLabel) return line.Substring(MarkerLength).Trim().Length == 0;
            return next == ' ' || next == '\t';
        }

        private static string Label(string line)
        {
            return line.Length > MarkerLength ? line.Substring(MarkerLength).Trim() : "";
        }

        /// <summary>Quick check used before a full scan (e.g. while painting).</summary>
        public static bool MayContainConflicts(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOf("<<<<<<<", StringComparison.Ordinal) >= 0 && text.IndexOf(">>>>>>>", StringComparison.Ordinal) >= 0;
        }

        public static List<ConflictBlock> FindBlocks(IList<string> lines)
        {
            List<ConflictBlock> blocks = new List<ConflictBlock>();
            if (lines == null) return blocks;
            ConflictBlock cur = null;
            int n = lines.Count;
            for (int i = 0; i < n; i++)
            {
                string l = lines[i];
                if (l == null || l.Length < MarkerLength) continue;
                char c0 = l[0];
                if (c0 != '<' && c0 != '|' && c0 != '=' && c0 != '>') continue;
                if (c0 == '<' && IsMarker(l, '<', true))
                {
                    cur = new ConflictBlock();
                    cur.StartLine = i;
                    cur.SeparatorLine = -1;
                    cur.CurrentLabel = Label(l);
                }
                else if (cur == null) continue;
                else if (c0 == '|' && IsMarker(l, '|', true) && cur.SeparatorLine < 0 && cur.BaseLine < 0)
                {
                    cur.BaseLine = i;
                    cur.BaseLabel = Label(l);
                }
                else if (c0 == '=' && IsMarker(l, '=', false) && cur.SeparatorLine < 0)
                {
                    cur.SeparatorLine = i;
                }
                else if (c0 == '>' && IsMarker(l, '>', true) && cur.SeparatorLine >= 0)
                {
                    cur.EndLine = i;
                    cur.IncomingLabel = Label(l);
                    blocks.Add(cur);
                    cur = null;
                }
            }
            return blocks;
        }

        public static List<ConflictBlock> FindBlocks(string text)
        {
            return FindBlocks(LineDiff.SplitLines(text));
        }

        public static int CountConflicts(string text)
        {
            if (!MayContainConflicts(text)) return 0;
            return FindBlocks(text).Count;
        }

        /// <summary>Block containing <paramref name="line"/> or null.</summary>
        public static ConflictBlock BlockAt(List<ConflictBlock> blocks, int line)
        {
            if (blocks == null) return null;
            foreach (ConflictBlock b in blocks) if (b.Contains(line)) return b;
            return null;
        }

        /// <summary>Next block starting after <paramref name="line"/> (wraps around); null when there are none.</summary>
        public static ConflictBlock Next(List<ConflictBlock> blocks, int line)
        {
            if (blocks == null || blocks.Count == 0) return null;
            foreach (ConflictBlock b in blocks) if (b.StartLine > line) return b;
            return blocks[0];
        }

        /// <summary>Previous block starting before <paramref name="line"/> (wraps around).</summary>
        public static ConflictBlock Previous(List<ConflictBlock> blocks, int line)
        {
            if (blocks == null || blocks.Count == 0) return null;
            for (int i = blocks.Count - 1; i >= 0; i--)
                if (blocks[i].StartLine < line && !blocks[i].Contains(line)) return blocks[i];
            ConflictBlock inside = BlockAt(blocks, line);
            for (int i = blocks.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(blocks[i], inside) || blocks.Count == 1) return blocks[i];
            return blocks[blocks.Count - 1];
        }

        public static List<string> CurrentLines(IList<string> lines, ConflictBlock b)
        {
            return Slice(lines, b.CurrentFirst, b.CurrentEndExclusive);
        }

        public static List<string> IncomingLines(IList<string> lines, ConflictBlock b)
        {
            return Slice(lines, b.IncomingFirst, b.IncomingEndExclusive);
        }

        public static List<string> BaseLines(IList<string> lines, ConflictBlock b)
        {
            if (b.BaseLine < 0) return new List<string>();
            return Slice(lines, b.BaseLine + 1, b.SeparatorLine);
        }

        private static List<string> Slice(IList<string> lines, int from, int toExclusive)
        {
            List<string> r = new List<string>();
            for (int i = Math.Max(0, from); i < toExclusive && i < lines.Count; i++) r.Add(lines[i]);
            return r;
        }

        /// <summary>The lines that replace the whole block (StartLine..EndLine inclusive) for a resolution.</summary>
        public static List<string> Replacement(IList<string> lines, ConflictBlock b, ConflictResolution how)
        {
            switch (how)
            {
                case ConflictResolution.Current: return CurrentLines(lines, b);
                case ConflictResolution.Incoming: return IncomingLines(lines, b);
                case ConflictResolution.Base: return BaseLines(lines, b);
                default:
                    List<string> both = CurrentLines(lines, b);
                    both.AddRange(IncomingLines(lines, b));
                    return both;
            }
        }

        /// <summary>Resolves one block in place in a line list. Returns the number of lines that replaced the block.</summary>
        public static int ResolveInLines(List<string> lines, ConflictBlock b, ConflictResolution how)
        {
            List<string> rep = Replacement(lines, b, how);
            lines.RemoveRange(b.StartLine, b.LineCount);
            lines.InsertRange(b.StartLine, rep);
            return rep.Count;
        }

        /// <summary>Resolves the block with index <paramref name="blockIndex"/> (or all blocks when -1) in a text.</summary>
        public static string Resolve(string text, int blockIndex, ConflictResolution how)
        {
            string eol = (text ?? "").IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            bool finalNewline = (text ?? "").EndsWith("\n", StringComparison.Ordinal);
            List<string> lines = new List<string>(LineDiff.SplitLines(text));
            List<ConflictBlock> blocks = FindBlocks(lines);
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blockIndex >= 0 && i != blockIndex) continue;
                ResolveInLines(lines, blocks[i], how);
            }
            string joined = string.Join(eol, lines.ToArray());
            if (finalNewline && lines.Count > 0) joined += eol;
            return joined;
        }

        /// <summary>Per-line conflict roles for highlighting; null when the lines contain no complete conflict.</summary>
        public static Dictionary<int, ConflictLineKind> LineKinds(List<ConflictBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0) return null;
            Dictionary<int, ConflictLineKind> map = new Dictionary<int, ConflictLineKind>();
            foreach (ConflictBlock b in blocks)
            {
                map[b.StartLine] = ConflictLineKind.CurrentHeader;
                for (int i = b.CurrentFirst; i < b.CurrentEndExclusive; i++) map[i] = ConflictLineKind.Current;
                if (b.BaseLine >= 0)
                {
                    map[b.BaseLine] = ConflictLineKind.BaseHeader;
                    for (int i = b.BaseLine + 1; i < b.SeparatorLine; i++) map[i] = ConflictLineKind.Base;
                }
                map[b.SeparatorLine] = ConflictLineKind.Separator;
                for (int i = b.IncomingFirst; i < b.IncomingEndExclusive; i++) map[i] = ConflictLineKind.Incoming;
                map[b.EndLine] = ConflictLineKind.IncomingHeader;
            }
            return map;
        }

        public const string LensCurrent = "Accept Current Change";
        public const string LensIncoming = "Accept Incoming Change";
        public const string LensBoth = "Accept Both Changes";
        public const string LensCompare = "Compare Changes";

        /// <summary>The code-lens segments shown on the "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" line (VS Code order).</summary>
        public static string[] LensSegments() { return new string[] { LensCurrent, LensIncoming, LensBoth, LensCompare }; }

        public static ConflictResolution? ResolutionForSegment(int segment)
        {
            if (segment == 0) return ConflictResolution.Current;
            if (segment == 1) return ConflictResolution.Incoming;
            if (segment == 2) return ConflictResolution.Both;
            return null;
        }
    }

    /// <summary>
    /// Caches the conflict scan of an editor buffer between paints. Cheap: one pass over the first character of each
    /// line, redone only when the line list or its length changes (or <see cref="Invalidate"/> is called).
    /// </summary>
    public sealed class BufferConflictCache
    {
        private object linesRef;
        private int lineCount = -1;
        private int stamp = -1;
        private List<ConflictBlock> blocks = new List<ConflictBlock>();
        private Dictionary<int, ConflictLineKind> kinds;

        public List<ConflictBlock> Blocks { get { return blocks; } }
        public Dictionary<int, ConflictLineKind> Kinds { get { return kinds; } }

        public void Invalidate() { linesRef = null; }

        /// <summary>Rescans when needed. <paramref name="editStamp"/> is any value that changes on edits (e.g. a version).</summary>
        public void Update(List<string> lines, int editStamp)
        {
            if (lines == null) { blocks = new List<ConflictBlock>(); kinds = null; return; }
            if (ReferenceEquals(lines, linesRef) && lines.Count == lineCount && editStamp == stamp) return;
            linesRef = lines;
            lineCount = lines.Count;
            stamp = editStamp;
            blocks = lines.Count > 400000 ? new List<ConflictBlock>() : GitConflictParser.FindBlocks(lines);
            kinds = GitConflictParser.LineKinds(blocks);
        }
    }

    /// <summary>
    /// Per-buffer conflict scan for editor decorations (both canvases). Rescans at most every 120 ms or when the
    /// line list changes, so painting stays cheap.
    /// </summary>
    public static class ConflictDecorations
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, BufferConflictCache> caches =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, BufferConflictCache>();

        public static BufferConflictCache CacheFor(EditorTabBuffer buf)
        {
            BufferConflictCache c = caches.GetOrCreateValue(buf);
            int stamp = Environment.TickCount / 120;
            c.Update(buf.Lines, stamp);
            return c;
        }

        public static List<ConflictBlock> Blocks(EditorTabBuffer buf)
        {
            if (buf == null || buf.Lines == null) return new List<ConflictBlock>();
            return CacheFor(buf).Blocks;
        }

        /// <summary>Fresh scan (after an edit), bypassing the throttle.</summary>
        public static List<ConflictBlock> Rescan(EditorTabBuffer buf)
        {
            if (buf == null || buf.Lines == null) return new List<ConflictBlock>();
            BufferConflictCache c = caches.GetOrCreateValue(buf);
            c.Invalidate();
            c.Update(buf.Lines, Environment.TickCount / 120);
            return c.Blocks;
        }

        public static ConflictLineKind KindAt(EditorTabBuffer buf, int line)
        {
            if (buf == null || buf.Lines == null || buf.Lines.Count > 400000) return ConflictLineKind.None;
            Dictionary<int, ConflictLineKind> k = CacheFor(buf).Kinds;
            ConflictLineKind v;
            return k != null && k.TryGetValue(line, out v) ? v : ConflictLineKind.None;
        }

        /// <summary>Applies a resolution to the buffer as one undoable edit. Returns false when the block is stale.</summary>
        public static bool ResolveInBuffer(EditorTabBuffer buf, ConflictBlock block, ConflictResolution how)
        {
            if (buf == null || block == null || buf.Lines == null) return false;
            List<ConflictBlock> fresh = Rescan(buf);
            ConflictBlock b = GitConflictParser.BlockAt(fresh, block.StartLine);
            if (b == null || b.StartLine != block.StartLine) return false;
            buf.PushUndoSnapshot();
            List<string> lines = new List<string>(buf.Lines);
            int n = GitConflictParser.ResolveInLines(lines, b, how);
            buf.Lines = lines;
            buf.CursorLine = Math.Min(Math.Max(0, b.StartLine), Math.Max(0, lines.Count - 1));
            buf.CursorCol = 0;
            buf.ClearSelection();
            buf.ClampCursor();
            buf.SyncContentFromLines();
            Rescan(buf);
            return true;
        }
    }
}
