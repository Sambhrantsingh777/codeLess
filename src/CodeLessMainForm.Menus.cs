using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code menu parity for the WinForms menu strip (same structure as the shared WorkbenchMenuModel used by the
    /// Avalonia menus): items the strip was missing (Switch Editor, New Breakpoint, Enable / Disable / Remove All
    /// Breakpoints, Configure Tasks, Emmet, Sticky Scroll, Release Notes, Report Issue) and the per-editor
    /// View: Toggle Word Wrap command shared by Alt+Z, the View menu, the tab action button and the palette. STRICT C# 5.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private ToolStripMenuItem stickyScrollMenuItem;

        /// <summary>
        /// View: Toggle Word Wrap for the focused editor (VS Code: per editor; the Settings "Word wrap" option is the
        /// default for editors and applies to all open editors when it changes).
        /// </summary>
        private void ToggleWordWrapCommand()
        {
            CodeLessEditorControl ctl = (splitEditorControl != null && isSplitEditorActive && splitEditorControl.ContainsFocus) ? splitEditorControl : editorControl;
            if (ctl == null) return;
            ctl.ToggleWordWrap();
            ctl.Invalidate(true);
            if (splitEditorControl != null && !ReferenceEquals(ctl, splitEditorControl)) splitEditorControl.Invalidate(true);
            if (wordWrapMenuItem != null && ctl.CurrentBuffer != null) wordWrapMenuItem.Checked = ctl.CurrentBuffer.WordWrap;
            UpdateTelemetryBadges();
        }

        /// <summary>Alt+C / W / R / L / P toggle the find options while an editor (or its find widget) has focus.</summary>
        private bool TryHandleEditorFindOptionKey(KeyEventArgs e)
        {
            if (e == null || !e.Alt || e.Control || e.Shift) return false;
            if (splitEditorControl != null && splitEditorControl.ContainsFocus) return splitEditorControl.TryHandleFindOptionKey(e);
            if (editorControl != null && editorControl.ContainsFocus) return editorControl.TryHandleFindOptionKey(e);
            return false;
        }

        /// <summary>Adds the VS Code menu items the strip was missing (called once from BuildTopMenuBar).</summary>
        private void AddMenuParityItems(ToolStripMenuItem editMenu, ToolStripMenuItem viewMenu, ToolStripMenuItem goMenu,
            ToolStripMenuItem runMenu, ToolStripMenuItem terminalMenu, ToolStripMenuItem helpMenu)
        {
            // Edit
            editMenu.DropDownItems.Add(CreateMenuItem("Emmet: Expand Abbreviation", "Tab", delegate
            {
                if (editorControl != null && editorControl.Canvas != null && !editorControl.Canvas.ExpandEmmetAbbreviation(activeTab))
                    DevStatus("Emmet: no abbreviation at the cursor");
            }));

            // View → Appearance: Sticky Scroll (checkable)
            foreach (ToolStripItem it in viewMenu.DropDownItems)
            {
                ToolStripMenuItem appearance = it as ToolStripMenuItem;
                if (appearance == null || appearance.Text != "Appearance") continue;
                stickyScrollMenuItem = CreateMenuItem("Sticky Scroll", "", delegate { ExecuteEditorNicetyCommand("nicety_sticky"); });
                int at = appearance.DropDownItems.IndexOf(showBreadcrumbsItem);
                appearance.DropDownItems.Insert(at >= 0 ? at + 1 : appearance.DropDownItems.Count, stickyScrollMenuItem);
                appearance.DropDownOpening += delegate { if (stickyScrollMenuItem != null) stickyScrollMenuItem.Checked = EditorNicetyOptions.StickyScroll; };
                break;
            }

            // Go → Switch Editor
            ToolStripMenuItem switchEditor = new ToolStripMenuItem("Switch Editor");
            switchEditor.DropDownItems.Add(CreateMenuItem("Next Editor", "Ctrl+PageDown", delegate { SwitchTabByOffset(1); }));
            switchEditor.DropDownItems.Add(CreateMenuItem("Previous Editor", "Ctrl+PageUp", delegate { SwitchTabByOffset(-1); }));
            switchEditor.DropDownItems.Add(new ToolStripSeparator());
            switchEditor.DropDownItems.Add(CreateMenuItem("Next Used Editor", "Ctrl+Tab", delegate { List<EditorTabBuffer> mru = EditorMruOrder; if (mru.Count > 1) ActivateTabWin(mru[1]); }));
            switchEditor.DropDownItems.Add(CreateMenuItem("Previous Used Editor", "Ctrl+Shift+Tab", delegate { List<EditorTabBuffer> mru = EditorMruOrder; if (mru.Count > 1) ActivateTabWin(mru[mru.Count - 1]); }));
            goMenu.DropDownItems.Insert(Math.Min(3, goMenu.DropDownItems.Count), switchEditor);
            goMenu.DropDownItems.Insert(Math.Min(4, goMenu.DropDownItems.Count), new ToolStripSeparator());

            // Run → New Breakpoint, Enable / Disable / Remove All Breakpoints
            ToolStripMenuItem newBp = new ToolStripMenuItem("New Breakpoint");
            newBp.DropDownItems.Add(CreateMenuItem("Conditional Breakpoint...", "", delegate { PromptBreakpointAtCursor(); }));
            newBp.DropDownItems.Add(CreateMenuItem("Logpoint...", "", delegate { PromptBreakpointAtCursor(); }));
            int bpAt = -1;
            for (int i = 0; i < runMenu.DropDownItems.Count; i++)
                if (runMenu.DropDownItems[i].Text == "Toggle Breakpoint") { bpAt = i; break; }
            int ins = bpAt >= 0 ? bpAt + 1 : runMenu.DropDownItems.Count;
            runMenu.DropDownItems.Insert(ins++, newBp);
            runMenu.DropDownItems.Insert(ins++, new ToolStripSeparator());
            runMenu.DropDownItems.Insert(ins++, CreateMenuItem("Enable All Breakpoints", "", delegate { if (debugController != null) debugController.Breakpoints.SetAllEnabled(true); }));
            runMenu.DropDownItems.Insert(ins++, CreateMenuItem("Disable All Breakpoints", "", delegate { if (debugController != null) debugController.Breakpoints.SetAllEnabled(false); }));
            runMenu.DropDownItems.Insert(ins++, CreateMenuItem("Remove All Breakpoints", "", delegate { if (debugController != null) debugController.Breakpoints.RemoveAll(); }));

            // Terminal → Configure Tasks...
            int buildAt = -1;
            for (int i = 0; i < terminalMenu.DropDownItems.Count; i++)
                if (terminalMenu.DropDownItems[i].Text == "Run Selected Text") { buildAt = i; break; }
            int tIns = buildAt >= 0 ? buildAt + 1 : terminalMenu.DropDownItems.Count;
            terminalMenu.DropDownItems.Insert(tIns, new ToolStripSeparator());
            terminalMenu.DropDownItems.Insert(tIns + 1, CreateMenuItem("Configure Tasks...", "", delegate { ExecuteParityCommand("par_configure_tasks"); }));

            // Help → Release Notes, Report Issue
            int docAt = -1;
            for (int i = 0; i < helpMenu.DropDownItems.Count; i++)
                if (helpMenu.DropDownItems[i].Text == "Documentation") { docAt = i; break; }
            int hIns = docAt >= 0 ? docAt + 1 : helpMenu.DropDownItems.Count;
            helpMenu.DropDownItems.Insert(hIns, CreateMenuItem("Release Notes", "", delegate { OpenProjectUrl("/releases"); }));
            helpMenu.DropDownItems.Insert(hIns + 1, CreateMenuItem("Report Issue", "", delegate { OpenProjectUrl("/issues/new"); }));
        }

        private void PromptBreakpointAtCursor()
        {
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine);
            else DevStatus("Open a saved file to add a breakpoint.");
        }

        private static void OpenProjectUrl(string suffix)
        {
            try { Process.Start("https://github.com/" + UpdateChecker.Repository + suffix); } catch { }
        }
    }
}
