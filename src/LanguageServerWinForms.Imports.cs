using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// WinForms side of VS Code-style path / module completion (same engine and behaviour as Avalonia): typing inside
    /// an import string lists files and folders (folders re-open the list), Python import / from statements list
    /// modules and names. Works with and without a language server; server items are merged as they arrive.
    /// </summary>
    internal sealed partial class WinFormsLspEditorBinding
    {
        private int importGeneration;

        private sealed class ImportRequest
        {
            public int Generation;
            public ImportCompletionContext Context;
            public EditorTabBuffer Buffer;
            public List<LspCompletionItem> Own;
            public List<LspCompletionItem> Server;
            public bool OwnDone, ServerDone, ServerExpected;
        }

        private EditorTabBuffer popupOwner;

        /// <summary>Switching editors closes the suggest list / tips opened for the previous document.</summary>
        public void HidePopupsIfEditorChanged()
        {
            EditorTabBuffer buf = Buf;
            if (!ReferenceEquals(buf, popupOwner))
            {
                HideAll();
                Editor.DismissAutocomplete();
            }
            popupOwner = buf;
        }

        private bool IsImportContextAtCaret(EditorTabBuffer b)
        {
            if (b == null || b.Lines == null || b.IsLargeFileViewer) return false;
            b.ClampCursor();
            return ImportCompletionEngine.DetectContext(b, b.CursorLine, b.CursorCol) != null;
        }

        private bool TryImportCompletionOnTyped(char ch)
        {
            EditorTabBuffer buf = Buf;
            if (buf == null || buf.IsLargeFileViewer) return false;
            buf.ClampCursor();
            ImportCompletionContext ctx = ImportCompletionEngine.DetectContext(buf, buf.CursorLine, buf.CursorCol);
            if (ctx == null)
            {
                if (session != null && session.ImportContext != null) CloseCompletion();
                return false;
            }
            if (session != null && completionFrame.Visible && session.ImportContext != null && session.Line == ctx.Line &&
                session.StartCol == ctx.SegmentStart && ImportCompletionEngine.IsSegmentChar(ctx, ch))
                return true; // OnBufferModified already re-filtered
            if (!ImportCompletionEngine.ShouldTrigger(ctx, ch.ToString()))
            {
                if (session != null && session.ImportContext != null) CloseCompletion();
                return true;
            }
            StartImportCompletion(ctx, ch.ToString());
            return true;
        }

        public bool TryStartImportCompletionAtCaret(string triggerCharacter)
        {
            EditorTabBuffer buf = Buf;
            if (buf == null || buf.IsLargeFileViewer) return false;
            buf.ClampCursor();
            ImportCompletionContext ctx = ImportCompletionEngine.DetectContext(buf, buf.CursorLine, buf.CursorCol);
            if (ctx == null) return false;
            StartImportCompletion(ctx, triggerCharacter);
            return true;
        }

        private void StartImportCompletion(ImportCompletionContext ctx, string typed)
        {
            EditorTabBuffer buf = Buf;
            if (buf == null) return;
            if (completionCts != null) completionCts.Cancel();
            ++completionGeneration;
            ImportRequest req = new ImportRequest();
            req.Generation = ++importGeneration;
            req.Context = ctx;
            req.Buffer = buf;
            LanguageServerManager lsp = Lsp;
            string path = PathOrNull;
            req.ServerExpected = lsp != null && path != null && lsp.IsActive(path);
            BuildOwn(req, buf.FilePath, owner.GetWorkspaceRoot != null ? owner.GetWorkspaceRoot() : null);
            if (req.ServerExpected) RequestServer(req, lsp, path, typed);
        }

        private async void BuildOwn(ImportRequest req, string filePath, string root)
        {
            List<LspCompletionItem> items;
            try
            {
                ImportCompletionContext ctx = req.Context;
                items = await Task.Run(delegate { return ImportCompletionEngine.BuildItems(ctx, filePath, root); });
            }
            catch { items = new List<LspCompletionItem>(); }
            if (req.Generation != importGeneration) return;
            req.Own = items;
            req.OwnDone = true;
            ShowImportSession(req);
        }

        private async void RequestServer(ImportRequest req, LanguageServerManager lsp, string path, string typed)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            completionCts = cts;
            string trig = null;
            try
            {
                List<string> triggers = lsp.GetCompletionTriggerCharacters(path);
                if (!string.IsNullOrEmpty(typed) && triggers != null && triggers.Contains(typed)) trig = typed;
            }
            catch { }
            LspCompletionList list = null;
            try { list = await lsp.RequestCompletionAsync(path, req.Context.Line, req.Context.Caret, trig, false, cts.Token); }
            catch { }
            if (cts.IsCancellationRequested || req.Generation != importGeneration) return;
            req.Server = list != null ? list.Items : null;
            req.ServerDone = true;
            ShowImportSession(req);
        }

        private void ShowImportSession(ImportRequest req)
        {
            EditorTabBuffer buf = Buf;
            if (buf == null || !ReferenceEquals(buf, req.Buffer)) return;
            buf.ClampCursor();
            ImportCompletionContext ctx = req.Context;
            if (buf.CursorLine != ctx.Line || buf.CursorCol < ctx.SegmentStart) { CloseCompletion(); return; }
            List<LspCompletionItem> items = ImportCompletionEngine.Merge(req.Own, req.Server);
            bool allDone = req.OwnDone && (!req.ServerExpected || req.ServerDone);
            if (items.Count == 0)
            {
                if (allDone && session != null && session.ImportContext != null) CloseCompletion();
                return;
            }
            string previous = session != null && session.ImportContext != null && session.Selected != null ? session.Selected.Label : null;
            LspCompletionSession s = new LspCompletionSession();
            s.FilePath = buf.FilePath;
            s.Line = ctx.Line;
            s.StartCol = ctx.SegmentStart;
            s.ImportContext = ctx;
            s.SegmentCharPredicate = delegate (char c) { return ImportCompletionEngine.IsSegmentChar(ctx, c); };
            LspCompletionList list = new LspCompletionList();
            list.Items = items;
            s.SetItems(list);
            if (!s.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol))
            {
                if (allDone) CloseCompletion();
                return;
            }
            if (previous != null)
            {
                int keep = s.Visible.FindIndex(delegate (LspCompletionItem i) { return i.Label == previous; });
                if (keep >= 0) s.SelectedIndex = keep;
            }
            session = s;
            Editor.DismissAutocomplete(); // never two suggest lists at once
            FillList();
            PositionCompletion();
            completionFrame.Visible = true;
            completionFrame.BringToFront();
            ScheduleResolve();
        }

        private static void PrepareImportAccept(LspCompletionSession s, LspCompletionItem item, EditorTabBuffer buf)
        {
            if (s == null || s.ImportContext == null || item == null || buf == null) return;
            if (!(item.Tag is ImportCompletionTag) && item.TextEdit != null) return;
            buf.ClampCursor();
            if (buf.CursorLine != s.Line) return;
            ImportCompletionEngine.PrepareAccept(s.ImportContext, item, buf.Lines[s.Line], s.Line, s.StartCol, buf.CursorCol);
        }

        private bool RetriggerAfterImportAccept(LspCompletionItem item)
        {
            ImportCompletionTag tag = item != null ? item.Tag as ImportCompletionTag : null;
            if (tag == null || !tag.Retrigger) return false;
            TryStartImportCompletionAtCaret("/");
            return true;
        }

        /// <summary>Files: their type badge; folders: a folder glyph (path completion items only).</summary>
        private static bool DrawPathKindGlyph(Graphics g, LspCompletionItem it, Rectangle r, Color fg)
        {
            ImportCompletionTag tag = it != null ? it.Tag as ImportCompletionTag : null;
            if (tag == null) return false;
            if (it.Kind == 19 || tag.IsFolder)
            {
                Color folder = Color.FromArgb(220, 182, 122);
                int x = r.X + (r.Width - 12) / 2, y = r.Y + (r.Height - 10) / 2;
                using (Pen p = new Pen(folder, 1.3f))
                {
                    g.DrawRectangle(p, x, y + 1, 12, 9);
                    g.DrawLine(p, x + 1, y, x + 5, y);
                }
                return true;
            }
            if (it.Kind != 17) return false;
            SetiIconInfo icon = SetiIconTheme.Resolve(it.Label ?? "", false, null);
            using (Font f = SetiWinFormsFont.CreateFont(10.5f))
                TextRenderer.DrawText(g, icon.Glyph, f, r, icon.Color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return true;
        }
    }
}
