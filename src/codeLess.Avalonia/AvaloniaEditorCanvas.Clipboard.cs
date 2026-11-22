using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Clipboard and undo behaviour of the Avalonia editor matching VS Code: copy / cut of every caret's selection (one
    /// per line, in document order), copy / cut of the whole line without a selection and pasting such a line above the
    /// caret line (editor.emptySelectionClipboard), CRLF-safe paste, and undo / redo that reveal the caret and drop
    /// stale extra carets.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        // text of the last whole-line copy (no selection); pasting exactly that text inserts it above the caret line
        private string lastWholeLineCopy;

        /// <summary>Undo / redo from the keyboard or the Edit menu: one notification path (dirty state, analysis, reveal).</summary>
        public void PerformUndoRedo(bool redo)
        {
            var buf = Buffer;
            if (buf == null) return;
            BindUndoCaretSource(buf);
            bool changed = redo ? buf.PerformRedo() : buf.PerformUndo();
            if (!changed) return;
            // VS Code restores every caret and selection recorded with the undo step (QA #29)
            ExtraCursors.Clear();
            if (buf.ExtraCarets != null)
            {
                foreach (var c in buf.ExtraCarets)
                    ExtraCursors.Add(new ExtraCursorCaret { Line = c.Line, Col = c.Col, AnchorLine = c.HasSelection() ? c.AnchorLine : -1, AnchorCol = c.HasSelection() ? c.AnchorCol : -1 });
                buf.ExtraCarets.Clear();
            }
            autoClosed.Clear();
            if (buf.Lines.Count <= DeferAnalysisLineThreshold && !buf.IsLargeFile)
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            RevealCaretAfterUndoRedo(buf); // centered when the change is off-screen (VS Code)
            owner.NotifyBufferModified();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        /// <summary>Undo snapshots of <paramref name="buf"/> record this canvas' extra carets (the editor that is editing it).</summary>
        internal void BindUndoCaretSource(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (undoCaretSource == null)
            {
                undoCaretSource = () =>
                {
                    var list = new List<EditorCaret>(ExtraCursors.Count);
                    foreach (var ec in ExtraCursors)
                        list.Add(new EditorCaret { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
                    return list;
                };
            }
            if (!ReferenceEquals(buf.ExtraCaretsForUndo, undoCaretSource)) buf.ExtraCaretsForUndo = undoCaretSource;
        }

        private Func<List<EditorCaret>> undoCaretSource;

        /// <summary>Text for Copy / Cut: every selection (document order, one per line) or, with no selection, the caret lines.</summary>
        public string GetCopyText(EditorTabBuffer buf)
        {
            lastWholeLineCopy = null;
            var carets = new List<CaretState> { ReadPrimary(buf) };
            foreach (var ec in ExtraCursors) carets.Add(new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
            bool anySelection = carets.Any(c => c.AnchorLine >= 0);
            var ordered = carets.OrderBy(c => c.AnchorLine >= 0 ? Math.Min(c.Line, c.AnchorLine) : c.Line)
                                .ThenBy(c => c.AnchorLine >= 0 && c.AnchorLine < c.Line ? c.AnchorCol : c.Col).ToList();
            if (!anySelection)
            {
                var sb = new StringBuilder();
                var seen = new HashSet<int>();
                foreach (var c in ordered)
                {
                    if (!seen.Add(c.Line)) continue;
                    sb.Append(buf.Lines[c.Line]).Append('\n');
                }
                lastWholeLineCopy = sb.ToString();
                return lastWholeLineCopy;
            }
            var parts = new List<string>();
            foreach (var c in ordered)
            {
                if (c.AnchorLine < 0) continue;
                parts.Add(SelectionText(buf, c));
            }
            return string.Join("\n", parts);
        }

        private static string SelectionText(EditorTabBuffer buf, CaretState c)
        {
            bool fwd = c.AnchorLine < c.Line || (c.AnchorLine == c.Line && c.AnchorCol <= c.Col);
            int sl = fwd ? c.AnchorLine : c.Line, sc = fwd ? c.AnchorCol : c.Col, el = fwd ? c.Line : c.AnchorLine, ec = fwd ? c.Col : c.AnchorCol;
            sl = Math.Max(0, Math.Min(buf.Lines.Count - 1, sl));
            el = Math.Max(0, Math.Min(buf.Lines.Count - 1, el));
            sc = Math.Max(0, Math.Min(buf.Lines[sl].Length, sc));
            ec = Math.Max(0, Math.Min(buf.Lines[el].Length, ec));
            if (sl == el) return buf.Lines[sl].Substring(sc, Math.Max(0, ec - sc));
            var sb = new StringBuilder();
            sb.Append(buf.Lines[sl].Substring(sc)).Append('\n');
            for (int i = sl + 1; i < el; i++) sb.Append(buf.Lines[i]).Append('\n');
            sb.Append(buf.Lines[el].Substring(0, ec));
            return sb.ToString();
        }

        /// <summary>Cut: delete every selection, or the caret lines when nothing is selected (one undo step).</summary>
        internal void CutSelectionsOrLines(EditorTabBuffer buf)
        {
            bool anySelection = buf.HasSelection() || ExtraCursors.Any(c => c.HasSelection);
            if (!anySelection && ExtraCursors.Count == 0) { DeleteActiveLine(); return; }
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            if (anySelection)
            {
                ApplyEditToAllCarets(buf, (b, k) => { if (b.HasSelection()) b.DeleteSelection(); });
            }
            else
            {
                var lines = new SortedSet<int> { buf.CursorLine };
                foreach (var ec in ExtraCursors) lines.Add(ec.Line);
                foreach (int l in lines.Reverse())
                {
                    if (buf.Lines.Count > 1) buf.Lines.RemoveAt(l);
                    else buf.Lines[0] = "";
                }
                ExtraCursors.Clear();
                buf.CursorLine = Math.Min(lines.Min, buf.Lines.Count - 1);
                buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[buf.CursorLine].Length);
                buf.ClearSelection();
            }
            FinalizeBufferEdit(buf);
        }

        /// <summary>Paste: CR / CRLF normalized; a whole line copied without a selection goes above the caret line (VS Code).</summary>
        public void PasteText(string clip)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(clip)) return;
            string norm = clip.Replace("\r\n", "\n").Replace("\r", "\n");
            if (lastWholeLineCopy != null && norm == lastWholeLineCopy && ExtraCursors.Count == 0 && !buf.HasSelection() && norm.EndsWith("\n"))
            {
                buf.PushUndoSnapshot();
                string[] lines = norm.Substring(0, norm.Length - 1).Split('\n');
                buf.Lines.InsertRange(buf.CursorLine, lines);
                buf.CursorLine += lines.Length;
                buf.ClearSelection();
                FinalizeBufferEdit(buf);
                return;
            }
            InsertTextAcrossCursors(norm);
        }
    }
}
