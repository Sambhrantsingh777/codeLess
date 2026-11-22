using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Multi-caret editing on the buffer model (primary caret + <see cref="EditorTabBuffer.ExtraCarets"/>), used by the
    /// WinForms editor (QA #33) with the same rules as the Avalonia editor: every edit runs per caret from the last caret
    /// to the first (so earlier positions stay valid) and already-processed carets are shifted by the edit's length
    /// delta; typed characters get VS Code's auto-closing / overtype / auto-surround when ALL carets qualify (VS Code
    /// checks every selection), and Tab inserts the indentation each caret needs to reach its own next tab stop.
    /// STRICT C# 5.
    /// </summary>
    public static class EditorMultiCaret
    {
        private struct Caret
        {
            public int Line, Col, AnchorLine, AnchorCol, Index;
            public long End;
        }

        public static long OffsetOf(EditorTabBuffer b, int line, int col)
        {
            long off = 0;
            line = Math.Max(0, Math.Min(b.Lines.Count - 1, line));
            for (int i = 0; i < line; i++) off += (b.Lines[i] ?? "").Length + 1;
            return off + Math.Max(0, Math.Min((b.Lines[line] ?? "").Length, col));
        }

        public static void PositionOf(EditorTabBuffer b, long offset, out int line, out int col)
        {
            long rem = Math.Max(0, offset);
            for (int i = 0; i < b.Lines.Count; i++)
            {
                int len = (b.Lines[i] ?? "").Length;
                if (rem <= len) { line = i; col = (int)rem; return; }
                rem -= len + 1;
            }
            line = b.Lines.Count - 1;
            col = (b.Lines[line] ?? "").Length;
        }

        private static long TotalLength(EditorTabBuffer b)
        {
            long t = Math.Max(0, b.Lines.Count - 1);
            for (int i = 0; i < b.Lines.Count; i++) t += (b.Lines[i] ?? "").Length;
            return t;
        }

        private static List<Caret> Collect(EditorTabBuffer b)
        {
            List<Caret> list = new List<Caret>();
            Caret p = new Caret();
            p.Line = b.CursorLine; p.Col = b.CursorCol; p.Index = 0;
            p.AnchorLine = b.HasSelection() ? b.SelectionAnchorLine : -1;
            p.AnchorCol = b.HasSelection() ? b.SelectionAnchorCol : -1;
            list.Add(p);
            if (b.ExtraCarets != null)
            {
                for (int i = 0; i < b.ExtraCarets.Count; i++)
                {
                    EditorCaret ec = b.ExtraCarets[i];
                    if (ec == null || ec.Line < 0 || ec.Line >= b.Lines.Count) continue;
                    Caret c = new Caret();
                    c.Line = ec.Line; c.Col = Math.Min(ec.Col, (b.Lines[ec.Line] ?? "").Length); c.Index = list.Count;
                    c.AnchorLine = ec.HasSelection() ? ec.AnchorLine : -1;
                    c.AnchorCol = ec.HasSelection() ? ec.AnchorCol : -1;
                    list.Add(c);
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                Caret c = list[i];
                long a = OffsetOf(b, c.Line, c.Col);
                long e = c.AnchorLine >= 0 ? Math.Max(a, OffsetOf(b, c.AnchorLine, c.AnchorCol)) : a;
                c.End = e;
                list[i] = c;
            }
            return list;
        }

        private static void WritePrimary(EditorTabBuffer b, Caret c)
        {
            b.CursorLine = c.Line;
            b.CursorCol = c.Col;
            b.SelectionAnchorLine = c.AnchorLine;
            b.SelectionAnchorCol = c.AnchorCol;
        }

        /// <summary>
        /// Runs <paramref name="edit"/> once per caret, each time with that caret as the buffer's primary caret (and its
        /// selection). <paramref name="trackedOffsets"/> (optional, absolute offsets such as auto-inserted closers) are
        /// shifted like the carets. Carets that end on the same position merge.
        /// </summary>
        public static void ApplyToAllCarets(EditorTabBuffer b, Action<EditorTabBuffer, int> edit, List<long> trackedOffsets)
        {
            List<Caret> carets = Collect(b);
            carets.Sort(delegate(Caret x, Caret y) { return y.End.CompareTo(x.End); });
            List<KeyValuePair<int, long>> done = new List<KeyValuePair<int, long>>();
            for (int k = 0; k < carets.Count; k++)
            {
                Caret c = carets[k];
                WritePrimary(b, c);
                long start = c.AnchorLine >= 0 ? Math.Min(OffsetOf(b, c.Line, c.Col), OffsetOf(b, c.AnchorLine, c.AnchorCol)) : OffsetOf(b, c.Line, c.Col);
                long before = TotalLength(b);
                edit(b, k);
                long delta = TotalLength(b) - before;
                for (int d = 0; d < done.Count; d++) done[d] = new KeyValuePair<int, long>(done[d].Key, done[d].Value + delta);
                if (trackedOffsets != null)
                {
                    for (int t = trackedOffsets.Count - 1; t >= 0; t--)
                    {
                        if (trackedOffsets[t] >= c.End) trackedOffsets[t] += delta;
                        else if (trackedOffsets[t] >= start && c.End > start) trackedOffsets.RemoveAt(t); // inside a replaced selection
                    }
                }
                done.Add(new KeyValuePair<int, long>(c.Index, OffsetOf(b, b.CursorLine, b.CursorCol)));
            }
            done.Sort(delegate(KeyValuePair<int, long> x, KeyValuePair<int, long> y) { return x.Key.CompareTo(y.Key); });
            if (b.ExtraCarets == null) b.ExtraCarets = new List<EditorCaret>();
            b.ExtraCarets.Clear();
            HashSet<long> seen = new HashSet<long>();
            foreach (KeyValuePair<int, long> kv in done)
            {
                if (kv.Key != 0) continue;
                int l, col;
                PositionOf(b, kv.Value, out l, out col);
                b.CursorLine = l; b.CursorCol = col; b.ClearSelection();
                seen.Add(kv.Value);
            }
            foreach (KeyValuePair<int, long> kv in done)
            {
                if (kv.Key == 0 || !seen.Add(kv.Value)) continue;
                int l, col;
                PositionOf(b, kv.Value, out l, out col);
                EditorCaret n = new EditorCaret();
                n.Line = l; n.Col = col; n.AnchorLine = -1; n.AnchorCol = -1;
                b.ExtraCarets.Add(n);
            }
        }

        /// <summary>Inserts <paramref name="text"/> at the primary caret, replacing its selection.</summary>
        public static void InsertAtCaret(EditorTabBuffer b, string text)
        {
            if (b.HasSelection()) DeleteSelection(b);
            string ln = b.Lines[b.CursorLine] ?? "";
            int col = Math.Max(0, Math.Min(ln.Length, b.CursorCol));
            string norm = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] parts = norm.Split('\n');
            string head = ln.Substring(0, col), tail = ln.Substring(col);
            if (parts.Length == 1)
            {
                b.Lines[b.CursorLine] = head + norm + tail;
                b.CursorCol = col + norm.Length;
                return;
            }
            b.Lines[b.CursorLine] = head + parts[0];
            for (int i = 1; i < parts.Length; i++) b.Lines.Insert(b.CursorLine + i, parts[i] + (i == parts.Length - 1 ? tail : ""));
            b.CursorLine += parts.Length - 1;
            b.CursorCol = parts[parts.Length - 1].Length;
        }

        private static void DeleteSelection(EditorTabBuffer b)
        {
            int sl, sc, el, ec;
            b.GetNormalizedSelection(out sl, out sc, out el, out ec);
            sl = Math.Max(0, Math.Min(b.Lines.Count - 1, sl));
            el = Math.Max(sl, Math.Min(b.Lines.Count - 1, el));
            string s = b.Lines[sl] ?? "", e = b.Lines[el] ?? "";
            sc = Math.Max(0, Math.Min(s.Length, sc));
            ec = Math.Max(0, Math.Min(e.Length, ec));
            if (sl == el && ec < sc) ec = sc;
            b.Lines[sl] = s.Substring(0, sc) + e.Substring(ec);
            if (el > sl) b.Lines.RemoveRange(sl + 1, el - sl);
            b.CursorLine = sl;
            b.CursorCol = sc;
            b.ClearSelection();
        }

        /// <summary>
        /// One typed character at every caret with VS Code's auto-closing rules (all carets or none):
        /// auto-surround when every caret has a selection, overtype when every caret sits before the same auto-inserted
        /// closer / the quote closing its string, auto-close when every caret qualifies, otherwise a plain insert.
        /// <paramref name="autoClosed"/> holds the absolute offsets of auto-inserted closers (kept up to date).
        /// Returns false when nothing was edited (only the carets moved over closers).
        /// </summary>
        public static bool TypeChar(EditorTabBuffer b, char ch, string path, List<long> autoClosed)
        {
            if (autoClosed == null) autoClosed = new List<long>();
            List<Caret> carets = Collect(b);
            char closer = EditorAutoClosing.CloserFor(ch, path);
            bool isOpener = closer != '\0';
            bool isCloserOrQuote = EditorAutoClosing.IsCloser(ch) || EditorAutoClosing.IsQuote(ch);
            bool anySel = false, allSel = true;
            foreach (Caret c in carets) { if (c.AnchorLine >= 0) anySel = true; else allSel = false; }

            string open, close;
            if (anySel && allSel && EditorAutoClosing.TryGetSurroundPair(ch, path, out open, out close) && (isOpener || EditorAutoClosing.IsQuote(ch)))
            {
                Surround(b, carets, open, close);
                autoClosed.Clear();
                return true;
            }
            if (!anySel && isCloserOrQuote)
            {
                bool all = true;
                foreach (Caret c in carets)
                {
                    long off = OffsetOf(b, c.Line, c.Col);
                    if (!EditorAutoClosing.ShouldOvertype(b.Lines[c.Line], c.Col, ch, autoClosed.Contains(off))) { all = false; break; }
                }
                if (all)
                {
                    foreach (Caret c in carets) autoClosed.Remove(OffsetOf(b, c.Line, c.Col));
                    b.CursorCol++;
                    foreach (EditorCaret ec in b.ExtraCarets) { ec.Col++; ec.AnchorLine = -1; ec.AnchorCol = -1; }
                    return false;
                }
            }
            if (!anySel && isOpener)
            {
                bool all = true;
                foreach (Caret c in carets)
                    if (!EditorAutoClosing.ShouldAutoClose(b.Lines[c.Line], c.Col, ch, path)) { all = false; break; }
                if (all)
                {
                    string pair = ch.ToString() + closer;
                    ApplyToAllCarets(b, delegate(EditorTabBuffer bb, int k) { InsertAtCaret(bb, pair); bb.CursorCol--; }, autoClosed);
                    autoClosed.Add(OffsetOf(b, b.CursorLine, b.CursorCol));
                    foreach (EditorCaret ec in b.ExtraCarets) autoClosed.Add(OffsetOf(b, ec.Line, ec.Col));
                    return true;
                }
            }
            string text = ch.ToString();
            ApplyToAllCarets(b, delegate(EditorTabBuffer bb, int k) { InsertAtCaret(bb, text); }, autoClosed);
            return true;
        }

        private static void Surround(EditorTabBuffer b, List<Caret> carets, string open, string close)
        {
            List<Caret> order = new List<Caret>(carets);
            order.Sort(delegate(Caret x, Caret y) { return y.End.CompareTo(x.End); });
            Caret[] result = new Caret[carets.Count];
            foreach (Caret c in order)
            {
                bool fwd = c.AnchorLine < c.Line || (c.AnchorLine == c.Line && c.AnchorCol <= c.Col);
                int sl = fwd ? c.AnchorLine : c.Line, sc = fwd ? c.AnchorCol : c.Col, el = fwd ? c.Line : c.AnchorLine, ecol = fwd ? c.Col : c.AnchorCol;
                b.Lines[el] = b.Lines[el].Insert(Math.Min(ecol, b.Lines[el].Length), close);
                b.Lines[sl] = b.Lines[sl].Insert(Math.Min(sc, b.Lines[sl].Length), open);
                int nsc = sc + open.Length, nec = ecol + (sl == el ? open.Length : 0);
                Caret r = new Caret();
                if (fwd) { r.AnchorLine = sl; r.AnchorCol = nsc; r.Line = el; r.Col = nec; }
                else { r.AnchorLine = el; r.AnchorCol = nec; r.Line = sl; r.Col = nsc; }
                result[c.Index] = r;
            }
            WritePrimary(b, result[0]);
            b.ExtraCarets.Clear();
            for (int i = 1; i < result.Length; i++)
            {
                EditorCaret n = new EditorCaret();
                n.Line = result[i].Line; n.Col = result[i].Col; n.AnchorLine = result[i].AnchorLine; n.AnchorCol = result[i].AnchorCol;
                b.ExtraCarets.Add(n);
            }
        }

        /// <summary>
        /// Backspace at every caret: a selection is deleted; an empty auto-inserted pair "(|)" loses both characters
        /// (VS Code autoClosingDelete "auto"); spaces-only indentation goes back to the previous tab stop; otherwise one
        /// character (surrogate-safe) or the line break.
        /// </summary>
        public static void Backspace(EditorTabBuffer b, int tabSize, string path, List<long> autoClosed)
        {
            if (autoClosed == null) autoClosed = new List<long>();
            List<long> pairs = new List<long>(autoClosed);
            ApplyToAllCarets(b, delegate(EditorTabBuffer bb, int k)
            {
                if (bb.HasSelection()) { DeleteSelection(bb); return; }
                string ln = bb.Lines[bb.CursorLine] ?? "";
                int col = Math.Min(ln.Length, bb.CursorCol);
                if (col > 0 && EditorAutoClosing.ShouldDeletePair(ln, col, path) && pairs.Contains(OffsetOf(bb, bb.CursorLine, col)))
                {
                    bb.Lines[bb.CursorLine] = ln.Remove(col - 1, 2);
                    bb.CursorCol = col - 1;
                    return;
                }
                if (col > 0)
                {
                    int n = EditorIndentation.BackspaceDeleteCount(ln, col, tabSize);
                    int start = n > 1 ? col - n : EditorTextLayout.PrevCharBoundary(ln, col);
                    bb.Lines[bb.CursorLine] = ln.Remove(start, col - start);
                    bb.CursorCol = start;
                    return;
                }
                if (bb.CursorLine > 0)
                {
                    string prev = bb.Lines[bb.CursorLine - 1] ?? "";
                    bb.Lines[bb.CursorLine - 1] = prev + ln;
                    bb.Lines.RemoveAt(bb.CursorLine);
                    bb.CursorLine--;
                    bb.CursorCol = prev.Length;
                }
            }, null);
            autoClosed.Clear(); // positions moved; VS Code forgets the remaining pairs after a delete as well
        }

        /// <summary>Tab at every caret: each caret gets the indentation to ITS next tab stop (a partial selection is replaced).</summary>
        public static void InsertTab(EditorTabBuffer b, int tabSize, bool insertSpaces, List<long> trackedOffsets)
        {
            ApplyToAllCarets(b, delegate(EditorTabBuffer bb, int k)
            {
                if (bb.HasSelection()) DeleteSelection(bb);
                InsertAtCaret(bb, EditorIndentation.TabInsertText(bb.Lines[bb.CursorLine], bb.CursorCol, tabSize, insertSpaces));
            }, trackedOffsets);
        }
    }
}
