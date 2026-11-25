# codeLess Keybinding Audit

| Command ID | Title | Mac Default | Win/Linux Default | codeLess Status | File:Line | Test Name |
|---|---|---|---|---|---|---|
| workbench.action.showCommands | Show Command Palette | `cmd+shift+p` | `ctrl+shift+p` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:178 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.quickOpen | Go to File... | `cmd+p` | `ctrl+p` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:179 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.newWindow | New Window | `cmd+shift+n` | `ctrl+shift+n` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:180 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.closeWindow | Close Window | `cmd+shift+w` | `ctrl+shift+w` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:181 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.openSettings | Preferences: Open Settings (UI) | `cmd+,` | `ctrl+,` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:182 | `DefectHeadlessTests.SimulateShortcut_CmdComma_OpensSettings` |
| workbench.action.openSettingsJson | Preferences: Open User Settings (JSON) | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:183 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| workbench.action.openGlobalKeybindings | Preferences: Open Keyboard Shortcuts | `cmd+k cmd+s` | `ctrl+k ctrl+s` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:195 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.terminal.toggleTerminal | Toggle Terminal | `ctrl+`` | `ctrl+`` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:208 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.toggleSidebarVisibility | Toggle Side Bar | `cmd+b` | `ctrl+b` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:214 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.toggleZenMode | Zen Mode | `cmd+k z` | `ctrl+k z` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:215 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.copyLinesDownAction | Copy Line Down | `shift+alt+down` | `shift+alt+down` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:70 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.copyLinesUpAction | Copy Line Up | `shift+alt+up` | `shift+alt+up` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:69 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.moveLinesDownAction | Move Line Down | `alt+down` | `alt+down` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:66 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.moveLinesUpAction | Move Line Up | `alt+up` | `alt+up` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:65 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.deleteLines | Delete Line | `cmd+shift+k` | `ctrl+shift+k` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:81 | `ContextKeysHeadlessTests2.DeleteLines_FiresInEditor_NotTerminal` |
| editor.action.insertLineAfter | Insert Line Below | `cmd+enter` | `ctrl+enter` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:68 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.insertLineBefore | Insert Line Above | `cmd+shift+enter` | `ctrl+shift+enter` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:67 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.commentLine | Toggle Line Comment | `cmd+/` | `ctrl+/` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:233 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.blockComment | Toggle Block Comment | `shift+alt+a` | `shift+alt+a` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:176 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.addSelectionToNextFindMatch | Add Selection To Next Find Match | `cmd+d` | `ctrl+d` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:71 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.selectHighlights | Select All Occurrences | `cmd+shift+l` | `ctrl+shift+l` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:122 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.revealDefinition | Go to Definition | `f12` | `f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:103 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.peekDefinition | Peek Definition | `alt+f12` | `alt+f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:99 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.gotoSymbol | Go to Symbol in File | `cmd+shift+o` | `ctrl+shift+o` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:156 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.showAllSymbols | Go to Symbol in Workspace | `cmd+t` | `ctrl+t` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:161 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.gotoLine | Go to Line | `ctrl+g` | `ctrl+g` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:155 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| actions.find | Find | `cmd+f` | `ctrl+f` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:216 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.startFindReplaceAction | Replace | `cmd+alt+f` | `ctrl+h` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:106 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.referenceSearch.trigger | Find All References | `shift+f12` | `shift+f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:102 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.closeActiveEditor | Close Editor | `cmd+w` | `ctrl+f4` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:229 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.nextEditor | Next Editor | `cmd+alt+right` | `ctrl+pagedown` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:230 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.previousEditor | Previous Editor | `cmd+alt+left` | `ctrl+pageup` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:231 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.openNextRecentlyUsedEditorInGroup | Switch Tabs | `ctrl+tab` | `ctrl+tab` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:232 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.save | Save | `cmd+s` | `ctrl+s` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:152 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.saveAs | Save As | `cmd+shift+s` | `ctrl+shift+s` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:129 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.saveAll | Save All | `cmd+k s` | `ctrl+k s` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:128 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.clipboardCutAction | Cut | `cmd+x` | `ctrl+x` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:93 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.clipboardCopyAction | Copy | `cmd+c` | `ctrl+c` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:92 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.clipboardPasteAction | Paste | `cmd+v` | `ctrl+v` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:94 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.jumpToBracket | Jump to Bracket | `cmd+shift+\\` | `ctrl+shift+\\` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:173 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.indentLines | Indent Line | `cmd+]` | `ctrl+]` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:82 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.outdentLines | Outdent Line | `cmd+[` | `ctrl+[` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:83 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| cursorHome | Line Start | `home` | `home` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:88 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| cursorEnd | Line End | `end` | `end` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:87 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| cursorTop | File Start | `cmd+up` | `ctrl+home` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:89 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| cursorBottom | File End | `cmd+down` | `ctrl+end` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:86 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editorScroll | Scroll Line Up / Down | `ctrl+cmd+pageup` | `ctrl+up` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:123 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.fold | Fold | `cmd+alt+[` | `ctrl+shift+[` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:84 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.unfold | Unfold | `cmd+alt+]` | `ctrl+shift+]` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:85 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.foldAll | Fold All | `cmd+k cmd+0` | `ctrl+k ctrl+0` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:171 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.unfoldAll | Unfold All | `cmd+k cmd+j` | `ctrl+k ctrl+j` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:172 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.addCommentLine | Add Line Comment | `cmd+k cmd+c` | `ctrl+k ctrl+c` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:174 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.removeCommentLine | Remove Line Comment | `cmd+k cmd+u` | `ctrl+k ctrl+u` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:175 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.toggleWordWrap | Toggle Word Wrap | `alt+z` | `alt+z` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:72 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| undo | Undo | `cmd+z` | `ctrl+z` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:112 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| redo | Redo | `cmd+shift+z` | `ctrl+y` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:111 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| deleteWordLeft | Delete Word Left | `alt+backspace` | `ctrl+backspace` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:90 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| deleteWordRight | Delete Word Right | `alt+delete` | `ctrl+delete` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:91 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.insertCursorAbove | Add Cursor Above | `cmd+alt+up` | `ctrl+alt+up` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:116 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.insertCursorBelow | Add Cursor Below | `cmd+alt+down` | `ctrl+alt+down` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:118 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| cursorUndo | Cursor Undo | `cmd+u` | `ctrl+u` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:113 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.insertCursorAtEndOfEachLineSelected | Add Cursors to Line Ends | `shift+alt+i` | `shift+alt+i` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:117 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| expandLineSelection | Expand Line Selection | `cmd+l` | `ctrl+l` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:110 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.changeAll | Change All Occurrences | `cmd+f2` | `ctrl+f2` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:114 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.smartSelect.expand | Expand Selection | `ctrl+shift+cmd+right` | `shift+alt+right` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:79 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.smartSelect.shrink | Shrink Selection | `ctrl+shift+cmd+left` | `shift+alt+left` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:80 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.nextMatchFindAction | Find Next | `f3` | `f3` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:98 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.previousMatchFindAction | Find Previous | `shift+f3` | `shift+f3` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:100 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.selectAllMatches | Select All Find Matches | `alt+enter` | `alt+enter` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:105 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.findInFiles | Find in Files | `cmd+shift+f` | `ctrl+shift+f` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:130 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.replaceInFiles | Replace in Files | `cmd+shift+h` | `ctrl+shift+h` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:132 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.triggerSuggest | Trigger Suggestions | `cmd+space` | `ctrl+space` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:108 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.triggerParameterHints | Parameter Hints | `cmd+shift+space` | `ctrl+shift+space` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:107 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| inlineChat.start | Inline AI Edit | `cmd+i` | `ctrl+i` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:217 | `CrossCuttingQaW7Tests.InteractionConflicts_TabPerContext_CmdKInTerminalVsEditor_CmdIInlineAi_AndEscapePriority` |
| editor.action.formatDocument | Format Document | `shift+alt+f` | `shift+alt+f` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:115 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.formatSelection | Format Selection | `cmd+k cmd+f` | `ctrl+k ctrl+f` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:95 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.revealDefinitionAside | Open Definition to Side | `cmd+k f12` | `ctrl+k f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:104 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.rename | Rename Symbol | `f2` | `f2` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:121 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.quickFix | Quick Fix | `cmd+.` | `ctrl+.` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:101 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.actions.view.problems | Show Problems | `cmd+shift+m` | `ctrl+shift+m` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:166 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.marker.next | Next Problem | `f8` | `f8` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:119 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.marker.prev | Previous Problem | `shift+f8` | `shift+f8` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:120 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.navigateBack | Go Back | `ctrl+-` | `alt+left` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:157 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.navigateForward | Go Forward | `ctrl+shift+-` | `alt+right` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:158 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.newUntitledFile | New Untitled File | `cmd+n` | `ctrl+n` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:126 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.openFile | Open File | `cmd+o` | `ctrl+o` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:127 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.closeAllEditors | Close All Editors | `cmd+k cmd+w` | `ctrl+k ctrl+w` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:124 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.closeUnmodifiedEditors | Close Saved Editors | `cmd+k u` | `ctrl+k u` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:237 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.reopenClosedEditor | Reopen Closed Editor | `cmd+shift+t` | `ctrl+shift+t` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:131 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.splitEditor | Split Editor | `cmd+\\` | `ctrl+\\` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:133 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.focusFirstEditorGroup | Focus First Editor Group | `cmd+1` | `ctrl+1` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:153 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.focusSecondEditorGroup | Focus Second Editor Group | `cmd+2` | `ctrl+2` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:154 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.zoomIn | Zoom In | `cmd+=` | `ctrl+=` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:136 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.zoomOut | Zoom Out | `cmd+-` | `ctrl+-` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:137 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.zoomReset | Zoom Reset | `cmd+numpad0` | `ctrl+numpad0` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:138 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.toggleFullScreen | Full Screen | `ctrl+cmd+f` | `f11` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:135 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.debug.action.toggleBreakpoint | Toggle Breakpoint | `f9` | `f9` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:109 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.debug.start | Start Debugging / Run Active File | `f5` | `f5` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:125 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.tasks.build | Run Build Task | `cmd+shift+b` | `ctrl+shift+b` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:162 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.terminal.new | Create New Terminal | `ctrl+shift+`` | `ctrl+shift+`` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:134 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.terminal.split | Split Terminal | `cmd+shift+5` | `ctrl+shift+5` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:164 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.terminal.kill | Kill Terminal | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:163 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| workbench.action.debug.stop | Stop Debugging | `shift+f5` | `shift+f5` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:150 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.debug.stepOver | Step Over | `f10` | `f10` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:149 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.debug.stepInto | Step Into | `f11` | `f11` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:147 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.debug.stepOut | Step Out | `shift+f11` | `shift+f11` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:148 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.view.scm | View: Show Source Control | `ctrl+shift+g` | `ctrl+shift+g` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:169 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.view.search | View: Show Search | `cmd+shift+f` | `ctrl+shift+f` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:170 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.view.explorer | View: Show Explorer | `cmd+shift+e` | `ctrl+shift+e` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:167 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.view.extensions | View: Show Extensions | `cmd+shift+x` | `ctrl+shift+x` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:168 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.closeOtherEditors | Close Other Editors | `cmd+option+t` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:139 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.togglePanel | Toggle Panel | `cmd+j` | `ctrl+j` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:165 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.goToDeclaration | Go to Definition | `f12` | `f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:96 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.goToReferences | Go to References | `shift+f12` | `shift+f12` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:97 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.toggleActivityBarVisibility | Toggle Activity Bar Visibility | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:219 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| editor.action.joinLines | Join Lines | `ctrl+j` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:220 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| editor.action.transformToUppercase | Transform to Uppercase | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:224 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| editor.action.transformToLowercase | Transform to Lowercase | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:225 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| editor.action.trimTrailingWhitespace | Trim Trailing Whitespace | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:226 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| workbench.action.files.openFolder | Open Folder... | `` | `ctrl+k ctrl+o` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:234 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.quit | Exit | `cmd+q` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:160 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.openRecent | Open Recent... | `ctrl+r` | `ctrl+r` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:159 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.files.revealActiveFileInWindows | Reveal in File Explorer | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:227 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |
| workbench.action.files.copyPathOfActiveFile | Copy Path of Active File | `cmd+k p` | `ctrl+k p` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:151 | `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable` |
| workbench.action.showAllEditors | Show All Editors | `` | `` | Fixed | src/codeLess.Avalonia/MainWindow.Keybindings.cs:228 | `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound` |

## Verification Tests
- `AuditTruthfulnessTests.Audit_AllFixedCommands_AreBound`
- `DataDrivenRegistryTests.AllRegistryDefaults_AreDispatchable`
- `DataDrivenRegistryTests.PaletteShortcutLabels_MatchRegistry`
- `ContextKeysHeadlessTests.ContextKeys_ReturnsRealState`
- `ContextKeysHeadlessTests.Keybindings_TextBoxIgnoresEditorCommands`
- `ContextKeysHeadlessTests2.DeleteLines_FiresInEditor_NotTerminal`
- `KeyboardShortcutsHeadlessTests.KeyboardShortcutsEditor_HasSearchAndConflicts`
- `KeyboardShortcutsHeadlessTests.KeyboardShortcutsEditor_RemoveBinding`
- `KeyboardShortcutsHeadlessTests.KeyboardShortcutsEditor_DefineBinding`
- `DefectHeadlessTests.InvalidJSON_ThenValidJSON_ToUserKeybindingsFile_ShowsNotificationThenClears_AndApplies`
- `DefectHeadlessTests.SimulateShortcut_CmdComma_OpensSettings`
- `DefectHeadlessTests.TextBox_Focus_KeepsTextEditingKeys_DoesNotFireEditorBindings`
- `MultiWindowHeadlessTests.WindowClose_DisposesWatchers`
- `WorkspaceSettingsHeadlessTests.WorkspaceSettings_ApplyInMemory_AndDoNotLeak`
- `WorkspaceSettingsHeadlessTests2.WorkspaceTheme_NotPersisted`
- `WorkspaceSettingsHeadlessTests2.OpenFolder_AppliesWorkspaceSettings`
- `WorkspaceSettingsHeadlessTests2.UserChange_KeepsWorkspaceOverride`
- `CrossCuttingQaW7Tests.ShortcutSweep_AllDefaultCommands_AndWorkbenchMenuModel_ExecuteInFocusContext_WithoutLeakingText`
