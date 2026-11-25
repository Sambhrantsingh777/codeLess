using System;
namespace codeLess.Core
{
    public static class DefaultCommands
    {
        public static void Register()
        {
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.showCommands", "Show Command Palette", "View", "cmd+shift+p", "ctrl+shift+p", "ctrl+shift+p", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.quickOpen", "Go to File...", "File", "cmd+p", "ctrl+p", "ctrl+p", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.newWindow", "New Window", "File", "cmd+shift+n", "ctrl+shift+n", "ctrl+shift+n", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeWindow", "Close Window", "File", "cmd+shift+w", "ctrl+shift+w", "ctrl+shift+w", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openSettings", "Preferences: Open Settings (UI)", "Preferences", "cmd+,", "ctrl+,", "ctrl+,", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openSettingsJson", "Preferences: Open User Settings (JSON)", "Preferences", "", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openGlobalKeybindings", "Preferences: Open Keyboard Shortcuts", "Preferences", "cmd+k cmd+s", "ctrl+k ctrl+s", "ctrl+k ctrl+s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.toggleTerminal", "Toggle Terminal", "View", "ctrl+`", "ctrl+`", "ctrl+`", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleSidebarVisibility", "Toggle Side Bar", "View", "cmd+b", "ctrl+b", "ctrl+b", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleZenMode", "Zen Mode", "View", "cmd+k z", "ctrl+k z", "ctrl+k z", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.copyLinesDownAction", "Copy Line Down", "Editor", "shift+alt+down", "shift+alt+down", "shift+alt+down", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.copyLinesUpAction", "Copy Line Up", "Editor", "shift+alt+up", "shift+alt+up", "shift+alt+up", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.moveLinesDownAction", "Move Line Down", "Editor", "alt+down", "alt+down", "alt+down", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.moveLinesUpAction", "Move Line Up", "Editor", "alt+up", "alt+up", "alt+up", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.deleteLines", "Delete Line", "Editor", "cmd+shift+k", "ctrl+shift+k", "ctrl+shift+k", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertLineAfter", "Insert Line Below", "Editor", "cmd+enter", "ctrl+enter", "ctrl+enter", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertLineBefore", "Insert Line Above", "Editor", "cmd+shift+enter", "ctrl+shift+enter", "ctrl+shift+enter", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.commentLine", "Toggle Line Comment", "Editor", "cmd+/", "ctrl+/", "ctrl+/", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.blockComment", "Toggle Block Comment", "Editor", "shift+alt+a", "shift+alt+a", "shift+alt+a", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.addSelectionToNextFindMatch", "Add Selection To Next Find Match", "Editor", "cmd+d", "ctrl+d", "ctrl+d", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.selectHighlights", "Select All Occurrences", "Editor", "cmd+shift+l", "ctrl+shift+l", "ctrl+shift+l", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.revealDefinition", "Go to Definition", "Editor", "f12", "f12", "f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.peekDefinition", "Peek Definition", "Editor", "alt+f12", "alt+f12", "alt+f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.gotoSymbol", "Go to Symbol in File", "View", "cmd+shift+o", "ctrl+shift+o", "ctrl+shift+o", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.showAllSymbols", "Go to Symbol in Workspace", "View", "cmd+t", "ctrl+t", "ctrl+t", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.gotoLine", "Go to Line", "View", "ctrl+g", "ctrl+g", "ctrl+g", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("actions.find", "Find", "Editor", "cmd+f", "ctrl+f", "ctrl+f", "editorTextFocus || terminalFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.startFindReplaceAction", "Replace", "Editor", "cmd+alt+f", "ctrl+h", "ctrl+h", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.referenceSearch.trigger", "Find All References", "Editor", "shift+f12", "shift+f12", "shift+f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeActiveEditor", "Close Editor", "View", "cmd+w", "ctrl+w", "ctrl+w", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.nextEditor", "Next Editor", "View", "cmd+shift+]", "ctrl+pagedown", "ctrl+pagedown", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.previousEditor", "Previous Editor", "View", "cmd+shift+[", "ctrl+pageup", "ctrl+pageup", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openNextRecentlyUsedEditorInGroup", "Switch Tabs", "View", "ctrl+tab", "ctrl+tab", "ctrl+tab", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.save", "Save", "File", "cmd+s", "ctrl+s", "ctrl+s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.saveAs", "Save As", "File", "cmd+shift+s", "ctrl+shift+s", "ctrl+shift+s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.saveAll", "Save All", "File", "cmd+k s", "ctrl+k s", "ctrl+k s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.clipboardCutAction", "Cut", "Editor", "cmd+x", "ctrl+x", "ctrl+x", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.clipboardCopyAction", "Copy", "Editor", "cmd+c", "ctrl+c", "ctrl+c", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.clipboardPasteAction", "Paste", "Editor", "cmd+v", "ctrl+v", "ctrl+v", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.jumpToBracket", "Jump to Bracket", "Editor", "cmd+shift+\\", "ctrl+shift+\\", "ctrl+shift+\\", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.indentLines", "Indent Line", "Editor", "cmd+]", "ctrl+]", "ctrl+]", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.outdentLines", "Outdent Line", "Editor", "cmd+[", "ctrl+[", "ctrl+[", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorHome", "Line Start", "Editor", "home", "home", "home", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorEnd", "Line End", "Editor", "end", "end", "end", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorTop", "File Start", "Editor", "cmd+up", "ctrl+home", "ctrl+home", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorBottom", "File End", "Editor", "cmd+down", "ctrl+end", "ctrl+end", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editorScroll", "Scroll Line Up", "Editor", "ctrl+cmd+pageup", "ctrl+up", "ctrl+up", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editorScroll", "Scroll Line Down", "Editor", "ctrl+cmd+pagedown", "ctrl+down", "ctrl+down", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editorScroll", "Scroll Page Up", "Editor", "cmd+pageup", "alt+pageup", "alt+pageup", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editorScroll", "Scroll Page Down", "Editor", "cmd+pagedown", "alt+pagedown", "alt+pagedown", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.fold", "Fold", "Editor", "cmd+alt+[", "ctrl+shift+[", "ctrl+shift+[", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.unfold", "Unfold", "Editor", "cmd+alt+]", "ctrl+shift+]", "ctrl+shift+]", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.foldAll", "Fold All", "Editor", "cmd+k cmd+0", "ctrl+k ctrl+0", "ctrl+k ctrl+0", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.unfoldAll", "Unfold All", "Editor", "cmd+k cmd+j", "ctrl+k ctrl+j", "ctrl+k ctrl+j", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.addCommentLine", "Add Line Comment", "Editor", "cmd+k cmd+c", "ctrl+k ctrl+c", "ctrl+k ctrl+c", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.removeCommentLine", "Remove Line Comment", "Editor", "cmd+k cmd+u", "ctrl+k ctrl+u", "ctrl+k ctrl+u", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.toggleWordWrap", "Toggle Word Wrap", "Editor", "alt+z", "alt+z", "alt+z", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("undo", "Undo", "Editor", "cmd+z", "ctrl+z", "ctrl+z", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("redo", "Redo", "Editor", "cmd+shift+z", "ctrl+y", "ctrl+y", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("deleteWordLeft", "Delete Word Left", "Editor", "alt+backspace", "ctrl+backspace", "ctrl+backspace", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("deleteWordRight", "Delete Word Right", "Editor", "alt+delete", "ctrl+delete", "ctrl+delete", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorAbove", "Add Cursor Above", "Editor", "cmd+alt+up", "ctrl+alt+up", "ctrl+alt+up", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorBelow", "Add Cursor Below", "Editor", "cmd+alt+down", "ctrl+alt+down", "ctrl+alt+down", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorUndo", "Cursor Undo", "Editor", "cmd+u", "ctrl+u", "ctrl+u", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorAtEndOfEachLineSelected", "Add Cursors to Line Ends", "Editor", "shift+alt+i", "shift+alt+i", "shift+alt+i", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("expandLineSelection", "Expand Line Selection", "Editor", "cmd+l", "ctrl+l", "ctrl+l", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.changeAll", "Change All Occurrences", "Editor", "cmd+f2", "ctrl+f2", "ctrl+f2", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.smartSelect.expand", "Expand Selection", "Editor", "ctrl+shift+cmd+right", "shift+alt+right", "shift+alt+right", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.smartSelect.shrink", "Shrink Selection", "Editor", "ctrl+shift+cmd+left", "shift+alt+left", "shift+alt+left", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.nextMatchFindAction", "Find Next", "Editor", "f3", "f3", "f3", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.previousMatchFindAction", "Find Previous", "Editor", "shift+f3", "shift+f3", "shift+f3", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.selectAllMatches", "Select All Find Matches", "Editor", "alt+enter", "alt+enter", "alt+enter", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.findInFiles", "Find in Files", "View", "cmd+shift+f", "ctrl+shift+f", "ctrl+shift+f", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.replaceInFiles", "Replace in Files", "View", "cmd+shift+h", "ctrl+shift+h", "ctrl+shift+h", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.triggerSuggest", "Trigger Suggestions", "Editor", "cmd+space", "ctrl+space", "ctrl+space", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.triggerParameterHints", "Parameter Hints", "Editor", "cmd+shift+space", "ctrl+shift+space", "ctrl+shift+space", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("inlineChat.start", "Inline AI Edit", "Editor", "cmd+i", "ctrl+i", "ctrl+i", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.formatDocument", "Format Document", "Editor", "shift+alt+f", "shift+alt+f", "shift+alt+f", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.formatSelection", "Format Selection", "Editor", "cmd+k cmd+f", "ctrl+k ctrl+f", "ctrl+k ctrl+f", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.revealDefinitionAside", "Open Definition to Side", "Editor", "cmd+k f12", "ctrl+k f12", "ctrl+k f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.rename", "Rename Symbol", "Editor", "f2", "f2", "f2", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.quickFix", "Quick Fix", "Editor", "cmd+.", "ctrl+.", "ctrl+.", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.actions.view.problems", "Show Problems", "View", "cmd+shift+m", "ctrl+shift+m", "ctrl+shift+m", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.marker.next", "Next Problem", "Editor", "f8", "f8", "f8", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.marker.prev", "Previous Problem", "Editor", "shift+f8", "shift+f8", "shift+f8", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.navigateBack", "Go Back", "View", "ctrl+-", "alt+left", "alt+left", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.navigateForward", "Go Forward", "View", "ctrl+shift+-", "alt+right", "alt+right", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.newUntitledFile", "New Untitled File", "File", "cmd+n", "ctrl+n", "ctrl+n", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.openFile", "Open File", "File", "cmd+o", "ctrl+o", "ctrl+o", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeAllEditors", "Close All Editors", "View", "cmd+k cmd+w", "ctrl+k ctrl+w", "ctrl+k ctrl+w", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeUnmodifiedEditors", "Close Saved Editors", "View", "cmd+k u", "ctrl+k u", "ctrl+k u", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.reopenClosedEditor", "Reopen Closed Editor", "View", "cmd+shift+t", "ctrl+shift+t", "ctrl+shift+t", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.splitEditor", "Split Editor", "View", "cmd+\\", "ctrl+\\", "ctrl+\\", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.focusFirstEditorGroup", "Focus First Editor Group", "View", "cmd+1", "ctrl+1", "ctrl+1", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.focusSecondEditorGroup", "Focus Second Editor Group", "View", "cmd+2", "ctrl+2", "ctrl+2", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomIn", "Zoom In", "View", "cmd+=", "ctrl+=", "ctrl+=", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomOut", "Zoom Out", "View", "cmd+-", "ctrl+-", "ctrl+-", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomReset", "Zoom Reset", "View", "cmd+numpad0", "ctrl+numpad0", "ctrl+numpad0", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleFullScreen", "Full Screen", "View", "f11", "f11", "f11", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.debug.action.toggleBreakpoint", "Toggle Breakpoint", "Debug", "f9", "f9", "f9", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.start", "Run Active File", "Debug", "f5", "f5", "f5", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.tasks.build", "Run Build Task", "Tasks", "cmd+shift+b", "ctrl+shift+b", "ctrl+shift+b", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.new", "New Terminal", "Terminal", "ctrl+shift+`", "ctrl+shift+`", "ctrl+shift+`", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.split", "Split Terminal", "Terminal", "cmd+shift+5", "ctrl+shift+5", "ctrl+shift+5", ""));
                CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.new", "Create New Terminal", "Terminal", "cmd+shift+`", "ctrl+shift+`", "ctrl+shift+`", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.kill", "Kill Terminal", "Terminal", "", "", "", "terminalFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.start", "Start Debugging", "Debug", "f5", "f5", "f5", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.stop", "Stop Debugging", "Debug", "shift+f5", "shift+f5", "shift+f5", "inDebugMode"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.stepOver", "Step Over", "Debug", "f10", "f10", "f10", "inDebugMode"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.stepInto", "Step Into", "Debug", "f11", "f11", "f11", "inDebugMode"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.debug.stepOut", "Step Out", "Debug", "shift+f11", "shift+f11", "shift+f11", "inDebugMode"));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.view.scm", "View: Show Source Control", "View", "ctrl+shift+g", "ctrl+shift+g", "ctrl+shift+g", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.view.search", "View: Show Search", "View", "cmd+shift+f", "ctrl+shift+f", "ctrl+shift+f", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.view.explorer", "View: Show Explorer", "View", "cmd+shift+e", "ctrl+shift+e", "ctrl+shift+e", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.view.extensions", "View: Show Extensions", "View", "cmd+shift+x", "ctrl+shift+x", "ctrl+shift+x", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeAllEditors", "Close All Editors", "View", "cmd+k cmd+w", "ctrl+k ctrl+w", "ctrl+k ctrl+w", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeOtherEditors", "Close Other Editors", "View", "cmd+option+t", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.splitEditor", "Split Editor", "View", "cmd+\\", "ctrl+\\", "ctrl+\\", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.togglePanel", "Toggle Panel", "View", "cmd+j", "ctrl+j", "ctrl+j", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.findInFiles", "Find in Files", "Search", "cmd+shift+f", "ctrl+shift+f", "ctrl+shift+f", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.replaceInFiles", "Replace in Files", "Search", "cmd+shift+h", "ctrl+shift+h", "ctrl+shift+h", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.formatDocument", "Format Document", "Editor", "shift+option+f", "shift+alt+f", "shift+ctrl+i", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.rename", "Rename Symbol", "Editor", "f2", "f2", "f2", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.goToDeclaration", "Go to Definition", "Editor", "f12", "f12", "f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.goToReferences", "Go to References", "Editor", "shift+f12", "shift+f12", "shift+f12", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.marker.next", "Go to Next Problem", "Editor", "f8", "f8", "f8", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.marker.prev", "Go to Previous Problem", "Editor", "shift+f8", "shift+f8", "shift+f8", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleActivityBarVisibility", "Toggle Activity Bar Visibility", "View", "", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleFullScreen", "Toggle Full Screen", "View", "ctrl+cmd+f", "f11", "f11", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomIn", "Zoom In", "View", "cmd+=", "ctrl+=", "ctrl+=", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomOut", "Zoom Out", "View", "cmd+-", "ctrl+-", "ctrl+-", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.zoomReset", "Reset Zoom", "View", "cmd+numpad0", "ctrl+numpad0", "ctrl+numpad0", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.joinLines", "Join Lines", "Editor", "ctrl+j", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.sortLinesAscending", "Sort Lines Ascending", "Editor", "", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.sortLinesDescending", "Sort Lines Descending", "Editor", "", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.transposeLetters", "Transpose Letters", "Editor", "ctrl+t", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.transformToUppercase", "Transform to Uppercase", "Editor", "", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.transformToLowercase", "Transform to Lowercase", "Editor", "", "", "", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorAbove", "Add Cursor Above", "Multi-cursor", "option+cmd+up", "ctrl+alt+up", "shift+alt+up", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorBelow", "Add Cursor Below", "Multi-cursor", "option+cmd+down", "ctrl+alt+down", "shift+alt+down", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.insertCursorAtEndOfEachLineSelected", "Add Cursors to Line Ends", "Multi-cursor", "shift+option+i", "shift+alt+i", "shift+alt+i", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.selectHighlights", "Select All Occurrences of Find Match", "Multi-cursor", "option+enter", "alt+enter", "alt+enter", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.changeAll", "Change All Occurrences", "Multi-cursor", "cmd+f2", "ctrl+f2", "ctrl+f2", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("cursorUndo", "Undo Cursor", "Multi-cursor", "cmd+u", "ctrl+u", "ctrl+u", "editorTextFocus"));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.trimTrailingWhitespace", "Trim Trailing Whitespace", "Editor", "", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.newUntitledFile", "New Untitled File", "File", "cmd+n", "ctrl+n", "ctrl+n", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.openFile", "Open File...", "File", "cmd+o", "ctrl+o", "ctrl+o", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.openFolder", "Open Folder...", "File", "", "ctrl+k ctrl+o", "ctrl+k ctrl+o", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.saveAs", "Save As...", "File", "cmd+shift+s", "ctrl+shift+s", "ctrl+shift+s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.saveAll", "Save All", "File", "option+cmd+s", "ctrl+k s", "ctrl+k s", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.quit", "Exit", "File", "cmd+q", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.reopenClosedEditor", "Reopen Closed Editor", "View", "cmd+shift+t", "ctrl+shift+t", "ctrl+shift+t", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openRecent", "Open Recent...", "File", "ctrl+r", "ctrl+r", "ctrl+r", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.revealActiveFileInWindows", "Reveal in File Explorer", "File", "", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.copyPathOfActiveFile", "Copy Path of Active File", "File", "cmd+k p", "ctrl+k p", "ctrl+k p", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.showAllEditors", "Show All Editors", "View", "", "", "", ""));;
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.files.openFolder", "Open Folder...", "File", "cmd+k o", "ctrl+k o", "ctrl+k o", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.closeFolder", "Close Folder", "File", "cmd+k f", "ctrl+k f", "ctrl+k f", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.selectTheme", "Preferences: Color Theme", "Preferences", "cmd+k cmd+t", "ctrl+k ctrl+t", "ctrl+k ctrl+t", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.keepEditor", "Keep Editor", "View", "cmd+k enter", "ctrl+k enter", "ctrl+k enter", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("copyFilePath", "Copy Path", "File", "cmd+k p", "ctrl+k p", "ctrl+k p", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("revealFileInOS", "Reveal in OS", "File", "cmd+k r", "ctrl+k r", "ctrl+k r", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.editor.changeLanguageMode", "Change Language Mode", "Editor", "cmd+k m", "ctrl+k m", "ctrl+k m", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("markdown.showPreviewToSide", "Markdown: Open Preview to the Side", "Markdown", "cmd+k v", "ctrl+k v", "ctrl+k v", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.inlineAiBar", "Inline AI Edit", "AI", "cmd+i", "ctrl+i", "ctrl+i", ""));
        }

        public static string FormatKeybindingsMarkdown()
        {
            Register();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("# codeLess Keyboard Shortcuts\n\n");
            sb.Append("This file is generated from the default keybindings registry (`DefaultCommands` / `CommandRegistry.AllCommands`) and `WorkbenchMenuModel`.\n\n");
            sb.Append("## Default Keybindings (`CommandRegistry`)\n\n");
            sb.Append("| Command ID | Command | Category | macOS | Windows | Linux | When |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- | --- |\n");

            System.Collections.Generic.List<CommandDefinition> list = new System.Collections.Generic.List<CommandDefinition>(CommandRegistry.AllCommands);
            list.Sort(delegate(CommandDefinition a, CommandDefinition b)
            {
                return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
            });

            for (int i = 0; i < list.Count; i++)
            {
                CommandDefinition c = list[i];
                sb.Append("| ");
                sb.Append(EscapeMarkdownCell(c.Id));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.Title));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.Category));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.DefaultKeyMac));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.DefaultKeyWin));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.DefaultKeyLinux));
                sb.Append(" | ");
                sb.Append(EscapeMarkdownCell(c.When));
                sb.Append(" |\n");
            }

            sb.Append("\n## Workbench Menu Shortcuts (`WorkbenchMenuModel`)\n\n");
            sb.Append("| Menu | Item | Command ID | macOS | Windows / Linux |\n");
            sb.Append("| --- | --- | --- | --- | --- |\n");

            System.Collections.Generic.List<codeLess.Studio.WorkbenchMenu> menus = codeLess.Studio.WorkbenchMenuModel.Build();
            for (int m = 0; m < menus.Count; m++)
            {
                AppendMenuEntriesMarkdown(sb, menus[m].Title, menus[m].Items);
            }

            return sb.ToString();
        }

        private static void AppendMenuEntriesMarkdown(System.Text.StringBuilder sb, string menuPath, System.Collections.Generic.List<codeLess.Studio.WorkbenchMenuEntry> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                codeLess.Studio.WorkbenchMenuEntry e = items[i];
                if (e == null || e.IsSeparator) continue;
                if (e.IsSubmenu)
                {
                    AppendMenuEntriesMarkdown(sb, menuPath + " > " + (e.Label ?? ""), e.Children);
                }
                else
                {
                    string macSc = e.VisibleOn(true) ? (e.ShortcutFor(true) ?? "") : "";
                    string winSc = e.VisibleOn(false) ? (e.ShortcutFor(false) ?? "") : "";
                    if (macSc.Length == 0 && winSc.Length == 0) continue;
                    sb.Append("| ");
                    sb.Append(EscapeMarkdownCell(menuPath));
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdownCell(e.Label));
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdownCell(e.Id));
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdownCell(macSc));
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdownCell(winSc));
                    sb.Append(" |\n");
                }
            }
        }

        private static string EscapeMarkdownCell(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("|", "\\|");
        }
    }
}
