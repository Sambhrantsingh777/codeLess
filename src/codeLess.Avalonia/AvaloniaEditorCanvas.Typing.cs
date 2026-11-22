using System;
using System.Collections.Generic;
using System.Linq;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Typing rules of the Avalonia editor that depend on the buffer's indentation settings (QA #12) and VS Code's
    /// editing defaults: Tab / Shift+Tab, Enter auto-indent ("{|}" split, trimAutoWhitespace, Python dedent after
    /// return / pass), Backspace to the previous tab stop and over empty pairs, auto-closing brackets and quotes with
    /// overtype of auto-inserted closers, auto-surround of selections, and "}" re-indenting a blank line.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        // auto-inserted closers of the primary caret (line, column of the closer), VS Code "autoClosingOvertype: auto"
        private readonly List<(int Line, int Col, char Ch)> autoClosed = new List<(int, int, char)>();

        private bool IsIndentBasedLanguage(EditorTabBuffer b) =>
            BracketLexProfile.ForFile(b?.FilePath ?? b?.FileName ?? "").IndentBased;

        /// <summary>Tab: indent the selected lines, replace a partial single-line selection, or insert indentation at every caret.</summary>
        internal void HandleTabKey(bool shift)
        {
            var buf = Buffer;
            if (buf == null) return;
            if (shift) { IndentOrOutdentLines(outdent: true); return; }
            multiAutoClosed.Clear();
            ClampExtraCursors(buf);
            bool anyMultiLine = false;
            var carets = new List<CaretState> { ReadPrimary(buf) };
            foreach (var ec in ExtraCursors) carets.Add(new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
            foreach (var c in carets)
            {
                if (c.AnchorLine < 0) continue;
                if (c.AnchorLine != c.Line) { anyMultiLine = true; break; }
                int lo = Math.Min(c.Col, c.AnchorCol), hi = Math.Max(c.Col, c.AnchorCol);
                if (lo == 0 && hi >= (buf.Lines[c.Line] ?? "").Length && hi > lo) { anyMultiLine = true; break; } // whole line selected
            }
            if (anyMultiLine) { IndentOrOutdentLines(outdent: false); return; }
            int tab = TabSize;
            bool spaces = InsertSpaces;
            bool coalesce = ExtraCursors.Count == 0 && !buf.HasSelection();
            buf.PushUndoSnapshot(coalesceTyping: coalesce);
            buf.GhostTextSuggestion = "";
            ApplyEditToAllCarets(buf, (b, k) =>
            {
                if (b.HasSelection()) b.DeleteSelection();
                b.InsertTextAtCursor(EditorIndentation.TabInsertText(b.Lines[b.CursorLine], b.CursorCol, tab, spaces));
            });
            FinalizeBufferEdit(buf);
        }

        /// <summary>Enter at every caret: keep the indentation, indent after an opener, split "{|}" into three lines.</summary>
        private void PerformNewlineAcrossCursors()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            multiAutoClosed.Clear();
            ClampExtraCursors(buf);
            int tab = TabSize;
            bool spaces = InsertSpaces;
            bool indentBased = IsIndentBasedLanguage(buf);
            ApplyEditToAllCarets(buf, (b, k) => InsertNewlineAtCaret(b, tab, spaces, indentBased));
            autoClosed.Clear();
            FinalizeBufferEdit(buf);
        }

        internal static void InsertNewlineAtCaret(EditorTabBuffer b, int tab, bool spaces, bool indentBased)
        {
            if (b.HasSelection()) b.DeleteSelection();
            string[] parts = EditorIndentation.SplitForEnter(b.Lines[b.CursorLine], b.CursorCol, tab, spaces, indentBased, out int row, out int col);
            int line = b.CursorLine;
            b.Lines[line] = parts[0];
            for (int i = 1; i < parts.Length; i++) b.Lines.Insert(line + i, parts[i]);
            b.CursorLine = line + row;
            b.CursorCol = col;
            b.SyncContentFromLines(false);
        }

        /// <summary>Cmd/Ctrl+Enter (line below, like End + Enter) and Cmd/Ctrl+Shift+Enter (line above, same indentation).</summary>
        internal void InsertLineAboveOrBelow(bool above)
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            int tab = TabSize;
            bool spaces = InsertSpaces;
            bool indentBased = IsIndentBasedLanguage(buf);
            ApplyEditToAllCarets(buf, (b, k) =>
            {
                b.ClearSelection();
                string cur = b.Lines[b.CursorLine] ?? "";
                if (above)
                {
                    string ind = cur.Substring(0, EditorIndentation.LeadingWhitespaceLength(cur));
                    b.Lines.Insert(b.CursorLine, ind);
                    b.CursorCol = ind.Length;
                    b.SyncContentFromLines(false);
                }
                else
                {
                    b.CursorCol = cur.Length;
                    InsertNewlineAtCaret(b, tab, spaces, indentBased);
                }
            });
            FinalizeBufferEdit(buf);
        }

        /// <summary>Backspace: empty pair "(|)" deletes both, spaces-only indentation deletes back to the previous tab stop.</summary>
        private void BackspaceAtCaret(EditorTabBuffer b, int tab)
        {
            if (b.HasSelection()) { b.DeleteSelection(); return; }
            string ln = b.Lines[b.CursorLine] ?? "";
            int col = Math.Min(ln.Length, b.CursorCol);
            if (col > 0 && EditorAutoClosing.ShouldDeletePair(ln, col, b.FilePath ?? b.FileName) && IsAutoClosedAt(b.CursorLine, col))
            {
                autoClosed.RemoveAll(a => a.Line == b.CursorLine && a.Col == col);
                b.Lines[b.CursorLine] = ln.Remove(col - 1, 2);
                b.CursorCol = col - 1;
                b.SyncContentFromLines(false);
                return;
            }
            int n = EditorIndentation.BackspaceDeleteCount(ln, col, tab);
            if (n > 1)
            {
                b.Lines[b.CursorLine] = ln.Remove(col - n, n);
                b.CursorCol = col - n;
                b.SyncContentFromLines(false);
                return;
            }
            b.PerformBackspace();
        }

        private bool IsAutoClosedAt(int line, int col)
        {
            // VS Code autoClosingDelete "auto": only pairs this editor inserted; multi-cursor edits do not track them
            for (int i = 0; i < autoClosed.Count; i++) if (autoClosed[i].Line == line && autoClosed[i].Col == col) return true;
            return false;
        }

        /// <summary>
        /// Auto-closing / overtype / auto-surround for one typed character (single caret or every caret). Returns false
        /// when the character should be inserted normally.
        /// </summary>
        internal bool TryHandleAutoClosingInput(string text)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(text) || text.Length != 1) return false;
            char ch = text[0];
            string path = buf.FilePath ?? buf.FileName ?? "";
            ClampExtraCursors(buf);
            if (ExtraCursors.Count > 0 && ch != '\n' && ch != '\r')
            {
                // several carets: the shared engine (same as WinForms) auto-closes / overtypes / surrounds at every
                // caret and remembers each auto-inserted closer, so ")" steps over them and Backspace deletes the pairs
                TypeCharAtAllCarets(buf, ch, path);
                TextTyped?.Invoke(text);
                return true;
            }
            multiAutoClosed.Clear();
            bool isOpener = EditorAutoClosing.CloserFor(ch, path) != '\0';
            bool isCloserOrQuote = EditorAutoClosing.IsCloser(ch) || EditorAutoClosing.IsQuote(ch);
            if (!isOpener && !isCloserOrQuote) { TrackTyping(buf, text); return false; }
            ClampExtraCursors(buf);
            bool multi = ExtraCursors.Count > 0;
            bool anySelection = buf.HasSelection() || ExtraCursors.Any(c => c.HasSelection);

            // auto-surround: every caret has a selection
            if (anySelection && isOpener && EditorAutoClosing.TryGetSurroundPair(ch, path, out string open, out string close) &&
                buf.HasSelection() && ExtraCursors.All(c => c.HasSelection))
            {
                buf.PushUndoSnapshot();
                buf.GhostTextSuggestion = "";
                var carets = new List<CaretState> { ReadPrimary(buf) };
                foreach (var ec in ExtraCursors) carets.Add(new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.AnchorLine, AnchorCol = ec.AnchorCol });
                // insert from the last selection to the first so earlier positions stay valid
                var order = carets.Select((c, i) => (c, i)).OrderByDescending(t => Math.Max(OffsetOf(buf, t.c.Line, t.c.Col), OffsetOf(buf, t.c.AnchorLine, t.c.AnchorCol))).ToList();
                var result = new CaretState[carets.Count];
                foreach (var (c, i) in order)
                {
                    bool fwd = c.AnchorLine < c.Line || (c.AnchorLine == c.Line && c.AnchorCol <= c.Col);
                    int sl = fwd ? c.AnchorLine : c.Line, sc = fwd ? c.AnchorCol : c.Col, el = fwd ? c.Line : c.AnchorLine, ec2 = fwd ? c.Col : c.AnchorCol;
                    buf.Lines[el] = buf.Lines[el].Insert(Math.Min(ec2, buf.Lines[el].Length), close);
                    buf.Lines[sl] = buf.Lines[sl].Insert(Math.Min(sc, buf.Lines[sl].Length), open);
                    int nsc = sc + open.Length, nec = ec2 + (sl == el ? open.Length : 0);
                    result[i] = fwd ? new CaretState { AnchorLine = sl, AnchorCol = nsc, Line = el, Col = nec }
                                    : new CaretState { AnchorLine = el, AnchorCol = nec, Line = sl, Col = nsc };
                }
                WritePrimary(buf, result[0]);
                ExtraCursors.Clear();
                for (int i = 1; i < result.Length; i++)
                    ExtraCursors.Add(new ExtraCursorCaret { Line = result[i].Line, Col = result[i].Col, AnchorLine = result[i].AnchorLine, AnchorCol = result[i].AnchorCol });
                autoClosed.Clear();
                FinalizeBufferEdit(buf);
                TextTyped?.Invoke(text);
                return true;
            }
            if (anySelection) { autoClosed.Clear(); return false; }

            // overtype: every caret sits before the same character
            if (isCloserOrQuote)
            {
                bool all = true;
                bool primaryAuto = IsAutoClosedAt(buf.CursorLine, buf.CursorCol);
                if (!EditorAutoClosing.ShouldOvertype(buf.Lines[buf.CursorLine], buf.CursorCol, ch, primaryAuto)) all = false;
                foreach (var ec in ExtraCursors)
                    if (!EditorAutoClosing.ShouldOvertype(buf.Lines[ec.Line], ec.Col, ch, true)) { all = false; break; }
                if (all)
                {
                    buf.CursorCol++;
                    for (int i = 0; i < ExtraCursors.Count; i++) { var ec = ExtraCursors[i]; ec.Col++; ExtraCursors[i] = ec; }
                    autoClosed.RemoveAll(a => a.Line == buf.CursorLine && a.Col == buf.CursorCol - 1);
                    owner.NotifyCaretMoved();
                    EnsureCursorVisible();
                    InvalidateVisual();
                    TextTyped?.Invoke(text);
                    return true;
                }
            }

            if (!isOpener)
            {
                TrackTyping(buf, text);
                if (EditorAutoClosing.IsCloser(ch) && !multi) return TryReindentClosingBracket(buf, ch, text);
                return false;
            }
            bool close1 = EditorAutoClosing.ShouldAutoClose(buf.Lines[buf.CursorLine], buf.CursorCol, ch, path);
            foreach (var ec in ExtraCursors)
                if (!EditorAutoClosing.ShouldAutoClose(buf.Lines[ec.Line], ec.Col, ch, path)) { close1 = false; break; }
            if (!close1) { TrackTyping(buf, text); return false; }
            char closer = EditorAutoClosing.CloserFor(ch, path);
            buf.PushUndoSnapshot(coalesceTyping: !multi);
            buf.GhostTextSuggestion = "";
            ApplyEditToAllCarets(buf, (b, k) =>
            {
                b.InsertTextAtCursor(ch.ToString() + closer);
                b.CursorCol--;
            });
            if (!multi)
            {
                ShiftAutoClosed(buf.CursorLine, buf.CursorCol - 1, 2);
                autoClosed.Add((buf.CursorLine, buf.CursorCol, closer));
            }
            FinalizeBufferEdit(buf);
            TextTyped?.Invoke(text);
            return true;
        }

        // absolute offsets of closers auto-inserted at several carets (EditorMultiCaret keeps them shifted)
        private readonly List<long> multiAutoClosed = new List<long>();

        private void SyncExtraCursorsToBuffer(EditorTabBuffer buf)
        {
            buf.ExtraCarets.Clear();
            foreach (var ec in ExtraCursors)
                buf.ExtraCarets.Add(new EditorCaret { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
        }

        private void SyncExtraCursorsFromBuffer(EditorTabBuffer buf)
        {
            ExtraCursors.Clear();
            foreach (var c in buf.ExtraCarets)
                ExtraCursors.Add(new ExtraCursorCaret { Line = c.Line, Col = c.Col, AnchorLine = c.HasSelection() ? c.AnchorLine : -1, AnchorCol = c.HasSelection() ? c.AnchorCol : -1 });
            buf.ExtraCarets.Clear();
        }

        private void TypeCharAtAllCarets(EditorTabBuffer buf, char ch, string path)
        {
            bool replacing = buf.HasSelection() || ExtraCursors.Any(c => c.HasSelection);
            buf.PushUndoSnapshot(coalesceTyping: !replacing);
            if (replacing) { buf.LastTypingUndoTick = Environment.TickCount; buf.LastTypingUndoLine = buf.CursorLine; }
            buf.GhostTextSuggestion = "";
            autoClosed.Clear();
            SyncExtraCursorsToBuffer(buf);
            bool edited = EditorMultiCaret.TypeChar(buf, ch, path, multiAutoClosed);
            SyncExtraCursorsFromBuffer(buf);
            if (edited) { FinalizeBufferEdit(buf); return; }
            owner.NotifyCaretMoved();
            EnsureCursorVisible();
            InvalidateVisual();
        }

        /// <summary>Backspace at several carets through the shared engine (auto-closed pairs, tab stops), same as WinForms.</summary>
        private void BackspaceAtAllCarets(EditorTabBuffer buf)
        {
            SyncExtraCursorsToBuffer(buf);
            EditorMultiCaret.Backspace(buf, TabSize, buf.FilePath ?? buf.FileName ?? "", multiAutoClosed);
            SyncExtraCursorsFromBuffer(buf);
        }

        /// <summary>Keeps the tracked auto-inserted closers aligned with plain typing on their line.</summary>
        private void TrackTyping(EditorTabBuffer buf, string text)
        {
            if (autoClosed.Count == 0) return;
            if (ExtraCursors.Count > 0 || buf.HasSelection() || text.IndexOf('\n') >= 0) { autoClosed.Clear(); return; }
            autoClosed.RemoveAll(a => a.Line != buf.CursorLine);
            ShiftAutoClosed(buf.CursorLine, buf.CursorCol, text.Length);
        }

        private void ShiftAutoClosed(int line, int fromCol, int delta)
        {
            for (int i = 0; i < autoClosed.Count; i++)
                if (autoClosed[i].Line == line && autoClosed[i].Col >= fromCol) autoClosed[i] = (line, autoClosed[i].Col + delta, autoClosed[i].Ch);
        }

        /// <summary>Typing "}" as the first character of a line re-indents it to its opener's line (VS Code decreaseIndentPattern).</summary>
        private bool TryReindentClosingBracket(EditorTabBuffer buf, char ch, string text)
        {
            string ln = buf.Lines[buf.CursorLine] ?? "";
            int col = buf.CursorCol;
            if (col == 0 || ln.Substring(0, Math.Min(col, ln.Length)).Trim().Length != 0) return false;
            buf.PushUndoSnapshot(coalesceTyping: true);
            buf.GhostTextSuggestion = "";
            buf.InsertTextAtCursor(text);
            string now = buf.Lines[buf.CursorLine];
            if (TryFindMatchingBracket(buf, buf.CursorLine, buf.CursorCol, out int ml, out int mc) && ml < buf.CursorLine)
            {
                string opener = buf.Lines[ml] ?? "";
                string ind = opener.Substring(0, EditorIndentation.LeadingWhitespaceLength(opener));
                int oldWs = EditorIndentation.LeadingWhitespaceLength(now);
                buf.Lines[buf.CursorLine] = ind + now.Substring(oldWs);
                buf.CursorCol = Math.Max(0, buf.CursorCol - oldWs + ind.Length);
            }
            FinalizeBufferEdit(buf);
            TextTyped?.Invoke(text);
            return true;
        }
    }
}
