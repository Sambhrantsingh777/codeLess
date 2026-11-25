using System;
using Avalonia.Input;
using codeLess.Core;
using codeLess.Studio;
using System.Linq;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private KeybindingManager keybindings;
        private KeybindingsFileWatcher keybindingsWatcher;
        private Avalonia.Controls.Border keybindingsNotification;
        private WindowContextKeyService contextKeyService;
        private bool isDispatchingCommandKey;
        private System.IO.FileSystemWatcher workspaceSettingsWatcher;
        private System.Threading.Timer workspaceSettingsTimer;
        private System.IO.FileSystemWatcher userSettingsWatcher;
        private System.Threading.Timer userSettingsTimer;
        private StudioSettings effectiveWorkspaceSettings;

        private void SendEditorKey(Key key, KeyModifiers mods)
        {
            if (PrimaryEditor?.Canvas == null) return;
            bool prev = isDispatchingCommandKey;
            isDispatchingCommandKey = true;
            try
            {
                var ev = new KeyEventArgs
                {
                    RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = mods
                };
                PrimaryEditor.Canvas.RaiseEvent(ev);
            }
            finally
            {
                isDispatchingCommandKey = prev;
            }
        }

        private void InitializeKeybindings()
        {
            DefaultCommands.Register();
            keybindings = new KeybindingManager(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX));
            KeybindingManager = keybindings;

            // Bind actions to commands
            foreach (var kvp in MenuCommands)
            {
                keybindings.BindAction(kvp.Key, kvp.Value);
            }

            // Bind palette commands by title
            foreach (var item in BuildAllPaletteCommands())
            {
                var cmd = codeLess.Core.CommandRegistry.AllCommands.FirstOrDefault(c => c.Title == item.Title);
                if (cmd != null && item.Execute != null)
                {
                    keybindings.BindAction(cmd.Id, item.Execute);
                }
            }

            // Bind VS Code command IDs to their workbench and editor actions
            keybindings.BindAction("editor.action.moveLinesUpAction", () => PrimaryEditor?.Canvas?.MoveActiveLine(-1));
            keybindings.BindAction("editor.action.moveLinesDownAction", () => PrimaryEditor?.Canvas?.MoveActiveLine(1));
            keybindings.BindAction("editor.action.insertLineBefore", () => PrimaryEditor?.Canvas?.InsertLineAboveOrBelow(true));
            keybindings.BindAction("editor.action.insertLineAfter", () => PrimaryEditor?.Canvas?.InsertLineAboveOrBelow(false));
            keybindings.BindAction("editor.action.copyLinesUpAction", () => PrimaryEditor?.Canvas?.CopyActiveLine(-1));
            keybindings.BindAction("editor.action.copyLinesDownAction", () => PrimaryEditor?.Canvas?.CopyActiveLine(1));
            keybindings.BindAction("editor.action.addSelectionToNextFindMatch", () => PrimaryEditor?.Canvas?.AddNextOccurrenceOrSelectWord());
            keybindings.BindAction("editor.action.toggleWordWrap", () =>
            {
                ToggleWordWrapCommand();
                suppressNextWindowTextInput = true;
                PrimaryEditor?.Canvas?.SuppressNextTextInput();
                RefreshNativeCheckStates();
            });
            keybindings.BindAction("editor.action.smartSelect.expand", () => PrimaryEditor?.Canvas?.ExpandSelection());
            keybindings.BindAction("editor.action.smartSelect.shrink", () => PrimaryEditor?.Canvas?.ShrinkSelection());
            keybindings.BindAction("editor.action.deleteLines", () => PrimaryEditor?.Canvas?.DeleteActiveLine());
            keybindings.BindAction("editor.action.indentLines", () => PrimaryEditor?.Canvas?.IndentOrOutdentLines(false));
            keybindings.BindAction("editor.action.outdentLines", () => PrimaryEditor?.Canvas?.IndentOrOutdentLines(true));
            keybindings.BindAction("editor.fold", () => PrimaryEditor?.Canvas?.FoldAtCaret());
            keybindings.BindAction("editor.unfold", () => PrimaryEditor?.Canvas?.UnfoldAtCaret());
            keybindings.BindAction("cursorBottom", () => ActiveEditorCanvas?.MoveCaretToDocumentBoundary(true, false));
            keybindings.BindAction("cursorEnd", () => ActiveEditorCanvas?.MoveCaretToLineBoundary(true, false));
            keybindings.BindAction("cursorHome", () => ActiveEditorCanvas?.MoveCaretToLineBoundary(false, false));
            keybindings.BindAction("cursorTop", () => ActiveEditorCanvas?.MoveCaretToDocumentBoundary(false, false));
            keybindings.BindAction("deleteWordLeft", () => SendEditorKey(Key.Back, MacKeymap.IsMacOS ? KeyModifiers.Alt : KeyModifiers.Control));
            keybindings.BindAction("deleteWordRight", () => SendEditorKey(Key.Delete, MacKeymap.IsMacOS ? KeyModifiers.Alt : KeyModifiers.Control));
            keybindings.BindAction("editor.action.clipboardCopyAction", () => RouteClipboardCommand("copy"));
            keybindings.BindAction("editor.action.clipboardCutAction", () => RouteClipboardCommand("cut"));
            keybindings.BindAction("editor.action.clipboardPasteAction", () => RouteClipboardCommand("paste"));
            keybindings.BindAction("editor.action.formatSelection", () => FormatDocumentOrSelection(true));
            keybindings.BindAction("editor.action.goToDeclaration", () => GoToDefinitionAtCursor());
            keybindings.BindAction("editor.action.goToReferences", () => FindAllReferencesAtCursor());
            keybindings.BindAction("editor.action.nextMatchFindAction", () => PrimaryEditor?.FindNext(1));
            keybindings.BindAction("editor.action.peekDefinition", () => GoToDefinitionAtCursor());
            keybindings.BindAction("editor.action.previousMatchFindAction", () => PrimaryEditor?.FindNext(-1));
            keybindings.BindAction("editor.action.quickFix", () => ShowQuickFixes());
            keybindings.BindAction("editor.action.referenceSearch.trigger", () => FindAllReferencesAtCursor());
            keybindings.BindAction("editor.action.revealDefinition", () => GoToDefinitionAtCursor());
            keybindings.BindAction("editor.action.revealDefinitionAside", () => { GoToDefinitionAtCursor(); if (!isEditorSplit) ToggleSplitEditor(); });
            keybindings.BindAction("editor.action.selectAllMatches", () => PrimaryEditor?.Canvas?.SelectAllFindOccurrences());
            keybindings.BindAction("editor.action.startFindReplaceAction", () => PrimaryEditor?.ShowFindBar(true));
            keybindings.BindAction("editor.action.triggerParameterHints", () => PrimaryLspBinding?.TriggerSignatureHelp(null, false));
            keybindings.BindAction("editor.action.triggerSuggest", () => PrimaryLspBinding?.TriggerCompletion(null, explicitInvoke: true));
            keybindings.BindAction("editor.debug.action.toggleBreakpoint", () => { if (!HandleDebugFunctionKey(Key.F9, KeyModifiers.None)) ToggleBreakpointAtCursor(); });
            keybindings.BindAction("expandLineSelection", () => SendEditorKey(Key.L, MacKeymap.PrimaryModifier));
            keybindings.BindAction("redo", () => PrimaryEditor?.Canvas?.PerformUndoRedo(true));
            keybindings.BindAction("undo", () => PrimaryEditor?.Canvas?.PerformUndoRedo(false));
            keybindings.BindAction("cursorUndo", () => SendEditorKey(Key.U, MacKeymap.PrimaryModifier));
            keybindings.BindAction("editor.action.changeAll", () => PrimaryEditor?.Canvas?.SelectAllFindOccurrences());
            keybindings.BindAction("editor.action.formatDocument", () => FormatDocumentOrSelection(false));
            keybindings.BindAction("editor.action.insertCursorAbove", () => PrimaryEditor?.Canvas?.AddCursorVertical(-1));
            keybindings.BindAction("editor.action.insertCursorAtEndOfEachLineSelected", () => PrimaryEditor?.Canvas?.AddCursorsToLineEnds());
            keybindings.BindAction("editor.action.insertCursorBelow", () => PrimaryEditor?.Canvas?.AddCursorVertical(1));
            keybindings.BindAction("editor.action.marker.next", () => JumpDiagnostic(1));
            keybindings.BindAction("editor.action.marker.prev", () => JumpDiagnostic(-1));
            keybindings.BindAction("editor.action.rename", () => RenameSymbolAtCursor());
            keybindings.BindAction("editor.action.selectHighlights", () => PrimaryEditor?.Canvas?.SelectAllFindOccurrences());
            keybindings.BindAction("editorScroll", () => PrimaryEditor?.Canvas?.ScrollLines(1));
            keybindings.BindAction("workbench.action.closeAllEditors", () => CloseAllTabs());
            keybindings.BindAction("workbench.action.debug.start", () => { if (!HandleDebugFunctionKey(Key.F5, KeyModifiers.None)) RunActiveFileInTerminal(); });
            keybindings.BindAction("workbench.action.files.newUntitledFile", () => CreateNewUntitledTab());
            keybindings.BindAction("workbench.action.files.openFile", () => _ = PromptOpenFileDialogAsync());
            keybindings.BindAction("workbench.action.files.saveAll", () => SaveAllTabs());
            keybindings.BindAction("workbench.action.files.saveAs", () => _ = PromptSaveAsAsync());
            keybindings.BindAction("workbench.action.findInFiles", () => ShowSearchView(false));
            keybindings.BindAction("workbench.action.reopenClosedEditor", () => ReopenClosedTab());
            keybindings.BindAction("workbench.action.replaceInFiles", () => ShowSearchView(true));
            keybindings.BindAction("workbench.action.splitEditor", () => ToggleSplitEditor());
            keybindings.BindAction("workbench.action.terminal.new", () => RequestNewTerminal());
            keybindings.BindAction("workbench.action.toggleFullScreen", () => ToggleFullScreenMode());
            keybindings.BindAction("workbench.action.zoomIn", () => AdjustZoom(1));
            keybindings.BindAction("workbench.action.zoomOut", () => AdjustZoom(-1));
            keybindings.BindAction("workbench.action.zoomReset", () => AdjustZoom(0));
            keybindings.BindAction("workbench.action.closeOtherEditors", () =>
            {
                if (activeTab != null)
                {
                    var others = openTabs.Where(t => !ReferenceEquals(t, activeTab) && !t.IsPinned).ToList();
                    foreach (var t in others) CloseTab(t);
                }
            });
            keybindings.BindAction("workbench.action.debug.stepInto", () => debugController?.StepInto());
            keybindings.BindAction("workbench.action.debug.stepOut", () => debugController?.StepOut());
            keybindings.BindAction("workbench.action.debug.stepOver", () => debugController?.StepOver());
            keybindings.BindAction("workbench.action.debug.stop", () => { if (debugController != null) _ = debugController.StopAsync(); });
            keybindings.BindAction("workbench.action.files.copyPathOfActiveFile", () => { if (activeTab != null) _ = CopyPathToClipboardAsync(activeTab.FilePath ?? activeTab.FileName); });
            keybindings.BindAction("workbench.action.files.save", () => SaveActiveTab());
            keybindings.BindAction("workbench.action.focusFirstEditorGroup", () => { if (openTabs.Count > 0) ActivateTab(openTabs[0]); });
            keybindings.BindAction("workbench.action.focusSecondEditorGroup", () => { if (openTabs.Count > 1) ActivateTab(openTabs[1]); });
            keybindings.BindAction("workbench.action.gotoLine", () => OpenCommandPalette(":"));
            keybindings.BindAction("workbench.action.gotoSymbol", () => OpenCommandPalette("@"));
            keybindings.BindAction("workbench.action.navigateBack", () => NavigateBack());
            keybindings.BindAction("workbench.action.navigateForward", () => NavigateForward());
            keybindings.BindAction("workbench.action.openRecent", () => { var r = RecentFolders().FirstOrDefault(); if (r != null) LoadWorkspace(r); });
            keybindings.BindAction("workbench.action.quit", () => Close());
            keybindings.BindAction("workbench.action.showAllSymbols", () => OpenCommandPalette("#"));
            keybindings.BindAction("workbench.action.tasks.build", () => RunWorkspaceBuildInTerminal());
            keybindings.BindAction("workbench.action.terminal.kill", () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); });
            keybindings.BindAction("workbench.action.terminal.split", () => ToggleSplitTerminal());
            keybindings.BindAction("workbench.action.togglePanel", () => ToggleBottomPanel());
            keybindings.BindAction("workbench.actions.view.problems", () => SwitchBottomTab("problems"));
            keybindings.BindAction("workbench.view.explorer", () => SwitchSidebarView("explorer"));
            keybindings.BindAction("workbench.view.extensions", () => SwitchSidebarView("extensions"));
            keybindings.BindAction("workbench.view.scm", () => SwitchSidebarView("git"));
            keybindings.BindAction("workbench.view.search", () => ShowSearchView(false));
            keybindings.BindAction("editor.foldAll", () => PrimaryEditor?.Canvas?.FoldAll());
            keybindings.BindAction("editor.unfoldAll", () => PrimaryEditor?.Canvas?.UnfoldAll());
            keybindings.BindAction("editor.action.jumpToBracket", () => PrimaryEditor?.Canvas?.JumpToMatchingBracket());
            keybindings.BindAction("editor.action.addCommentLine", () => PrimaryEditor?.Canvas?.ToggleLineComment());
            keybindings.BindAction("editor.action.removeCommentLine", () => PrimaryEditor?.Canvas?.ToggleLineComment());
            keybindings.BindAction("editor.action.blockComment", () => PrimaryEditor?.Canvas?.ToggleBlockComment());

            keybindings.BindAction("workbench.action.showCommands", () => OpenCommandPalette(">"));
            keybindings.BindAction("workbench.action.quickOpen", () => OpenQuickOpen());
            keybindings.BindAction("workbench.action.newWindow", () => CreateNewWindowInstance());
            keybindings.BindAction("workbench.action.closeWindow", () => Close());
            keybindings.BindAction("workbench.action.openSettings", () => ShowSettingsModal());
            keybindings.BindAction("workbench.action.openSettingsJson", () =>
            {
                string p = VscodeSettingsManager.GetUserSettingsPath();
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                    if (!System.IO.File.Exists(p)) System.IO.File.WriteAllText(p, "{\n}");
                }
                catch { }
                OpenFileInTab(p);
            });
            keybindings.BindAction("workbench.action.openWorkspaceSettingsFile", () => OpenWorkspaceSettingsFile());
            keybindings.BindAction("workbench.action.openGlobalKeybindings", () => ShowKeyboardShortcutsModal());
            keybindings.BindAction("workbench.action.openGlobalKeybindingsFile", () =>
            {
                string p = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                    if (!System.IO.File.Exists(p)) System.IO.File.WriteAllText(p, "[\n]");
                }
                catch { }
                OpenFileInTab(p);
            });
            keybindings.BindAction("workbench.action.openDefaultKeybindingsFile", () => OpenDefaultKeybindingsFile());
            keybindings.BindAction("workbench.action.terminal.toggleTerminal", () =>
            {
                if (!bottomPanelBorder.IsVisible) { ToggleBottomPanel(); SwitchBottomTab("terminal"); }
                else if (activeBottomTab != "terminal") SwitchBottomTab("terminal");
                else ToggleBottomPanel();
            });
            keybindings.BindAction("workbench.action.toggleSidebarVisibility", () => TogglePrimarySidebar());
            keybindings.BindAction("workbench.action.toggleZenMode", () => ToggleZenMode());
            keybindings.BindAction("actions.find", () => PrimaryEditor?.ShowFindBar(false));
            keybindings.BindAction("inlineChat.start", () => PrimaryEditor?.ShowInlineAiBar());

            keybindings.BindAction("workbench.action.toggleActivityBarVisibility", () => { if (activityBarBorder != null) activityBarBorder.IsVisible = !activityBarBorder.IsVisible; });
            keybindings.BindAction("editor.action.joinLines", () => PrimaryEditor?.Canvas?.JoinLines());
            keybindings.BindAction("editor.action.sortLinesAscending", () => PrimaryEditor?.Canvas?.SortLinesAscending());
            keybindings.BindAction("editor.action.sortLinesDescending", () => PrimaryEditor?.Canvas?.SortLinesDescending());
            keybindings.BindAction("editor.action.transposeLetters", () => PrimaryEditor?.Canvas?.TransposeLetters());
            keybindings.BindAction("editor.action.transformToUppercase", () => PrimaryEditor?.Canvas?.TransformToUppercase());
            keybindings.BindAction("editor.action.transformToLowercase", () => PrimaryEditor?.Canvas?.TransformToLowercase());
            keybindings.BindAction("editor.action.trimTrailingWhitespace", () => PrimaryEditor?.Canvas?.TrimTrailingWhitespace());
            keybindings.BindAction("workbench.action.files.revealActiveFileInWindows", () => { if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) RevealInOsFileManager(activeTab.FilePath); });
            keybindings.BindAction("workbench.action.showAllEditors", () => OpenCommandPalette("edt "));
            keybindings.BindAction("workbench.action.closeActiveEditor", () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); });
            keybindings.BindAction("workbench.action.nextEditor", () => CycleEditorTab(1));
            keybindings.BindAction("workbench.action.previousEditor", () => CycleEditorTab(-1));
            keybindings.BindAction("workbench.action.openNextRecentlyUsedEditorInGroup", () => OpenOrAdvanceEditorSwitcher(false));
            keybindings.BindAction("editor.action.commentLine", () => PrimaryEditor?.Canvas?.ToggleLineComment());
            keybindings.BindAction("workbench.action.files.openFolder", () => { _ = PromptOpenFolderDialogAsync(); });
            keybindings.BindAction("workbench.action.closeFolder", () => CloseWorkspaceFolder());
            keybindings.BindAction("workbench.action.selectTheme", () => OpenThemePickerPalette());
            keybindings.BindAction("workbench.action.closeUnmodifiedEditors", () => { var saved = openTabs.Where(t => !t.IsDirty && !t.IsPinned).ToList(); foreach (var t in saved) CloseTab(t, forceWithoutPrompt: true); });
            keybindings.BindAction("workbench.action.keepEditor", () => { if (activeTab != null) { activeTab.IsPinned = true; activeTab.IsPreviewTab = false; RefreshTabStrip(); } });
            keybindings.BindAction("copyFilePath", () => { if (activeTab != null) _ = CopyPathToClipboardAsync(activeTab.FilePath ?? activeTab.FileName); });
            keybindings.BindAction("revealFileInOS", () => { if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) RevealInOsFileManager(activeTab.FilePath); });
            keybindings.BindAction("workbench.action.editor.changeLanguageMode", () => OpenLanguageModePicker());
            keybindings.BindAction("markdown.showPreviewToSide", () => OpenMarkdownPreview(toSide: true));
            keybindings.BindAction("editor.action.inlineAiBar", () => PrimaryEditor?.ShowInlineAiBar());
            keybindings.BindAction("cursorHomeSelect", () => ActiveEditorCanvas?.MoveCaretToLineBoundary(false, true));
            keybindings.BindAction("cursorEndSelect", () => ActiveEditorCanvas?.MoveCaretToLineBoundary(true, true));
            keybindings.BindAction("cursorLineStart", () => ActiveEditorCanvas?.MoveCaretToModelLineBoundary(false, false));
            keybindings.BindAction("cursorLineStartSelect", () => ActiveEditorCanvas?.MoveCaretToModelLineBoundary(false, true));
            keybindings.BindAction("cursorLineEnd", () => ActiveEditorCanvas?.MoveCaretToModelLineBoundary(true, false));
            keybindings.BindAction("cursorLineEndSelect", () => ActiveEditorCanvas?.MoveCaretToModelLineBoundary(true, true));
            keybindings.BindAction("cursorTopSelect", () => ActiveEditorCanvas?.MoveCaretToDocumentBoundary(false, true));
            keybindings.BindAction("cursorBottomSelect", () => ActiveEditorCanvas?.MoveCaretToDocumentBoundary(true, true));
            keybindings.BindAction("cursorWordLeft", () => ActiveEditorCanvas?.MoveCaretHorizontal(-1, false, true));
            keybindings.BindAction("cursorWordLeftSelect", () => ActiveEditorCanvas?.MoveCaretHorizontal(-1, true, true));
            keybindings.BindAction("cursorWordEndRight", () => ActiveEditorCanvas?.MoveCaretHorizontal(1, false, true));
            keybindings.BindAction("cursorWordEndRightSelect", () => ActiveEditorCanvas?.MoveCaretHorizontal(1, true, true));

            contextKeyService = new WindowContextKeyService(this);
            InitializeVscodeSettingsWatcher();
            InitializeWorkspaceSettingsWatcher();

            string path = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            keybindingsWatcher = new KeybindingsFileWatcher(path, content =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (windowClosed) return;
                    string err = keybindings.LoadUserBindings(content);
                    if (err != null)
                    {
                        ShowKeybindingsNotification("Keybindings parse error: " + err);
                    }
                    else
                    {
                        HideKeybindingsNotification();
                    }
                });
            });
            // Initial load
            try
            {
                if (System.IO.File.Exists(path))
                {
                    string err = keybindings.LoadUserBindings(System.IO.File.ReadAllText(path));
                    if (err != null) ShowKeybindingsNotification("Keybindings parse error: " + err);
                }
            }
            catch { }
        }

        private AvaloniaVirtualEditorCanvas ActiveEditorCanvas =>
            (FocusManager?.GetFocusedElement() as AvaloniaVirtualEditorCanvas) ?? PrimaryEditor?.Canvas;

        private bool DispatchKeybinding(Key key, KeyModifiers modifiers, bool isTerminalFocus)
        {
            if (keybindings == null || isDispatchingCommandKey) return false;
            if ((DateTime.UtcNow - lastCtrlKChordTime).TotalMilliseconds < 3500) return false;
            if (MacKeymap.ForceMacKeymapOverride.HasValue &&
                MacKeymap.ForceMacKeymapOverride.Value != System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                return false;
            }
            string ks = KeyStringParser.FromKeyName(key.ToString(), (int)modifiers);
            isDispatchingCommandKey = true;
            try
            {
                return keybindings.Dispatch(ks, contextKeyService, msg =>
                {
                    if (!string.IsNullOrEmpty(msg))
                        statusChordLabel.Text = msg;
                    else
                        statusChordLabel.Text = "";
                }, isTerminalFocus);
            }
            finally
            {
                isDispatchingCommandKey = false;
            }
        }

        private void ShowKeybindingsNotification(string message)
        {
            HideKeybindingsNotification();
            keybindingsNotification = WorkbenchStyles.BuildNotification(currentTheme, "error", new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, null, new Avalonia.Controls.Button[0], false, HideKeybindingsNotification);
            keybindingsNotification.Margin = new Avalonia.Thickness(0, 0, 12, 12);
            keybindingsNotification.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            keybindingsNotification.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            keybindingsNotification.ZIndex = 70;
            Avalonia.Controls.Grid.SetColumnSpan(keybindingsNotification, 2);
            editorSplitGrid.Children.Add(keybindingsNotification);
        }

        private void HideKeybindingsNotification()
        {
            if (keybindingsNotification != null)
            {
                editorSplitGrid.Children.Remove(keybindingsNotification);
                keybindingsNotification = null;
            }
        }

        private void OpenDefaultKeybindingsFile()
        {
            var defaults = codeLess.Core.CommandRegistry.AllCommands;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[");
            bool isMac = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
            bool isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
            bool first = true;
            foreach (var cmd in defaults)
            {
                string key = isMac ? cmd.DefaultKeyMac : (isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin);
                if (string.IsNullOrEmpty(key) && !isMac) key = cmd.DefaultKeyWin;
                if (string.IsNullOrEmpty(key)) continue;

                if (!first) sb.AppendLine(",");
                first = false;

                sb.AppendLine("  {");
                sb.AppendLine("    \"key\": \"" + key.ToLowerInvariant() + "\",");
                sb.AppendLine("    \"command\": \"" + cmd.Id + "\"" + (!string.IsNullOrEmpty(cmd.When) ? "," : ""));
                if (!string.IsNullOrEmpty(cmd.When))
                {
                    sb.AppendLine("    \"when\": \"" + cmd.When + "\"");
                }
                sb.Append("  }");
            }
            sb.AppendLine();
            sb.AppendLine("]");

            string settingsDir = codeLess.Studio.IdePersistentSettings.GetSettingsFilePath().Replace("settings.json", "");
            string tmpDir = System.IO.Path.Combine(settingsDir, ".tmp");
            System.IO.Directory.CreateDirectory(tmpDir);
            string tempPath = System.IO.Path.Combine(tmpDir, "defaultKeybindings_" + System.Guid.NewGuid().ToString().Substring(0, 8) + ".json");
            try
            {
                System.IO.File.WriteAllText(tempPath, sb.ToString());
            }
            catch { }
            var tab = OpenFileInTab(tempPath, preview: false);
            if (tab != null)
            {
                tab.IsReadOnly = true;
                tab.RelativePath = "Default Keybindings";
                tab.FileName = "Default Keybindings";
            }
        }

        private void InitializeWorkspaceSettingsWatcher()
        {
            if (workspaceSettingsTimer != null)
            {
                workspaceSettingsTimer.Dispose();
                workspaceSettingsTimer = null;
            }
            if (workspaceSettingsWatcher != null)
            {
                workspaceSettingsWatcher.EnableRaisingEvents = false;
                workspaceSettingsWatcher.Dispose();
                workspaceSettingsWatcher = null;
            }
            if (string.IsNullOrEmpty(workspaceRoot) || !System.IO.Directory.Exists(workspaceRoot)) return;
            string path = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);

            workspaceSettingsWatcher = new System.IO.FileSystemWatcher(workspaceRoot);
            workspaceSettingsWatcher.IncludeSubdirectories = true;
            workspaceSettingsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName;
            System.IO.FileSystemEventHandler handler = (s, e) =>
            {
                if (windowClosed) return;
                if (e != null && (e.FullPath.IndexOf(".vscode", StringComparison.OrdinalIgnoreCase) < 0 || !(e.FullPath.EndsWith("settings.json") || e.FullPath.EndsWith(".vscode")))) return;
                if (workspaceSettingsTimer != null) { workspaceSettingsTimer.Dispose(); workspaceSettingsTimer = null; }
                workspaceSettingsTimer = new System.Threading.Timer(_ =>
                {
                    if (windowClosed) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (windowClosed) return;
                        try
                        {
                            var clone = codeLess.Studio.StudioSettings.Load();
                            string userPath = VscodeSettingsManager.GetUserSettingsPath();
                            if (System.IO.File.Exists(userPath)) VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(userPath), clone);
                            if (System.IO.File.Exists(path))
                            {
                                string content = System.IO.File.ReadAllText(path);
                                string err = VscodeSettingsManager.ApplySettings(content, clone);
                                if (err != null) ShowKeybindingsNotification("Workspace settings parse error: " + err);
                                else HideKeybindingsNotification();
                            }
                            ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                        }
                        catch { }
                    });
                }, null, 100, System.Threading.Timeout.Infinite);
            };
            workspaceSettingsWatcher.Changed += handler;
            workspaceSettingsWatcher.Created += handler;
            workspaceSettingsWatcher.Renamed += (s, e) => handler(s, null);
            workspaceSettingsWatcher.EnableRaisingEvents = true;

            try
            {
                if (System.IO.File.Exists(path))
                {
                    var clone = codeLess.Studio.StudioSettings.Load();
                    string userPath = VscodeSettingsManager.GetUserSettingsPath();
                    if (System.IO.File.Exists(userPath)) VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(userPath), clone);
                    string err = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(path), clone);
                    if (err != null) ShowKeybindingsNotification("Workspace settings parse error: " + err);
                    ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                }
            }
            catch { }
        }

        private void InitializeVscodeSettingsWatcher()
        {
            if (userSettingsTimer != null)
            {
                userSettingsTimer.Dispose();
                userSettingsTimer = null;
            }
            if (userSettingsWatcher != null)
            {
                userSettingsWatcher.EnableRaisingEvents = false;
                userSettingsWatcher.Dispose();
                userSettingsWatcher = null;
            }
            string path = VscodeSettingsManager.GetUserSettingsPath();
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
            }
            userSettingsWatcher = new System.IO.FileSystemWatcher(dir, System.IO.Path.GetFileName(path));
            userSettingsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName;
            System.IO.FileSystemEventHandler handler = (s, e) =>
            {
                if (windowClosed) return;
                if (userSettingsTimer != null) { userSettingsTimer.Dispose(); userSettingsTimer = null; }
                userSettingsTimer = new System.Threading.Timer(_ =>
                {
                    if (windowClosed) return;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (windowClosed) return;
                        try
                        {
                            string content = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                            var clone = codeLess.Studio.StudioSettings.Load();
                            string err = VscodeSettingsManager.ApplySettings(content, clone);

                            string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
                            if (wsPath != null && System.IO.File.Exists(wsPath))
                            {
                                string wsErr = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(wsPath), clone);
                                if (err == null) err = wsErr;
                            }
                            ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                            if (err != null) ShowKeybindingsNotification("Settings parse error: " + err);
                            else HideKeybindingsNotification();
                        }
                        catch { }
                    });
                }, null, 100, System.Threading.Timeout.Infinite);
            };
            userSettingsWatcher.Changed += handler;
            userSettingsWatcher.Created += handler;
            userSettingsWatcher.Renamed += (s, e) => handler(s, null);
            userSettingsWatcher.EnableRaisingEvents = true;

            try
            {
                if (System.IO.File.Exists(path))
                {
                    string err = ApplyVscodeSettings(System.IO.File.ReadAllText(path));
                    if (err != null) ShowKeybindingsNotification("Settings parse error: " + err);
                }
            }
            catch { }
        }

        private string ApplyVscodeSettings(string content, StudioSettings targetSettings = null)
        {
            var target = targetSettings ?? (settings as StudioSettings);
            string parseError = null;
            if (!string.IsNullOrWhiteSpace(content))
            {
                parseError = VscodeSettingsManager.ApplySettings(content, target);

                if (targetSettings == null && !string.IsNullOrEmpty(workspaceRoot))
                {
                    string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
                    if (wsPath != null && System.IO.File.Exists(wsPath))
                    {
                        try
                        {
                            string wsErr = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(wsPath), target);
                            if (parseError == null) parseError = wsErr;
                        }
                        catch { }
                    }
                }
            }

            var studioSettings = target;
            if (studioSettings != null)
            {
                if (!ReferenceEquals(studioSettings, settings))
                {
                    effectiveWorkspaceSettings = studioSettings;
                }
                ApplyTheme(StudioTheme.GetById(studioSettings.ActiveThemeId), persistSetting: false);
                PrimaryEditor.ShowMinimap = studioSettings.ShowMinimap;
                PrimaryEditor.ShowBreadcrumbs = studioSettings.ShowBreadcrumbs;
                PrimaryEditor.Canvas.FontSizePx = studioSettings.EditorFontSize;
                ApplyEditorIndentAndWrapSettings(studioSettings);
                ApplyGitParitySettings();
                ApplyEditorNicetySettings();
                UpdateStatusBar();
            }
            return parseError;
        }

        private void OpenWorkspaceSettingsFile()
        {
            string path = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
            if (path == null) return;
            if (!System.IO.File.Exists(path))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, "{\n}");
                }
                catch { }
            }
            OpenFileInTab(path);
        }
    }
}
