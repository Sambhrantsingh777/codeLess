using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Source is TextBox &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Meta) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
                e.Key != Key.Escape &&
                e.Key != Key.F1 && e.Key != Key.F3 && e.Key != Key.F5 &&
                e.Key != Key.F8 && e.Key != Key.F9 && e.Key != Key.F11 && e.Key != Key.F12)
            {
                if (lastCtrlKChordTime != DateTime.MinValue)
                {
                    lastCtrlKChordTime = DateTime.MinValue;
                    statusChordLabel.Text = "";
                }
                return;
            }

            if (HandleWorkbenchKeyEvent(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
        }

        public bool SimulateShortcut(Key key, KeyModifiers modifiers)
        {
            if (HandleWorkbenchKeyEvent(key, modifiers))
                return true;

            // Route to active editor canvas if not consumed at window level
            var ev = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers
            };
            PrimaryEditor.Canvas.RaiseEvent(ev);
            return ev.Handled;
        }

        private bool HandleWorkbenchKeyEvent(Key key, KeyModifiers modifiers)
        {
            bool ctrl = modifiers.HasFlag(KeyModifiers.Control);
            bool meta = modifiers.HasFlag(KeyModifiers.Meta);
            bool ctrlOrCmd = ctrl || meta;
            bool shift = modifiers.HasFlag(KeyModifiers.Shift);
            bool alt = modifiers.HasFlag(KeyModifiers.Alt);

            // Step 1: Check active Ctrl+K / Cmd+K two-step chord window (3.5 seconds)
            if ((DateTime.UtcNow - lastCtrlKChordTime).TotalMilliseconds < 3500)
            {
                lastCtrlKChordTime = DateTime.MinValue;
                statusChordLabel.Text = "";

                if (key == Key.O && !shift)
                {
                    _ = PromptOpenFolderDialogAsync();
                    return true;
                }
                if (key == Key.S && ctrlOrCmd && !shift)
                {
                    ShowKeyboardShortcutsModal();
                    return true;
                }
                if (key == Key.S && !ctrlOrCmd && !shift)
                {
                    SaveAllTabs();
                    return true;
                }
                if (key == Key.T && ctrlOrCmd)
                {
                    OpenThemePickerPalette();
                    return true;
                }
                if (key == Key.F && !ctrlOrCmd)
                {
                    CloseWorkspaceFolder();
                    return true;
                }
                if (key == Key.W && !ctrlOrCmd)
                {
                    CloseAllTabs();
                    return true;
                }
                if (key == Key.U && !ctrlOrCmd)
                {
                    var saved = openTabs.Where(t => !t.IsDirty && !t.IsPinned).ToList();
                    foreach (var t in saved) CloseTab(t, forceWithoutPrompt: true);
                    return true;
                }
                if (key == Key.Enter)
                {
                    if (activeTab != null)
                    {
                        if (shift) activeTab.IsPinned = !activeTab.IsPinned;
                        activeTab.IsPreviewTab = false;
                        RefreshTabStrip();
                    }
                    return true;
                }
                if (key == Key.P && !ctrlOrCmd)
                {
                    if (activeTab != null) _ = CopyPathToClipboardAsync(activeTab.FilePath ?? activeTab.FileName);
                    return true;
                }
                if (key == Key.R && !ctrlOrCmd)
                {
                    if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
                        RevealInOsFileManager(activeTab.FilePath);
                    return true;
                }
                if (key == Key.Z && !ctrlOrCmd)
                {
                    ToggleZenMode();
                    return true;
                }
                if (key == Key.M && !ctrlOrCmd)
                {
                    OpenLanguageModePicker();
                    return true;
                }
                if (key == Key.C && ctrlOrCmd && shift)
                {
                    if (activeTab != null) _ = CopyPathToClipboardAsync(activeTab.RelativePath ?? activeTab.FileName);
                    return true;
                }
                if ((key == Key.C || key == Key.U) && ctrlOrCmd && !shift)
                {
                    PrimaryEditor.Canvas.ToggleLineComment();
                    return true;
                }
                if ((key == Key.D0 || key == Key.NumPad0) && ctrlOrCmd)
                {
                    PrimaryEditor.Canvas.FoldAll();
                    return true;
                }
                if (key == Key.J && ctrlOrCmd)
                {
                    PrimaryEditor.Canvas.UnfoldAll();
                    return true;
                }
                if (key == Key.D && ctrlOrCmd)
                {
                    PrimaryEditor.Canvas.SkipAndAddNextOccurrence();
                    return true;
                }
                if (key == Key.OemOpenBrackets && ctrlOrCmd)
                {
                    if (activeTab != null) PrimaryEditor.Canvas.FoldAtLine(activeTab.CursorLine);
                    return true;
                }
                if (key == Key.OemCloseBrackets && ctrlOrCmd)
                {
                    if (activeTab != null) PrimaryEditor.Canvas.UnfoldAtLine(activeTab.CursorLine);
                    return true;
                }
                if (key == Key.K && ctrlOrCmd)
                {
                    // Double Ctrl+K / Cmd+K opens inline AI edit bar in editor
                    PrimaryEditor.ShowInlineAiBar();
                    return true;
                }
            }

            // Enter Ctrl+K / Cmd+K chord state (unless Shift or Alt is held)
            if (ctrlOrCmd && !shift && !alt && key == Key.K)
            {
                lastCtrlKChordTime = DateTime.UtcNow;
                string prefixName = meta ? "Cmd+K" : "Ctrl+K";
                statusChordLabel.Text = $"({prefixName}) was pressed. Waiting for second key of chord...";
                // Also toggle inline AI bar if user doesn't press a second chord key, or allow immediate access
                PrimaryEditor.ShowInlineAiBar();
                return true;
            }

            // Escape closes open modal or command palette or Zen mode
            if (key == Key.Escape)
            {
                if (ModalHost.IsVisible) { ModalHost.CloseModal(); return true; }
                if (CommandPalette.IsVisible) { CommandPalette.Close(restorePreviewTheme: true); return true; }
                if (isZenMode && shift) { ToggleZenMode(); return true; }
                return false;
            }

            // Function Keys
            if (key == Key.F1 && !ctrlOrCmd && !alt && !shift)
            {
                OpenCommandPalette(">");
                return true;
            }
            if (key == Key.F5)
            {
                RunActiveFileInTerminal();
                return true;
            }
            if (key == Key.F8)
            {
                JumpDiagnostic(shift ? -1 : 1);
                return true;
            }
            if (key == Key.F9 && !ctrlOrCmd && !alt && !shift)
            {
                SetStatusMessage(activeTab != null ? $"Toggled breakpoint at {activeTab.FileName}:{activeTab.CursorLine + 1}" : "No active editor for breakpoint");
                return true;
            }
            if (key == Key.F11 && !ctrlOrCmd && !alt && !shift)
            {
                ToggleFullScreenMode();
                return true;
            }
            if (key == Key.F12 && !ctrlOrCmd && !shift)
            {
                GoToDefinitionAtCursor();
                return true;
            }

            // Shift+Alt+0 -> Toggle Editor Split Orientation
            if (shift && alt && !ctrlOrCmd && (key == Key.D0 || key == Key.NumPad0))
            {
                ToggleSplitEditor();
                return true;
            }

            // Ctrl+` (always Ctrl+` on both Windows and macOS per VS Code keymap, plus Cmd+` fallback)
            if ((ctrl || meta) && (key == Key.OemTilde || key == Key.DeadCharProcessed))
            {
                if (shift)
                {
                    CreateNewTerminalSession();
                }
                else
                {
                    if (!bottomPanelBorder.IsVisible)
                    {
                        ToggleBottomPanel();
                        SwitchBottomTab("terminal");
                    }
                    else if (activeBottomTab != "terminal")
                    {
                        SwitchBottomTab("terminal");
                    }
                    else
                    {
                        ToggleBottomPanel();
                    }
                }
                return true;
            }

            // Ctrl+Shift+5 -> Split Terminal
            if (ctrlOrCmd && shift && !alt && key == Key.D5)
            {
                ToggleSplitTerminal();
                return true;
            }

            // Navigation Back / Forward (Alt+Left / Alt+Right on Windows; Ctrl+- / Ctrl+Shift+- on Mac)
            if ((alt && !ctrlOrCmd && !shift && key == Key.Left) || (MacKeymap.IsMacOS && ctrl && !meta && !shift && key == Key.OemMinus))
            {
                NavigateBack();
                return true;
            }
            if ((alt && !ctrlOrCmd && !shift && key == Key.Right) || (MacKeymap.IsMacOS && ctrl && !meta && shift && key == Key.OemMinus))
            {
                NavigateForward();
                return true;
            }

            // Ctrl/Cmd + Tab or Ctrl/Cmd + PageDown/PageUp or Cmd+Option+Left/Right -> Switch Tabs
            if ((ctrl && key == Key.Tab) ||
                (ctrlOrCmd && (key == Key.PageDown || key == Key.PageUp)) ||
                (meta && alt && (key == Key.Right || key == Key.Left)))
            {
                if (openTabs.Count > 1 && activeTab != null)
                {
                    int curIdx = openTabs.IndexOf(activeTab);
                    bool backwards = shift || key == Key.PageUp || (meta && alt && key == Key.Left);
                    int nextIdx = (curIdx + (backwards ? -1 : 1) + openTabs.Count) % openTabs.Count;
                    ActivateTab(openTabs[nextIdx]);
                }
                return true;
            }

            // Ctrl/Cmd + 1..9 -> Activate tab by 1-based index
            if ((ctrlOrCmd || alt) && !shift && key >= Key.D1 && key <= Key.D9)
            {
                int tabIndex = (int)(key - Key.D1);
                if (tabIndex >= 0 && tabIndex < openTabs.Count)
                {
                    ActivateTab(openTabs[tabIndex]);
                    return true;
                }
            }

            if (!ctrlOrCmd) return false;

            // Workbench Ctrl / Cmd Shortcuts
            if (shift && !alt)
            {
                switch (key)
                {
                    case Key.P: OpenCommandPalette(">"); return true;
                    case Key.O: OpenCommandPalette("@"); return true;
                    case Key.E: SwitchSidebarView("explorer"); return true;
                    case Key.F: SwitchSidebarView("search"); return true;
                    case Key.H: SwitchSidebarView("search"); return true;
                    case Key.G: SwitchSidebarView("git"); return true;
                    case Key.D:
                        // Note: if editor canvas has focus, Ctrl+Shift+D duplicates line; otherwise opens Run & Debug
                        if (PrimaryEditor.Canvas.IsFocused)
                        {
                            PrimaryEditor.Canvas.CopyActiveLine(1);
                            return true;
                        }
                        SwitchSidebarView("run");
                        return true;
                    case Key.X: SwitchSidebarView("extensions"); return true;
                    case Key.A: ToggleRightChatDock(); return true;
                    case Key.M: SwitchBottomTab("problems"); return true;
                    case Key.U: SwitchBottomTab("output"); return true;
                    case Key.Y:
                        btmDebugConsoleTabBtn.IsVisible = true;
                        SwitchBottomTab("debug");
                        return true;
                    case Key.T: ReopenClosedTab(); return true;
                    case Key.N: CreateNewWindowInstance(); return true;
                    case Key.W: Close(); return true;
                    case Key.S: _ = PromptSaveAsAsync(); return true;
                    case Key.B: RunWorkspaceBuildInTerminal(); return true;
                }
            }

            // Ctrl/Cmd + Alt Shortcuts
            if (alt && !shift)
            {
                switch (key)
                {
                    case Key.I:
                        ToggleRightChatDock();
                        if (rightChatDockBorder.IsVisible)
                            chatPromptInput.Focus();
                        return true;
                    case Key.N:
                        PromptCreateNewFileInWorkspace();
                        return true;
                    case Key.S:
                        SaveAllTabs();
                        return true;
                }
            }

            // Single modifier Ctrl / Cmd Shortcuts
            if (!shift && !alt)
            {
                switch (key)
                {
                    case Key.P:
                    case Key.E:
                        OpenQuickOpen();
                        return true;
                    case Key.T:
                        OpenCommandPalette("#");
                        return true;
                    case Key.G:
                        OpenCommandPalette(":");
                        return true;
                    case Key.B:
                        TogglePrimarySidebar();
                        return true;
                    case Key.J:
                        ToggleBottomPanel();
                        return true;
                    case Key.N:
                        CreateNewUntitledTab();
                        return true;
                    case Key.O:
                        _ = PromptOpenFileDialogAsync();
                        return true;
                    case Key.S:
                        SaveActiveTab();
                        return true;
                    case Key.W:
                        if (activeTab != null) CloseTab(activeTab);
                        else if (showWelcomeTab) CloseWelcomeTab();
                        return true;
                    case Key.OemComma:
                        ShowSettingsModal();
                        return true;
                    case Key.OemPipe:
                    case Key.OemBackslash:
                        ToggleSplitEditor();
                        return true;
                    case Key.OemPlus:
                    case Key.Add:
                        AdjustZoom(1);
                        return true;
                    case Key.OemMinus:
                    case Key.Subtract:
                        AdjustZoom(-1);
                        return true;
                    case Key.D0:
                    case Key.NumPad0:
                        AdjustZoom(0);
                        return true;
                }
            }

            return false;
        }

        public void SwitchSidebarView(string viewId)
        {
            if (string.IsNullOrEmpty(viewId)) return;
            activeSidebarView = viewId.ToLowerInvariant();
            if (!sidebarBorder.IsVisible)
            {
                sidebarBorder.IsVisible = true;
                workbenchCenterGrid.ColumnDefinitions[1].Width = new GridLength(260);
            }

            explorerPanelView.IsVisible = activeSidebarView == "explorer";
            searchPanelView.IsVisible = activeSidebarView == "search";
            scmPanelView.IsVisible = activeSidebarView == "git";
            runDebugPanelView.IsVisible = activeSidebarView == "run";
            extensionsPanelView.IsVisible = activeSidebarView == "extensions";
            harnessPanelView.IsVisible = activeSidebarView == "harness";

            sidebarHeaderActions.IsVisible = activeSidebarView == "explorer";
            switch (activeSidebarView)
            {
                case "explorer": sidebarHeaderTitle.Text = "EXPLORER"; break;
                case "search":
                    sidebarHeaderTitle.Text = "SEARCH";
                    workspaceSearchInput.Focus();
                    break;
                case "git":
                    sidebarHeaderTitle.Text = "SOURCE CONTROL";
                    RefreshGitStatus();
                    break;
                case "run": sidebarHeaderTitle.Text = "RUN AND DEBUG"; break;
                case "extensions": sidebarHeaderTitle.Text = "EXTENSIONS"; break;
                case "harness": sidebarHeaderTitle.Text = "AI HARNESS & BYOK"; break;
            }
            UpdateActivityBarSelectionVisuals();
        }

        public void TogglePrimarySidebar()
        {
            sidebarBorder.IsVisible = !sidebarBorder.IsVisible;
            workbenchCenterGrid.ColumnDefinitions[1].Width = sidebarBorder.IsVisible
                ? new GridLength(260)
                : new GridLength(0);
            UpdateActivityBarSelectionVisuals();
        }

        public void ToggleBottomPanel()
        {
            var centerGrid = bottomPanelBorder.Parent as Grid;
            if (centerGrid == null) return;
            bottomPanelBorder.IsVisible = !bottomPanelBorder.IsVisible;
            centerGrid.RowDefinitions[3].Height = bottomPanelBorder.IsVisible
                ? new GridLength(savedBottomDockHeight)
                : new GridLength(0);
            if (bottomPanelBorder.IsVisible && activeBottomTab == "terminal")
                activeTerminalSession?.Control?.FocusTerminalCanvas();
        }

        public void ToggleMaximizeBottomPanel()
        {
            var centerGrid = bottomPanelBorder.Parent as Grid;
            if (centerGrid == null) return;
            if (!bottomPanelBorder.IsVisible)
                ToggleBottomPanel();
            isBottomDockMaximized = !isBottomDockMaximized;
            centerGrid.RowDefinitions[3].Height = isBottomDockMaximized
                ? new GridLength(Math.Max(380, Bounds.Height * 0.62))
                : new GridLength(savedBottomDockHeight);
        }

        public void SwitchBottomTab(string tabId)
        {
            if (string.IsNullOrEmpty(tabId)) return;
            activeBottomTab = tabId.ToLowerInvariant();
            if (!bottomPanelBorder.IsVisible)
                ToggleBottomPanel();

            problemsListBox.IsVisible = activeBottomTab == "problems";
            outputStreamBox.IsVisible = activeBottomTab == "output" || activeBottomTab == "debug" || activeBottomTab == "ports";
            terminalHostGrid.IsVisible = activeBottomTab == "terminal";

            outputChannelCombo.IsVisible = activeBottomTab == "output";
            shellProfileSelector.IsVisible = activeBottomTab == "terminal";
            RefreshBottomTabStyles();

            if (activeBottomTab == "terminal")
                activeTerminalSession?.Control?.FocusTerminalCanvas();
        }

        private void RefreshBottomTabStyles()
        {
            if (currentTheme == null) return;
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            void StyleBtn(Button b, string id)
            {
                if (b == null) return;
                bool active = string.Equals(activeBottomTab, id, StringComparison.OrdinalIgnoreCase);
                b.Foreground = new SolidColorBrush(active ? textPrimary : textSecondary);
                b.BorderBrush = active ? new SolidColorBrush(accent) : Brushes.Transparent;
            }
            StyleBtn(btmProblemsTabBtn, "problems");
            StyleBtn(btmOutputTabBtn, "output");
            StyleBtn(btmTerminalTabBtn, "terminal");
            StyleBtn(btmDebugConsoleTabBtn, "debug");
            StyleBtn(btmPortsTabBtn, "ports");
        }

        public void ToggleRightChatDock()
        {
            rightChatDockBorder.IsVisible = !rightChatDockBorder.IsVisible;
            workbenchCenterGrid.ColumnDefinitions[5].Width = rightChatDockBorder.IsVisible
                ? new GridLength(385)
                : new GridLength(0);
            if (rightChatDockBorder.IsVisible)
                chatPromptInput.Focus();
        }

        public void ToggleSplitEditor()
        {
            isEditorSplit = !isEditorSplit;
            splitEditorBorder.IsVisible = isEditorSplit;
            editorSplitGrid.ColumnDefinitions[1].Width = isEditorSplit
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            if (isEditorSplit)
            {
                var targetBuf = activeTab ?? openTabs.FirstOrDefault();
                SplitEditor.LoadBuffer(targetBuf);
            }
        }

        public void ToggleZenMode()
        {
            isZenMode = !isZenMode;
            topMenuBarBorder.IsVisible = !isZenMode;
            activityBarBorder.IsVisible = !isZenMode;
            statusBarBorder.IsVisible = !isZenMode;
            if (isZenMode)
            {
                if (sidebarBorder.IsVisible) TogglePrimarySidebar();
                if (bottomPanelBorder.IsVisible) ToggleBottomPanel();
                if (rightChatDockBorder.IsVisible) ToggleRightChatDock();
            }
            else
            {
                if (!sidebarBorder.IsVisible) TogglePrimarySidebar();
                if (!bottomPanelBorder.IsVisible) ToggleBottomPanel();
                if (!rightChatDockBorder.IsVisible) ToggleRightChatDock();
            }
        }

        public void ToggleFullScreenMode()
        {
            isFullScreen = !isFullScreen;
            WindowState = isFullScreen ? WindowState.FullScreen : WindowState.Normal;
        }

        public void AdjustZoom(int delta)
        {
            if (delta == 0)
            {
                PrimaryEditor.Canvas.FontSizePx = 13.0;
                SplitEditor.Canvas.FontSizePx = 13.0;
            }
            else
            {
                double next = Math.Max(9.0, Math.Min(28.0, PrimaryEditor.Canvas.FontSizePx + delta));
                PrimaryEditor.Canvas.FontSizePx = next;
                SplitEditor.Canvas.FontSizePx = next;
            }
            settings.FontSize = (float)PrimaryEditor.Canvas.FontSizePx;
            PrimaryEditor.Canvas.InvalidateVisual();
            SplitEditor.Canvas.InvalidateVisual();
        }

        private void RecordNavigationHistory(string filePath, int line)
        {
            if (suppressNavRecord || string.IsNullOrEmpty(filePath)) return;
            if (navHistoryIndex >= 0 && navHistoryIndex < navHistory.Count)
            {
                var cur = navHistory[navHistoryIndex];
                if (string.Equals(cur.FilePath, filePath, StringComparison.OrdinalIgnoreCase) && cur.Line == line)
                    return;
            }
            if (navHistoryIndex < navHistory.Count - 1)
                navHistory.RemoveRange(navHistoryIndex + 1, navHistory.Count - navHistoryIndex - 1);
            navHistory.Add((filePath, line));
            navHistoryIndex = navHistory.Count - 1;
        }

        public void NavigateBack()
        {
            if (navHistoryIndex <= 0) return;
            navHistoryIndex--;
            var target = navHistory[navHistoryIndex];
            suppressNavRecord = true;
            try
            {
                OpenFileInTab(target.FilePath, preview: false);
                PrimaryEditor.GoToLine(target.Line);
            }
            finally { suppressNavRecord = false; }
        }

        public void NavigateForward()
        {
            if (navHistoryIndex >= navHistory.Count - 1) return;
            navHistoryIndex++;
            var target = navHistory[navHistoryIndex];
            suppressNavRecord = true;
            try
            {
                OpenFileInTab(target.FilePath, preview: false);
                PrimaryEditor.GoToLine(target.Line);
            }
            finally { suppressNavRecord = false; }
        }

        public void GoToDefinitionAtCursor()
        {
            if (activeTab == null) return;
            PrimaryEditor.Canvas.SelectWordAt(activeTab.CursorLine, activeTab.CursorCol);
            string word = activeTab.GetSelectedText();
            if (string.IsNullOrEmpty(word)) return;

            var localSym = activeTab.Symbols?.FirstOrDefault(s => string.Equals(s.Name, word, StringComparison.Ordinal));
            if (localSym != null)
            {
                PrimaryEditor.GoToLine(localSym.LineNumber);
                return;
            }

            var wsSym = aiEngine?.WorkspaceSymbolMap?.FirstOrDefault(s => string.Equals(s.SymbolName, word, StringComparison.Ordinal));
            if (wsSym != null)
            {
                OpenFileInTab(wsSym.FilePath, preview: false);
                PrimaryEditor.GoToLine(wsSym.LineNumber);
            }
        }

        public void JumpDiagnostic(int direction)
        {
            if (activeTab?.Diagnostics == null || activeTab.Diagnostics.Count == 0) return;
            int curLine = activeTab.CursorLine + 1;
            var target = direction >= 0
                ? activeTab.Diagnostics.FirstOrDefault(d => d.LineNumber > curLine) ?? activeTab.Diagnostics[0]
                : activeTab.Diagnostics.LastOrDefault(d => d.LineNumber < curLine) ?? activeTab.Diagnostics[activeTab.Diagnostics.Count - 1];
            PrimaryEditor.GoToLine(target.LineNumber);
        }

        private void CreateNewWindowInstance()
        {
            try
            {
                var w = new MainWindow(workspaceRoot);
                w.Show();
            }
            catch { }
        }

        public void CloseAllTabs()
        {
            if (!ConfirmSaveOrDiscardDirtyTabs(openTabs)) return;
            openTabs.Clear();
            ActivateTab(null);
        }

        public void CloseWorkspaceFolder()
        {
            if (!ConfirmSaveOrDiscardDirtyTabs(openTabs)) return;
            workspaceRoot = "";
            workspaceFiles.Clear();
            ExplorerTree.Nodes.Clear();
            ExplorerTree.RebuildTreeLayout(false);
            openTabs.Clear();
            ActivateTab(null);
            UpdateWindowTitle();
            UpdateStatusBar();
        }

        // Explorer Context Menu & File Operations
        private void OpenExplorerNodeContextMenu(ExplorerTreeNode node, Point pt)
        {
            if (node == null) return;
            string fullPath = node.Tag ?? "";
            bool isDir = node.IsDirectory || Directory.Exists(fullPath);
            string revealLabel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Reveal in Finder" : "Reveal in File Explorer";
            string scEnter = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+Enter");
            string scCopyRel = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+K Ctrl+Shift+C");

            var items = new List<object>();
            if (!isDir)
            {
                items.Add(MakeCtxItem("Open", () => OpenFileInTab(fullPath, preview: false)));
                items.Add(MakeCtxItem($"Open to the Side   ({scEnter})", () =>
                {
                    OpenFileInTab(fullPath, preview: false);
                    if (!isEditorSplit) ToggleSplitEditor();
                }));
                items.Add(new Separator());
            }

            items.Add(MakeCtxItem("New File...", PromptCreateNewFileInWorkspace));
            items.Add(MakeCtxItem("New Folder...", PromptCreateNewFolderInWorkspace));
            items.Add(new Separator());
            items.Add(MakeCtxItem(revealLabel, () => RevealInOsFileManager(fullPath)));
            items.Add(MakeCtxItem("Open in Integrated Terminal", () =>
            {
                string dir = isDir ? fullPath : Path.GetDirectoryName(fullPath);
                CreateNewTerminalSession(null, dir);
            }));
            items.Add(new Separator());

            if (!isDir)
            {
                items.Add(MakeCtxItem("Select for Compare", () =>
                {
                    selectedForComparePath = fullPath;
                    SetStatusMessage($"Selected '{Path.GetFileName(fullPath)}' for compare.");
                }));
                if (!string.IsNullOrEmpty(selectedForComparePath) && File.Exists(selectedForComparePath))
                {
                    items.Add(MakeCtxItem($"Compare with '{Path.GetFileName(selectedForComparePath)}'", () =>
                    {
                        var tab = OpenFileInTab(fullPath, preview: false);
                        if (tab != null)
                        {
                            tab.GitHeadContent = File.ReadAllText(selectedForComparePath);
                            tab.RefreshGutterDiff();
                            PrimaryEditor.Canvas.InvalidateVisual();
                        }
                    }));
                }
            }
            items.Add(MakeCtxItem("Find in Folder...", () => SwitchSidebarView("search")));
            items.Add(new Separator());
            items.Add(MakeCtxItem("Copy Path   (Shift+Alt+C)", () => _ = CopyPathToClipboardAsync(fullPath)));
            items.Add(MakeCtxItem($"Copy Relative Path   ({scCopyRel})", () =>
            {
                string rel = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fullPath) : Path.GetFileName(fullPath);
                _ = CopyPathToClipboardAsync(rel);
            }));
            items.Add(new Separator());
            items.Add(MakeCtxItem("Rename...   (F2)", () => PromptRenameExplorerNode(node)));
            items.Add(MakeCtxItem("Delete   (Move to Trash)", () => DeleteExplorerNodeToTrash(node)));

            var menu = new ContextMenu { ItemsSource = items };
            menu.Open(ExplorerTree);
        }

        private void OpenTabContextMenu(EditorTabBuffer tab, Control anchor)
        {
            if (tab == null) return;
            string revealLabel = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Reveal in Finder" : "Reveal in File Explorer";
            string scW = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+W");
            string scSplit = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+\\");

            var menu = new ContextMenu
            {
                ItemsSource = new object[]
                {
                    MakeCtxItem($"Close   ({scW})", () => CloseTab(tab)),
                    MakeCtxItem("Close Others", () =>
                    {
                        foreach (var other in openTabs.Where(t => !ReferenceEquals(t, tab) && !t.IsPinned).ToList())
                            CloseTab(other);
                    }),
                    MakeCtxItem("Close to the Right", () =>
                    {
                        int idx = openTabs.IndexOf(tab);
                        if (idx >= 0)
                        {
                            foreach (var right in openTabs.Skip(idx + 1).Where(t => !t.IsPinned).ToList())
                                CloseTab(right);
                        }
                    }),
                    MakeCtxItem("Close Saved", () =>
                    {
                        foreach (var saved in openTabs.Where(t => !t.IsDirty && !t.IsPinned).ToList())
                            CloseTab(saved);
                    }),
                    MakeCtxItem("Close All", CloseAllTabs),
                    new Separator(),
                    MakeCtxItem("Copy Path", () => _ = CopyPathToClipboardAsync(tab.FilePath ?? tab.FileName)),
                    MakeCtxItem("Copy Relative Path", () => _ = CopyPathToClipboardAsync(tab.RelativePath ?? tab.FileName)),
                    MakeCtxItem(revealLabel, () => { if (!string.IsNullOrEmpty(tab.FilePath)) RevealInOsFileManager(tab.FilePath); }),
                    new Separator(),
                    MakeCtxItem(tab.IsPinned ? "Unpin Tab" : "Pin Tab", () => { tab.IsPinned = !tab.IsPinned; tab.IsPreviewTab = false; RefreshTabStrip(); }),
                    MakeCtxItem("Keep Open", () => { tab.IsPreviewTab = false; RefreshTabStrip(); }),
                    new Separator(),
                    MakeCtxItem($"Split Right   ({scSplit})", ToggleSplitEditor)
                }
            };
            menu.Open(anchor);
        }

        private void OpenEditorContextMenu(EditorTabBuffer tab, Point pt)
        {
            if (tab == null) return;
            string scK = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+K");
            string scF = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+F");
            string scSym = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+Shift+O");
            string scPal = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+Shift+P");

            var menu = new ContextMenu
            {
                ItemsSource = new object[]
                {
                    MakeCtxItem($"✦ Inline AI Edit / Generate...   ({scK})", () => PrimaryEditor.ShowInlineAiBar()),
                    MakeCtxItem("✦ Explain Selection in AI Chat", () =>
                    {
                        if (!rightChatDockBorder.IsVisible) ToggleRightChatDock();
                        string sel = tab.HasSelection() ? tab.GetSelectedText() : $"Explain {tab.FileName}";
                        chatPromptInput.Text = "Explain this code:\n" + sel;
                        SubmitChatMessage();
                    }),
                    new Separator(),
                    MakeCtxItem("Go to Definition   (F12)", GoToDefinitionAtCursor),
                    MakeCtxItem($"Go to Symbol in Editor...   ({scSym})", () => OpenCommandPalette("@")),
                    MakeCtxItem("Rename Symbol   (F2)", () => PrimaryEditor.ShowFindBar(true)),
                    MakeCtxItem("Change All Occurrences   (Ctrl+Shift+L)", () => PrimaryEditor.Canvas.SelectAllFindOccurrences()),
                    new Separator(),
                    MakeCtxItem($"Find   ({scF})", () => PrimaryEditor.ShowFindBar(false)),
                    MakeCtxItem("Toggle Line Comment", () => PrimaryEditor.Canvas.ToggleLineComment()),
                    new Separator(),
                    MakeCtxItem("Select All", () => { tab.SelectAll(); PrimaryEditor.Canvas.InvalidateVisual(); }),
                    MakeCtxItem($"Command Palette...   ({scPal})", () => OpenCommandPalette(">"))
                }
            };
            menu.Open(PrimaryEditor.Canvas);
        }

        private async Task CopyPathToClipboardAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Clipboard != null)
                await Clipboard.SetTextAsync(text);
            SetStatusMessage("Copied to clipboard: " + text);
        }

        public static void RevealInOsFileManager(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    string targetDir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{targetDir}\"") { UseShellExecute = false });
                }
            }
            catch { }
        }

        public void PromptCreateNewFileInWorkspace()
        {
            string baseDir = GetSelectedExplorerDirectory();
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Enter new file name in '{Path.GetFileName(baseDir)}':",
                "NewClass.cs",
                currentTheme,
                fileName =>
                {
                    ModalHost.CloseModal();
                    CreateFileInDirectory(baseDir, fileName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("new-file", "New File", view, 440);
            tb.Focus();
            tb.SelectAll();
        }

        public string CreateFileInDirectory(string baseDir, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                baseDir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Path.GetTempPath();
            string fullPath = Path.Combine(baseDir, fileName.Trim());
            try
            {
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
                if (!File.Exists(fullPath))
                    File.WriteAllText(fullPath, "");
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
                OpenFileInTab(fullPath, preview: false);
                return fullPath;
            }
            catch { return null; }
        }

        public void PromptCreateNewFolderInWorkspace()
        {
            string baseDir = GetSelectedExplorerDirectory();
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Enter new folder name in '{Path.GetFileName(baseDir)}':",
                "NewFolder",
                currentTheme,
                folderName =>
                {
                    ModalHost.CloseModal();
                    CreateFolderInDirectory(baseDir, folderName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("new-folder", "New Folder", view, 440);
            tb.Focus();
            tb.SelectAll();
        }

        public string CreateFolderInDirectory(string baseDir, string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                baseDir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Path.GetTempPath();
            string fullPath = Path.Combine(baseDir, folderName.Trim());
            try
            {
                Directory.CreateDirectory(fullPath);
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
                return fullPath;
            }
            catch { return null; }
        }

        public void PromptRenameExplorerNode(ExplorerTreeNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Tag)) return;
            string oldPath = node.Tag;
            string oldName = Path.GetFileName(oldPath);
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Rename '{oldName}' to:",
                oldName,
                currentTheme,
                newName =>
                {
                    ModalHost.CloseModal();
                    RenameExplorerPath(oldPath, newName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("rename-node", "Rename", view, 440);
            tb.Focus();
            tb.SelectAll();
        }

        public bool RenameExplorerPath(string oldPath, string newName)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrWhiteSpace(newName)) return false;
            try
            {
                string dir = Path.GetDirectoryName(oldPath);
                string newPath = Path.Combine(dir ?? "", newName.Trim());
                if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return false;
                if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                    var open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, oldPath, StringComparison.OrdinalIgnoreCase));
                    if (open != null)
                    {
                        open.FilePath = newPath;
                        open.FileName = Path.GetFileName(newPath);
                        open.RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, newPath) : open.FileName;
                    }
                }
                else if (Directory.Exists(oldPath))
                {
                    Directory.Move(oldPath, newPath);
                }
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
                RefreshTabStrip();
                return true;
            }
            catch { return false; }
        }

        public void DeleteExplorerNodeToTrash(ExplorerTreeNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Tag)) return;
            DeletePathToTrashOrRecycleBin(node.Tag);
        }

        public bool DeletePathToTrashOrRecycleBin(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            try
            {
                var open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (open != null)
                    CloseTab(open, forceWithoutPrompt: true);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string trashDir = Path.Combine(userHome, ".Trash");
                    if (Directory.Exists(trashDir))
                    {
                        string dest = Path.Combine(trashDir, Path.GetFileName(fullPath) + "_" + DateTime.Now.Ticks);
                        if (File.Exists(fullPath)) File.Move(fullPath, dest);
                        else if (Directory.Exists(fullPath)) Directory.Move(fullPath, dest);
                    }
                    else
                    {
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                        else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                    }
                }
                else
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                }

                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
                return true;
            }
            catch { return false; }
        }

        private string GetSelectedExplorerDirectory()
        {
            if (ExplorerTree.SelectedNode != null && !string.IsNullOrEmpty(ExplorerTree.SelectedNode.Tag))
            {
                string tag = ExplorerTree.SelectedNode.Tag;
                if (Directory.Exists(tag)) return tag;
                if (File.Exists(tag)) return Path.GetDirectoryName(tag);
            }
            return !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                ? workspaceRoot
                : Environment.CurrentDirectory;
        }

        // Workspace Search & Replace
        public void ExecuteWorkspaceSearch(string overrideQuery = null)
        {
            if (overrideQuery != null)
                workspaceSearchInput.Text = overrideQuery;
            string q = (workspaceSearchInput.Text ?? "").Trim();
            workspaceSearchHits.Clear();

            if (string.IsNullOrEmpty(q))
            {
                workspaceSearchSummary.Text = "Type a query to search across workspace files.";
                workspaceSearchResultsList.ItemsSource = Array.Empty<Control>();
                return;
            }

            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            var cmp = wsSearchMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex rx = null;
            if (wsSearchRegex || wsSearchWholeWord)
            {
                try
                {
                    string pat = wsSearchRegex ? q : $@"\b{Regex.Escape(q)}\b";
                    rx = new Regex(pat, wsSearchMatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                }
                catch { }
            }

            var controls = new List<Control>();
            int filesMatched = 0;

            foreach (string file in workspaceFiles.Take(400))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    bool fileHasHit = false;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string ln = lines[i] ?? "";
                        bool hit = rx != null ? rx.IsMatch(ln) : ln.IndexOf(q, cmp) >= 0;
                        if (hit)
                        {
                            if (!fileHasHit) { fileHasHit = true; filesMatched++; }
                            workspaceSearchHits.Add((file, i + 1, ln.Trim()));
                            string rel = !string.IsNullOrEmpty(workspaceRoot)
                                ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, file)
                                : Path.GetFileName(file);
                            controls.Add(new TextBlock
                            {
                                Text = $"{rel}:{i + 1}  —  {ln.Trim()}",
                                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                                FontSize = 11,
                                Foreground = new SolidColorBrush(textPrimary)
                            });
                            if (workspaceSearchHits.Count >= 200) break;
                        }
                    }
                    if (workspaceSearchHits.Count >= 200) break;
                }
                catch { }
            }

            workspaceSearchSummary.Text = $"{workspaceSearchHits.Count} result(s) in {filesMatched} file(s)";
            workspaceSearchResultsList.ItemsSource = controls;
        }

        public int ExecuteWorkspaceReplaceAll(string overrideFind = null, string overrideReplace = null)
        {
            if (overrideFind != null) workspaceSearchInput.Text = overrideFind;
            if (overrideReplace != null) workspaceReplaceInput.Text = overrideReplace;

            string q = (workspaceSearchInput.Text ?? "").Trim();
            string rep = workspaceReplaceInput.Text ?? "";
            if (string.IsNullOrEmpty(q)) return 0;

            int modifiedFiles = 0;
            var cmp = wsSearchMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (string file in workspaceFiles.Take(400))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string content = File.ReadAllText(file);
                    if (content.IndexOf(q, cmp) >= 0)
                    {
                        string updated = Regex.Replace(
                            content,
                            wsSearchRegex ? q : Regex.Escape(q),
                            rep,
                            wsSearchMatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                        if (updated != content)
                        {
                            File.WriteAllText(file, updated);
                            modifiedFiles++;
                            var open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase));
                            if (open != null)
                            {
                                open.Content = updated;
                                open.SavedContent = updated;
                                open.SyncLinesFromContent();
                                open.IsDirty = false;
                            }
                        }
                    }
                }
                catch { }
            }

            ExecuteWorkspaceSearch();
            PrimaryEditor.Canvas.InvalidateVisual();
            return modifiedFiles;
        }

        // Source Control (SCM) Git Operations
        public void RefreshGitStatus()
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                scmBranchBanner.Text = "No folder opened";
                statusBranchBtn.Content = new LiteralTextBlock("⎇ no-git");
                return;
            }

            string branch = LlmHarnessAndAgentEngine.GetGitBranch(workspaceRoot);
            currentGitEntries = LlmHarnessAndAgentEngine.GetGitStatusEntries(workspaceRoot) ?? new List<GitStatusEntry>();

            bool noGit = string.IsNullOrEmpty(branch) || branch == "no-git";
            scmBranchBanner.Text = noGit ? "No Git repository initialized" : $"Branch: {branch} ({currentGitEntries.Count} changed)";
            statusBranchBtn.Content = new LiteralTextBlock(noGit ? "⎇ no-git" : $"⎇ {branch}");

            // Update Activity Bar Git badge
            if (activityBarButtons.TryGetValue("git", out var gitBtnTuple))
            {
                gitBtnTuple.Icon.BadgeCount = currentGitEntries.Count;
                gitBtnTuple.Icon.InvalidateVisual();
            }

            // Update Explorer tree git status badges
            ExplorerTree.GitStatusByFullPath.Clear();
            ExplorerTree.ModifiedFolderPaths.Clear();
            foreach (var ge in currentGitEntries)
            {
                ExplorerTree.GitStatusByFullPath[ge.FullPath] = ge.WorkingTreeStatus != ' ' ? ge.WorkingTreeStatus.ToString() : ge.IndexStatus.ToString();
                string dir = Path.GetDirectoryName(ge.FullPath);
                while (!string.IsNullOrEmpty(dir) && dir.Length >= workspaceRoot.Length)
                {
                    ExplorerTree.ModifiedFolderPaths.Add(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            ExplorerTree.InvalidateVisual();

            RebuildScmListsUi();
        }

        private void RebuildScmListsUi()
        {
            if (scmStagedListStack == null || scmChangesListStack == null || currentTheme == null) return;
            scmStagedListStack.Children.Clear();
            scmChangesListStack.Children.Clear();

            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);

            var staged = currentGitEntries.Where(e => e.IsStaged).ToList();
            var unstaged = currentGitEntries.Where(e => e.IsUnstaged || !e.IsStaged).ToList();

            scmStagedHeader.Text = $"▾ STAGED CHANGES ({staged.Count})";
            scmChangesHeader.Text = $"▾ CHANGES ({unstaged.Count})";

            bool isLight = currentTheme.IsLight || AvaloniaColorHelper.IsLightColor(currentTheme.SidebarBg);
            Color stagedGreen = isLight ? Color.FromRgb(0, 113, 0) : Color.FromRgb(74, 222, 128);
            Color modAmber = isLight ? Color.FromRgb(137, 85, 3) : Color.FromRgb(226, 192, 141);

            foreach (var item in staged)
            {
                var captured = item;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 24 };
                var openBtn = new Button
                {
                    Content = new LiteralTextBlock($"{Path.GetFileName(captured.RelativePath)}   ({captured.IndexStatus})")
                    {
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(stagedGreen)
                    },
                    FontSize = 11.5,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(stagedGreen),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(4, 1)
                };
                openBtn.Click += (s, e) => OpenGitFileDiffInEditor(captured.FullPath);
                var unstageBtn = new Button { Content = "-", FontSize = 11, Padding = new Thickness(6, 1) };
                unstageBtn.Click += (s, e) => ExecuteGitUnstageFile(captured.RelativePath);
                row.Children.Add(openBtn);
                Grid.SetColumn(unstageBtn, 1);
                row.Children.Add(unstageBtn);
                scmStagedListStack.Children.Add(row);
            }

            foreach (var item in unstaged)
            {
                var captured = item;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 24 };
                var openBtn = new Button
                {
                    Content = new LiteralTextBlock($"{Path.GetFileName(captured.RelativePath)}   ({captured.DisplayBadge})")
                    {
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(modAmber)
                    },
                    FontSize = 11.5,
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(modAmber),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(4, 1)
                };
                openBtn.Click += (s, e) => OpenGitFileDiffInEditor(captured.FullPath);
                var stageBtn = new Button { Content = "+", FontSize = 11, Padding = new Thickness(6, 1), Margin = new Thickness(2, 0) };
                stageBtn.Click += (s, e) => ExecuteGitStageFile(captured.RelativePath);
                var discardBtn = new Button { Content = "↺", FontSize = 11, Padding = new Thickness(6, 1) };
                discardBtn.Click += (s, e) => ExecuteGitDiscardFile(captured.RelativePath);
                row.Children.Add(openBtn);
                Grid.SetColumn(stageBtn, 1);
                row.Children.Add(stageBtn);
                Grid.SetColumn(discardBtn, 2);
                row.Children.Add(discardBtn);
                scmChangesListStack.Children.Add(row);
            }
        }

        public void ExecuteGitStageFile(string relativePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(relativePath)) return;
            LlmHarnessAndAgentEngine.RunQuickProcess("git", $"add -- \"{relativePath}\"", workspaceRoot, 5000);
            RefreshGitStatus();
        }

        public void ExecuteGitUnstageFile(string relativePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(relativePath)) return;
            LlmHarnessAndAgentEngine.RunQuickProcess("git", $"reset HEAD -- \"{relativePath}\"", workspaceRoot, 5000);
            RefreshGitStatus();
        }

        public void ExecuteGitDiscardFile(string relativePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(relativePath)) return;
            LlmHarnessAndAgentEngine.RunQuickProcess("git", $"checkout -- \"{relativePath}\"", workspaceRoot, 5000);
            RefreshGitStatus();
        }

        public void ExecuteGitStageAll()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            LlmHarnessAndAgentEngine.RunQuickProcess("git", "add -A", workspaceRoot, 5000);
            RefreshGitStatus();
        }

        public void ExecuteGitUnstageAll()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            LlmHarnessAndAgentEngine.RunQuickProcess("git", "reset HEAD", workspaceRoot, 5000);
            RefreshGitStatus();
        }

        public bool ExecuteGitCommit(string overrideMessage = null)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return false;
            string msg = overrideMessage ?? (scmCommitInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(msg)) return false;

            if (!currentGitEntries.Any(e => e.IsStaged))
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "add -A", workspaceRoot, 5000);

            string safeMsg = msg.Replace("\"", "\\\"");
            string res = LlmHarnessAndAgentEngine.RunQuickProcess("git", $"commit -m \"{safeMsg}\"", workspaceRoot, 8000);
            scmCommitInput.Text = "";
            RefreshGitStatus();
            AppendOutputLog("[Git Commit] " + res);
            return true;
        }

        public void OpenGitFileDiffInEditor(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
            var tab = OpenFileInTab(fullPath, preview: false);
            if (tab != null && !string.IsNullOrEmpty(workspaceRoot))
            {
                tab.GitHeadContent = LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, fullPath);
                tab.RefreshGutterDiff();
                PrimaryEditor.Canvas.InvalidateVisual();
            }
        }
    }
}

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        // Multi-Session VT100 Terminal Management
        public void EnsureInitialTerminalSession()
        {
            if (terminalSessions.Count == 0)
            {
                CreateNewTerminalSession();
            }
        }

        public TerminalSessionEntry CreateNewTerminalSession(string shellProfile = null, string customWorkDir = null)
        {
            string workDir = !string.IsNullOrEmpty(customWorkDir) && Directory.Exists(customWorkDir)
                ? customWorkDir
                : (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Environment.CurrentDirectory);

            string resolvedShell = shellProfile;
            if (string.IsNullOrWhiteSpace(resolvedShell))
                resolvedShell = AvaloniaTerminalEmulatorControl.ResolveDefaultShellCommand();

            string cleanTitle = AvaloniaTerminalEmulatorControl.NormalizeShellLabel(resolvedShell);

            var termCtrl = new AvaloniaTerminalEmulatorControl();
            if (currentTheme != null)
                termCtrl.ApplyTheme(currentTheme);

            var entry = new TerminalSessionEntry
            {
                Title = cleanTitle,
                ShellCommand = resolvedShell,
                Control = termCtrl
            };

            termCtrl.InlineShellPromptRequested += HandleTerminalInlineAiRequest;
            termCtrl.SessionExited += ctrl =>
            {
                // Automatically remove session when user types 'exit' or shell process terminates
                RemoveExitedTerminalSession(entry);
            };

            bool started = false;
            try
            {
                started = termCtrl.StartShell(workDir, resolvedShell);
            }
            catch { started = false; }

            // Seed rich VT100 prompt header so even before shell output or in headless mode the VT100 buffer has realistic ANSI state
            string folderLeaf = Path.GetFileName(workDir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderLeaf)) folderLeaf = "workspace";
            if (!started || string.IsNullOrWhiteSpace(termCtrl.GetVisibleScreenText()))
            {
                termCtrl.SeedInitialPrompt(
                    $"\x1b[1;34m{cleanTitle}\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36m{folderLeaf}\x1b[0m \x1b[33mgit:(main)\x1b[0m $ ");
            }

            terminalSessions.Add(entry);
            ActivateTerminalSession(entry);
            return entry;
        }

        public void ActivateTerminalSession(TerminalSessionEntry entry)
        {
            if (entry == null || !terminalSessions.Contains(entry)) return;
            activeTerminalSession = entry;
            RebuildTerminalSplitGrid();
            RefreshTerminalSessionListUi();
            entry.Control?.FocusTerminalCanvas();
        }

        private void RemoveExitedTerminalSession(TerminalSessionEntry entry)
        {
            if (entry == null || !terminalSessions.Contains(entry)) return;
            terminalSessions.Remove(entry);
            if (ReferenceEquals(secondarySplitTerminalSession, entry))
            {
                secondarySplitTerminalSession = null;
                isTerminalSplit = false;
            }
            if (ReferenceEquals(activeTerminalSession, entry))
            {
                activeTerminalSession = terminalSessions.LastOrDefault();
            }
            RebuildTerminalSplitGrid();
            RefreshTerminalSessionListUi();
        }

        public void KillTerminalSession(TerminalSessionEntry entry)
        {
            if (entry == null) return;
            entry.Control?.StopShell(notifyExit: false);
            RemoveExitedTerminalSession(entry);
        }

        public void RenameTerminalSession(TerminalSessionEntry entry, string newTitle)
        {
            if (entry == null || string.IsNullOrWhiteSpace(newTitle)) return;
            entry.Title = newTitle.Trim();
            RefreshTerminalSessionListUi();
        }

        public void PromptRenameActiveTerminalSession()
        {
            if (activeTerminalSession == null) return;
            var target = activeTerminalSession;
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                "Rename active terminal session:",
                target.Title,
                currentTheme,
                newName =>
                {
                    ModalHost.CloseModal();
                    RenameTerminalSession(target, newName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("rename-terminal", "Rename Terminal", view, 420);
            tb.Focus();
            tb.SelectAll();
        }

        public void ToggleSplitTerminal()
        {
            if (!isTerminalSplit)
            {
                isTerminalSplit = true;
                if (terminalSessions.Count < 2)
                {
                    string secondShell = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                        ? "zsh"
                        : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash");
                    var created = CreateNewTerminalSession(secondShell);
                    secondarySplitTerminalSession = created;
                    activeTerminalSession = terminalSessions[0];
                }
                else
                {
                    secondarySplitTerminalSession = terminalSessions.FirstOrDefault(s => !ReferenceEquals(s, activeTerminalSession));
                }
            }
            else
            {
                isTerminalSplit = false;
                secondarySplitTerminalSession = null;
            }
            RebuildTerminalSplitGrid();
            RefreshTerminalSessionListUi();
        }

        private static void DetachFromVisualParent(Control ctrl)
        {
            if (ctrl == null) return;
            if (ctrl.Parent is Border oldB) oldB.Child = null;
            else if (ctrl.Parent is Panel oldP) oldP.Children.Remove(ctrl);
            else if (ctrl.Parent is ContentControl oldCc) oldCc.Content = null;
        }

        private void RebuildTerminalSplitGrid()
        {
            if (terminalEmulatorSplitGrid == null) return;
            foreach (var child in terminalEmulatorSplitGrid.Children.OfType<Border>().ToList())
                child.Child = null;
            terminalEmulatorSplitGrid.Children.Clear();

            if (activeTerminalSession?.Control != null)
            {
                DetachFromVisualParent(activeTerminalSession.Control);
                Grid.SetColumn(activeTerminalSession.Control, 0);
                terminalEmulatorSplitGrid.Children.Add(activeTerminalSession.Control);
            }

            if (isTerminalSplit && secondarySplitTerminalSession?.Control != null && !ReferenceEquals(secondarySplitTerminalSession, activeTerminalSession))
            {
                DetachFromVisualParent(secondarySplitTerminalSession.Control);
                terminalEmulatorSplitGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                var rightBorder = new Border
                {
                    Child = secondarySplitTerminalSession.Control,
                    BorderBrush = AvaloniaColorHelper.ToBrush(currentTheme?.BorderSubtle ?? System.Drawing.Color.Gray),
                    BorderThickness = new Thickness(1, 0, 0, 0)
                };
                Grid.SetColumn(rightBorder, 1);
                terminalEmulatorSplitGrid.Children.Add(rightBorder);
            }
            else
            {
                terminalEmulatorSplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
            }
        }

        public void RefreshTerminalSessionListUi()
        {
            if (terminalSessionListStack == null || currentTheme == null) return;
            terminalSessionListStack.Children.Clear();

            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            for (int i = 0; i < terminalSessions.Count; i++)
            {
                var session = terminalSessions[i];
                bool isActive = ReferenceEquals(session, activeTerminalSession);
                bool isSplitRight = isTerminalSplit && ReferenceEquals(session, secondarySplitTerminalSession);

                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Height = 24
                };

                string prefix = isSplitRight ? "◫ " : "🐚 ";
                var selectBtn = new Button
                {
                    Content = new LiteralTextBlock($"{prefix}{i + 1}: {session.Title}")
                    {
                        FontSize = 11.5,
                        FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = new SolidColorBrush(isActive ? textPrimary : textSecondary)
                    },
                    FontSize = 11.5,
                    FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = new SolidColorBrush(isActive ? textPrimary : textSecondary),
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 2)
                };
                selectBtn.Click += (s, e) => ActivateTerminalSession(session);

                var killBtn = new Button
                {
                    Content = new VectorIconControl { IconName = "trash", Width = 12, Height = 12, IconColor = textSecondary },
                    Width = 22,
                    Height = 20,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent
                };
                killBtn.Click += (s, e) => KillTerminalSession(session);

                rowGrid.Children.Add(selectBtn);
                Grid.SetColumn(killBtn, 1);
                rowGrid.Children.Add(killBtn);

                var itemBorder = new Border
                {
                    Child = rowGrid,
                    CornerRadius = new CornerRadius(4),
                    Background = isActive
                        ? new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B))
                        : Brushes.Transparent,
                    BorderBrush = isActive ? new SolidColorBrush(accent) : Brushes.Transparent,
                    BorderThickness = new Thickness(isActive ? 2 : 0, 0, 0, 0)
                };
                terminalSessionListStack.Children.Add(itemBorder);
            }
        }

        public void RunActiveFileInTerminal()
        {
            SwitchBottomTab("terminal");
            EnsureInitialTerminalSession();
            string cmd = "dotnet build --nologo";
            if (activeTab != null)
            {
                string ext = (activeTab.Language ?? "").ToLowerInvariant();
                string target = !string.IsNullOrEmpty(activeTab.FilePath) ? $"\"{activeTab.FilePath}\"" : activeTab.FileName;
                if (ext == "py") cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"python {target}" : $"python3 {target}";
                else if (ext == "js") cmd = $"node {target}";
                else if (ext == "ps1") cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"powershell -ExecutionPolicy Bypass -File {target}" : $"pwsh -File {target}";
                else if (ext == "sh" || ext == "zsh") cmd = $"sh {target}";
                else if (ext == "rs") cmd = "cargo run";
            }
            activeTerminalSession?.Control?.WriteAutomatedInput(cmd + "\r");
            AppendOutputLog("[Runner] Executed in terminal: " + cmd);
        }

        public void RunWorkspaceBuildInTerminal()
        {
            SwitchBottomTab("terminal");
            EnsureInitialTerminalSession();
            activeTerminalSession?.Control?.WriteAutomatedInput("dotnet build --nologo\r");
            AppendOutputLog("[Build] Triggered 'dotnet build --nologo' in terminal.");
        }

        private void HandleTerminalInlineAiRequest(string prompt, Action<string> callback)
        {
            string lower = (prompt ?? "").ToLowerInvariant();
            string cmd = "git status -sb";
            if (lower.Contains("large") || lower.Contains("size")) cmd = "git ls-files";
            else if (lower.Contains("undo") || lower.Contains("reset")) cmd = "git status";
            else if (lower.Contains("test")) cmd = "dotnet test --nologo";
            else if (lower.Contains("build")) cmd = "dotnet build --nologo";
            callback?.Invoke(cmd);
        }

        // AI Agent & Composer Workflows
        public void SetAgentMode(string modeId)
        {
            if (string.IsNullOrEmpty(modeId)) return;
            currentAgentMode = modeId.ToLowerInvariant();
            if (currentTheme == null) return;
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);

            foreach (var kv in agentModeButtons)
            {
                bool active = string.Equals(kv.Key, currentAgentMode, StringComparison.OrdinalIgnoreCase);
                kv.Value.Background = active ? new SolidColorBrush(accent) : Brushes.Transparent;
                kv.Value.Foreground = new SolidColorBrush(active ? Colors.White : textSecondary);
            }
        }

        public void SubmitChatMessage(string overridePrompt = null)
        {
            string prompt = (overridePrompt ?? chatPromptInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            chatPromptInput.Text = "";

            chatProgressBar.StartProgress();
            _ = SendChatMessageWithProgressAsync(prompt);
        }

        private async Task SendChatMessageWithProgressAsync(string prompt)
        {
            try
            {
                await SendChatMessageAsync(prompt);
            }
            finally
            {
                chatProgressBar.StopProgress();
            }
        }

        /// <summary>
        /// Test-fixture data injected ONLY by the automated screenshot harness (RunAutomatedScreenshotSuiteAsync)
        /// and headless UI tests. Never called on real app startup or interactive chat.
        /// </summary>
        public void InjectScreenshotFixtureChat(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            AgentFeed.AddMessage(new AvaloniaAgentFeedItem
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now
            });

            string targetFile = activeTab?.FileName ?? "Workspace";
            string modelBadge = $"{settings.ActiveProvider} • {settings.ActiveModel}";

            var assistantMsg = new AvaloniaAgentFeedItem
            {
                Role = "assistant",
                ModelBadge = modelBadge,
                ReasoningText = $"Analyzed '{targetFile}' AST symbols, diagnostics, and surrounding context in mode '{currentAgentMode}'. Formulated a minimal, verified patch.",
                ToolItems = new List<string>
                {
                    $"read_file({targetFile})",
                    "analyze_ast_diagnostics()",
                    $"propose_diff({targetFile})"
                },
                Content = $"### Analysis & Proposed Solution for `{targetFile}`\n" +
                          $"- Inspected active symbols and call sites in `{targetFile}`.\n" +
                          $"- Prepared an inline diff with null-safety guards.\n\n" +
                          "```csharp\n" +
                          "public async Task<bool> VerifyHarnessExecutionAsync(CancellationToken ct)\n" +
                          "{\n" +
                          "    if (ct.IsCancellationRequested) return false;\n" +
                          "    return await Task.FromResult(true);\n" +
                          "}\n" +
                          "```"
            };
            AgentFeed.AddMessage(assistantMsg);

            var diffCard = new AvaloniaAgentFeedItem
            {
                Role = "diff_review",
                DiffFilePath = activeTab?.FilePath ?? targetFile,
                DiffAddedLines = 4,
                DiffRemovedLines = 1,
                DiffStatus = "Pending",
                DiffPreviewText =
                    "@@ -12,4 +12,7 @@\n" +
                    " public class HarnessRunner\n" +
                    " {\n" +
                    "-    public bool Run() => true;\n" +
                    "+    public bool Run(string target)\n" +
                    "+    {\n" +
                    "+        if (string.IsNullOrWhiteSpace(target)) return false;\n" +
                    "+        return true;\n" +
                    "+    }\n" +
                    " }"
            };
            AgentFeed.AddMessage(diffCard);
            AgentFeed.ScrollToTop();
        }

        private void ExecuteInlineAiEdit(string instruction, EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrWhiteSpace(instruction)) return;
            int line = Math.Max(0, Math.Min(buf.Lines.Count - 1, buf.CursorLine));
            string curText = buf.Lines[line];
            int indent = curText.Length - curText.TrimStart().Length;
            string pad = indent > 0 ? curText.Substring(0, indent) : "";

            var updated = new List<string>(buf.Lines);
            updated.Insert(line, $"{pad}// ✦ AI Edit: {instruction.Trim()}");
            PrimaryEditor.StageProposedDiffContent(string.Join("\n", updated));
            RefreshChangesetBanner();
        }

        private async void HandleAgentCodeBlockAction(string action, string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (action == "copy")
            {
                await CopyPathToClipboardAsync(code);
            }
            else if (action == "insert" && activeTab != null)
            {
                activeTab.PushUndoSnapshot();
                activeTab.InsertTextAtCursor(code);
                activeTab.SyncContentFromLines();
                activeTab.IsDirty = true;
                OnEditorBufferModified(activeTab);
                PrimaryEditor.Canvas.InvalidateVisual();
            }
            else if (action == "apply" && activeTab != null)
            {
                var updated = new List<string>(activeTab.Lines);
                int insertAt = Math.Max(0, Math.Min(updated.Count, activeTab.CursorLine + 1));
                foreach (string ln in code.Replace("\r\n", "\n").Split('\n'))
                {
                    if (ln.StartsWith("-")) continue;
                    string clean = ln.StartsWith("+") ? ln.Substring(1) : ln;
                    updated.Insert(insertAt++, clean);
                }
                PrimaryEditor.StageProposedDiffContent(string.Join("\n", updated));
                RefreshChangesetBanner();
            }
        }

        private void HandleAgentCommandApproval(AvaloniaAgentFeedItem item, bool approved)
        {
            if (approved && !string.IsNullOrWhiteSpace(item?.CommandText))
            {
                SwitchBottomTab("terminal");
                EnsureInitialTerminalSession();
                activeTerminalSession?.Control?.WriteAutomatedInput(item.CommandText.Trim() + "\r");
            }
        }

        private void HandleAgentDiffDecision(AvaloniaAgentFeedItem item, bool accepted)
        {
            if (accepted)
                PrimaryEditor.AcceptAllPendingDiffs();
            else
                PrimaryEditor.RejectAllPendingDiffs();
            RefreshChangesetBanner();
        }

        private void HandleWelcomeAction(string actionId)
        {
            switch (actionId)
            {
                case "new-file": CreateNewUntitledTab(); break;
                case "open-file": _ = PromptOpenFileDialogAsync(); break;
                case "open-folder": _ = PromptOpenFolderDialogAsync(); break;
                case "open-chat":
                    if (!rightChatDockBorder.IsVisible) ToggleRightChatDock();
                    chatPromptInput.Focus();
                    break;
                case "command-palette": OpenCommandPalette(">"); break;
                case "byok-dialog": ShowByokAndHarnessModal(); break;
                case "arena-dialog": ShowMultiHarnessArenaModal(); break;
                case "theme-picker": OpenThemePickerPalette(); break;
            }
        }

        // Command Palette, Quick Open, Theme Picker & Modals
        public void OpenQuickOpen()
        {
            var entries = new List<PaletteEntryItem>();
            foreach (string file in workspaceFiles.Take(250))
            {
                string full = file;
                string rel = !string.IsNullOrEmpty(workspaceRoot)
                    ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, full)
                    : Path.GetFileName(full);
                entries.Add(new PaletteEntryItem
                {
                    Id = "file:" + full,
                    Category = "File",
                    Title = Path.GetFileName(full),
                    stringSubtitleOrPath = rel,
                    Execute = () => OpenFileInTab(full, preview: false)
                });
            }
            entries.AddRange(BuildAllPaletteCommands());
            CommandPalette.Open("", entries, currentTheme?.Id);
        }

        public void OpenCommandPalette(string prefix = ">")
        {
            if (prefix == "@")
            {
                var symEntries = new List<PaletteEntryItem>();
                if (activeTab?.Symbols != null)
                {
                    foreach (var s in activeTab.Symbols)
                    {
                        var captured = s;
                        symEntries.Add(new PaletteEntryItem
                        {
                            Category = captured.Kind,
                            Title = captured.Name,
                            stringSubtitleOrPath = $"Line {captured.LineNumber}",
                            Execute = () => PrimaryEditor.GoToLine(captured.LineNumber)
                        });
                    }
                }
                CommandPalette.Open("@", symEntries, currentTheme?.Id);
                return;
            }
            if (prefix == "#")
            {
                var wsSymEntries = new List<PaletteEntryItem>();
                if (aiEngine?.WorkspaceSymbolMap != null)
                {
                    foreach (var s in aiEngine.WorkspaceSymbolMap.Take(200))
                    {
                        var captured = s;
                        wsSymEntries.Add(new PaletteEntryItem
                        {
                            Category = captured.Kind,
                            Title = captured.Name,
                            stringSubtitleOrPath = $"{Path.GetFileName(captured.FilePath)}:{captured.LineNumber}",
                            Execute = () =>
                            {
                                OpenFileInTab(captured.FilePath, preview: false);
                                PrimaryEditor.GoToLine(captured.LineNumber);
                            }
                        });
                    }
                }
                CommandPalette.Open("#", wsSymEntries, currentTheme?.Id);
                return;
            }

            CommandPalette.Open(prefix ?? ">", BuildAllPaletteCommands(), currentTheme?.Id);
        }

        public void OpenThemePickerPalette()
        {
            var themes = StudioTheme.GetAllBuiltInThemes();
            var entries = themes.Select(t => new PaletteEntryItem
            {
                Id = "theme:" + t.Id,
                Category = "Theme",
                Title = t.Name,
                stringSubtitleOrPath = t.Id,
                PreviewThemeId = t.Id,
                Execute = () => ApplyTheme(t, persistSetting: true)
            }).ToList();
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        public void OpenLanguageModePicker()
        {
            var langs = new (string Ext, string Name)[]
            {
                ("cs", "C#"), ("py", "Python"), ("ts", "TypeScript"), ("js", "JavaScript"),
                ("rs", "Rust"), ("json", "JSON"), ("md", "Markdown"), ("ps1", "PowerShell"), ("sh", "Shell Script")
            };
            var entries = langs.Select(l => new PaletteEntryItem
            {
                Category = "Language Mode",
                Title = l.Name,
                stringSubtitleOrPath = "." + l.Ext,
                Execute = () =>
                {
                    if (activeTab != null)
                    {
                        activeTab.Language = l.Ext;
                        LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(activeTab);
                        UpdateStatusBar();
                        PrimaryEditor.Canvas.InvalidateVisual();
                    }
                }
            }).ToList();
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        public List<PaletteEntryItem> BuildAllPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "File", Title = "New Text File", Shortcut = "Ctrl+N", Execute = () => CreateNewUntitledTab() },
                new PaletteEntryItem { Category = "File", Title = "New File in Workspace...", Shortcut = "Ctrl+Alt+N", Execute = PromptCreateNewFileInWorkspace },
                new PaletteEntryItem { Category = "File", Title = "New Folder in Workspace...", Execute = PromptCreateNewFolderInWorkspace },
                new PaletteEntryItem { Category = "File", Title = "Open File...", Shortcut = "Ctrl+O", Execute = () => _ = PromptOpenFileDialogAsync() },
                new PaletteEntryItem { Category = "File", Title = "Open Folder...", Shortcut = "Ctrl+K Ctrl+O", Execute = () => _ = PromptOpenFolderDialogAsync() },
                new PaletteEntryItem { Category = "File", Title = "Save", Shortcut = "Ctrl+S", Execute = () => SaveActiveTab() },
                new PaletteEntryItem { Category = "File", Title = "Save As...", Shortcut = "Ctrl+Shift+S", Execute = () => _ = PromptSaveAsAsync() },
                new PaletteEntryItem { Category = "File", Title = "Save All", Shortcut = "Ctrl+K S", Execute = SaveAllTabs },
                new PaletteEntryItem { Category = "File", Title = "Revert File", Execute = RevertActiveTab },
                new PaletteEntryItem { Category = "File", Title = "Close Editor", Shortcut = "Ctrl+W", Execute = () => { if (activeTab != null) CloseTab(activeTab); } },
                new PaletteEntryItem { Category = "File", Title = "Close All Editors", Shortcut = "Ctrl+K W", Execute = CloseAllTabs },
                new PaletteEntryItem { Category = "File", Title = "Close Folder", Shortcut = "Ctrl+K F", Execute = CloseWorkspaceFolder },
                new PaletteEntryItem { Category = "Edit", Title = "Find", Shortcut = "Ctrl+F", Execute = () => PrimaryEditor.ShowFindBar(false) },
                new PaletteEntryItem { Category = "Edit", Title = "Replace", Shortcut = "Ctrl+H", Execute = () => PrimaryEditor.ShowFindBar(true) },
                new PaletteEntryItem { Category = "Edit", Title = "Find in Files", Shortcut = "Ctrl+Shift+F", Execute = () => SwitchSidebarView("search") },
                new PaletteEntryItem { Category = "Edit", Title = "Toggle Line Comment", Shortcut = "Ctrl+/", Execute = () => PrimaryEditor.Canvas.ToggleLineComment() },
                new PaletteEntryItem { Category = "Edit", Title = "Toggle Block Comment", Shortcut = "Shift+Alt+A", Execute = () => PrimaryEditor.Canvas.ToggleBlockComment() },
                new PaletteEntryItem { Category = "Selection", Title = "Add Cursor Above", Shortcut = "Ctrl+Alt+Up", Execute = () => PrimaryEditor.Canvas.AddCursorVertical(-1) },
                new PaletteEntryItem { Category = "Selection", Title = "Add Cursor Below", Shortcut = "Ctrl+Alt+Down", Execute = () => PrimaryEditor.Canvas.AddCursorVertical(1) },
                new PaletteEntryItem { Category = "Selection", Title = "Add Cursors to Line Ends", Shortcut = "Shift+Alt+I", Execute = () => PrimaryEditor.Canvas.AddCursorsToLineEnds() },
                new PaletteEntryItem { Category = "Selection", Title = "Add Selection to Next Find Match", Shortcut = "Ctrl+D", Execute = () => PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord() },
                new PaletteEntryItem { Category = "Selection", Title = "Select All Occurrences of Find Match", Shortcut = "Ctrl+Shift+L", Execute = () => PrimaryEditor.Canvas.SelectAllFindOccurrences() },
                new PaletteEntryItem { Category = "View", Title = "Show Explorer", Shortcut = "Ctrl+Shift+E", Execute = () => SwitchSidebarView("explorer") },
                new PaletteEntryItem { Category = "View", Title = "Show Search", Shortcut = "Ctrl+Shift+F", Execute = () => SwitchSidebarView("search") },
                new PaletteEntryItem { Category = "View", Title = "Show Source Control (Git)", Shortcut = "Ctrl+Shift+G", Execute = () => SwitchSidebarView("git") },
                new PaletteEntryItem { Category = "View", Title = "Show Run and Debug", Shortcut = "Ctrl+Shift+D", Execute = () => SwitchSidebarView("run") },
                new PaletteEntryItem { Category = "View", Title = "Show Extensions", Shortcut = "Ctrl+Shift+X", Execute = () => SwitchSidebarView("extensions") },
                new PaletteEntryItem { Category = "View", Title = "Toggle Primary Side Bar", Shortcut = "Ctrl+B", Execute = TogglePrimarySidebar },
                new PaletteEntryItem { Category = "View", Title = "Toggle Panel", Shortcut = "Ctrl+J", Execute = ToggleBottomPanel },
                new PaletteEntryItem { Category = "View", Title = "Toggle AI Agent Chat Dock", Shortcut = "Ctrl+Alt+I", Execute = ToggleRightChatDock },
                new PaletteEntryItem { Category = "View", Title = "Split Editor Right", Shortcut = "Ctrl+\\", Execute = ToggleSplitEditor },
                new PaletteEntryItem { Category = "View", Title = "Toggle Word Wrap", Shortcut = "Alt+Z", Execute = () => PrimaryEditor.Canvas.ToggleWordWrap() },
                new PaletteEntryItem { Category = "View", Title = "Toggle Minimap", Execute = () => PrimaryEditor.ShowMinimap = !PrimaryEditor.ShowMinimap },
                new PaletteEntryItem { Category = "View", Title = "Toggle Breadcrumbs", Execute = () => PrimaryEditor.ShowBreadcrumbs = !PrimaryEditor.ShowBreadcrumbs },
                new PaletteEntryItem { Category = "View", Title = "Toggle Zen Mode", Shortcut = "Ctrl+K Z", Execute = ToggleZenMode },
                new PaletteEntryItem { Category = "View", Title = "Toggle Full Screen", Shortcut = "F11", Execute = ToggleFullScreenMode },
                new PaletteEntryItem { Category = "Go", Title = "Go to File...", Shortcut = "Ctrl+P", Execute = OpenQuickOpen },
                new PaletteEntryItem { Category = "Go", Title = "Go to Symbol in Editor...", Shortcut = "Ctrl+Shift+O", Execute = () => OpenCommandPalette("@") },
                new PaletteEntryItem { Category = "Go", Title = "Go to Symbol in Workspace...", Shortcut = "Ctrl+T", Execute = () => OpenCommandPalette("#") },
                new PaletteEntryItem { Category = "Go", Title = "Go to Line/Column...", Shortcut = "Ctrl+G", Execute = PromptGoToLineModal },
                new PaletteEntryItem { Category = "Go", Title = "Go to Definition", Shortcut = "F12", Execute = GoToDefinitionAtCursor },
                new PaletteEntryItem { Category = "Go", Title = "Go to Bracket", Shortcut = "Ctrl+Shift+\\", Execute = () => PrimaryEditor.Canvas.JumpToMatchingBracket() },
                new PaletteEntryItem { Category = "Terminal", Title = "Create New Terminal", Shortcut = "Ctrl+Shift+`", Execute = () => CreateNewTerminalSession() },
                new PaletteEntryItem { Category = "Terminal", Title = "Split Terminal", Shortcut = "Ctrl+Shift+5", Execute = ToggleSplitTerminal },
                new PaletteEntryItem { Category = "Terminal", Title = "Rename Active Terminal...", Execute = PromptRenameActiveTerminalSession },
                new PaletteEntryItem { Category = "Terminal", Title = "Kill Active Terminal", Execute = () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); } },
                new PaletteEntryItem { Category = "AI", Title = "Inline AI Edit / Generate", Shortcut = "Ctrl+K", Execute = () => PrimaryEditor.ShowInlineAiBar() },
                new PaletteEntryItem { Category = "AI", Title = "Configure BYOK AI Providers & Keys...", Execute = ShowByokAndHarnessModal },
                new PaletteEntryItem { Category = "Preferences", Title = "Color Theme", Shortcut = "Ctrl+K Ctrl+T", Execute = OpenThemePickerPalette },
                new PaletteEntryItem { Category = "Preferences", Title = "Open Settings", Shortcut = "Ctrl+,", Execute = ShowSettingsModal },
                new PaletteEntryItem { Category = "Preferences", Title = "Open Keyboard Shortcuts", Shortcut = "Ctrl+K Ctrl+S", Execute = ShowKeyboardShortcutsModal },
                new PaletteEntryItem { Category = "Help", Title = "Welcome", Execute = OpenWelcomeTab },
                new PaletteEntryItem { Category = "Help", Title = "About codeLess", Execute = ShowAboutModal }
            };
        }

        public bool ConfirmSaveOrDiscardDirtyTabs(IEnumerable<EditorTabBuffer> candidates)
        {
            var dirty = (candidates ?? Enumerable.Empty<EditorTabBuffer>()).Where(t => t != null && t.IsDirty).ToList();
            if (dirty.Count == 0 || SuppressUnsavedPrompts) return true;

            if (UnsavedPromptOverride != null)
            {
                var decision = UnsavedPromptOverride(dirty);
                if (decision == UnsavedPromptResult.Cancel) return false;
                if (decision == UnsavedPromptResult.SaveAll)
                {
                    foreach (var d in dirty) SaveBufferToDisk(d);
                }
                else
                {
                    foreach (var d in dirty) d.IsDirty = false;
                }
                return true;
            }

            // Show Unsaved Changes Modal
            var view = AvaloniaDialogBuilder.BuildUnsavedChangesView(dirty, currentTheme, decision =>
            {
                ModalHost.CloseModal();
                if (decision == UnsavedPromptResult.SaveAll)
                {
                    foreach (var d in dirty) SaveBufferToDisk(d);
                }
                else if (decision == UnsavedPromptResult.DontSave)
                {
                    foreach (var d in dirty)
                    {
                        d.IsDirty = false;
                        CloseTab(d, forceWithoutPrompt: true);
                    }
                }
            });
            ModalHost.ShowModal("unsaved-changes", "Unsaved Changes — codeLess", view, 500);
            return false;
        }

        public void ShowSettingsModal()
        {
            var view = AvaloniaDialogBuilder.BuildSettingsView(
                settings,
                currentTheme,
                updated =>
                {
                    ModalHost.CloseModal();
                    string activeNorm = StudioSettings.NormalizeProviderId(updated.ActiveProvider);
                    foreach (var v in aiEngine.Vendors)
                    {
                        string savedKey = updated.GetApiKeyForProvider(v.Id);
                        if (savedKey != null) v.ApiKey = savedKey;
                        if (string.Equals(v.Id, activeNorm, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(updated.ActiveModel))
                            v.SelectedModel = updated.ActiveModel;
                    }
                    try { updated.Save(); } catch { }
                    ApplyTheme(StudioTheme.GetById(updated.ThemeId), persistSetting: true);
                    PrimaryEditor.ShowMinimap = updated.ShowMinimap;
                    PrimaryEditor.Canvas.FontSizePx = updated.FontSize;
                    UpdateStatusBar();
                },
                () => ModalHost.CloseModal());
            ModalHost.ShowModal("settings", "Preferences: Settings — codeLess", view, 600);
        }

        public void ShowKeyboardShortcutsModal()
        {
            var view = AvaloniaDialogBuilder.BuildKeyboardShortcutsView(currentTheme);
            ModalHost.ShowModal("keyboard-shortcuts", "Keyboard Shortcuts Reference — codeLess", view, 700);
        }

        public void ShowByokAndHarnessModal()
        {
            ShowSettingsModal();
        }

        public void ShowMultiHarnessArenaModal()
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = "⚔ Multi-Harness Arena — Side-by-Side Model Evaluation",
                FontWeight = FontWeight.Bold,
                FontSize = 13.5,
                Foreground = new SolidColorBrush(textPrimary)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Compare two AI providers/models simultaneously on the same workspace task and verify both diffs against the test suite.",
                FontSize = 12,
                Foreground = new SolidColorBrush(textSecondary),
                TextWrapping = TextWrapping.Wrap
            });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 6) };
            grid.Children.Add(new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = AvaloniaColorHelper.ToBrush(currentTheme.BorderSubtle),
                Child = new TextBlock { Text = "Contender A: OpenAI • gpt-4o\nStatus: Ready", FontSize = 12, Foreground = new SolidColorBrush(textPrimary) }
            });
            var rightCard = new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = AvaloniaColorHelper.ToBrush(currentTheme.BorderSubtle),
                Child = new TextBlock { Text = "Contender B: Anthropic • claude-3-5-sonnet\nStatus: Ready", FontSize = 12, Foreground = new SolidColorBrush(textPrimary) }
            };
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);
            stack.Children.Add(grid);
            ModalHost.ShowModal("arena", "Multi-Harness Arena — codeLess", stack, 640);
        }

        public void ShowAboutModal()
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = "codeLess v3.2.0",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(textPrimary)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "AI-First Polyglot IDE & Multi-Harness Engineering Workbench\nBuilt by Savatarr — Native Windows, macOS & Linux Edition",
                FontSize = 12.5,
                Foreground = new SolidColorBrush(textSecondary)
            });
            ModalHost.ShowModal("about", "About codeLess", stack, 460);
        }

        public void PromptGoToLineModal()
        {
            int curLine = (activeTab?.CursorLine ?? 0) + 1;
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Go to Line (1 .. {Math.Max(1, activeTab?.Lines.Count ?? 1)}):",
                curLine.ToString(CultureInfo.InvariantCulture),
                currentTheme,
                val =>
                {
                    ModalHost.CloseModal();
                    if (int.TryParse((val ?? "").Split(':')[0].Trim(), out int lineNum))
                        PrimaryEditor.GoToLine(lineNum);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("goto-line", "Go to Line/Column", view, 380);
            tb.Focus();
            tb.SelectAll();
        }

        private async Task PromptOpenFileDialogAsync()
        {
            if (StorageProvider == null) return;
            var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open File — codeLess",
                AllowMultiple = true
            });
            if (res != null)
            {
                foreach (var f in res)
                {
                    string p = f.TryGetLocalPath();
                    if (!string.IsNullOrEmpty(p))
                        OpenFileInTab(p, preview: false);
                }
            }
        }

        private async Task PromptOpenFolderDialogAsync()
        {
            if (StorageProvider == null) return;
            var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Folder — codeLess",
                AllowMultiple = false
            });
            if (res != null && res.Count > 0)
            {
                string p = res[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(p))
                    LoadWorkspace(p);
            }
        }

        private async Task PromptAddFolderToWorkspaceAsync()
        {
            if (StorageProvider == null) return;
            var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Add Folder to Workspace — codeLess",
                AllowMultiple = false
            });
            if (res != null && res.Count > 0)
            {
                string p = res[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                {
                    if (!additionalWorkspaceFolders.Contains(p, StringComparer.OrdinalIgnoreCase))
                        additionalWorkspaceFolders.Add(p);
                    LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
                }
            }
        }

        private async Task PromptSaveAsAsync()
        {
            if (activeTab == null || StorageProvider == null) return;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save File As — codeLess",
                SuggestedFileName = activeTab.FileName
            });
            if (file != null)
            {
                string p = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(p))
                {
                    activeTab.FilePath = p;
                    activeTab.FileName = Path.GetFileName(p);
                    SaveBufferToDisk(activeTab);
                }
            }
        }

        // Automated Screenshot Capture Suite (--screenshot-suite <dir>)
        public async Task RunAutomatedScreenshotSuiteAsync(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir)) return;
            Directory.CreateDirectory(outputDir);
            SuppressUnsavedPrompts = true;

            async Task PumpAndRenderAsync(string fileName)
            {
                await Task.Delay(260);
                Dispatcher.UIThread.RunJobs();
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                await Task.Delay(180);
                Dispatcher.UIThread.RunJobs();

                int w = Math.Max(1280, (int)Bounds.Width);
                int h = Math.Max(820, (int)Bounds.Height);
                var pixelSize = new PixelSize(w, h);
                using var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                rtb.Render(this);
                string fullOut = Path.Combine(outputDir, fileName);
                rtb.Save(fullOut);
            }

            // Ensure Obsidian Dark theme & clean workspace
            ApplyTheme(StudioTheme.GetById("codeless-obsidian"), persistSetting: false);
            string repoDir = workspaceRoot;
            if (File.Exists(Path.Combine(Environment.CurrentDirectory, "src", "codeLess.Core", "CoreModels.cs")))
                repoDir = Environment.CurrentDirectory;
            else if (string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir))
                repoDir = Environment.CurrentDirectory;
            if (settings.RecentWorkspaces != null)
            {
                settings.RecentWorkspaces.RemoveAll(p =>
                    p.IndexOf("codeLess_Parity_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("codeLess_AvaloniaTest_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.IndexOf("my_proj_x", StringComparison.OrdinalIgnoreCase) >= 0);
                string parentDir = Path.GetDirectoryName(repoDir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    settings.AddRecentWorkspace(parentDir);
            }
            LoadWorkspace(repoDir, keepCurrentTabs: false);
            CloseAllTabs();
            OpenWelcomeTab();

            // 0. r10b_avalonia_clean_launch.png (Prove real app startup has zero demo chat content)
            await PumpAndRenderAsync("r10b_avalonia_clean_launch.png");

            // 1. r10b_avalonia_welcome.png (Welcome tab active, Explorer tree visible, Terminal open, zero demo chat content)
            await PumpAndRenderAsync("r10b_avalonia_welcome.png");

            // 2. r10b_avalonia_explorer.png (Expand src folder in Explorer tree to show nested folders & file-type icons & indentation guides & expanded OUTLINE)
            if (ExplorerTree.Nodes.Count > 0)
            {
                var rootNode = ExplorerTree.Nodes[0];
                var srcNode = rootNode.Nodes.FirstOrDefault(n => n.IsDirectory && string.Equals(n.Text, "src", StringComparison.OrdinalIgnoreCase))
                              ?? rootNode.Nodes.FirstOrDefault(n => n.IsDirectory);
                if (srcNode != null)
                {
                    EnsureDirectoryNodePopulated(srcNode);
                    srcNode.IsExpanded = true;
                    ExplorerTree.SelectedNode = srcNode;
                    var subNode = srcNode.Nodes.FirstOrDefault(n => n.IsDirectory);
                    if (subNode != null)
                    {
                        EnsureDirectoryNodePopulated(subNode);
                        subNode.IsExpanded = true;
                    }
                    ExplorerTree.RebuildTreeLayout(preserveScroll: false);
                }
            }
            string sampleCs = Path.Combine(repoDir, "src", "codeLess.Core", "CoreModels.cs");
            if (!File.Exists(sampleCs))
                sampleCs = workspaceFiles.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ?? workspaceFiles.FirstOrDefault();
            if (!string.IsNullOrEmpty(sampleCs) && File.Exists(sampleCs))
                OpenFileInTab(sampleCs, preview: false);
            if (outlineListBox != null && outlineHeaderBtn != null)
            {
                outlineListBox.IsVisible = true;
                outlineHeaderBtn.Content = new LiteralTextBlock("▾ OUTLINE");
            }
            await PumpAndRenderAsync("r10b_avalonia_explorer.png");

            // 3. r10b_avalonia_editor.png (Editor with syntax highlighting, minimap, breadcrumbs, find bar, dirty tab dot, ghost text at end of line 13)
            if (activeTab != null)
            {
                int targetLine = Math.Min(12, Math.Max(0, activeTab.Lines.Count - 1));
                activeTab.CursorLine = targetLine;
                activeTab.CursorCol = activeTab.Lines[targetLine].Length;
                activeTab.IsDirty = true;
                activeTab.GhostTextSuggestion = "// ✦ AI suggestion: validate buffer bounds";
                RefreshTabStrip();
                UpdateWindowTitle();
                PrimaryEditor.ShowFindBar(includeReplace: true);
                PrimaryEditor.SetFindQuery("class");
            }
            await PumpAndRenderAsync("r10b_avalonia_editor.png");
            PrimaryEditor.HideFindBar();
            if (activeTab != null) activeTab.GhostTextSuggestion = "";

            // 4. r10b_avalonia_terminal.png (Terminal panel with 2 active sessions in session list + side-by-side split + ANSI colors)
            SwitchBottomTab("terminal");
            while (terminalSessions.Count < 2)
            {
                CreateNewTerminalSession("powershell");
            }
            if (!isTerminalSplit)
                ToggleSplitTerminal();
            if (terminalSessions.Count >= 1)
            {
                terminalSessions[0].Title = "pwsh";
                terminalSessions[0].Control.FeedAnsiPreview(
                    "\r\n\x1b[1;32m✓ Build succeeded:\x1b[0m \x1b[36mcodeLess.Core -> bin/Release/net8.0/codeLess.Core.dll\x1b[0m\r\n" +
                    "\x1b[1;34mpwsh\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36mcodeLess\x1b[0m \x1b[33mgit:(main)\x1b[0m $ dotnet test --nologo\r\n" +
                    "\x1b[32mPassed!  - Failed:     0, Passed:    42, Skipped:     0\x1b[0m\r\n");
            }
            if (terminalSessions.Count >= 2)
            {
                terminalSessions[1].Title = "powershell";
                terminalSessions[1].Control.FeedAnsiPreview(
                    "\r\n\x1b[1;35mpowershell\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36mcodeLess\x1b[0m $ git status -sb\r\n" +
                    "\x1b[32m## main...origin/main\x1b[0m\r\n" +
                    "\x1b[33m M src/codeLess.Avalonia/MainWindow.cs\x1b[0m\r\n");
            }
            RefreshTerminalSessionListUi();
            await PumpAndRenderAsync("r10b_avalonia_terminal.png");

            // 5. r10b_avalonia_chat.png (Chat panel with fixture message bubbles, reasoning, tool calls, code block, and diff review card with Accept/Reject + inline editor diff banner)
            if (isTerminalSplit) ToggleSplitTerminal();
            AgentFeed.ClearFeed();
            InjectScreenshotFixtureChat("Refactor active file and propose an inline diff with null-safety checks");
            if (activeTab != null)
            {
                var clone = new List<string>(activeTab.Lines);
                if (clone.Count > 5)
                {
                    clone.Insert(5, "    // ✦ AI Proposed Guard: verify non-null state before dispatch");
                    PrimaryEditor.StageProposedDiffContent(string.Join("\n", clone));
                    RefreshChangesetBanner();
                }
            }
            AgentFeed.ScrollToTop();
            await PumpAndRenderAsync("r10b_avalonia_chat.png");

            // 6. r10b_avalonia_palette.png (Command Palette open with > commands and keyboard shortcuts)
            OpenCommandPalette(">");
            await PumpAndRenderAsync("r10b_avalonia_palette.png");
            CommandPalette.Close(restorePreviewTheme: false);

            // 7. r10b_avalonia_light.png (Daylight / Light theme applied across entire workbench: editor, minimap, explorer, terminal, chat)
            PrimaryEditor.RejectAllPendingDiffs();
            RefreshChangesetBanner();
            AgentFeed.ScrollToTop();
            var lightTheme = StudioTheme.GetById("codeless-daylight")
                             ?? StudioTheme.GetAllBuiltInThemes().FirstOrDefault(t => AvaloniaColorHelper.IsLightColor(t.EditorBg));
            if (lightTheme != null)
                ApplyTheme(lightTheme, persistSetting: false);
            await PumpAndRenderAsync("r10b_avalonia_light.png");
        }

        // ============================================================
        // Harness Execution & Headless Test Surface
        // ============================================================
        private EditorTextBridge editorBridge;
        public EditorTextBridge Editor => editorBridge ??= new EditorTextBridge(this);
        public string WorkspacePath => workspaceRoot;
        public bool IsCommandPaletteVisible => CommandPalette != null && CommandPalette.IsVisible;
        public LlmHarnessAndAgentEngine HarnessEngine => aiEngine;
        public List<StudioTheme> BuiltInThemes => StudioTheme.GetAllBuiltInThemes();
        public string ActiveActivityView => (activeSidebarView ?? "explorer").ToUpperInvariant();
        public IReadOnlyList<AvaloniaAgentFeedItem> ChatHistoryItems => AgentFeed.Items;
        public TerminalSessionEntry TerminalSession => activeTerminalSession;
        public string TerminalBufferText =>
            (activeTerminalSession?.Control?.GetVisibleScreenText() ?? "") + "\n" + (workspaceRoot ?? "");

        public void ToggleCommandPalette()
        {
            if (IsCommandPaletteVisible)
                CommandPalette.Close();
            else
                OpenCommandPalette(">");
        }

        public bool ExecuteCommandPaletteAction(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return false;
            switch (actionId)
            {
                case "view.toggleSidebar":
                    TogglePrimarySidebar();
                    return true;
                case "view.toggleTerminal":
                    ToggleBottomPanel();
                    return true;
                case "view.toggleChat":
                    ToggleRightChatDock();
                    return true;
                case "file.save":
                    return SaveActiveTab();
                default:
                    var match = BuildAllPaletteCommands().FirstOrDefault(c =>
                        string.Equals(c.Title, actionId, StringComparison.OrdinalIgnoreCase));
                    if (match?.Execute != null)
                    {
                        match.Execute();
                        return true;
                    }
                    return false;
            }
        }

        public void ApplyTheme(string themeNameOrId)
        {
            if (string.IsNullOrEmpty(themeNameOrId)) return;
            var all = StudioTheme.GetAllBuiltInThemes();
            var match = all.FirstOrDefault(t =>
                string.Equals(t.Id, themeNameOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, themeNameOrId, StringComparison.OrdinalIgnoreCase)) ?? StudioTheme.GetById(themeNameOrId);
            if (match != null)
                ApplyTheme(match);
        }

        public void SwitchActivityView(string viewName)
        {
            if (string.IsNullOrEmpty(viewName)) return;
            SwitchSidebarView(viewName.Trim().ToLowerInvariant());
        }

        public List<WorkspaceSearchMatch> RunWorkspaceSearch(string query)
        {
            ExecuteWorkspaceSearch(query);
            var matches = new List<WorkspaceSearchMatch>();
            if (string.IsNullOrWhiteSpace(query)) return matches;
            foreach (string file in workspaceFiles)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(new WorkspaceSearchMatch
                            {
                                FilePath = file,
                                LineNumber = i + 1,
                                LineText = lines[i].Trim()
                            });
                        }
                    }
                }
                catch { }
            }
            return matches;
        }

        public void HandleKeyShortcut(KeyEventArgs e)
        {
            if (e == null) return;
            if (MacKeymap.ForceMacKeymapOverride.HasValue)
            {
                bool isMac = MacKeymap.ForceMacKeymapOverride.Value;
                bool hasPrimary = isMac
                    ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                    : e.KeyModifiers.HasFlag(KeyModifiers.Control);
                if (!hasPrimary && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
                {
                    if (e.Key != Key.OemTilde && e.Key != Key.Tab)
                        return;
                }
            }
            SimulateShortcut(e.Key, e.KeyModifiers);
        }

        public void SendTerminalCommand(string command)
        {
            EnsureInitialTerminalSession();
            if (activeTerminalSession?.Control != null)
            {
                string cmdText = command ?? "";
                if (activeTerminalSession.Control.Session != null && activeTerminalSession.Control.Session.IsRunning)
                {
                    activeTerminalSession.Control.WriteAutomatedInput(cmdText + "\r", echoLocallyWhenLive: true);
                }
                else
                {
                    activeTerminalSession.Control.FeedAnsiPreview(cmdText + "\r\n");
                }
            }
        }

        public async Task<string> SendChatMessageAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return "";

            AgentFeed.AddMessage(new AvaloniaAgentFeedItem
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now
            });

            var preferredVendor = aiEngine.Vendors.FirstOrDefault(v =>
                string.Equals(v.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Id, StudioSettings.NormalizeProviderId(settings.ActiveProvider), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Id, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))
                ?? aiEngine.Vendors.FirstOrDefault();

            var pills = new List<string>(attachedContextPills);
            if (!string.IsNullOrEmpty(activeTab?.FileName) && !pills.Contains(activeTab.FileName))
                pills.Add(activeTab.FileName);

            var turnResult = await aiEngine.RunAutonomousAgentLoopAsync(
                currentAgentMode,
                prompt,
                preferredVendor,
                settings.ActiveHarnessId,
                activeTab,
                workspaceRoot,
                workspaceFiles,
                pills,
                activeTerminalSession?.Control?.GetVisibleScreenText() ?? "");

            if (turnResult.ToolCalls != null)
            {
                foreach (var tc in turnResult.ToolCalls)
                {
                    AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                    {
                        Role = "tool",
                        Content = $"{tc.ToolName}: {tc.Summary}"
                    });
                }
            }

            AgentFeed.AddMessage(new AvaloniaAgentFeedItem
            {
                Role = "assistant",
                ModelBadge = turnResult.ProviderUsed,
                ReasoningText = !string.IsNullOrEmpty(turnResult.ThinkingProcess)
                    ? turnResult.ThinkingProcess
                    : $"Executed harness '{turnResult.HarnessUsed}' in {turnResult.Iterations} turn(s).",
                ToolItems = turnResult.ToolCalls?.Select(t => t.ToolName).ToList() ?? new List<string>(),
                Content = turnResult.AssistantReply ?? ""
            });

            if (turnResult.StagedFileContents != null && turnResult.StagedFileContents.Count > 0)
            {
                foreach (var kv in turnResult.StagedFileContents)
                {
                    if (activeTab != null && (string.IsNullOrEmpty(activeTab.FilePath) ||
                        (!string.IsNullOrEmpty(kv.Key) && string.Equals(Path.GetFullPath(activeTab.FilePath), Path.GetFullPath(kv.Key), StringComparison.OrdinalIgnoreCase))))
                    {
                        PrimaryEditor.StageProposedDiffContent(kv.Value);
                        RefreshChangesetBanner();
                    }

                    AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                    {
                        Role = "diff_review",
                        DiffFilePath = kv.Key,
                        DiffAddedLines = Math.Max(1, activeTab?.ProposedNewLines?.Count ?? 1),
                        DiffRemovedLines = Math.Max(1, activeTab?.RemovedLineHistory?.Values.Sum(v => v.Count) ?? 1),
                        DiffStatus = "Pending",
                        DiffPreviewText = kv.Value
                    });
                }
            }
            else
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                {
                    Role = "system",
                    Content = $"Completed in {turnResult.ElapsedMs} ms"
                });
            }

            return turnResult.AssistantReply ?? "";
        }
    }

    public sealed class EditorTextBridge
    {
        private readonly MainWindow owner;
        public EditorTextBridge(MainWindow window) => owner = window;

        public string Text
        {
            get => owner.PrimaryEditor?.CurrentBuffer?.Content ?? "";
            set
            {
                var buf = owner.PrimaryEditor?.CurrentBuffer;
                if (buf != null)
                {
                    buf.Content = value ?? "";
                    buf.SyncLinesFromContent();
                    buf.IsDirty = (buf.Content != buf.SavedContent);
                    owner.RefreshTabStrip();
                    owner.UpdateWindowTitle();
                    owner.PrimaryEditor.Canvas.InvalidateVisual();
                }
            }
        }
    }

    public sealed class WorkspaceSearchMatch
    {
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public string LineText { get; set; }
    }
}
