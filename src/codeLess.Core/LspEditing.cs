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
        /// <summary>True when the whole edit was refused because it was stale (nothing was applied).</summary>
        public bool Rejected;

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

        /// <summary>Word under / just before the caret ("" when none).</summary>
        public static string WordAtOrEmpty(string line, int col)
        {
            int start;
            return WordAt(line, col, out start);
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
            return ApplyWorkspaceEdit(edit, findOpenBuffer, onRenamed, null);
        }

        /// <summary>
        /// Same as <see cref="ApplyWorkspaceEdit(LspWorkspaceEdit, Func{string, EditorTabBuffer}, Action{string, string})"/>, but
        /// first validates the edit against <paramref name="guard"/> (document versions / buffer text at request time).
        /// A stale edit is rejected as a whole - nothing is applied - exactly like VS Code, instead of landing at shifted offsets.
        /// </summary>
        public static LspApplyResult ApplyWorkspaceEdit(LspWorkspaceEdit edit, Func<string, EditorTabBuffer> findOpenBuffer, Action<string, string> onRenamed, LspEditGuard guard)
        {
            LspApplyResult result = new LspApplyResult();
            if (edit == null) return result;
            if (guard != null)
            {
                string stale = guard.Validate(edit, findOpenBuffer);
                if (stale != null)
                {
                    result.Errors.Add(stale);
                    result.Rejected = true;
                    return result;
                }
            }
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

        /// <summary>
        /// Applies edits to a file on disk. The file is decoded with the editor's lossless detection (BOM / strict UTF-8 /
        /// ANSI-Latin-1), edited on its exact text (mixed line endings untouched, inserted text uses the dominant EOL) and
        /// re-encoded in the same encoding + BOM. Fails - without touching the file - when the new text can't be represented
        /// in the file's encoding (instead of silently writing '?').
        /// </summary>
        public static void ApplyEditsToFile(string path, IList<LspTextEdit> edits)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
            Encoding enc;
            bool hasBom;
            string text = EditorTabBuffer.ReadFileExact(path, out enc, out hasBom);
            string eol = DominantEol(text);
            List<LspTextEdit> normalized = new List<LspTextEdit>();
            foreach (LspTextEdit e in edits) normalized.Add(new LspTextEdit(e.Range, LspText.NormalizeEol(e.NewText, eol)));
            string updated = LspText.ApplyTextEdits(text, normalized, null);
            if (updated == null) throw new InvalidOperationException("Overlapping edits");
            if (string.Equals(updated, text, StringComparison.Ordinal)) return;
            byte[] bytes;
            try { bytes = EditorTabBuffer.EncodeTextStrict(updated, enc, hasBom); }
            catch (EncoderFallbackException)
            {
                throw new InvalidOperationException("The new text contains characters that can't be saved in the file's encoding (" +
                    enc.WebName + "). Open the file and save it as UTF-8 first.");
            }
            EditorTabBuffer.WriteBytesInPlace(path, bytes);
        }

        private static string DominantEol(string text)
        {
            int crlf = 0, lf = 0, cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n') lf++;
            }
            if (crlf == 0 && lf == 0 && cr == 0) return "\n";
            if (crlf >= lf && crlf >= cr) return "\r\n";
            if (cr > lf) return "\r";
            return "\n";
        }

        /// <summary>True when two paths name the same file system entry (case-insensitive volumes, /private aliases).</summary>
        public static bool IsSameFileSystemEntry(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string fa, fb;
            try { fa = Path.GetFullPath(a); fb = Path.GetFullPath(b); } catch { return false; }
            if (string.Equals(fa, fb, StringComparison.Ordinal)) return true;
            if (!string.Equals(LspText.PathKey(fa), LspText.PathKey(fb), StringComparison.Ordinal) &&
                !string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return false;
            // Same name modulo case: same entry only if the volume is case-insensitive, i.e. the other spelling resolves to
            // the existing entry. Probe with the parent directory listing so case-sensitive volumes are handled correctly.
            try
            {
                if (!File.Exists(fa) && !Directory.Exists(fa)) return false;
                if (!File.Exists(fb) && !Directory.Exists(fb)) return false;
                string dir = Path.GetDirectoryName(fa);
                string nameB = Path.GetFileName(fb);
                string nameA = Path.GetFileName(fa);
                bool hasA = false, hasB = false;
                foreach (string e in Directory.GetFileSystemEntries(dir))
                {
                    string n = Path.GetFileName(e);
                    if (string.Equals(n, nameA, StringComparison.Ordinal)) hasA = true;
                    if (string.Equals(n, nameB, StringComparison.Ordinal)) hasB = true;
                }
                // On a case-sensitive volume both spellings are listed separately; on a case-insensitive one only one is.
                return !(hasA && hasB && !string.Equals(nameA, nameB, StringComparison.Ordinal));
            }
            catch { return false; }
        }

        private static bool EntryExists(string p)
        {
            return File.Exists(p) || Directory.Exists(p);
        }

        private static void MoveEntry(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        private static string UniqueSiblingTempName(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            for (int i = 0; i < 100; i++)
            {
                string cand = Path.Combine(dir, "." + Path.GetFileName(path) + ".codeless-rename-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                if (!EntryExists(cand)) return cand;
            }
            throw new IOException("Could not find a temporary name next to " + path);
        }

        /// <summary>
        /// Renames a file or folder safely. Case-only renames (Foo.ts -> foo.ts) on case-insensitive volumes go through a
        /// temporary name. With <paramref name="overwrite"/> an existing, different target is first moved aside and only
        /// deleted after the move succeeded (restored otherwise) - the source is never deleted.
        /// </summary>
        public static void RenameEntrySafely(string from, string to, bool overwrite)
        {
            if (!EntryExists(from)) throw new FileNotFoundException("Source not found", from);
            string dir = Path.GetDirectoryName(Path.GetFullPath(to));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (IsSameFileSystemEntry(from, to))
            {
                if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal)) return;
                string tmp = UniqueSiblingTempName(from);
                MoveEntry(from, tmp);
                try { MoveEntry(tmp, to); }
                catch
                {
                    try { MoveEntry(tmp, from); } catch { }
                    throw;
                }
                return;
            }
            if (!EntryExists(to))
            {
                MoveEntry(from, to);
                return;
            }
            if (!overwrite) throw new IOException("Target exists: " + to);
            string backup = UniqueSiblingTempName(to);
            MoveEntry(to, backup);
            try
            {
                MoveEntry(from, to);
            }
            catch
            {
                try { MoveEntry(backup, to); } catch { }
                throw;
            }
            try
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                else File.Delete(backup);
            }
            catch { }
        }

        /// <summary>Resource operations with the LSP option semantics (overwrite wins over ignoreIfExists; delete honours ignoreIfNotExists).</summary>
        private static void ApplyResourceOperation(LspResourceOperation op, Func<string, EditorTabBuffer> findOpenBuffer, Action<string, string> onRenamed, LspApplyResult result)
        {
            if (op.Kind == "create")
            {
                if (EntryExists(op.FilePath))
                {
                    if (!op.Overwrite)
                    {
                        if (op.IgnoreIfExists) return;
                        throw new IOException("File exists: " + op.FilePath);
                    }
                    if (Directory.Exists(op.FilePath)) throw new IOException("A folder exists at " + op.FilePath);
                }
                string dir = Path.GetDirectoryName(op.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                EditorTabBuffer open = findOpenBuffer != null ? findOpenBuffer(op.FilePath) : null;
                if (open != null)
                {
                    // Overwrite of an open file: empty the buffer as one undoable step instead of truncating the file
                    // behind the editor's back (the buffer would otherwise save its old text over the new file).
                    open.PushUndoSnapshot();
                    open.Content = "";
                    open.SyncLinesFromContent();
                    open.SyncContentFromLines(false);
                    open.ClearSelection();
                    open.CursorLine = 0;
                    open.CursorCol = 0;
                    if (!result.ChangedBuffers.Contains(open)) result.ChangedBuffers.Add(open);
                }
                if (File.Exists(op.FilePath)) EditorTabBuffer.WriteBytesInPlace(op.FilePath, new byte[0]);
                else File.WriteAllBytes(op.FilePath, new byte[0]);
                result.EditsApplied++;
                result.ChangedFilesOnDisk.Add(op.FilePath);
            }
            else if (op.Kind == "rename")
            {
                if (!EntryExists(op.FilePath)) throw new FileNotFoundException("Source not found", op.FilePath);
                if (EntryExists(op.NewFilePath) && !IsSameFileSystemEntry(op.FilePath, op.NewFilePath) && !op.Overwrite)
                {
                    if (op.IgnoreIfExists) return;
                    throw new IOException("Target exists: " + op.NewFilePath);
                }
                RenameEntrySafely(op.FilePath, op.NewFilePath, op.Overwrite);
                if (onRenamed != null) onRenamed(op.FilePath, op.NewFilePath);
                result.EditsApplied++;
                result.ChangedFilesOnDisk.Add(op.NewFilePath);
            }
            else if (op.Kind == "delete")
            {
                if (Directory.Exists(op.FilePath))
                {
                    if (!op.Recursive && Directory.GetFileSystemEntries(op.FilePath).Length > 0)
                        throw new IOException("Folder is not empty (recursive delete not requested): " + op.FilePath);
                    Directory.Delete(op.FilePath, op.Recursive);
                }
                else if (File.Exists(op.FilePath))
                {
                    File.Delete(op.FilePath);
                }
                else
                {
                    if (op.IgnoreIfNotExists) return;
                    throw new FileNotFoundException("File not found", op.FilePath);
                }
                result.EditsApplied++;
            }
        }
    }

    /// <summary>
    /// Guards a WorkspaceEdit against stale offsets (LSP TextDocumentEdit.version semantics + VS Code's "file changed
    /// since the request" check). Capture it on the UI thread when sending rename / code action requests; for
    /// server-initiated workspace/applyEdit use it without a snapshot (the version and "no unsent typing" checks still apply).
    /// </summary>
    public sealed class LspEditGuard
    {
        private readonly Dictionary<string, string> textAtRequest = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Version last sent to the server for an open document (-1 = not open on the server).</summary>
        public Func<string, int> SentVersion;
        /// <summary>Text the server currently has for an open document (null = not open on the server).</summary>
        public Func<string, string> SentText;

        /// <summary>Snapshots the text of every open buffer (UI thread, at request time).</summary>
        public static LspEditGuard Capture(IEnumerable<EditorTabBuffer> openBuffers, LanguageServerManager lsp)
        {
            LspEditGuard g = ForServer(lsp);
            if (openBuffers != null)
            {
                foreach (EditorTabBuffer b in openBuffers)
                {
                    if (b == null || string.IsNullOrEmpty(b.FilePath)) continue;
                    g.textAtRequest[LspText.PathKey(b.FilePath)] = LspEditing.BufferText(b);
                }
            }
            return g;
        }

        /// <summary>Guard without a request snapshot (workspace/applyEdit from the server).</summary>
        public static LspEditGuard ForServer(LanguageServerManager lsp)
        {
            LspEditGuard g = new LspEditGuard();
            if (lsp != null)
            {
                g.SentVersion = lsp.GetSentVersion;
                g.SentText = lsp.GetSentText;
            }
            return g;
        }

        /// <summary>Records the text a buffer had when the request was sent.</summary>
        public void Remember(string path, string text)
        {
            if (!string.IsNullOrEmpty(path)) textAtRequest[LspText.PathKey(path)] = text ?? "";
        }

        /// <summary>Null when the edit may be applied, else a user-facing reason why it is stale.</summary>
        public string Validate(LspWorkspaceEdit edit, Func<string, EditorTabBuffer> findOpenBuffer)
        {
            if (edit == null) return null;
            foreach (LspWorkspaceEdit.Step step in edit.Steps)
            {
                if (step.Resource != null || step.Edits == null || step.Edits.Count == 0 || string.IsNullOrEmpty(step.FilePath)) continue;
                EditorTabBuffer buf = findOpenBuffer != null ? findOpenBuffer(step.FilePath) : null;
                string name = Path.GetFileName(step.FilePath);
                if (buf == null)
                {
                    continue; // closed file: edited on disk with its current content
                }
                string key = LspText.PathKey(step.FilePath);
                string now = LspEditing.BufferText(buf);
                int sentVersion = SentVersion != null ? SafeVersion(step.FilePath) : -1;
                if (step.Version >= 0 && sentVersion >= 0 && step.Version != sentVersion)
                    return "The edit was computed for an older version of " + name + " (v" + step.Version + ", now v" + sentVersion + "). Nothing was changed; try again.";
                string before;
                if (textAtRequest.TryGetValue(key, out before))
                {
                    if (!string.Equals(before, now, StringComparison.Ordinal))
                        return name + " changed while the language server was computing the edit. Nothing was changed; try again.";
                }
                else if (SentText != null)
                {
                    string sent = null;
                    try { sent = SentText(step.FilePath); } catch { }
                    if (sent != null && !string.Equals(LspText.NormalizeEol(sent, "\n"), now, StringComparison.Ordinal))
                        return name + " has changes the language server hasn't seen yet. Nothing was changed; try again.";
                }
            }
            return null;
        }

        private int SafeVersion(string path)
        {
            try { return SentVersion(path); } catch { return -1; }
        }
    }
}
