using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>Where a menu entry is shown.</summary>
    public enum MenuPlatform { All, MacOnly, NonMac }

    /// <summary>
    /// One entry of the workbench menu bar: a command (Id), a separator (Id == null, Label == null) or a submenu
    /// (Children). <see cref="Shortcut"/> uses the Windows / Linux notation ("Ctrl+Shift+F", chords "Ctrl+K Ctrl+O");
    /// <see cref="MacShortcut"/> overrides it on macOS ("Cmd+Alt+F"); when it is null, Ctrl becomes Cmd.
    /// <see cref="DisplayOnly"/> shortcuts are shown but not registered as menu key equivalents (Tab, chords).
    /// </summary>
    public sealed class WorkbenchMenuEntry
    {
        public string Id;
        public string Label;
        public string Shortcut;
        public string MacShortcut;
        public bool Checkable;
        public bool DisplayOnly;
        public MenuPlatform Platform = MenuPlatform.All;
        public List<WorkbenchMenuEntry> Children;

        public bool IsSeparator { get { return Id == null && Label == null && Children == null; } }
        public bool IsSubmenu { get { return Children != null; } }

        /// <summary>Shortcut for the platform (null = none).</summary>
        public string ShortcutFor(bool mac)
        {
            if (!mac) return Shortcut;
            if (MacShortcut != null) return MacShortcut.Length == 0 ? null : MacShortcut;
            if (string.IsNullOrEmpty(Shortcut)) return null;
            return Shortcut.Replace("Ctrl+", "Cmd+");
        }

        public bool VisibleOn(bool mac)
        {
            return Platform == MenuPlatform.All || (Platform == MenuPlatform.MacOnly) == mac;
        }
    }

    /// <summary>A top-level menu (File, Edit, ...).</summary>
    public sealed class WorkbenchMenu
    {
        public string Title;
        public MenuPlatform Platform = MenuPlatform.All;
        public List<WorkbenchMenuEntry> Items = new List<WorkbenchMenuEntry>();

        public bool VisibleOn(bool mac)
        {
            return Platform == MenuPlatform.All || (Platform == MenuPlatform.MacOnly) == mac;
        }
    }

    /// <summary>
    /// The workbench menu bar in VS Code's structure (macOS app menu, File, Edit, Selection, View, Go, Run, Terminal,
    /// Window, Help), shared by the macOS native menu, the in-window menu (Windows / Linux Avalonia) and the WinForms
    /// menu strip. Each UI maps the command ids to its own actions and checked-state providers; a headless test walks
    /// this model and fails on items without a working command or with a shortcut that is not the real key binding.
    /// STRICT C# 5.
    /// </summary>
    public static class WorkbenchMenuModel
    {
        private static WorkbenchMenuEntry C(string id, string label, string shortcut)
        {
            WorkbenchMenuEntry e = new WorkbenchMenuEntry();
            e.Id = id; e.Label = label; e.Shortcut = shortcut;
            return e;
        }

        private static WorkbenchMenuEntry C(string id, string label, string shortcut, string mac)
        {
            WorkbenchMenuEntry e = C(id, label, shortcut);
            e.MacShortcut = mac;
            return e;
        }

        private static WorkbenchMenuEntry Check(WorkbenchMenuEntry e) { e.Checkable = true; return e; }
        private static WorkbenchMenuEntry Display(WorkbenchMenuEntry e) { e.DisplayOnly = true; return e; }
        private static WorkbenchMenuEntry Only(WorkbenchMenuEntry e, MenuPlatform p) { e.Platform = p; return e; }
        private static WorkbenchMenuEntry Sep() { return new WorkbenchMenuEntry(); }

        private static WorkbenchMenuEntry Sub(string label, params WorkbenchMenuEntry[] children)
        {
            WorkbenchMenuEntry e = new WorkbenchMenuEntry();
            e.Label = label;
            e.Children = new List<WorkbenchMenuEntry>(children);
            return e;
        }

        private static WorkbenchMenu M(string title, MenuPlatform p, params WorkbenchMenuEntry[] items)
        {
            WorkbenchMenu m = new WorkbenchMenu();
            m.Title = title;
            m.Platform = p;
            m.Items.AddRange(items);
            return m;
        }

        /// <summary>Id of the dynamic "Open Recent" submenu (filled by the UI with recent folders).</summary>
        public const string OpenRecentId = "file.openRecent";

        public static List<WorkbenchMenu> Build()
        {
            List<WorkbenchMenu> r = new List<WorkbenchMenu>();

            r.Add(M("codeLess", MenuPlatform.MacOnly,
                C("app.about", "About codeLess", null),
                C("app.checkUpdates", "Check for Updates...", null),
                Sep(),
                Sub("Settings",
                    C("pref.settings", "Settings", "Ctrl+,"),
                    Display(C("pref.keyboardShortcuts", "Keyboard Shortcuts", "Ctrl+K Ctrl+S")),
                    Display(C("pref.colorTheme", "Color Theme", "Ctrl+K Ctrl+T")),
                    C("view.extensions", "Extensions", "Ctrl+Shift+X")),
                Sep(),
                C("app.hide", "Hide codeLess", null, "Cmd+H"),
                C("app.hideOthers", "Hide Others", null, "Cmd+Alt+H"),
                C("app.showAll", "Show All", null),
                Sep(),
                C("app.quit", "Quit codeLess", null, "Cmd+Q")));

            r.Add(M("File", MenuPlatform.All,
                C("file.newTextFile", "New Text File", "Ctrl+N"),
                C("file.newFile", "New File...", "Ctrl+Alt+N"),
                C("file.newFolder", "New Folder...", null),
                C("file.newWindow", "New Window", "Ctrl+Shift+N"),
                Sep(),
                C("file.open", "Open File...", "Ctrl+O"),
                Display(C("file.openFolder", "Open Folder...", "Ctrl+K Ctrl+O")),
                Sub("Open Recent", C(OpenRecentId, "Recent folders", null)),
                C("file.addFolder", "Add Folder to Workspace...", null),
                Sep(),
                C("file.save", "Save", "Ctrl+S"),
                C("file.saveAs", "Save As...", "Ctrl+Shift+S"),
                Display(C("file.saveAll", "Save All", "Ctrl+K S", "Cmd+Alt+S")),
                Sep(),
                Check(C("file.autoSave", "Auto Save", null)),
                Only(Sub("Preferences",
                    C("pref.settings", "Settings", "Ctrl+,"),
                    Display(C("pref.keyboardShortcuts", "Keyboard Shortcuts", "Ctrl+K Ctrl+S")),
                    Display(C("pref.colorTheme", "Color Theme", "Ctrl+K Ctrl+T")),
                    C("view.extensions", "Extensions", "Ctrl+Shift+X")), MenuPlatform.NonMac),
                Sep(),
                C("file.revert", "Revert File", null),
                C("file.closeEditor", "Close Editor", "Ctrl+W"),
                Display(C("file.closeFolder", "Close Folder", "Ctrl+K F")),
                C("file.closeWindow", "Close Window", "Ctrl+Shift+W"),
                Only(Sep(), MenuPlatform.NonMac),
                Only(Display(C("file.exit", "Exit", "Alt+F4")), MenuPlatform.NonMac)));

            r.Add(M("Edit", MenuPlatform.All,
                C("edit.undo", "Undo", "Ctrl+Z"),
                C("edit.redo", "Redo", "Ctrl+Y", "Cmd+Shift+Z"),
                Sep(),
                C("edit.cut", "Cut", "Ctrl+X"),
                C("edit.copy", "Copy", "Ctrl+C"),
                C("edit.paste", "Paste", "Ctrl+V"),
                Sep(),
                C("edit.find", "Find", "Ctrl+F"),
                C("edit.replace", "Replace", "Ctrl+H", "Cmd+Alt+F"),
                Sep(),
                C("edit.findInFiles", "Find in Files", "Ctrl+Shift+F"),
                C("edit.replaceInFiles", "Replace in Files", "Ctrl+Shift+H"),
                Sep(),
                C("edit.toggleLineComment", "Toggle Line Comment", "Ctrl+/"),
                C("edit.toggleBlockComment", "Toggle Block Comment", "Shift+Alt+A"),
                Display(C("edit.emmetExpand", "Emmet: Expand Abbreviation", "Tab"))));

            r.Add(M("Selection", MenuPlatform.All,
                C("sel.selectAll", "Select All", "Ctrl+A"),
                C("sel.expand", "Expand Selection", "Shift+Alt+Right", "Ctrl+Shift+Cmd+Right"),
                C("sel.shrink", "Shrink Selection", "Shift+Alt+Left", "Ctrl+Shift+Cmd+Left"),
                Sep(),
                C("sel.copyLineUp", "Copy Line Up", "Shift+Alt+Up"),
                C("sel.copyLineDown", "Copy Line Down", "Shift+Alt+Down"),
                C("sel.moveLineUp", "Move Line Up", "Alt+Up"),
                C("sel.moveLineDown", "Move Line Down", "Alt+Down"),
                C("sel.duplicate", "Duplicate Selection", null),
                Sep(),
                C("sel.addCursorAbove", "Add Cursor Above", "Ctrl+Alt+Up"),
                C("sel.addCursorBelow", "Add Cursor Below", "Ctrl+Alt+Down"),
                C("sel.addCursorsToLineEnds", "Add Cursors to Line Ends", "Shift+Alt+I"),
                C("sel.addNextOccurrence", "Add Next Occurrence", "Ctrl+D"),
                C("sel.selectAllOccurrences", "Select All Occurrences", "Ctrl+Shift+L"),
                Sep(),
                Check(C("sel.columnSelectionMode", "Column Selection Mode", null))));

            r.Add(M("View", MenuPlatform.All,
                C("view.commandPalette", "Command Palette...", "Ctrl+Shift+P"),
                Sep(),
                Sub("Appearance",
                    Check(C("view.fullScreen", "Full Screen", "F11", "Ctrl+Cmd+F")),
                    Display(C("view.zenMode", "Zen Mode", "Ctrl+K Z")),
                    Sep(),
                    Check(C("view.toggleSidebar", "Primary Side Bar", "Ctrl+B")),
                    Check(C("view.toggleChat", "Secondary Side Bar (AI Chat)", "Ctrl+Alt+I")),
                    Check(C("view.togglePanel", "Panel", "Ctrl+J")),
                    Sep(),
                    Check(C("view.minimap", "Minimap", null)),
                    Check(C("view.breadcrumbs", "Breadcrumbs", null)),
                    Check(C("view.stickyScroll", "Sticky Scroll", null)),
                    Sep(),
                    C("view.zoomIn", "Zoom In", "Ctrl+="),
                    C("view.zoomOut", "Zoom Out", "Ctrl+-"),
                    C("view.resetZoom", "Reset Zoom", "Ctrl+0")),
                Sub("Editor Layout",
                    C("view.splitEditor", "Split Editor Right", "Ctrl+\\")),
                Sep(),
                C("view.explorer", "Explorer", "Ctrl+Shift+E"),
                C("view.search", "Search", "Ctrl+Shift+F"),
                C("view.scm", "Source Control", "Ctrl+Shift+G", "Ctrl+Shift+G"), // macOS: literal Control+Shift+G like VS Code
                C("view.run", "Run and Debug", "Ctrl+Shift+D"),
                C("view.extensions", "Extensions", "Ctrl+Shift+X"),
                Sep(),
                C("view.problems", "Problems", "Ctrl+Shift+M"),
                C("view.output", "Output", "Ctrl+Shift+U"),
                C("view.debugConsole", "Debug Console", "Ctrl+Shift+Y"),
                C("view.terminal", "Terminal", "Ctrl+`", "Ctrl+`"),
                C("view.networkActivity", "Network Activity", null),
                Check(C("view.offlineMode", "Offline Mode", null)),
                Sep(),
                Check(C("view.wordWrap", "Word Wrap", "Alt+Z"))));

            r.Add(M("Go", MenuPlatform.All,
                C("go.back", "Back", "Alt+Left", "Ctrl+-"),
                C("go.forward", "Forward", "Alt+Right", "Ctrl+Shift+-"),
                Sep(),
                Sub("Switch Editor",
                    C("go.nextEditor", "Next Editor", "Ctrl+PageDown", "Cmd+Alt+Right"),
                    C("go.previousEditor", "Previous Editor", "Ctrl+PageUp", "Cmd+Alt+Left"),
                    Sep(),
                    Display(C("go.nextUsedEditor", "Next Used Editor", "Ctrl+Tab", "Ctrl+Tab")),
                    Display(C("go.previousUsedEditor", "Previous Used Editor", "Ctrl+Shift+Tab", "Ctrl+Shift+Tab"))),
                Sep(),
                C("go.file", "Go to File...", "Ctrl+P"),
                C("go.symbolInEditor", "Go to Symbol in Editor...", "Ctrl+Shift+O"),
                C("go.symbolInWorkspace", "Go to Symbol in Workspace...", "Ctrl+T"),
                Sep(),
                C("go.definition", "Go to Definition", "F12"),
                C("go.references", "Go to References", "Shift+F12"),
                Sep(),
                C("go.line", "Go to Line/Column...", "Ctrl+G", "Ctrl+G"), // macOS: literal Control+G like VS Code (Cmd+G = Find Next)
                C("go.bracket", "Go to Bracket", "Ctrl+Shift+\\"),
                Sep(),
                C("go.nextProblem", "Next Problem", "F8"),
                C("go.previousProblem", "Previous Problem", "Shift+F8")));

            r.Add(M("Run", MenuPlatform.All,
                C("run.start", "Start Debugging", "F5"),
                C("run.withoutDebugging", "Run Without Debugging", "Ctrl+F5", "Ctrl+F5"),
                C("run.stop", "Stop Debugging", "Shift+F5"),
                C("run.restart", "Restart Debugging", "Ctrl+Shift+F5"),
                Sep(),
                C("run.addConfiguration", "Add Configuration...", null),
                Sep(),
                C("run.stepOver", "Step Over", "F10"),
                C("run.stepInto", "Step Into", "F11"),
                C("run.stepOut", "Step Out", "Shift+F11"),
                Display(C("run.continue", "Continue", "F5")),
                Sep(),
                C("run.toggleBreakpoint", "Toggle Breakpoint", "F9"),
                Sub("New Breakpoint",
                    C("run.conditionalBreakpoint", "Conditional Breakpoint...", null),
                    C("run.logpoint", "Logpoint...", null)),
                Sep(),
                C("run.enableAllBreakpoints", "Enable All Breakpoints", null),
                C("run.disableAllBreakpoints", "Disable All Breakpoints", null),
                C("run.removeAllBreakpoints", "Remove All Breakpoints", null)));

            r.Add(M("Terminal", MenuPlatform.All,
                C("term.new", "New Terminal", "Ctrl+Shift+`", "Ctrl+Shift+`"),
                C("term.split", "Split Terminal", null),
                Sep(),
                C("term.runTask", "Run Task...", null),
                C("term.runBuildTask", "Run Build Task...", "Ctrl+Shift+B"),
                C("term.runActiveFile", "Run Active File", null),
                Sep(),
                C("term.configureTasks", "Configure Tasks...", null),
                Sep(),
                C("term.clear", "Clear Terminal", null),
                C("term.kill", "Kill Terminal", null)));

            r.Add(M("Window", MenuPlatform.MacOnly,
                C("win.minimize", "Minimize", null, "Cmd+M"),
                C("win.zoom", "Zoom", null),
                Sep(),
                C("win.bringAllToFront", "Bring All to Front", null)));

            r.Add(M("Help", MenuPlatform.All,
                C("help.welcome", "Welcome", null),
                C("help.showAllCommands", "Show All Commands", "Ctrl+Shift+P"),
                C("help.documentation", "Documentation", null),
                C("help.releaseNotes", "Release Notes", null),
                Sep(),
                C("help.keyboardShortcuts", "Keyboard Shortcuts Reference", null),
                C("help.byok", "AI Provider & Harness Setup...", null),
                Sep(),
                C("help.reportIssue", "Report Issue", null),
                // users look for updates in Help on macOS too (it also stays in the codeLess app menu, like VS Code)
                Sep(),
                C("app.checkUpdates", "Check for Updates...", null),
                Only(C("app.about", "About", null), MenuPlatform.NonMac)));
            return r;
        }

        /// <summary>Every command entry (submenus flattened) visible on the platform.</summary>
        public static List<WorkbenchMenuEntry> AllCommands(bool mac)
        {
            List<WorkbenchMenuEntry> res = new List<WorkbenchMenuEntry>();
            foreach (WorkbenchMenu m in Build())
                if (m.VisibleOn(mac)) Collect(m.Items, mac, res);
            return res;
        }

        private static void Collect(List<WorkbenchMenuEntry> items, bool mac, List<WorkbenchMenuEntry> into)
        {
            foreach (WorkbenchMenuEntry e in items)
            {
                if (!e.VisibleOn(mac)) continue;
                if (e.IsSubmenu) Collect(e.Children, mac, into);
                else if (!e.IsSeparator) into.Add(e);
            }
        }

        /// <summary>
        /// Display text of a shortcut: macOS symbols ("⌥⌘F", "⌃`", chords "⌘K ⌘O") or the Windows text unchanged.
        /// </summary>
        public static string FormatShortcut(string shortcut, bool mac)
        {
            if (string.IsNullOrEmpty(shortcut)) return "";
            if (!mac) return shortcut;
            string[] chords = shortcut.Split(' ');
            List<string> outp = new List<string>();
            foreach (string chord in chords)
            {
                if (chord.Length == 0) continue;
                List<string> parts = SplitKeys(chord);
                string mods = "";
                string key = parts.Count > 0 ? parts[parts.Count - 1] : "";
                bool ctrl = false, alt = false, shift = false, cmd = false;
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    string p = parts[i];
                    if (p == "Ctrl") ctrl = true;
                    else if (p == "Alt" || p == "Option") alt = true;
                    else if (p == "Shift") shift = true;
                    else if (p == "Cmd" || p == "Meta") cmd = true;
                }
                if (ctrl) mods += "⌃";
                if (alt) mods += "⌥";
                if (shift) mods += "⇧";
                if (cmd) mods += "⌘";
                outp.Add(mods + MacKeyName(key));
            }
            return string.Join(" ", outp.ToArray());
        }

        /// <summary>Splits "Ctrl+Shift+=" / "Ctrl+-" / "Ctrl++" into modifier and key tokens.</summary>
        public static List<string> SplitKeys(string chord)
        {
            List<string> parts = new List<string>();
            int start = 0;
            for (int i = 0; i < chord.Length; i++)
            {
                if (chord[i] == '+' && i > start)
                {
                    parts.Add(chord.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < chord.Length) parts.Add(chord.Substring(start));
            return parts;
        }

        private static string MacKeyName(string key)
        {
            switch (key)
            {
                case "Left": return "←";
                case "Right": return "→";
                case "Up": return "↑";
                case "Down": return "↓";
                case "Enter": return "↩";
                case "Tab": return "⇥";
                case "Escape": return "⎋";
                case "PageUp": return "⇞";
                case "PageDown": return "⇟";
                case "Delete": return "⌦";
                case "Backspace": return "⌫";
            }
            return key.Length == 1 ? key.ToUpperInvariant() : key;
        }
    }
}
