using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>Outcome of applying a WorkspaceEdit.</summary>
    public class LspApplyResult
    {
        public int FilesChanged;
        public int EditsApplied;
        public List<EditorTabBuffer> ChangedBuffers = new List<EditorTabBuffer>();
        public List<string> ChangedFilesOnDisk = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Success { get { return Errors.Count == 0; } }

        public string Summary
        {
            get
            {
                string s = EditsApplied + " edit" + (EditsApplied == 1 ? "" : "s") + " in " + FilesChanged + " file" + (FilesChanged == 1 ? "" : "s");
                if (Errors.Count > 0) s += " (" + Errors.Count + " failed: " + Errors[0] + ")";
                return s;
            }
        }
    }

    /// <summary>
    /// Applies LSP edits to codeLess buffers (with an undo snapshot) and to files on disk, and inserts completion
    /// items (snippets expanded to plain text, caret on the first tab stop). Shared by both UIs. C# 5 only.
    /// </summary>
    public static class LspEditing
    {
        public static string BufferText(EditorTabBuffer buf)
        {
            if (buf == null) return "";
            if (buf.Lines == null || buf.Lines.Count == 0) return buf.Content ?? "";
            return string.Join("\n", buf.Lines.ToArray());
        }

        private static int OffsetOfEditorPosition(EditorTabBuffer buf, int line, int col)
        {
            int off = 0;
            int n = Math.Min(line, buf.Lines.Count);
            for (int i = 0; i < n; i++) off += buf.Lines[i].Length + 1;
            if (line < buf.Lines.Count) off += Math.Max(0, Math.Min(col, buf.Lines[line].Length));
            return off;
        }

        /// <summary>Maps an offset in the original text through a set of edits (offsets inside a replaced span move to its end).</summary>
        private static int MapOffset(string text, List<int> starts, IList<LspTextEdit> edits, int offset)
        {
            int delta = 0;
            foreach (LspTextEdit e in edits)
            {
                int s = LspText.OffsetOf(text, starts, e.Range.StartLine, e.Range.StartCharacter, null);
                int en = LspText.OffsetOf(text, starts, e.Range.EndLine, e.Range.EndCharacter, null);
                if (en < s) { int t = s; s = en; en = t; }
                int insLen = (e.NewText ?? "").Length;
                if (en <= offset) delta += insLen - (en - s);
                else if (s < offset) delta += (s + insLen) - offset; // caret inside a replaced span -> end of replacement
            }
            return offset + delta;
        }

        private static void SetCaretFromOffset(EditorTabBuffer buf, int offset)
        {
            int remaining = Math.Max(0, offset);
            for (int i = 0; i < buf.Lines.Count; i++)
            {
                int len = buf.Lines[i].Length;
                if (remaining <= len || i == buf.Lines.Count - 1)
                {
                    buf.CursorLine = i;
                    buf.CursorCol = Math.Min(remaining, len);
                    return;
                }
                remaining -= len + 1;
            }
        }

        /// <summary>Applies TextEdits to an open buffer as one undoable step; the caret follows the text. Returns false on overlap.</summary>
        public static bool ApplyEditsToBuffer(EditorTabBuffer buf, IList<LspTextEdit> edits, bool pushUndo)
        {
            if (buf == null || edits == null) return false;
            if (edits.Count == 0) return true;
            string text = BufferText(buf);
            List<LspTextEdit> normalized = new List<LspTextEdit>();
            foreach (LspTextEdit e in edits) normalized.Add(new LspTextEdit(e.Range, LspText.NormalizeEol(e.NewText, "\n")));
            string newText = LspText.ApplyTextEdits(text, normalized, null);
            if (newText == null) return false;
            if (string.Equals(newText, text, StringComparison.Ordinal)) return true;
            List<int> starts = LspText.LineStarts(text);
            int caret = OffsetOfEditorPosition(buf, buf.CursorLine, buf.CursorCol);
            int newCaret = MapOffset(text, starts, normalized, caret);
            if (pushUndo) buf.PushUndoSnapshot();
            buf.Content = newText;
            buf.SyncLinesFromContent();
            buf.SyncContentFromLines(false);
            buf.ClearSelection();
            if (buf.ExtraCarets != null) buf.ExtraCarets.Clear();
            SetCaretFromOffset(buf, newCaret);
            return true;
        }

        /// <summary>Identifier characters for word-prefix detection (letters, digits, _ and $).</summary>
        public static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }

        /// <summary>Column where the identifier ending at <paramref name="col"/> starts.</summary>
        public static int WordStart(string line, int col)
        {
            if (line == null) return 0;
            col = Math.Max(0, Math.Min(col, line.Length));
            int s = col;
            while (s > 0 && IsWordChar(line[s - 1])) s--;
            return s;
        }

        /// <summary>Word under / just before the caret, or "".</summary>
        public static string WordAt(string line, int col, out int start)
        {
            start = 0;
            if (string.IsNullOrEmpty(line)) return "";
            col = Math.Max(0, Math.Min(col, line.Length));
            int s = col, e = col;
            while (s > 0 && IsWordChar(line[s - 1])) s--;
            while (e < line.Length && IsWordChar(line[e])) e++;
            start = s;
            return line.Substring(s, e - s);
        }

        /// <summary>
        /// Inserts a completion item at the buffer's caret: uses the item's textEdit (end extended to the caret when
        /// the user kept typing), else replaces the identifier prefix; expands snippets; applies additionalTextEdits
        /// (auto-imports); places the caret on the first tab stop and selects its placeholder.
        /// </summary>
        public static bool ApplyCompletion(EditorTabBuffer buf, LspCompletionItem item)
        {
            if (buf == null || item == null) return false;
            buf.ClampCursor();
            int line = buf.CursorLine;
            int col = buf.CursorCol;
            string lineText = buf.Lines[line];
            LspRange range;
            string insert;
            if (item.TextEdit != null && item.TextEdit.Range != null && item.TextEdit.Range.StartLine == line)
            {
                LspRange r = item.TextEdit.Range;
                int endCol = Math.Max(r.EndCharacter, col);
                if (r.EndLine != line) endCol = col;
                range = new LspRange(line, Math.Min(r.StartCharacter, col), line, Math.Min(endCol, lineText.Length));
                insert = item.TextEdit.NewText ?? "";
            }
            else
            {
                int ws = WordStart(lineText, col);
                range = new LspRange(line, ws, line, col);
                insert = item.InsertText ?? item.Label ?? "";
            }
            int caretInInsert = insert.Length, selLen = 0;
            string text = insert;
            if (item.IsSnippet) text = LspText.ExpandSnippet(insert, out caretInInsert, out selLen);
            if (text.IndexOf('\n') >= 0)
            {
                int ind = 0;
                while (ind < lineText.Length && (lineText[ind] == ' ' || lineText[ind] == '\t')) ind++;
                if (ind > 0)
                {
                    int nlBefore = 0;
                    for (int k = 0; k < Math.Min(caretInInsert, text.Length); k++) if (text[k] == '\n') nlBefore++;
                    caretInInsert += nlBefore * ind;
                    text = ReindentContinuationLines(text, lineText);
                }
            }

            string full = BufferText(buf);
            List<int> starts = LspText.LineStarts(full);
            int mainStart = LspText.OffsetOf(full, starts, range.StartLine, range.StartCharacter, null);
            List<LspTextEdit> all = new List<LspTextEdit>();
            all.Add(new LspTextEdit(range, text));
            int shiftBefore = 0;
            if (item.AdditionalTextEdits != null)
            {
                foreach (LspTextEdit ae in item.AdditionalTextEdits)
                {
                    string at = LspText.NormalizeEol(ae.NewText, "\n");
                    all.Add(new LspTextEdit(ae.Range, at));
                    int s = LspText.OffsetOf(full, starts, ae.Range.StartLine, ae.Range.StartCharacter, null);
                    int e = LspText.OffsetOf(full, starts, ae.Range.EndLine, ae.Range.EndCharacter, null);
                    if (e <= mainStart) shiftBefore += at.Length - (e - s);
                }
            }
            string newText = LspText.ApplyTextEdits(full, all, null);
            if (newText == null)
            {
                // Overlapping additional edits (non-conforming server): insert just the main text.
                newText = LspText.ApplyTextEdits(full, new List<LspTextEdit> { all[0] }, null);
                shiftBefore = 0;
                if (newText == null) return false;
            }
            buf.PushUndoSnapshot();
            buf.Content = newText;
            buf.SyncLinesFromContent();
            buf.SyncContentFromLines(false);
            buf.ClearSelection();
            int caretOffset = mainStart + shiftBefore + caretInInsert;
            if (selLen > 0)
            {
                SetCaretFromOffset(buf, caretOffset);
                buf.SelectionAnchorLine = buf.CursorLine;
                buf.SelectionAnchorCol = buf.CursorCol;
                SetCaretFromOffset(buf, caretOffset + selLen);
            }
            else SetCaretFromOffset(buf, caretOffset);
            return true;
        }

        /// <summary>Multi-line completion text: indent continuation lines like the current line (VS Code behaviour).</summary>
        private static string ReindentContinuationLines(string text, string currentLine)
        {
            if (text.IndexOf('\n') < 0) return text;
            int i = 0;
            while (i < currentLine.Length && (currentLine[i] == ' ' || currentLine[i] == '\t')) i++;
            if (i == 0) return text;
            string indent = currentLine.Substring(0, i);
            return text.Replace("\n", "\n" + indent);
        }

        // ---------------------------------------------------------------- workspace edits

        /// <summary>
        /// Applies a WorkspaceEdit: text edits go to the open buffer when the file is open (one undo step per buffer),
        /// otherwise to the file on disk (encoding, BOM and line endings preserved). Resource operations
        /// create / rename / delete files. <paramref name="findOpenBuffer"/> maps a path to an open buffer (or null);
        /// <paramref name="onRenamed"/> lets the UI retarget an open tab after a rename.
        /// </summary>
        public static LspApplyResult ApplyWorkspaceEdit(LspWorkspaceEdit edit, Func<string, EditorTabBuffer> findOpenBuffer, Action<string, string> onRenamed)
        {
            LspApplyResult result = new LspApplyResult();
            if (edit == null) return result;
            HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (LspWorkspaceEdit.Step step in edit.Steps)
            {
                try
                {
                    if (step.Resource != null)
                    {
                        ApplyResourceOperation(step.Resource, findOpenBuffer, onRenamed, result);
                        continue;
                    }
                    if (step.Edits == null || step.Edits.Count == 0) continue;
                    EditorTabBuffer buf = findOpenBuffer != null ? findOpenBuffer(step.FilePath) : null;
                    if (buf != null)
                    {
                        if (!ApplyEditsToBuffer(buf, step.Edits, true))
                        {
                            result.Errors.Add("Overlapping edits for " + Path.GetFileName(step.FilePath));
                            continue;
                        }
                        if (!result.ChangedBuffers.Contains(buf)) result.ChangedBuffers.Add(buf);
                    }
                    else
                    {
                        ApplyEditsToFile(step.FilePath, step.Edits);
                        if (!result.ChangedFilesOnDisk.Contains(step.FilePath)) result.ChangedFilesOnDisk.Add(step.FilePath);
                    }
                    result.EditsApplied += step.Edits.Count;
                    touched.Add(LspText.PathKey(step.FilePath));
                }
                catch (Exception ex)
                {
                    result.Errors.Add(Path.GetFileName(step.FilePath ?? (step.Resource != null ? step.Resource.FilePath : "")) + ": " + ex.Message);
                }
            }
            result.FilesChanged = touched.Count;
            return result;
        }

        /// <summary>Applies edits to a file on disk, preserving its encoding / BOM and dominant line ending.</summary>
        public static void ApplyEditsToFile(string path, IList<LspTextEdit> edits)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
            byte[] raw = File.ReadAllBytes(path);
            Encoding enc;
            string text;
            using (MemoryStream ms = new MemoryStream(raw))
            using (StreamReader sr = new StreamReader(ms, new UTF8Encoding(false), true))
            {
                text = sr.ReadToEnd();
                enc = sr.CurrentEncoding;
            }
            bool hasUtf8Bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            if (enc is UTF8Encoding) enc = new UTF8Encoding(hasUtf8Bom);
            string eol = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            List<LspTextEdit> normalized = new List<LspTextEdit>();
            foreach (LspTextEdit e in edits) normalized.Add(new LspTextEdit(e.Range, LspText.NormalizeEol(e.NewText, eol)));
            string updated = LspText.ApplyTextEdits(text, normalized, null);
            if (updated == null) throw new InvalidOperationException("Overlapping edits");
            if (string.Equals(updated, text, StringComparison.Ordinal)) return;
            string tmp = path + ".codeless-tmp";
            File.WriteAllText(tmp, updated, enc);
            try
            {
                File.Copy(tmp, path, true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static void ApplyResourceOperation(LspResourceOperation op, Func<string, EditorTabBuffer> findOpenBuffer, Action<string, string> onRenamed, LspApplyResult result)
        {
            if (op.Kind == "create")
            {
                if (File.Exists(op.FilePath))
                {
                    if (op.IgnoreIfExists || !op.Overwrite) return;
                }
                string dir = Path.GetDirectoryName(op.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(op.FilePath, "");
                result.EditsApplied++;
                result.ChangedFilesOnDisk.Add(op.FilePath);
            }
            else if (op.Kind == "rename")
            {
                if (File.Exists(op.NewFilePath))
                {
                    if (op.IgnoreIfExists) return;
                    if (!op.Overwrite) throw new IOException("Target exists: " + op.NewFilePath);
                    File.Delete(op.NewFilePath);
                }
                string dir = Path.GetDirectoryName(op.NewFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (Directory.Exists(op.FilePath)) Directory.Move(op.FilePath, op.NewFilePath);
                else File.Move(op.FilePath, op.NewFilePath);
                if (onRenamed != null) onRenamed(op.FilePath, op.NewFilePath);
                result.EditsApplied++;
                result.ChangedFilesOnDisk.Add(op.NewFilePath);
            }
            else if (op.Kind == "delete")
            {
                if (Directory.Exists(op.FilePath))
                {
                    Directory.Delete(op.FilePath, op.Recursive);
                }
                else if (File.Exists(op.FilePath))
                {
                    File.Delete(op.FilePath);
                }
                else if (!op.IgnoreIfExists) return;
                result.EditsApplied++;
            }
        }
    }
}
