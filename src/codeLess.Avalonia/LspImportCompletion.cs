using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code-style path / module completion in the suggest widget (with or without a language server): typing inside
    /// an import string (or after '/', '.', the opening quote) lists files and folders; Python "import" / "from" list
    /// modules and "from x import" lists names. Items are built on the thread pool and merged with the server's
    /// results as they arrive (same label = one entry); the server is never waited for.
    /// </summary>
    public sealed partial class LspEditorBinding
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

        /// <summary>
        /// Switching editors closes the suggest widget / hover / signature help opened for the previous document
        /// (accepting a stale item used to insert it into the newly active file).
        /// </summary>
        public void HidePopupsIfEditorChanged()
        {
            var buf = Buf;
            if (!ReferenceEquals(buf, popupOwner)) HideAll();
            popupOwner = buf;
        }

        private EditorTabBuffer popupOwner;

        /// <summary>True when the typed text was handled as path / module completion (or is inside such a context).</summary>
        private bool TryImportCompletionOnTyped(string text)
        {
            var buf = Buf;
            if (buf == null) return false;
            buf.ClampCursor();
            var ctx = ImportCompletionEngine.DetectContext(buf, buf.CursorLine, buf.CursorCol);
            if (ctx == null)
            {
                if (session?.ImportContext != null) CloseCompletion();
                return false;
            }
            char last = text[text.Length - 1];
            if (session != null && Completion.IsVisible && session.ImportContext != null && session.Line == ctx.Line &&
                session.StartCol == ctx.SegmentStart && text.Length == 1 && ImportCompletionEngine.IsSegmentChar(ctx, last))
                return true; // OnBufferModified already re-filtered the open list
            if (!ImportCompletionEngine.ShouldTrigger(ctx, text))
            {
                if (session?.ImportContext != null) CloseCompletion();
                return true;
            }
            StartImportCompletion(ctx, last.ToString());
            return true;
        }

        /// <summary>Ctrl+Space / trigger-suggest commands inside an import context.</summary>
        private bool TryStartImportCompletionAtCaret(string triggerCharacter)
        {
            var buf = Buf;
            if (buf == null) return false;
            buf.ClampCursor();
            var ctx = ImportCompletionEngine.DetectContext(buf, buf.CursorLine, buf.CursorCol);
            if (ctx == null) return false;
            StartImportCompletion(ctx, triggerCharacter);
            return true;
        }

        private void StartImportCompletion(ImportCompletionContext ctx, string typed)
        {
            var buf = Buf;
            if (buf == null) return;
            completionCts?.Cancel();
            ++completionGeneration; // drop late answers of an earlier plain completion request
            var req = new ImportRequest { Generation = ++importGeneration, Context = ctx, Buffer = buf };
            string filePath = buf.FilePath;
            string root = window.WorkspaceRoot;
            var lsp = Lsp;
            string path = PathOrNull;
            req.ServerExpected = lsp != null && path != null && lsp.IsActive(path);
            _ = BuildOwnAsync(req, filePath, root);
            if (req.ServerExpected) _ = RequestServerAsync(req, lsp, path, typed);
        }

        private async Task BuildOwnAsync(ImportRequest req, string filePath, string root)
        {
            List<LspCompletionItem> items;
            try { items = await Task.Run(() => ImportCompletionEngine.BuildItems(req.Context, filePath, root)); }
            catch { items = new List<LspCompletionItem>(); }
            if (req.Generation != importGeneration) return;
            req.Own = items;
            req.OwnDone = true;
            ShowImportSession(req);
        }

        private async Task RequestServerAsync(ImportRequest req, LanguageServerManager lsp, string path, string typed)
        {
            var cts = new CancellationTokenSource();
            completionCts = cts;
            var ctx = req.Context;
            string trig = null;
            try
            {
                var triggers = lsp.GetCompletionTriggerCharacters(path);
                if (!string.IsNullOrEmpty(typed) && triggers != null && triggers.Contains(typed)) trig = typed;
            }
            catch { }
            LspCompletionList list = null;
            try { list = await lsp.RequestCompletionAsync(path, ctx.Line, ctx.Caret, trig, false, cts.Token); }
            catch { }
            if (cts.IsCancellationRequested || req.Generation != importGeneration) return;
            req.Server = list?.Items;
            req.ServerDone = true;
            ShowImportSession(req);
        }

        private void ShowImportSession(ImportRequest req)
        {
            var buf = Buf;
            if (!ReferenceEquals(buf, req.Buffer) || buf == null) return;
            buf.ClampCursor();
            var ctx = req.Context;
            if (buf.CursorLine != ctx.Line || buf.CursorCol < ctx.SegmentStart) { CloseCompletion(); return; }
            var items = ImportCompletionEngine.Merge(req.Own, req.Server);
            bool allDone = req.OwnDone && (!req.ServerExpected || req.ServerDone);
            if (items.Count == 0)
            {
                if (allDone && session?.ImportContext != null) CloseCompletion();
                return;
            }
            string previous = session?.ImportContext != null ? session.Selected?.Label : null;
            var s = new LspCompletionSession
            {
                FilePath = buf.FilePath,
                Line = ctx.Line,
                StartCol = ctx.SegmentStart,
                ImportContext = ctx,
                SegmentCharPredicate = c => ImportCompletionEngine.IsSegmentChar(ctx, c)
            };
            s.SetItems(new LspCompletionList { Items = items });
            if (!s.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol))
            {
                if (allDone) CloseCompletion();
                return;
            }
            if (previous != null)
            {
                int keep = s.Visible.FindIndex(i => i.Label == previous);
                if (keep >= 0) s.SelectedIndex = keep; // server items arriving later must not move the selection
            }
            session = s;
            PositionCompletion();
            Completion.Show(s);
            ScheduleResolve();
        }

        /// <summary>Path / module items (and server items without an edit) replace the whole segment around the caret.</summary>
        private static void PrepareImportAccept(LspCompletionSession s, LspCompletionItem item, EditorTabBuffer buf)
        {
            if (s?.ImportContext == null || item == null || buf == null) return;
            if (!(item.Tag is ImportCompletionTag) && item.TextEdit != null) return;
            buf.ClampCursor();
            if (buf.CursorLine != s.Line) return;
            ImportCompletionEngine.PrepareAccept(s.ImportContext, item, buf.Lines[s.Line], s.Line, s.StartCol, buf.CursorCol);
        }

        private bool RetriggerAfterImportAccept(LspCompletionItem item)
        {
            if (!(item?.Tag is ImportCompletionTag tag) || !tag.Retrigger) return false;
            TryStartImportCompletionAtCaret("/");
            return true;
        }
    }

    /// <summary>Suggest-widget glyphs for path completion: the explorer's file-type badge for files, its folder glyph for folders.</summary>
    internal static class CompletionKindIcons
    {
        public static Avalonia.Controls.Control ForItem(LspCompletionItem it, StudioTheme theme)
        {
            if (it == null || !(it.Tag is ImportCompletionTag tag)) return null;
            if (it.Kind == 19 || tag.IsFolder) return new FolderGlyph();
            if (it.Kind != 17) return null;
            AvaloniaExplorerTree.GetFileTypeBadge(it.Label ?? "", out string badge, out Avalonia.Media.Color color);
            return new Avalonia.Controls.TextBlock
            {
                Text = badge,
                FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 8.5,
                Foreground = new Avalonia.Media.SolidColorBrush(color),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
        }

        private sealed class FolderGlyph : Avalonia.Controls.Control
        {
            public FolderGlyph()
            {
                Width = 14; Height = 12;
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                IsHitTestVisible = false;
            }

            public override void Render(Avalonia.Media.DrawingContext context)
            {
                var pen = new Avalonia.Media.Pen(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(220, 182, 122)), 1.3);
                context.DrawRectangle(null, pen, new Avalonia.Rect(1, 2, 12, 9), 1.2, 1.2);
                context.DrawLine(pen, new Avalonia.Point(2, 1), new Avalonia.Point(6, 1));
            }
        }
    }
}
