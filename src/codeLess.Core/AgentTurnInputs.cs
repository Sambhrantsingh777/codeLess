using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Inputs of an agent turn captured on the UI thread, so the turn (LLM calls, tool execution, shell commands) can
    /// run on a worker thread without reading editor state the UI keeps changing (QA #25). Strict C# 5.
    /// </summary>
    public static class AgentTurnInputs
    {
        /// <summary>
        /// A detached copy of the active editor buffer: text (from the live lines), path, language, caret, selection,
        /// diagnostics and symbols. Call on the UI thread.
        /// </summary>
        public static EditorTabBuffer SnapshotTab(EditorTabBuffer t)
        {
            if (t == null) return null;
            EditorTabBuffer c = new EditorTabBuffer();
            c.FilePath = t.FilePath;
            c.RelativePath = t.RelativePath;
            c.FileName = t.FileName;
            c.Language = t.Language;
            c.IsUntitled = t.IsUntitled;
            c.EolSequence = t.EolSequence;
            c.HasFinalNewline = t.HasFinalNewline;
            c.SavedContent = t.SavedContent;
            c.Content = t.Lines != null && t.Lines.Count > 0 ? string.Join("\n", t.Lines.ToArray()) : (t.Content ?? "");
            c.SyncLinesFromContent();
            c.CursorLine = t.CursorLine;
            c.CursorCol = t.CursorCol;
            c.SelectionAnchorLine = t.SelectionAnchorLine;
            c.SelectionAnchorCol = t.SelectionAnchorCol;
            c.ClampCursor();
            c.IsDirty = t.IsDirty;
            if (t.Diagnostics != null) c.Diagnostics = new List<DiagnosticItem>(t.Diagnostics);
            if (t.Symbols != null) c.Symbols = new List<AstSymbolItem>(t.Symbols);
            return c;
        }

        public static List<string> SnapshotList(IEnumerable<string> items)
        {
            return items != null ? new List<string>(items) : new List<string>();
        }
    }
}
