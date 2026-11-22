using System;
using System.Collections.Generic;
using System.Linq;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Indentation and wrap settings of the active editor (QA #11 / #12), like VS Code's status bar "Spaces: 4" /
    /// "Tab Size: 4" item: Indent Using Spaces / Tabs, Change Tab Display Size, Detect Indentation from Content,
    /// Convert Indentation to Spaces / Tabs. Per-file values come from .editorconfig or detection on open.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Status bar text for the active buffer ("Spaces: 2" / "Tab Size: 4").</summary>
        public string StatusIndentationText
        {
            get
            {
                int tab = EditorIndentation.EffectiveTabSize(activeTab, settings.TabSize);
                bool spaces = EditorIndentation.EffectiveInsertSpaces(activeTab, settings.InsertSpaces);
                return EditorIndentation.StatusText(tab, spaces);
            }
        }

        /// <summary>Entries of the indentation quick pick (tests / automation).</summary>
        public List<PaletteEntryItem> LastIndentationPickerEntries { get; private set; } = new List<PaletteEntryItem>();

        public void OpenIndentationPicker()
        {
            var tab = activeTab;
            if (tab == null) return;
            var entries = new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Indentation", Title = "Indent Using Spaces", Execute = () => OpenTabSizePicker(true) },
                new PaletteEntryItem { Category = "Indentation", Title = "Indent Using Tabs", Execute = () => OpenTabSizePicker(false) },
                new PaletteEntryItem { Category = "Indentation", Title = "Change Tab Display Size", Execute = () => OpenTabSizePicker(EditorIndentation.EffectiveInsertSpaces(activeTab, settings.InsertSpaces)) },
                new PaletteEntryItem { Category = "Indentation", Title = "Detect Indentation from Content", Execute = DetectIndentationForActiveTab },
                new PaletteEntryItem { Category = "Indentation", Title = "Convert Indentation to Spaces", Execute = () => ConvertActiveIndentation(true) },
                new PaletteEntryItem { Category = "Indentation", Title = "Convert Indentation to Tabs", Execute = () => ConvertActiveIndentation(false) },
            };
            LastIndentationPickerEntries = entries;
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        private void OpenTabSizePicker(bool spaces)
        {
            var tab = activeTab;
            if (tab == null) return;
            int cur = EditorIndentation.EffectiveTabSize(tab, settings.TabSize);
            var entries = new List<PaletteEntryItem>();
            for (int n = 1; n <= 8; n++)
            {
                int size = n;
                entries.Add(new PaletteEntryItem
                {
                    Category = spaces ? "Indent Using Spaces" : "Indent Using Tabs",
                    Title = size.ToString(System.Globalization.CultureInfo.InvariantCulture) + (size == cur ? "   (current)" : ""),
                    Execute = () => SetActiveIndentation(spaces, size)
                });
            }
            LastIndentationPickerEntries = entries;
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        /// <summary>Sets the active buffer's indentation (per file, like VS Code's status bar picker).</summary>
        public void SetActiveIndentation(bool insertSpaces, int tabSize)
        {
            var tab = activeTab;
            if (tab == null) return;
            tab.CustomInsertSpaces = insertSpaces;
            tab.CustomTabSize = Math.Max(1, Math.Min(16, tabSize));
            RepaintEditors();
            UpdateStatusBar();
        }

        public void DetectIndentationForActiveTab()
        {
            var tab = activeTab;
            if (tab == null) return;
            EditorIndentation.Guess(tab.Lines, Math.Max(1, settings.TabSize), settings.InsertSpaces, out int t, out bool s, out int evidence);
            if (evidence > 0)
            {
                tab.CustomInsertSpaces = s;
                tab.CustomTabSize = s ? t : EditorIndentation.EffectiveTabSize(tab, settings.TabSize);
                SetStatusMessage("Detected indentation: " + EditorIndentation.StatusText(tab.CustomTabSize.Value, s));
            }
            else SetStatusMessage("No indentation found in " + tab.FileName);
            RepaintEditors();
            UpdateStatusBar();
        }

        /// <summary>Converts the leading whitespace of every line to spaces or tabs (one undo step).</summary>
        public void ConvertActiveIndentation(bool toSpaces)
        {
            var tab = activeTab;
            if (tab == null || tab.IsLargeFileViewer) return;
            int size = EditorIndentation.EffectiveTabSize(tab, settings.TabSize);
            var changed = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < tab.Lines.Count; i++)
            {
                string ln = tab.Lines[i] ?? "";
                int ws = EditorIndentation.LeadingWhitespaceLength(ln);
                if (ws == 0) continue;
                int cols = EditorTextLayout.VisualColumn(ln, ws, size);
                string nl = EditorIndentation.Build(cols, size, toSpaces) + ln.Substring(ws);
                if (nl != ln) changed.Add(new KeyValuePair<int, string>(i, nl));
            }
            tab.CustomInsertSpaces = toSpaces;
            tab.CustomTabSize = size;
            if (changed.Count > 0)
            {
                tab.PushUndoSnapshot();
                foreach (var kv in changed) tab.Lines[kv.Key] = kv.Value;
                tab.ClampCursor();
                if (ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) PrimaryEditor.Canvas.CommitExternalEdit();
                else tab.SyncContentFromLines();
            }
            RepaintEditors();
            UpdateStatusBar();
        }

        /// <summary>Applies the editor.tabSize / insertSpaces / wordWrap settings to the canvases and open editors.</summary>
        internal void ApplyEditorIndentAndWrapSettings(StudioSettings s)
        {
            if (s == null) return;
            AvaloniaVirtualEditorCanvas.DisplayTabSize = Math.Max(1, Math.Min(16, s.TabSize <= 0 ? 4 : s.TabSize));
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = s.InsertSpaces;
            // editor.wordWrap is an editor setting: every open editor follows it (Alt+Z still toggles one editor)
            foreach (var t in openTabs)
            {
                if (t.WordWrap == s.WordWrap) continue;
                t.WordWrap = s.WordWrap;
                t.ScrollLeftPx = 0;
                t.ScrollTopWrapRow = 0;
            }
            RepaintEditors();
        }

        /// <summary>Screenshot suite: a soft-wrapped editor and folded Allman blocks.</summary>
        private async System.Threading.Tasks.Task RunWrapAndFoldScreensAsync(string outputDir, string csFile)
        {
            var tab = OpenFileInTab(csFile, preview: false);
            if (tab == null) return;
            var canvas = PrimaryEditor.Canvas;
            tab.WordWrap = true;
            tab.ScrollTopLine = 14;
            tab.ScrollTopWrapRow = 0;
            tab.CursorLine = 20;
            tab.CursorCol = Math.Min(tab.Lines[20].Length, 120);
            tab.ClearSelection();
            canvas.InvalidateVisual();
            await CaptureScreenshotAsync(outputDir, "r12_word_wrap.png");
            tab.WordWrap = false;
            tab.ScrollTopWrapRow = 0;
            int process = tab.Lines.FindIndex(l => l.Contains("Process("));
            int loop = tab.Lines.FindIndex(l => l.TrimStart().StartsWith("foreach"));
            tab.ScrollTopLine = 0;
            tab.CursorLine = Math.Max(0, process);
            tab.CursorCol = 8;
            if (loop > 0) canvas.FoldAtLine(loop);
            await CaptureScreenshotAsync(outputDir, "r12_folding_inner.png");
            if (process > 0) canvas.FoldAtLine(process);
            await CaptureScreenshotAsync(outputDir, "r12_folding.png");
            canvas.UnfoldAll();
        }

        private void RepaintEditors()
        {
            PrimaryEditor?.Canvas?.InvalidateVisual();
            SplitEditor?.Canvas?.InvalidateVisual();
        }
    }
}
