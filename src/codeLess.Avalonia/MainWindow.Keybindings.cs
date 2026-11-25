using System;
using Avalonia.Input;
using codeLess.Core;
using System.Linq;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private KeybindingManager keybindings;
        private KeybindingsFileWatcher keybindingsWatcher;
        private Avalonia.Controls.Border keybindingsNotification;
        private WindowContextKeyService contextKeyService;

        private void InitializeKeybindings()
        {
            DefaultCommands.Register();
            keybindings = new KeybindingManager();

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
            // Add custom mappings not in the menu
            keybindings.BindAction("editor.action.moveLinesUpAction", () => PrimaryEditor?.Canvas?.MoveActiveLine(-1));
            keybindings.BindAction("editor.action.moveLinesDownAction", () => PrimaryEditor?.Canvas?.MoveActiveLine(1));
            keybindings.BindAction("editor.action.insertLineBefore", () => PrimaryEditor?.Canvas?.InsertLineAboveOrBelow(true));
            keybindings.BindAction("editor.action.insertLineAfter", () => PrimaryEditor?.Canvas?.InsertLineAboveOrBelow(false));
            keybindings.BindAction("editor.action.copyLinesUpAction", () => PrimaryEditor?.Canvas?.CopyActiveLine(-1));
            keybindings.BindAction("editor.action.copyLinesDownAction", () => PrimaryEditor?.Canvas?.CopyActiveLine(1));
            keybindings.BindAction("editor.action.addSelectionToNextFindMatch", () => PrimaryEditor?.Canvas?.AddNextOccurrenceOrSelectWord());
            keybindings.BindAction("editor.action.toggleWordWrap", () => PrimaryEditor?.Canvas?.ToggleWordWrap());
            keybindings.BindAction("editor.action.smartSelect.expand", () => PrimaryEditor?.Canvas?.ExpandSelection());
            keybindings.BindAction("editor.action.smartSelect.shrink", () => PrimaryEditor?.Canvas?.ShrinkSelection());
            keybindings.BindAction("editor.action.deleteLines", () => PrimaryEditor?.Canvas?.DeleteActiveLine());
            keybindings.BindAction("editor.action.indentLines", () => PrimaryEditor?.Canvas?.IndentOrOutdentLines(false));
            keybindings.BindAction("editor.action.outdentLines", () => PrimaryEditor?.Canvas?.IndentOrOutdentLines(true));
            keybindings.BindAction("editor.fold", () => PrimaryEditor?.Canvas?.FoldAtCaret());
            keybindings.BindAction("editor.unfold", () => PrimaryEditor?.Canvas?.UnfoldAtCaret());
            keybindings.BindAction("cursorBottom", null);
            keybindings.BindAction("cursorEnd", null);
            keybindings.BindAction("cursorHome", null);
            keybindings.BindAction("cursorTop", null);
            keybindings.BindAction("deleteWordLeft", null);
            keybindings.BindAction("deleteWordRight", null);
            keybindings.BindAction("editor.action.clipboardCopyAction", null);
            keybindings.BindAction("editor.action.clipboardCutAction", null);
            keybindings.BindAction("editor.action.clipboardPasteAction", null);
            keybindings.BindAction("editor.action.formatSelection", null);
            keybindings.BindAction("editor.action.goToDeclaration", null);
            keybindings.BindAction("editor.action.goToReferences", null);
            keybindings.BindAction("editor.action.nextMatchFindAction", null);
            keybindings.BindAction("editor.action.peekDefinition", null);
            keybindings.BindAction("editor.action.previousMatchFindAction", null);
            keybindings.BindAction("editor.action.quickFix", null);
            keybindings.BindAction("editor.action.referenceSearch.trigger", null);
            keybindings.BindAction("editor.action.revealDefinition", null);
            keybindings.BindAction("editor.action.revealDefinitionAside", null);
            keybindings.BindAction("editor.action.selectAllMatches", null);
            keybindings.BindAction("editor.action.startFindReplaceAction", null);
            keybindings.BindAction("editor.action.triggerParameterHints", null);
            keybindings.BindAction("editor.action.triggerSuggest", null);
            keybindings.BindAction("editor.debug.action.toggleBreakpoint", null);
            keybindings.BindAction("expandLineSelection", null);
            keybindings.BindAction("redo", null);
            keybindings.BindAction("undo", null);
            keybindings.BindAction("cursorUndo", null);
            keybindings.BindAction("editor.action.changeAll", null);
            keybindings.BindAction("editor.action.formatDocument", null);
            keybindings.BindAction("editor.action.insertCursorAbove", null);
            keybindings.BindAction("editor.action.insertCursorAtEndOfEachLineSelected", null);
            keybindings.BindAction("editor.action.insertCursorBelow", null);
            keybindings.BindAction("editor.action.marker.next", null);
            keybindings.BindAction("editor.action.marker.prev", null);
            keybindings.BindAction("editor.action.rename", null);
            keybindings.BindAction("editor.action.selectHighlights", null);
            keybindings.BindAction("editorScroll", null);
            keybindings.BindAction("workbench.action.closeAllEditors", null);
            keybindings.BindAction("workbench.action.debug.start", null);
            keybindings.BindAction("workbench.action.files.newUntitledFile", null);
            keybindings.BindAction("workbench.action.files.openFile", null);
            keybindings.BindAction("workbench.action.files.saveAll", null);
            keybindings.BindAction("workbench.action.files.saveAs", null);
            keybindings.BindAction("workbench.action.findInFiles", null);
            keybindings.BindAction("workbench.action.reopenClosedEditor", null);
            keybindings.BindAction("workbench.action.replaceInFiles", null);
            keybindings.BindAction("workbench.action.splitEditor", null);
            keybindings.BindAction("workbench.action.terminal.new", null);
            keybindings.BindAction("workbench.action.toggleFullScreen", null);
            keybindings.BindAction("workbench.action.zoomIn", null);
            keybindings.BindAction("workbench.action.zoomOut", null);
            keybindings.BindAction("workbench.action.zoomReset", null);
            keybindings.BindAction("workbench.action.closeOtherEditors", null);
            keybindings.BindAction("workbench.action.debug.stepInto", null);
            keybindings.BindAction("workbench.action.debug.stepOut", null);
            keybindings.BindAction("workbench.action.debug.stepOver", null);
            keybindings.BindAction("workbench.action.debug.stop", null);
            keybindings.BindAction("workbench.action.files.copyPathOfActiveFile", null);
            keybindings.BindAction("workbench.action.files.save", null);
            keybindings.BindAction("workbench.action.focusFirstEditorGroup", null);
            keybindings.BindAction("workbench.action.focusSecondEditorGroup", null);
            keybindings.BindAction("workbench.action.gotoLine", null);
            keybindings.BindAction("workbench.action.gotoSymbol", null);
            keybindings.BindAction("workbench.action.navigateBack", null);
            keybindings.BindAction("workbench.action.navigateForward", null);
            keybindings.BindAction("workbench.action.openRecent", null);
            keybindings.BindAction("workbench.action.quit", null);
            keybindings.BindAction("workbench.action.showAllSymbols", null);
            keybindings.BindAction("workbench.action.tasks.build", null);
            keybindings.BindAction("workbench.action.terminal.kill", null);
            keybindings.BindAction("workbench.action.terminal.split", null);
            keybindings.BindAction("workbench.action.togglePanel", null);
            keybindings.BindAction("workbench.actions.view.problems", null);
            keybindings.BindAction("workbench.view.explorer", null);
            keybindings.BindAction("workbench.view.extensions", null);
            keybindings.BindAction("workbench.view.scm", null);
            keybindings.BindAction("workbench.view.search", null);
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
            keybindings.BindAction("workbench.action.openSettingsJson", () => OpenFileInTab(VscodeSettingsManager.GetUserSettingsPath()));
            keybindings.BindAction("workbench.action.openWorkspaceSettingsFile", () => OpenWorkspaceSettingsFile());
            keybindings.BindAction("workbench.action.openGlobalKeybindings", () => ShowKeyboardShortcutsModal());
            keybindings.BindAction("workbench.action.openGlobalKeybindingsFile", () => OpenFileInTab(codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath()));
            keybindings.BindAction("workbench.action.openDefaultKeybindingsFile", () => OpenDefaultKeybindingsFile());
            keybindings.BindAction("workbench.action.terminal.toggleTerminal", () => ToggleBottomPanel()); // Simple mapping
            keybindings.BindAction("workbench.action.toggleSidebarVisibility", () => TogglePrimarySidebar());
            keybindings.BindAction("workbench.action.toggleZenMode", () => ToggleZenMode());
            keybindings.BindAction("actions.find", () => PrimaryEditor?.ShowFindBar(false));
            keybindings.BindAction("inlineChat.start", () => PrimaryEditor?.ShowInlineAiBar());
            
            keybindings.BindAction("workbench.action.toggleActivityBarVisibility", () => { if (activityBarBorder != null) activityBarBorder.IsVisible = !activityBarBorder.IsVisible; });
            keybindings.BindAction("editor.action.joinLines", () => PrimaryEditor?.Canvas?.JoinLines());
            keybindings.BindAction("editor.action.transformToUppercase", () => PrimaryEditor?.Canvas?.TransformToUppercase());
            keybindings.BindAction("editor.action.transformToLowercase", () => PrimaryEditor?.Canvas?.TransformToLowercase());
            keybindings.BindAction("editor.action.trimTrailingWhitespace", () => PrimaryEditor?.Canvas?.TrimTrailingWhitespace());
            keybindings.BindAction("workbench.action.files.revealActiveFileInWindows", () => { if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) RevealInOsFileManager(activeTab.FilePath); });
            keybindings.BindAction("workbench.action.showAllEditors", () => OpenCommandPalette("edt "));
            keybindings.BindAction("workbench.action.closeActiveEditor", () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); });
            keybindings.BindAction("workbench.action.nextEditor", () => CycleEditorTab(1));
            keybindings.BindAction("workbench.action.previousEditor", () => CycleEditorTab(-1));
            keybindings.BindAction("workbench.action.openNextRecentlyUsedEditorInGroup", () => OpenOrAdvanceEditorSwitcher(false));
            keybindings.BindAction("editor.action.commentLine", () => PrimaryEditor.Canvas.ToggleLineComment());
            keybindings.BindAction("workbench.action.files.openFolder", () => { _ = PromptOpenFolderDialogAsync(); });
            keybindings.BindAction("workbench.action.closeFolder", () => CloseWorkspaceFolder());
            keybindings.BindAction("workbench.action.files.saveAll", () => SaveAllTabs());
            keybindings.BindAction("workbench.action.selectTheme", () => OpenThemePickerPalette());
            keybindings.BindAction("workbench.action.closeAllEditors", () => CloseAllTabs());
            keybindings.BindAction("workbench.action.closeUnmodifiedEditors", () => { var saved = openTabs.Where(t => !t.IsDirty && !t.IsPinned).ToList(); foreach (var t in saved) CloseTab(t, forceWithoutPrompt: true); });
            keybindings.BindAction("workbench.action.keepEditor", () => { if (activeTab != null) { activeTab.IsPinned = true; activeTab.IsPreviewTab = false; RefreshTabStrip(); } });
            keybindings.BindAction("copyFilePath", () => { if (activeTab != null) _ = CopyPathToClipboardAsync(activeTab.FilePath ?? activeTab.FileName); });
            keybindings.BindAction("revealFileInOS", () => { if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) RevealInOsFileManager(activeTab.FilePath); });
            keybindings.BindAction("workbench.action.editor.changeLanguageMode", () => OpenLanguageModePicker());
            keybindings.BindAction("markdown.showPreviewToSide", () => OpenMarkdownPreview(toSide: true));
            keybindings.BindAction("editor.action.inlineAiBar", () => PrimaryEditor?.ShowInlineAiBar());


            contextKeyService = new WindowContextKeyService(this);
            InitializeVscodeSettingsWatcher();
            InitializeWorkspaceSettingsWatcher();

            string path = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            keybindingsWatcher = new KeybindingsFileWatcher(path, content => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    string err = keybindings.LoadUserBindings(content);
                    if (err != null) {
                        ShowKeybindingsNotification("Keybindings parse error: " + err);
                    } else {
                        HideKeybindingsNotification();
                    }
                });
            });
            // Initial load
            try { 
                string err = keybindings.LoadUserBindings(System.IO.File.ReadAllText(path)); 
                if (err != null) ShowKeybindingsNotification("Keybindings parse error: " + err);
            } catch { }

        }

        private bool DispatchKeybinding(Key key, KeyModifiers modifiers, bool isTerminalFocus)
        {
            if (keybindings == null) return false;
            string ks = KeyStringParser.FromKey((int)key, (int)modifiers);
                        return keybindings.Dispatch(ks, contextKeyService, msg => {
                if (!string.IsNullOrEmpty(msg))
                    statusChordLabel.Text = msg;
                else
                    statusChordLabel.Text = "";
                            }, isTerminalFocus);
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
            try {
                System.IO.File.WriteAllText(tempPath, sb.ToString());
            } catch { }
            var tab = OpenFileInTab(tempPath, preview: false);
            if (tab != null) {
                tab.IsReadOnly = true;
                tab.RelativePath = "Default Keybindings";
                tab.FileName = "Default Keybindings";
            }
        }

                private System.IO.FileSystemWatcher workspaceSettingsWatcher;
        
        private void InitializeWorkspaceSettingsWatcher()
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !System.IO.Directory.Exists(workspaceRoot)) return;
            string path = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
            
            workspaceSettingsWatcher = new System.IO.FileSystemWatcher(workspaceRoot);
            workspaceSettingsWatcher.IncludeSubdirectories = true;
            workspaceSettingsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName;
            System.Threading.Timer t = null;
            System.IO.FileSystemEventHandler handler = (s, e) => {
                if (e != null && !(e.FullPath.EndsWith("settings.json") || e.FullPath.EndsWith(".vscode"))) return;
                if (t != null) { t.Dispose(); t = null; }
                t = new System.Threading.Timer(_ => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        try {
                            var clone = codeLess.Studio.StudioSettings.Load(); // load base
                            string userPath = VscodeSettingsManager.GetUserSettingsPath();
                            if (System.IO.File.Exists(userPath)) VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(userPath), clone);
                            if (System.IO.File.Exists(path)) {
                                string content = System.IO.File.ReadAllText(path);
                                string err = VscodeSettingsManager.ApplySettings(content, clone);
                                if (err != null) ShowKeybindingsNotification("Workspace settings parse error: " + err);
                                else HideKeybindingsNotification();
                            }
                            ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                        } catch { }
                    });
                }, null, 100, System.Threading.Timeout.Infinite);
            };
            workspaceSettingsWatcher.Changed += handler;
            workspaceSettingsWatcher.Created += handler;
            workspaceSettingsWatcher.Renamed += (s, e) => handler(s, null);
            workspaceSettingsWatcher.EnableRaisingEvents = true;
            
            try { 
                if (System.IO.File.Exists(path)) {
                    var clone = codeLess.Studio.StudioSettings.Load();
                    string userPath = VscodeSettingsManager.GetUserSettingsPath();
                    if (System.IO.File.Exists(userPath)) VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(userPath), clone);
                    string err = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(path), clone);
                    if (err != null) ShowKeybindingsNotification("Workspace settings parse error: " + err);
                    ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                }
            } catch { }
        }

        private System.IO.FileSystemWatcher userSettingsWatcher;
        
        private void InitializeVscodeSettingsWatcher()
        {
            string path = VscodeSettingsManager.GetUserSettingsPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            if (!System.IO.File.Exists(path)) {
                try { System.IO.File.WriteAllText(path, "{\n}"); } catch { }
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            userSettingsWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileName(path));
            userSettingsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName;
            System.Threading.Timer t = null;
            System.IO.FileSystemEventHandler handler = (s, e) => {
                if (t != null) { t.Dispose(); t = null; }
                t = new System.Threading.Timer(_ => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        try {
                            string content = System.IO.File.ReadAllText(path);
                            var clone = codeLess.Studio.StudioSettings.Load();
                            string err = VscodeSettingsManager.ApplySettings(content, clone);
                            
                            string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
                            if (wsPath != null && System.IO.File.Exists(wsPath)) {
                                string wsErr = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(wsPath), clone);
                                if (err == null) err = wsErr;
                            }
                            ApplyVscodeSettings("", clone as codeLess.Studio.StudioSettings);
                            if (err != null) ShowKeybindingsNotification("Settings parse error: " + err);
                            else HideKeybindingsNotification();
                        } catch { }
                    });
                }, null, 100, System.Threading.Timeout.Infinite);
            };
            userSettingsWatcher.Changed += handler;
            userSettingsWatcher.Created += handler;
            userSettingsWatcher.Renamed += (s, e) => handler(s, null);
            userSettingsWatcher.EnableRaisingEvents = true;
            
            try { string err = ApplyVscodeSettings(System.IO.File.ReadAllText(path)); if (err != null) ShowKeybindingsNotification("Settings parse error: " + err); } catch { }
        }

        private string ApplyVscodeSettings(string content, StudioSettings targetSettings = null)
        {
            var target = targetSettings ?? (settings as StudioSettings);
            string parseError = null;
            if (!string.IsNullOrWhiteSpace(content)) {
                parseError = VscodeSettingsManager.ApplySettings(content, target);
                
                if (targetSettings == null && !string.IsNullOrEmpty(workspaceRoot))
                {
                    string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
                    if (wsPath != null && System.IO.File.Exists(wsPath))
                    {
                        try {
                            string wsErr = VscodeSettingsManager.ApplySettings(System.IO.File.ReadAllText(wsPath), target);
                            if (parseError == null) parseError = wsErr;
                        } catch { }
                    }
                }
            }
            // No save here
            
            var studioSettings = target;
            if (studioSettings != null)
            {
                ApplyTheme(StudioTheme.GetById(studioSettings.ActiveThemeId), persistSetting: targetSettings == null);
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
                try {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, "{\n}");
                } catch { }
            }
            OpenFileInTab(path);
        }
    }
}


