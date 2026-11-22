using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// WinForms editor layout and typing rules shared with the Avalonia editor through codeLess.Core (STRICT C# 5):
    /// the visual column model with the real tab size and wide / astral characters (QA #20), VS Code word wrap with
    /// the "same" wrapped indent (QA #11 parity), folding ranges cached per buffer version (QA #13 / #16), and the
    /// indentation-aware Tab / Enter / Backspace edits (QA #12).
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        private readonly WrapLayoutCache wfWrapCache = new WrapLayoutCache();
        private readonly Dictionary<long, int> wfPreferredVcol = new Dictionary<long, int>();
        private System.Windows.Forms.Timer wfFoldTimer;
        private int wfFoldScheduledVersion = -1;
        /// <summary>Documents up to this many lines recompute folding synchronously on paint; bigger ones debounce.</summary>
        public static int FoldSyncLineLimitWF = 5000;
        // the closer this canvas auto-inserted last (VS Code "autoClosingOvertype / autoClosingDelete: auto")
        private int wfAutoCloseLine = -1, wfAutoCloseCol = -1;
        // the primary caret is drawn at the END of its row when it sits exactly on a soft break after End / Up / Down
        // (VS Code cursor affinity, QA #32); any other caret change drops it
        private int wfAffinityLine = -1, wfAffinityCol = -1;
        // absolute offsets of closers auto-inserted at multiple carets (overtype / pair delete only for those, QA #33)
        private readonly List<long> wfMultiAutoClosed = new List<long>();

        private bool HasRowEndAffinityWF(int line, int col)
        {
            return line == wfAffinityLine && col == wfAffinityCol;
        }

        internal void ClearRowEndAffinityWF()
        {
            wfAffinityLine = -1;
            wfAffinityCol = -1;
        }

        /// <summary>Home on a wrapped row: row start first, then the line's first non-blank / column 0 (shared rule).</summary>
        private bool TryWrappedHomeWF(EditorTabBuffer buf, ref int line, ref int col, bool primary)
        {
            if (!buf.WordWrap) return false;
            int t = EditorWrapNavigation.Home(WrapOfWF(buf, line), col, primary && HasRowEndAffinityWF(line, col));
            if (t < 0) return false;
            col = t;
            return true;
        }

        /// <summary>End on a wrapped row: end of the row (caret stays on that row), End again: end of the line.</summary>
        private bool TryWrappedEndWF(EditorTabBuffer buf, ref int line, ref int col, bool primary, ref bool rowEndAffinity)
        {
            if (!buf.WordWrap) return false;
            int len = (buf.Lines[line] ?? "").Length;
            int t = EditorWrapNavigation.End(WrapOfWF(buf, line), len, col, primary && HasRowEndAffinityWF(line, col));
            if (t < 0) return false;
            col = t;
            rowEndAffinity = true;
            return true;
        }

        private int TabWF(EditorTabBuffer buf)
        {
            return EditorIndentation.EffectiveTabSize(buf, owner != null ? owner.TabSize : 4);
        }

        private bool SpacesWF(EditorTabBuffer buf)
        {
            return EditorIndentation.EffectiveInsertSpaces(buf, owner == null || owner.InsertSpaces);
        }

        private WrapLayout WrapOfWF(EditorTabBuffer buf, int line)
        {
            if (buf == null || !buf.WordWrap || buf.Lines == null || line < 0 || line >= buf.Lines.Count) return WrapLayout.Single;
            wfWrapCache.Configure(GetWrapCols(), TabWF(buf), true);
            return wfWrapCache.Get(buf.Lines[line] ?? "");
        }

        /// <summary>Visual column (cells) of <paramref name="col"/> on a painted row, including the wrapped indent.</summary>
        private int SegVcol(string full, VisualRowEntry seg, int col)
        {
            return EditorTextLayout.VisualColumnFrom(full ?? "", seg.ColStart, col, TabWF(owner.CurrentBuffer), seg.StartVcol);
        }

        // ------------------------------------------------------------------ folding
        private EditorFoldingModel FoldModelWF(EditorTabBuffer buf, bool forceCurrent)
        {
            EditorFoldingModel m = EditorFoldingModel.For(buf);
            if (m == null || buf.Lines == null) return m;
            int tab = TabWF(buf);
            if (m.IsUpToDate(buf, tab)) return m;
            if (forceCurrent) { m.Update(buf, tab, 0); return m; }
            if (buf.Lines.Count <= FoldSyncLineLimitWF)
            {
                if (!m.Update(buf, tab, 4)) EnsureFoldTimerWF(false);
            }
            else
            {
                bool edited = buf.EditVersion != wfFoldScheduledVersion;
                wfFoldScheduledVersion = buf.EditVersion;
                EnsureFoldTimerWF(edited);
            }
            return m;
        }

        private void EnsureFoldTimerWF(bool restart)
        {
            if (wfFoldTimer == null)
            {
                wfFoldTimer = new System.Windows.Forms.Timer();
                wfFoldTimer.Interval = 150;
                wfFoldTimer.Tick += delegate
                {
                    EditorTabBuffer b = owner.CurrentBuffer;
                    if (b == null || IsDisposed || !Visible) { wfFoldTimer.Stop(); return; }
                    EditorFoldingModel m = EditorFoldingModel.For(b);
                    if (m.Update(b, TabWF(b), 25)) { wfFoldTimer.Stop(); Invalidate(); }
                };
            }
            if (restart) wfFoldTimer.Stop();
            if (!wfFoldTimer.Enabled) wfFoldTimer.Start();
        }

        /// <summary>Last hidden line of the folded region headed by <paramref name="header"/> (-1 when not foldable).</summary>
        private int FoldEndWF(EditorTabBuffer buf, int header)
        {
            EditorFoldingModel m = FoldModelWF(buf, false);
            return m == null ? -1 : m.EndOf(header);
        }

        private int NextVisibleLineWF(EditorTabBuffer buf, int line)
        {
            if (buf.FoldedLines != null && buf.FoldedLines.Contains(line))
            {
                int e = FoldEndWF(buf, line);
                if (e > line) return e + 1;
            }
            return line + 1;
        }

        /// <summary>The folded header hiding <paramref name="line"/>, or -1.</summary>
        private int HiddenByWF(EditorTabBuffer buf, int line)
        {
            if (buf.FoldedLines == null || buf.FoldedLines.Count == 0) return -1;
            int best = -1;
            foreach (int h in buf.FoldedLines)
            {
                if (h >= line || (best >= 0 && h >= best)) continue;
                if (FoldEndWF(buf, h) >= line) best = h;
            }
            return best;
        }

        private int PrevVisibleLineWF(EditorTabBuffer buf, int line)
        {
            int l = line - 1;
            if (l < 0) return -1;
            int h = HiddenByWF(buf, l);
            return h >= 0 ? h : l;
        }

        // ------------------------------------------------------------------ caret motion (visual rows, sticky column)
        private static long PosKeyWF(int line, int col)
        {
            return ((long)line << 32) | (uint)col;
        }

        /// <summary>Up / Down by visual rows (wrap- and fold-aware) keeping the desired visual column, like VS Code.</summary>
        private void MoveVerticalWF(EditorTabBuffer buf, ref int line, ref int col, int delta)
        {
            bool unusedAffinity = false;
            MoveVerticalWF(buf, ref line, ref col, delta, false, ref unusedAffinity);
        }

        /// <summary>Vertical move; the primary caret starts from the row it is drawn on and may land at a row end (affinity).</summary>
        private void MoveVerticalWF(EditorTabBuffer buf, ref int line, ref int col, int delta, bool primary, ref bool rowEndAffinity)
        {
            int tab = TabWF(buf);
            string text = buf.Lines[line] ?? "";
            WrapLayout wl = WrapOfWF(buf, line);
            int row = wl.RowOf(Math.Min(col, text.Length), primary && HasRowEndAffinityWF(line, col));
            int x;
            if (!wfPreferredVcol.TryGetValue(PosKeyWF(line, col), out x))
                x = EditorTextLayout.VisualColumnFrom(text, wl.RowStarts[row], col, tab, wl.RowStartVcol(row));
            int tLine = line, tRow = row;
            int steps = Math.Abs(delta);
            for (int i = 0; i < steps; i++)
            {
                if (delta > 0)
                {
                    if (tRow + 1 < WrapOfWF(buf, tLine).RowCount) { tRow++; continue; }
                    int next = NextVisibleLineWF(buf, tLine);
                    if (next >= buf.Lines.Count) break;
                    tLine = next; tRow = 0;
                }
                else
                {
                    if (tRow > 0) { tRow--; continue; }
                    int prev = PrevVisibleLineWF(buf, tLine);
                    if (prev < 0) break;
                    tLine = prev; tRow = WrapOfWF(buf, prev).RowCount - 1;
                }
            }
            if (tLine == line && tRow == row)
            {
                col = delta < 0 ? 0 : text.Length; // VS Code: Up on the first line -> start, Down on the last -> end
                return;
            }
            string t2 = buf.Lines[tLine] ?? "";
            WrapLayout w2 = WrapOfWF(buf, tLine);
            int start = w2.RowStarts[tRow], end = w2.RowEnd(tRow, t2.Length);
            int c = EditorTextLayout.CharIndexForVisualColumnInRange(t2, start, end, x, tab, w2.RowStartVcol(tRow));
            if (tRow < w2.RowCount - 1 && c >= end)
            {
                // past the end of a continuation row: the primary caret stays on that row at its end (VS Code affinity)
                if (primary) { c = end; rowEndAffinity = true; }
                else c = Math.Max(start, end - 1);
            }
            line = tLine;
            col = c;
            wfPreferredVcol[PosKeyWF(line, col)] = x;
        }

        /// <summary>Forget the sticky column (any key other than Up / Down / Page Up / Page Down, clicks, typing).</summary>
        internal void ResetPreferredColumnWF()
        {
            if (wfPreferredVcol.Count > 0) wfPreferredVcol.Clear();
        }

        // ------------------------------------------------------------------ typing
        private bool IsIndentBasedWF(EditorTabBuffer buf)
        {
            return BracketLexProfile.ForFile(buf.FilePath ?? buf.FileName ?? "").IndentBased;
        }

        /// <summary>Enter at the primary caret (VS Code auto-indent, "{|}" split, trimAutoWhitespace).</summary>
        private void InsertNewlineWF(EditorTabBuffer buf)
        {
            if (buf.HasSelection()) ReplaceSelectionWithText("");
            int caretRow, caretCol;
            string[] parts = EditorIndentation.SplitForEnter(buf.Lines[buf.CursorLine], buf.CursorCol, TabWF(buf), SpacesWF(buf), IsIndentBasedWF(buf), out caretRow, out caretCol);
            int line = buf.CursorLine;
            buf.Lines[line] = parts[0];
            for (int i = 1; i < parts.Length; i++) buf.Lines.Insert(line + i, parts[i]);
            buf.CursorLine = line + caretRow;
            buf.CursorCol = caretCol;
            buf.ClearSelection();
            wfAutoCloseLine = -1;
        }

        /// <summary>Tab without a multi-line selection: indentation up to the next tab stop (or a tab), replacing a partial selection.</summary>
        private void InsertTabWF(EditorTabBuffer buf)
        {
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            if (buf.HasSelection()) ReplaceSelectionWithText("");
            ReplaceSelectionWithText(EditorIndentation.TabInsertText(buf.Lines[buf.CursorLine], buf.CursorCol, TabWF(buf), SpacesWF(buf)));
        }

        /// <summary>True when Tab should indent lines (a selection over several lines, or a whole line).</summary>
        private static bool TabIndentsLinesWF(EditorTabBuffer buf)
        {
            if (!buf.HasSelection()) return false;
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            return sLine != eLine || (sCol == 0 && eCol >= (buf.Lines[sLine] ?? "").Length && eCol > 0);
        }

        /// <summary>Backspace at the primary caret: empty auto-closed pair, spaces back to the previous tab stop, surrogate-safe.</summary>
        private void BackspaceWF(EditorTabBuffer buf)
        {
            string ln = buf.Lines[buf.CursorLine] ?? "";
            int col = Math.Min(ln.Length, buf.CursorCol);
            if (col > 0 && wfAutoCloseLine == buf.CursorLine && wfAutoCloseCol == col && EditorAutoClosing.ShouldDeletePair(ln, col, buf.FilePath ?? buf.FileName))
            {
                buf.Lines[buf.CursorLine] = ln.Remove(col - 1, 2);
                buf.CursorCol = col - 1;
                wfAutoCloseLine = -1;
                return;
            }
            if (col > 0)
            {
                int n = EditorIndentation.BackspaceDeleteCount(ln, col, TabWF(buf));
                int start = n > 1 ? col - n : EditorTextLayout.PrevCharBoundary(ln, col);
                buf.Lines[buf.CursorLine] = ln.Remove(start, col - start);
                buf.CursorCol = start;
                if (wfAutoCloseLine == buf.CursorLine && wfAutoCloseCol > start) wfAutoCloseCol -= col - start;
            }
            else if (buf.CursorLine > 0)
            {
                int prevLen = buf.Lines[buf.CursorLine - 1].Length;
                buf.Lines[buf.CursorLine - 1] += buf.Lines[buf.CursorLine];
                buf.Lines.RemoveAt(buf.CursorLine);
                buf.CursorLine--;
                buf.CursorCol = prevLen;
                wfAutoCloseLine = -1;
            }
        }

        /// <summary>A closing bracket typed as the first character of a line re-indents it to its opener's line (VS Code).</summary>
        private void ReindentClosingBracketWF(EditorTabBuffer buf)
        {
            if (buf.HasMultiCursors) return;
            string ln = buf.Lines[buf.CursorLine] ?? "";
            int col = buf.CursorCol;
            if (col <= 0 || col > ln.Length || ln.Substring(0, col - 1).Trim().Length != 0) return;
            int aL, aC, bL, bC;
            if (!FindMatchingBracket(buf.Lines, buf.CursorLine, col - 1, out aL, out aC, out bL, out bC) || bL >= buf.CursorLine) return;
            string opener = buf.Lines[bL] ?? "";
            string ind = opener.Substring(0, EditorIndentation.LeadingWhitespaceLength(opener));
            int oldWs = EditorIndentation.LeadingWhitespaceLength(ln);
            buf.Lines[buf.CursorLine] = ind + ln.Substring(oldWs);
            buf.CursorCol = Math.Max(0, col - oldWs + ind.Length);
        }

        /// <summary>Delete at the primary caret (a surrogate pair is deleted as one character).</summary>
        private void DeleteForwardWF(EditorTabBuffer buf)
        {
            string ln = buf.Lines[buf.CursorLine] ?? "";
            if (buf.CursorCol < ln.Length)
            {
                int end = EditorTextLayout.NextCharBoundary(ln, buf.CursorCol);
                buf.Lines[buf.CursorLine] = ln.Remove(buf.CursorCol, end - buf.CursorCol);
            }
            else if (buf.CursorLine + 1 < buf.Lines.Count)
            {
                buf.Lines[buf.CursorLine] += buf.Lines[buf.CursorLine + 1];
                buf.Lines.RemoveAt(buf.CursorLine + 1);
            }
        }
    }
}
