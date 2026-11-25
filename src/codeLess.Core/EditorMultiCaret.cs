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

        /// <summary>
        /// VS Code multi-cursor paste distribution: when pasting N lines onto N carets (or N lines with a single trailing
        /// newline from whole-line copy), returns the N lines in document order; otherwise returns null so the full text
        /// is inserted at every caret. Preserves empty lines (e.g. when the last copied selection was empty).
        /// </summary>
        public static string[] TrySplitPasteForCarets(string normalizedText, int caretCount)
        {
            if (string.IsNullOrEmpty(normalizedText) || caretCount <= 1) return null;
            string[] raw = normalizedText.Split('\n');
            if (raw.Length == caretCount) return raw;
            if (raw.Length == caretCount + 1 && raw[caretCount].Length == 0)
            {
                string[] trimmed = new string[caretCount];
                Array.Copy(raw, 0, trimmed, 0, caretCount);
                return trimmed;
            }
            return null;
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

        private sealed class LineBlock
        {
            public int First;
            public int Last;
            public readonly List<Caret> Carets = new List<Caret>();
        }

        private static void GetCaretLineRange(Caret c, int maxLine, out int first, out int last)
        {
            if (c.AnchorLine >= 0 && (c.AnchorLine != c.Line || c.AnchorCol != c.Col))
            {
                int sL = Math.Min(c.Line, c.AnchorLine);
                int eL = Math.Max(c.Line, c.AnchorLine);
                int endCol = (c.Line > c.AnchorLine || (c.Line == c.AnchorLine && c.Col >= c.AnchorCol)) ? c.Col : c.AnchorCol;
                first = Math.Max(0, Math.Min(maxLine, sL));
                last = Math.Max(first, Math.Min(maxLine, (eL > sL && endCol == 0) ? eL - 1 : eL));
            }
            else
            {
                first = last = Math.Max(0, Math.Min(maxLine, c.Line));
            }
        }

        /// <summary>
        /// Groups all carets into line blocks and merges overlapping or adjacent blocks (VS Code MoveLinesCommand / CopyLinesCommand).
        /// Returned blocks are ordered top-to-bottom.
        /// </summary>
        private static List<LineBlock> BuildMergedLineBlocks(EditorTabBuffer b, List<Caret> carets)
        {
            int maxLine = Math.Max(0, b.Lines.Count - 1);
            List<LineBlock> raw = new List<LineBlock>(carets.Count);
            for (int i = 0; i < carets.Count; i++)
            {
                Caret c = carets[i];
                int f, l;
                GetCaretLineRange(c, maxLine, out f, out l);
                LineBlock blk = new LineBlock();
                blk.First = f;
                blk.Last = l;
                blk.Carets.Add(c);
                raw.Add(blk);
            }
            raw.Sort(delegate(LineBlock x, LineBlock y)
            {
                int cmp = x.First.CompareTo(y.First);
                return cmp != 0 ? cmp : x.Last.CompareTo(y.Last);
            });
            List<LineBlock> merged = new List<LineBlock>();
            for (int i = 0; i < raw.Count; i++)
            {
                LineBlock cur = raw[i];
                if (merged.Count > 0 && cur.First <= merged[merged.Count - 1].Last + 1)
                {
                    LineBlock prev = merged[merged.Count - 1];
                    if (cur.Last > prev.Last) prev.Last = cur.Last;
                    prev.Carets.AddRange(cur.Carets);
                }
                else
                {
                    merged.Add(cur);
                }
            }
            return merged;
        }

        private static void RestoreCaretsByIndex(EditorTabBuffer b, List<Caret> updated, int totalCount)
        {
            Caret[] byIdx = new Caret[totalCount];
            for (int i = 0; i < updated.Count; i++)
            {
                Caret c = updated[i];
                if (c.Index >= 0 && c.Index < totalCount) byIdx[c.Index] = c;
            }
            int maxLine = Math.Max(0, b.Lines.Count - 1);
            for (int i = 0; i < totalCount; i++)
            {
                Caret c = byIdx[i];
                c.Line = Math.Max(0, Math.Min(maxLine, c.Line));
                c.Col = Math.Max(0, Math.Min((b.Lines[c.Line] ?? "").Length, c.Col));
                if (c.AnchorLine >= 0)
                {
                    c.AnchorLine = Math.Max(0, Math.Min(maxLine, c.AnchorLine));
                    c.AnchorCol = Math.Max(0, Math.Min((b.Lines[c.AnchorLine] ?? "").Length, c.AnchorCol));
                    if (c.AnchorLine == c.Line && c.AnchorCol == c.Col) { c.AnchorLine = -1; c.AnchorCol = -1; }
                }
                byIdx[i] = c;
            }
            WritePrimary(b, byIdx[0]);
            if (b.ExtraCarets == null) b.ExtraCarets = new List<EditorCaret>();
            else b.ExtraCarets.Clear();
            for (int i = 1; i < totalCount; i++)
            {
                Caret c = byIdx[i];
                EditorCaret ec = new EditorCaret();
                ec.Line = c.Line;
                ec.Col = c.Col;
                ec.AnchorLine = c.AnchorLine;
                ec.AnchorCol = c.AnchorCol;
                b.ExtraCarets.Add(ec);
            }
        }

        /// <summary>
        /// VS Code MoveLinesCommand (Alt+Up / Alt+Down) across single or multiple carets/selections:
        /// overlapping or adjacent line blocks merge first; each disjoint block moves independently (a block at the top
        /// or bottom of the document is a no-op for that block only); all carets and selections are preserved in one undo step.
        /// Returns true if at least one block moved.
        /// </summary>
        public static bool MoveLines(EditorTabBuffer b, int direction)
        {
            if (b == null || b.Lines == null || b.Lines.Count == 0 || direction == 0) return false;
            List<Caret> carets = Collect(b);
            List<LineBlock> blocks = BuildMergedLineBlocks(b, carets);
            int lineCount = b.Lines.Count;
            bool anyCanMove = false;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (direction < 0 && blocks[i].First > 0) { anyCanMove = true; break; }
                if (direction > 0 && blocks[i].Last < lineCount - 1) { anyCanMove = true; break; }
            }
            if (!anyCanMove) return false;

            b.PushUndoSnapshot();
            List<Caret> updated = new List<Caret>(carets.Count);
            if (direction < 0)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    LineBlock blk = blocks[i];
                    if (blk.First <= 0)
                    {
                        updated.AddRange(blk.Carets);
                        continue;
                    }
                    string above = b.Lines[blk.First - 1];
                    b.Lines.RemoveAt(blk.First - 1);
                    b.Lines.Insert(blk.Last, above);
                    for (int k = 0; k < blk.Carets.Count; k++)
                    {
                        Caret c = blk.Carets[k];
                        c.Line = Math.Max(0, c.Line - 1);
                        if (c.AnchorLine >= 0) c.AnchorLine = Math.Max(0, c.AnchorLine - 1);
                        updated.Add(c);
                    }
                }
            }
            else
            {
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    LineBlock blk = blocks[i];
                    if (blk.Last >= lineCount - 1)
                    {
                        updated.AddRange(blk.Carets);
                        continue;
                    }
                    string below = b.Lines[blk.Last + 1];
                    b.Lines.RemoveAt(blk.Last + 1);
                    b.Lines.Insert(blk.First, below);
                    for (int k = 0; k < blk.Carets.Count; k++)
                    {
                        Caret c = blk.Carets[k];
                        if (c.AnchorLine >= 0)
                        {
                            if (c.Line + 1 >= lineCount && c.Col == 0)
                            {
                                c.Line = lineCount - 1;
                                c.Col = (b.Lines[c.Line] ?? "").Length;
                            }
                            else c.Line = Math.Min(lineCount - 1, c.Line + 1);

                            if (c.AnchorLine + 1 >= lineCount && c.AnchorCol == 0)
                            {
                                c.AnchorLine = lineCount - 1;
                                c.AnchorCol = (b.Lines[c.AnchorLine] ?? "").Length;
                            }
                            else c.AnchorLine = Math.Min(lineCount - 1, c.AnchorLine + 1);
                        }
                        else
                        {
                            c.Line = Math.Min(lineCount - 1, c.Line + 1);
                        }
                        updated.Add(c);
                    }
                }
            }
            RestoreCaretsByIndex(b, updated, carets.Count);
            return true;
        }

        /// <summary>
        /// VS Code CopyLinesCommand (Shift+Alt+Up / Shift+Alt+Down) across single or multiple carets/selections:
        /// overlapping or adjacent line blocks merge first; each disjoint block is duplicated independently;
        /// carets/selections stay on the upper copy for Up and move to the lower copy for Down, in one undo step.
        /// </summary>
        public static bool CopyLines(EditorTabBuffer b, int direction)
        {
            if (b == null || b.Lines == null || b.Lines.Count == 0 || direction == 0) return false;
            List<Caret> carets = Collect(b);
            List<LineBlock> blocks = BuildMergedLineBlocks(b, carets);
            if (blocks.Count == 0) return false;

            b.PushUndoSnapshot();
            List<Caret> updated = new List<Caret>(carets.Count);
            int cumInserted = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                LineBlock blk = blocks[i];
                int curFirst = blk.First + cumInserted;
                int curLast = blk.Last + cumInserted;
                int count = blk.Last - blk.First + 1;
                List<string> slice = b.Lines.GetRange(curFirst, count);
                b.Lines.InsertRange(direction < 0 ? curFirst : curLast + 1, slice);
                int shift = cumInserted + (direction > 0 ? count : 0);
                for (int k = 0; k < blk.Carets.Count; k++)
                {
                    Caret c = blk.Carets[k];
                    c.Line += shift;
                    if (c.AnchorLine >= 0) c.AnchorLine += shift;
                    updated.Add(c);
                }
                cumInserted += count;
            }
            RestoreCaretsByIndex(b, updated, carets.Count);
            return true;
        }

        private struct CaretOffsetPair
        {
            public int Index;
            public long CursorOff;
            public long AnchorOff;
        }

        /// <summary>
        /// VS Code BlockCommentCommand (Shift+Alt+A) across single or multiple carets/selections:
        /// each selection/caret toggles its block comment independently in one undo step, preserving all carets and selections.
        /// </summary>
        public static bool ToggleBlockComments(EditorTabBuffer b)
        {
            if (b == null || b.Lines == null || b.Lines.Count == 0) return false;
            CommentTokens tok = EditorComments.For(b.FilePath ?? b.FileName ?? ("x." + (b.Language ?? "")));
            if (tok.Line == null && tok.BlockStart == null) return false;
            if (tok.BlockStart == null)
            {
                // Language has only line comments (e.g. Python): fall back to multi-cursor line comments
                List<int[]> ranges = new List<int[]>();
                List<Caret> all = Collect(b);
                for (int i = 0; i < all.Count; i++)
                {
                    int first, last;
                    GetCaretLineRange(all[i], b.Lines.Count - 1, out first, out last);
                    ranges.Add(new int[] { first, last });
                }
                b.PushUndoSnapshot();
                List<CommentEdit> edits = EditorComments.ToggleLineComments(b, ranges);
                if (edits == null || edits.Count == 0) { b.PopUndoSnapshotAndRestore(); return false; }
                if (b.ExtraCarets != null)
                {
                    for (int i = 0; i < b.ExtraCarets.Count; i++)
                    {
                        EditorCaret ec = b.ExtraCarets[i];
                        ec.Col = EditorComments.AdjustColumn(edits, ec.Line, ec.Col);
                        if (ec.AnchorLine >= 0) ec.AnchorCol = EditorComments.AdjustColumn(edits, ec.AnchorLine, ec.AnchorCol);
                    }
                }
                return true;
            }

            List<Caret> carets = Collect(b);
            b.PushUndoSnapshot();
            if (carets.Count == 1)
            {
                if (!EditorComments.ToggleBlockComment(b)) { b.PopUndoSnapshotAndRestore(); return false; }
                return true;
            }

            List<Caret> ordered = new List<Caret>(carets);
            ordered.Sort(delegate(Caret x, Caret y)
            {
                int cmp = y.End.CompareTo(x.End);
                if (cmp != 0) return cmp;
                return y.Index.CompareTo(x.Index);
            });

            List<CaretOffsetPair> done = new List<CaretOffsetPair>(ordered.Count);
            bool anyChanged = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                Caret item = ordered[i];
                WritePrimary(b, item);
                long before = TotalLength(b);
                if (EditorComments.ToggleBlockComment(b)) anyChanged = true;
                long delta = TotalLength(b) - before;
                for (int d = 0; d < done.Count; d++)
                {
                    CaretOffsetPair prev = done[d];
                    prev.CursorOff += delta;
                    if (prev.AnchorOff >= 0) prev.AnchorOff += delta;
                    done[d] = prev;
                }
                CaretOffsetPair cur = new CaretOffsetPair();
                cur.Index = item.Index;
                cur.CursorOff = OffsetOf(b, b.CursorLine, b.CursorCol);
                cur.AnchorOff = b.HasSelection() ? OffsetOf(b, b.SelectionAnchorLine, b.SelectionAnchorCol) : -1;
                done.Add(cur);
            }
            if (!anyChanged)
            {
                b.PopUndoSnapshotAndRestore();
                return false;
            }

            List<Caret> restored = new List<Caret>(done.Count);
            for (int i = 0; i < done.Count; i++)
            {
                CaretOffsetPair p = done[i];
                Caret rc = new Caret();
                rc.Index = p.Index;
                PositionOf(b, p.CursorOff, out rc.Line, out rc.Col);
                if (p.AnchorOff >= 0)
                {
                    PositionOf(b, p.AnchorOff, out rc.AnchorLine, out rc.AnchorCol);
                }
                else
                {
                    rc.AnchorLine = -1;
                    rc.AnchorCol = -1;
                }
                restored.Add(rc);
            }
            RestoreCaretsByIndex(b, restored, carets.Count);
            return true;
        }
    }
}
