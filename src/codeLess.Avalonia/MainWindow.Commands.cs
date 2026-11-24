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
        /// <summary>True when the key event originates from the integrated terminal grid.</summary>
        /// <summary>The terminal control that contains <paramref name="source"/> (focused grid canvas, split terminals), or null.</summary>
        private static AvaloniaTerminalEmulatorControl TerminalControlOf(object source)
        {
            var v = source as global::Avalonia.Visual;
            while (v != null)
            {
                if (v is AvaloniaTerminalEmulatorControl t) return t;
                v = global::Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
            }
            return null;
        }

        private static bool IsFromTerminal(object source)
        {
            var v = source as global::Avalonia.Visual;
            while (v != null)
            {
                if (v is AvaloniaTerminalEmulatorControl) return true;
                v = global::Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
            }
            return false;
        }

        /// <summary>
        /// VS Code "commandsToSkipShell": the few workbench shortcuts that still work while the terminal has focus.
        /// Everything else (Ctrl+A/E/K/W/R/P/N/B..., Option/Alt combos, F-keys) must reach the shell / TUI app.
        /// </summary>
        public static bool IsWorkbenchShortcutAllowedFromTerminal(Key key, KeyModifiers mods)
        {
            bool ctrl = mods.HasFlag(KeyModifiers.Control);
            bool meta = mods.HasFlag(KeyModifiers.Meta);
            bool shift = mods.HasFlag(KeyModifiers.Shift);
            if (key == Key.F1) return true;
            if (DebugKeysSkipShell && (key == Key.F5 || key == Key.F6 || key == Key.F9 || key == Key.F10 || key == Key.F11)) return true;
            if (ctrl && (key == Key.OemTilde || key == Key.Tab || key == Key.PageUp || key == Key.PageDown)) return true;
            if (MacKeymap.IsMacOS)
            {
                // Cmd shortcuts are never shell input; the terminal itself handles Cmd+C/V/K/A/I.
                if (!meta) return false;
                return key != Key.C && key != Key.V && key != Key.K && key != Key.A && key != Key.I &&
                       key != Key.Left && key != Key.Right && key != Key.Back && key != Key.Delete;
            }
            if (!ctrl) return false;
            if (shift) return key != Key.C && key != Key.V; // Ctrl+Shift+P, Ctrl+Shift+` ... but not copy/paste
            return key == Key.P || key == Key.B || key == Key.J || (key >= Key.D1 && key <= Key.D9) || key == Key.OemComma;
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
            if (DispatchKeybinding(e.Key, e.KeyModifiers)) { e.Handled = true; return; }
            if (DispatchKeybinding(e.Key, e.KeyModifiers)) { e.Handled = true; return; }

        {
            if (editorSwitcher.IsOpen && HandleEditorSwitcherKey(e.Key, e.KeyModifiers)) { e.Handled = true; return; }
            if (IsFromTerminal(e.Source) && !IsWorkbenchShortcutAllowedFromTerminal(e.Key, e.KeyModifiers))
                return;

            if (e.Source is TextBox &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Meta) &&
                !e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
                e.Key != Key.Escape &&
                e.Key != Key.F1 && e.Key != Key.F3 && e.Key != Key.F4 && e.Key != Key.F5 &&
                e.Key != Key.F6 && e.Key != Key.F10 &&
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
            using (MacKeymap.BeginLenientPrimaryMatching())
                return SimulateShortcutCore(key, modifiers);
        }

        private bool SimulateShortcutCore(Key key, KeyModifiers modifiers)
        {
            if (editorSwitcher.IsOpen && HandleEditorSwitcherKey(key, modifiers)) return true;
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
            bool ctrlOrCmd = MacKeymap.IsPrimary(modifiers);
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
                if (key == Key.V && !ctrlOrCmd && !shift)
                {
                    OpenMarkdownPreview(toSide: true);
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
                // Do NOT open/focus the inline AI bar here: stealing focus into its TextBox made every letter-only
                // chord (Cmd+K S/F/Z/W/M/Enter...) type into the box instead. Cmd+K Cmd+K or Cmd+I opens it.
                return true;
            }

            if (HandleDevToolsShortcut(key, modifiers)) return true;
            if (HandleParityShortcut(key, modifiers)) return true;

            // Escape closes open modal or command palette or Zen mode
            if (key == Key.Escape)
            {
                if (ModalHost.IsVisible) { ModalHost.CloseModal(); return true; }
                if (CommandPalette.IsVisible) { CommandPalette.Close(restorePreviewTheme: true); return true; }
                if (isZenMode && shift) { ToggleZenMode(); return true; }
                return false;
            }

            if (HandleDebugFunctionKey(key, modifiers)) return true;

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
            // F4 / Shift+F4 -> next / previous Search result (VS Code); falls through when there are no results
            if (key == Key.F4 && !ctrlOrCmd && !alt && NavigateSearchResult(shift ? -1 : 1))
                return true;
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
                    RequestNewTerminal();
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
            // On macOS Option+Left/Right is word navigation in the editor/terminal, so it must not be swallowed here.
            if ((!MacKeymap.IsMacOS && alt && !ctrlOrCmd && !shift && key == Key.Left) || (MacKeymap.IsMacOS && ctrl && !meta && !shift && key == Key.OemMinus))
            {
                NavigateBack();
                return true;
            }
            if ((!MacKeymap.IsMacOS && alt && !ctrlOrCmd && !shift && key == Key.Right) || (MacKeymap.IsMacOS && ctrl && !meta && shift && key == Key.OemMinus))
            {
                NavigateForward();
                return true;
            }

            // Ctrl+Tab / Ctrl+Shift+Tab (every platform, also on macOS like VS Code): recently used editors picker (MRU).
            if (ctrl && !alt && key == Key.Tab)
            {
                OpenOrAdvanceEditorSwitcher(backwards: shift);
                return true;
            }

            // Ctrl/Cmd + PageDown/PageUp or Cmd+Option+Left/Right -> next / previous editor in tab-strip order
            if ((ctrlOrCmd && (key == Key.PageDown || key == Key.PageUp)) ||
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

            // Ctrl/Cmd + 1..9 -> Activate tab by 1-based index (Alt+digit too on Windows/Linux; on macOS
            // Option+digit types characters such as ¡ ™ £ and must reach the editor/terminal).
            if ((ctrlOrCmd || (alt && !MacKeymap.IsMacOS)) && !shift && key >= Key.D1 && key <= Key.D9)
            {
                int tabIndex = (int)(key - Key.D1);
                if (tabIndex >= 0 && tabIndex < openTabs.Count)
                {
                    ActivateTab(openTabs[tabIndex]);
                    return true;
                }
            }

            // macOS uses VS Code's mac keymap here: Control+G = Go to Line, Control+Shift+G = Source Control (literal
            // Control, not Cmd), while Cmd+G / Cmd+Shift+G are Find Next / Find Previous.
            if (MacKeymap.IsMacOS && key == Key.G && !alt)
            {
                if (ctrl && !meta)
                {
                    if (shift) SwitchSidebarView("git"); else OpenCommandPalette(":");
                    return true;
                }
                if (meta && !ctrl)
                {
                    var ed = FocusedEditorControl() ?? PrimaryEditor;
                    ed?.FindNext(shift ? -1 : 1);
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
                    case Key.F: ShowSearchView(false); return true;
                    case Key.H: ShowSearchView(true); return true;
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
                        SwitchBottomTab("debug");
                        FocusBottomTabContent("debug");
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
            outputStreamBox.IsVisible = activeBottomTab == "output" || activeBottomTab == "ports";
            if (debugConsolePanel != null) debugConsolePanel.IsVisible = activeBottomTab == "debug";
            terminalHostGrid.IsVisible = activeBottomTab == "terminal";

            outputChannelCombo.IsVisible = activeBottomTab == "output";
            shellProfileSelector.IsVisible = activeBottomTab == "terminal";
            UpdatePanelToolbarForActiveTab();
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
            foreach (var ib in panelToolbarIconButtons) WorkbenchStyles.ThemeIconButton(ib, currentTheme);
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
            CaptureCurrentNavPosition();
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
            CaptureCurrentNavPosition();
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

        /// <summary>F12 / context menu "Go to Definition": same resolution as Cmd/Ctrl+Click at the caret.</summary>
        public async void GoToDefinitionAtCursor()
        {
            if (activeTab == null) return;
            int line = activeTab.CursorLine;
            int col = activeTab.CursorCol;
            // A running language server answers first; the built-in resolver is the fallback.
            var srcTab = activeTab;
            if (await TryLspNavigateAsync(srcTab, line, col, "textDocument/definition", "definitions")) return;
            if (!ReferenceEquals(srcTab, activeTab)) return;
            var target = PrimaryEditor.ResolveLinkAt(activeTab, line, col);
            if (target == null && col > 0) target = PrimaryEditor.ResolveLinkAt(activeTab, line, col - 1);
            if (target == null)
            {
                SetStatusMessage("No definition found at the cursor.");
                return;
            }
            FollowEditorLink(activeTab, target);
        }

        /// <summary>
        /// VS Code "follow link": URLs open in the browser, imports / includes / paths open the file (at :line:col or at the
        /// imported symbol), identifiers jump to their definition (imported module first, then this file, then the workspace).
        /// </summary>
        public void FollowEditorLink(EditorTabBuffer source, EditorLinkTarget target)
        {
            if (target == null) return;
            if (target.Kind == EditorLinkTarget.KindUrl)
            {
                if (EditorLinkResolver.OpenExternalUrl(target.Url)) SetStatusMessage("Opened " + target.Url);
                return;
            }
            if (!string.IsNullOrEmpty(target.FilePath))
            {
                NavigateToFileLocation(source, target.FilePath, target.Line, target.Column, target.SymbolName);
                return;
            }
            if (!string.IsNullOrEmpty(target.SymbolName))
                GoToSymbolDefinition(source, target.SymbolName);
        }

        private void NavigateToFileLocation(EditorTabBuffer source, string path, int line, int col, string symbol)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatusMessage("File not found: " + path);
                return;
            }
            var origin = source ?? activeTab;
            if (origin != null) RecordNavigationHistory(origin.FilePath, origin.CursorLine + 1);
            // Only the origin and the final destination go into Back/Forward history (not the tab's previous caret).
            EditorTabBuffer tab;
            bool prevSuppress = suppressNavRecord;
            suppressNavRecord = true;
            try
            {
                tab = OpenFileInTab(path, preview: false);
                if (tab == null) return;
                if (line > 0)
                {
                    PrimaryEditor.GoToLine(line, Math.Max(1, col));
                }
                else if (!string.IsNullOrEmpty(symbol) && tab.Lines != null &&
                         EditorLinkResolver.FindDefinitionInLines(tab.Lines, symbol, true, out int defLine, out int defCol))
                {
                    PrimaryEditor.GoToLine(defLine + 1, defCol + 1);
                }
            }
            finally { suppressNavRecord = prevSuppress; }
            RecordNavigationHistory(tab.FilePath, tab.CursorLine + 1);
            PrimaryEditor.Canvas.Focus();
            string rel = !string.IsNullOrEmpty(workspaceRoot) && path.StartsWith(workspaceRoot, PathKeyComparison)
                ? Path.GetRelativePath(workspaceRoot, path)
                : path;
            SetStatusMessage(string.IsNullOrEmpty(symbol) ? "Opened " + rel : $"Definition: {symbol} ({rel}:{tab.CursorLine + 1})");
        }

        private void GoToSymbolDefinition(EditorTabBuffer source, string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            var buf = source ?? activeTab;

            void jumpInSource(int line, int col)
            {
                if (buf == null) return;
                if (!string.IsNullOrEmpty(buf.FilePath))
                {
                    OpenFileAtLocation(buf.FilePath, line, col, 0, preview: false, focusEditor: true);
                    return;
                }
                RecordNavigationHistory(buf.FilePath, buf.CursorLine + 1);
                bool prevSuppress = suppressNavRecord;
                suppressNavRecord = true;
                try
                {
                    if (!ReferenceEquals(activeTab, buf)) ActivateTab(buf);
                    PrimaryEditor.GoToLine(line, col);
                }
                finally { suppressNavRecord = prevSuppress; }
                RecordNavigationHistory(buf.FilePath, line);
            }

            var localSym = buf?.Symbols?.FirstOrDefault(s => string.Equals(s.Name, word, StringComparison.Ordinal));
            if (localSym != null)
            {
                jumpInSource(localSym.LineNumber, 1);
                return;
            }
            if (buf?.Lines != null && EditorLinkResolver.FindDefinitionInLines(buf.Lines, word, false, out int dl, out int dc) && dl != buf.CursorLine)
            {
                jumpInSource(dl + 1, dc + 1);
                return;
            }

            void jump()
            {
                var wsSym = aiEngine?.WorkspaceSymbolMap?.FirstOrDefault(s => string.Equals(s.SymbolName, word, StringComparison.Ordinal))
                            ?? aiEngine?.WorkspaceSymbolMap?.FirstOrDefault(s => string.Equals(s.SymbolName, word, StringComparison.OrdinalIgnoreCase));
                if (wsSym != null && File.Exists(wsSym.FilePath))
                {
                    OpenFileAtLocation(wsSym.FilePath, wsSym.LineNumber, 1, 0, preview: false, focusEditor: true);
                    SetStatusMessage($"Definition: {word} ({Path.GetFileName(wsSym.FilePath)}:{wsSym.LineNumber})");
                }
                else
                {
                    SetStatusMessage($"No definition found for '{word}'");
                }
            }
            if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) EnsureWorkspaceIndexedInBackground(jump);
            else { EnsureWorkspaceIndexed(); jump(); }
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
            if (!ConfirmSaveOrDiscardDirtyTabs(openTabs, CloseAllTabs)) return;
            openTabs.Clear();
            ActivateTab(null);
        }

        public void CloseWorkspaceFolder()
        {
            if (!ConfirmSaveOrDiscardDirtyTabs(openTabs, CloseWorkspaceFolder)) return;
            StopWorkspaceWatcher();
            workspaceRoot = "";
            LspOnWorkspaceChanged();
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

            string ctxDir = isDir ? fullPath : Path.GetDirectoryName(fullPath);
            items.Add(MakeCtxItem("New File...", () => PromptCreateNewFileInWorkspace(ctxDir)));
            items.Add(MakeCtxItem("New Folder...", () => PromptCreateNewFolderInWorkspace(ctxDir)));
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
            items.Add(MakeCtxItem("Find in Folder...", () => FindInFolder(ctxDir)));
            items.Add(new Separator());
            items.Add(MakeCtxItem("Copy Path   (Shift+Alt+C)", () => _ = CopyPathToClipboardAsync(fullPath)));
            items.Add(MakeCtxItem($"Copy Relative Path   ({scCopyRel})", () =>
            {
                string rel = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fullPath) : Path.GetFileName(fullPath);
                _ = CopyPathToClipboardAsync(rel);
            }));
            items.Add(new Separator());
            if (isDir && ExplorerTree.Nodes.Contains(node))
            {
                // A workspace root: VS Code offers Remove Folder from Workspace instead of rename / delete.
                if (ExplorerTree.Nodes.Count > 1)
                    items.Add(MakeCtxItem("Remove Folder from Workspace", () => RemoveFolderFromWorkspace(fullPath)));
                items.Add(MakeCtxItem("Add Folder to Workspace...", () => _ = PromptAddFolderToWorkspaceAsync()));
            }
            else
            {
                items.Add(MakeCtxItem("Rename...   (F2)", () => PromptRenameExplorerNode(node)));
                items.Add(MakeCtxItem("Delete   (Move to Trash)", () => DeleteExplorerNodeToTrash(node)));
            }

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
                ItemsSource = BuildParityEditorContextItems(tab).Concat(new object[]
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
                    MakeCtxItem("Go to Type Definition", GoToTypeDefinitionAtCursor),
                    MakeCtxItem("Go to Implementation", GoToImplementationAtCursor),
                    MakeCtxItem("Find All References   (Shift+F12)", FindAllReferencesAtCursor),
                    MakeCtxItem($"Go to Symbol in Editor...   ({scSym})", () => OpenCommandPalette("@")),
                    MakeCtxItem("Rename Symbol   (F2)", RenameSymbolAtCursor),
                    MakeCtxItem("Quick Fix...   (" + AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+.") + ")", ShowQuickFixes),
                    MakeCtxItem("Format Document   (" + AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Shift+Alt+F") + ")", () => FormatDocumentOrSelection(false)),
                    MakeCtxItem("Change All Occurrences   (Ctrl+Shift+L)", () => PrimaryEditor.Canvas.SelectAllFindOccurrences()),
                    new Separator(),
                    MakeCtxItem($"Find   ({scF})", () => PrimaryEditor.ShowFindBar(false)),
                    MakeCtxItem("Toggle Line Comment", () => PrimaryEditor.Canvas.ToggleLineComment()),
                    new Separator(),
                    MakeCtxItem("Select All", () => { tab.SelectAll(); PrimaryEditor.Canvas.InvalidateVisual(); }),
                    MakeCtxItem($"Command Palette...   ({scPal})", () => OpenCommandPalette(">"))
                }).ToList()
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

        /// <summary>Focus a modal's text box once it is attached and laid out (an immediate Focus() is a no-op).</summary>
        private static void FocusPromptTextBox(TextBox tb, bool selectNameWithoutExtension = false)
        {
            void apply()
            {
                tb.Focus();
                string t = tb.Text ?? "";
                int dot = selectNameWithoutExtension ? t.LastIndexOf('.') : -1;
                if (dot > 0)
                {
                    tb.SelectionStart = 0;
                    tb.SelectionEnd = dot;
                }
                else
                {
                    tb.SelectAll();
                }
            }
            apply();
            Dispatcher.UIThread.Post(apply, DispatcherPriority.Loaded); // static helper, already on the UI thread
        }

        public void PromptCreateNewFileInWorkspace() => PromptCreateNewFileInWorkspace(null);

        public void PromptCreateNewFileInWorkspace(string targetDir)
        {
            string baseDir = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir) ? targetDir : GetSelectedExplorerDirectory();
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Enter new file name in '{Path.GetFileName(baseDir.TrimEnd('/', '\\'))}' (use / for sub-folders):",
                "",
                currentTheme,
                fileName =>
                {
                    ModalHost.CloseModal();
                    CreateFileInDirectory(baseDir, fileName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("new-file", "New File", view, 440);
            FocusPromptTextBox(tb);
        }

        public string CreateFileInDirectory(string baseDir, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                baseDir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Path.GetTempPath();
            string err = ValidateNewEntryName(baseDir, fileName, out string fullPath);
            if (err != null) { ReportFileOperationError("New File", err); return null; }
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                ReportFileOperationError("New File", $"A file or folder '{Path.GetFileName(fullPath)}' already exists at this location. Please choose a different name.");
                return null;
            }
            try
            {
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
                using (new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write)) { }
                RefreshWorkspaceAfterFileOperation(fullPath);
                OpenFileInTab(fullPath, preview: false);
                return fullPath;
            }
            catch (Exception ex)
            {
                ReportFileOperationError("New File", $"Unable to create '{fileName.Trim()}': {ex.Message}");
                return null;
            }
        }

        public void PromptCreateNewFolderInWorkspace() => PromptCreateNewFolderInWorkspace(null);

        public void PromptCreateNewFolderInWorkspace(string targetDir)
        {
            string baseDir = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir) ? targetDir : GetSelectedExplorerDirectory();
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                $"Enter new folder name in '{Path.GetFileName(baseDir.TrimEnd('/', '\\'))}' (use / for nested folders):",
                "",
                currentTheme,
                folderName =>
                {
                    ModalHost.CloseModal();
                    CreateFolderInDirectory(baseDir, folderName);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("new-folder", "New Folder", view, 440);
            FocusPromptTextBox(tb);
        }

        public string CreateFolderInDirectory(string baseDir, string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                baseDir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Path.GetTempPath();
            string err = ValidateNewEntryName(baseDir, folderName, out string fullPath);
            if (err != null) { ReportFileOperationError("New Folder", err); return null; }
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                ReportFileOperationError("New Folder", $"A file or folder '{Path.GetFileName(fullPath)}' already exists at this location. Please choose a different name.");
                return null;
            }
            try
            {
                Directory.CreateDirectory(fullPath);
                RefreshWorkspaceAfterFileOperation(fullPath);
                return fullPath;
            }
            catch (Exception ex)
            {
                ReportFileOperationError("New Folder", $"Unable to create folder '{folderName.Trim()}': {ex.Message}");
                return null;
            }
        }

        private void ReportFileOperationError(string title, string message)
        {
            SetStatusMessage(message);
            AppendOutputLog($"[{title}] {message}");
            if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
                ShowMessageModal(title, message);
        }

        public void PromptRenameExplorerNode(ExplorerTreeNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Tag)) return;
            if (ExplorerTree.Nodes.Contains(node)) return; // workspace roots are renamed in the OS, not in-place
            string oldPath = node.Tag;
            string oldName = Path.GetFileName(oldPath);
            bool isFile = File.Exists(oldPath);
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
            FocusPromptTextBox(tb, selectNameWithoutExtension: isFile);
        }

        public bool RenameExplorerPath(string oldPath, string newName)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrWhiteSpace(newName)) return false;
            string dir = Path.GetDirectoryName(oldPath.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(dir)) return false;
            string err = ValidateNewEntryName(dir, newName, out string newPath);
            if (err != null) { ReportFileOperationError("Rename", err); return false; }
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return false;
            bool caseOnlyChange = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnlyChange && (File.Exists(newPath) || Directory.Exists(newPath)))
            {
                ReportFileOperationError("Rename", $"A file or folder '{Path.GetFileName(newPath)}' already exists at this location. Please choose a different name.");
                return false;
            }
            try
            {
                string parent = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                bool isDir = Directory.Exists(oldPath);
                if (caseOnlyChange)
                {
                    // Case-insensitive volumes (default macOS / Windows) need a two-step rename for "foo" -> "Foo".
                    string tmp = oldPath + ".codeless-rename-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    if (isDir) { Directory.Move(oldPath, tmp); Directory.Move(tmp, newPath); }
                    else { File.Move(oldPath, tmp); File.Move(tmp, newPath); }
                }
                else if (isDir) Directory.Move(oldPath, newPath);
                else File.Move(oldPath, newPath);

                // Re-point open editors for the file itself or anything below a renamed folder.
                string oldPrefix = oldPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                foreach (var t in openTabs)
                {
                    if (string.IsNullOrEmpty(t.FilePath)) continue;
                    string updated = null;
                    if (string.Equals(t.FilePath, oldPath, PathKeyComparison)) updated = newPath;
                    else if (isDir && t.FilePath.StartsWith(oldPrefix, PathKeyComparison))
                        updated = Path.Combine(newPath, t.FilePath.Substring(oldPrefix.Length));
                    if (updated == null) continue;
                    t.FilePath = updated;
                    t.FileName = Path.GetFileName(updated);
                    t.RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, updated) : t.FileName;
                }
                RefreshWorkspaceAfterFileOperation(newPath);
                RefreshTabStrip();
                UpdateWindowTitle();
                return true;
            }
            catch (Exception ex)
            {
                ReportFileOperationError("Rename", $"Unable to rename '{Path.GetFileName(oldPath)}': {ex.Message}");
                return false;
            }
        }

        public void DeleteExplorerNodeToTrash(ExplorerTreeNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Tag)) return;
            if (ExplorerTree.Nodes.Contains(node)) return; // never delete a workspace root from the tree
            string path = node.Tag;
            bool isDir = Directory.Exists(path);
            string trashName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Recycle Bin" : "Trash";
            string msg = isDir
                ? $"Are you sure you want to delete '{Path.GetFileName(path)}' and its contents?\n\nYou can restore it from the {trashName}."
                : $"Are you sure you want to delete '{Path.GetFileName(path)}'?\n\nYou can restore it from the {trashName}.";
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                DeletePathToTrashOrRecycleBin(path);
                return;
            }
            ShowConfirmModal("Delete", msg, $"Move to {trashName}", () => DeletePathToTrashOrRecycleBin(path));
        }

        public bool DeletePathToTrashOrRecycleBin(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            bool isDir = Directory.Exists(fullPath);
            string prefix = fullPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var affected = openTabs.Where(t => !string.IsNullOrEmpty(t.FilePath) &&
                (string.Equals(t.FilePath, fullPath, PathKeyComparison) || (isDir && t.FilePath.StartsWith(prefix, PathKeyComparison)))).ToList();

            bool done = TryMoveToSystemTrash(fullPath, out string trashError);
            if (!done && AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                // Trash unavailable (e.g. network volume): ask before deleting permanently, like VS Code.
                ShowConfirmModal("Delete Permanently",
                    $"Could not move '{Path.GetFileName(fullPath)}' to the trash ({trashError ?? "unsupported location"}).\n\nDelete it permanently? This cannot be undone.",
                    "Delete Permanently",
                    () =>
                    {
                        if (PermanentlyDeletePath(fullPath))
                            FinishDelete(affected, fullPath);
                    });
                return false;
            }
            if (!done) done = PermanentlyDeletePath(fullPath);
            if (done) FinishDelete(affected, fullPath);
            return done;
        }

        private bool PermanentlyDeletePath(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
                else if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                return true;
            }
            catch (Exception ex)
            {
                ReportFileOperationError("Delete", $"Unable to delete '{Path.GetFileName(fullPath)}': {ex.Message}");
                return false;
            }
        }

        private void FinishDelete(List<EditorTabBuffer> affectedTabs, string fullPath)
        {
            foreach (var t in affectedTabs)
                CloseTab(t, forceWithoutPrompt: true);
            RefreshWorkspaceAfterFileOperation(Path.GetDirectoryName(fullPath.TrimEnd('/', '\\')));
            SetStatusMessage($"Deleted '{Path.GetFileName(fullPath)}'.");
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

        // Source Control (SCM) Git Operations
        public void RefreshGitStatus()
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                scmBranchBanner.Text = "No folder opened";
                SetStatusBranch("no-git");
                return;
            }

            string branch = LlmHarnessAndAgentEngine.GetGitBranch(workspaceRoot);
            bool noGit = string.IsNullOrEmpty(branch) || branch == "no-git";
            var entries = noGit
                ? new List<GitStatusEntry>()
                : (LlmHarnessAndAgentEngine.GetGitStatusEntries(workspaceRoot) ?? new List<GitStatusEntry>());
            ApplyGitEntriesToUi(branch, entries);
        }

        private void ApplyGitEntriesToUi(string branch, List<GitStatusEntry> entries)
        {
            currentGitEntries = entries ?? new List<GitStatusEntry>();
            bool noGit = string.IsNullOrEmpty(branch) || branch == "no-git";
            if (!noGit) RefreshGutterBaselines(); // staging / commits change the index the gutter compares with
            scmBranchBanner.Text = IsWorkspaceRestricted && noGit ? "Source control is disabled in Restricted Mode (Manage Workspace Trust)"
                : noGit ? "No Git repository initialized" : $"Branch: {branch} ({currentGitEntries.Count} changed)";
            SetStatusBranch(noGit ? "no-git" : branch);

            // Update Activity Bar Git badge
            if (activityBarButtons.TryGetValue("git", out var gitBtnTuple))
            {
                gitBtnTuple.Icon.BadgeCount = currentGitEntries.Count;
                gitBtnTuple.Icon.InvalidateVisual();
            }
            RefreshBranchStatusAsync();

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
            if (RebuildScmGroupsUi()) return;
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
                    Content = new LiteralTextBlock($"{ScmLabel(captured)}   ({captured.IndexStatus})")
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
                var unstageBtn = WorkbenchStyles.IconButton("minus", "Unstage Changes", () => ExecuteGitUnstageFile(captured), currentTheme, 22, 14);
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
                    Content = new LiteralTextBlock($"{ScmLabel(captured)}   ({(captured.IndexStatus == '?' ? 'U' : captured.DisplayBadge)})")
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
                var stageBtn = WorkbenchStyles.IconButton("plus", "Stage Changes", () => ExecuteGitStageFile(captured), currentTheme, 22, 14);
                var discardBtn = WorkbenchStyles.IconButton("discard", "Discard Changes", () => ConfirmAndDiscardGitFile(captured), currentTheme, 22, 14);
                row.Children.Add(openBtn);
                Grid.SetColumn(stageBtn, 1);
                row.Children.Add(stageBtn);
                Grid.SetColumn(discardBtn, 2);
                row.Children.Add(discardBtn);
                scmChangesListStack.Children.Add(row);
            }
        }

        private string ScmLabel(GitStatusEntry e)
        {
            string name = Path.GetFileName((e.RepoRelativePath ?? e.RelativePath ?? "").TrimEnd('/'));
            bool multiRepo = currentGitEntries.Select(x => x.RepoRoot).Distinct().Count() > 1;
            return multiRepo && !string.IsNullOrEmpty(e.RepoRoot) ? $"{name}  ·  {Path.GetFileName(e.RepoRoot)}" : name;
        }

        /// <summary>Runs git with properly escaped arguments (no shell, no manual quoting).</summary>
        public static string RunGit(string repoRoot, int timeoutMs, out int exitCode, params string[] args)
        {
            exitCode = -1;
            if (GitSafety.BlockReason(repoRoot, args) != null) return ""; // Restricted Mode
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };
                GitSafety.Apply(psi, repoRoot);
                psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                foreach (string a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return "git could not be started.";
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return "git timed out.";
                }
                exitCode = p.ExitCode;
                Task.WaitAll(new Task[] { outTask, errTask }, 2000);
                string o = outTask.IsCompleted ? outTask.Result : "";
                string er = errTask.IsCompleted ? errTask.Result : "";
                return (o + (string.IsNullOrWhiteSpace(er) ? "" : (o.Length > 0 ? "\n" : "") + er)).Trim();
            }
            catch (Exception ex)
            {
                return "git is not available: " + ex.Message;
            }
        }

        private IEnumerable<string> RepoRootsForScm()
        {
            var roots = currentGitEntries.Select(e => e.RepoRoot).Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
            if (roots.Count == 0)
            {
                string r = LlmHarnessAndAgentEngine.FindGitRepoRoot(workspaceRoot);
                if (r != null) roots.Add(r);
            }
            return roots;
        }

        private void ReportGitFailure(string op, string output)
        {
            AppendOutputLog($"[Git {op}] {output}");
            SetStatusMessage($"Git {op} failed: {(output ?? "").Split('\n').FirstOrDefault()}");
            if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
                ShowMessageModal($"Git {op} failed", output);
        }

        private bool ResolveEntry(GitStatusEntry e, out string repo, out string rel)
        {
            repo = !string.IsNullOrEmpty(e?.RepoRoot) ? e.RepoRoot : LlmHarnessAndAgentEngine.FindGitRepoRoot(workspaceRoot);
            rel = !string.IsNullOrEmpty(e?.RepoRelativePath) ? e.RepoRelativePath : e?.RelativePath;
            return !string.IsNullOrEmpty(repo) && !string.IsNullOrEmpty(rel);
        }

        /// <summary>Workspace-relative overloads (used by tests / automation).</summary>
        private GitStatusEntry EntryForWorkspaceRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(workspaceRoot)) return null;
            string full = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
            var known = currentGitEntries.FirstOrDefault(e => string.Equals(e.FullPath, full, PathKeyComparison));
            if (known != null) return known;
            string repo = LlmHarnessAndAgentEngine.FindGitRepoRoot(Path.GetDirectoryName(full));
            if (repo == null) return null;
            return new GitStatusEntry
            {
                FullPath = full,
                RelativePath = relativePath,
                RepoRoot = repo,
                RepoRelativePath = LlmHarnessAndAgentEngine.GetRelativePath(repo, full),
                IndexStatus = ' ',
                WorkingTreeStatus = 'M'
            };
        }

        public void ExecuteGitStageFile(string relativePath) => ExecuteGitStageFile(EntryForWorkspaceRelativePath(relativePath));
        public void ExecuteGitUnstageFile(string relativePath) => ExecuteGitUnstageFile(EntryForWorkspaceRelativePath(relativePath));
        public void ExecuteGitDiscardFile(string relativePath) => ExecuteGitDiscardFile(EntryForWorkspaceRelativePath(relativePath));

        public void ExecuteGitStageFile(GitStatusEntry entry)
        {
            if (!ResolveEntry(entry, out string repo, out string rel)) return;
            string res = RunGit(repo, 15000, out int code, "add", "-A", "--", rel);
            if (code != 0) ReportGitFailure("Stage", res);
            RefreshGitStatus();
        }

        public void ExecuteGitUnstageFile(GitStatusEntry entry)
        {
            if (!ResolveEntry(entry, out string repo, out string rel)) return;
            string res = RunGit(repo, 15000, out int code, "restore", "--staged", "--", rel);
            if (code != 0) res = RunGit(repo, 15000, out code, "reset", "-q", "--", rel); // git < 2.23 / unborn branch
            if (code != 0) ReportGitFailure("Unstage", res);
            RefreshGitStatus();
        }

        public void ConfirmAndDiscardGitFile(GitStatusEntry entry)
        {
            if (entry == null) return;
            bool untracked = entry.IndexStatus == '?';
            string name = Path.GetFileName(entry.FullPath.TrimEnd('/', '\\'));
            string msg = untracked
                ? $"Are you sure you want to DELETE '{name}'?\n\nThis file is untracked; it will be moved to the trash."
                : $"Are you sure you want to discard changes in '{name}'?\n\nThis is IRREVERSIBLE: your current working set will be lost.";
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) { ExecuteGitDiscardFile(entry); return; }
            ShowConfirmModal("Discard Changes", msg, untracked ? "Delete File" : "Discard File", () => ExecuteGitDiscardFile(entry));
        }

        public void ExecuteGitDiscardFile(GitStatusEntry entry)
        {
            if (!ResolveEntry(entry, out string repo, out string rel)) return;
            if (entry.IndexStatus == '?')
            {
                // "git checkout" cannot discard an untracked file; VS Code deletes it.
                if (!TryMoveToSystemTrash(entry.FullPath, out _))
                {
                    try
                    {
                        if (File.Exists(entry.FullPath)) File.Delete(entry.FullPath);
                        else if (Directory.Exists(entry.FullPath)) Directory.Delete(entry.FullPath, true);
                    }
                    catch (Exception ex) { ReportGitFailure("Discard", ex.Message); }
                }
            }
            else if (entry.IndexStatus == 'A')
            {
                // Newly added (staged) file: unstage and remove the working copy.
                RunGit(repo, 15000, out _, "rm", "-f", "--cached", "--", rel);
                try { if (File.Exists(entry.FullPath)) File.Delete(entry.FullPath); } catch { }
            }
            else
            {
                string res = RunGit(repo, 15000, out int code, "checkout", "--", rel);
                if (code != 0) ReportGitFailure("Discard", res);
            }
            var tab = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, entry.FullPath, PathKeyComparison));
            if (tab != null)
            {
                if (File.Exists(entry.FullPath)) { tab.IsDirty = false; ReloadOpenTabIfChangedOnDisk(entry.FullPath); }
                else CloseTab(tab, forceWithoutPrompt: true);
            }
            RefreshGitStatus();
            RefreshExplorerTree();
        }

        public void ExecuteGitStageAll()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            foreach (string repo in RepoRootsForScm())
            {
                string res = RunGit(repo, 30000, out int code, "add", "-A");
                if (code != 0) ReportGitFailure("Stage All", res);
            }
            RefreshGitStatus();
        }

        public void ExecuteGitUnstageAll()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            foreach (string repo in RepoRootsForScm())
            {
                string res = RunGit(repo, 30000, out int code, "restore", "--staged", ":/");
                if (code != 0) RunGit(repo, 30000, out code, "reset", "-q");
            }
            RefreshGitStatus();
        }

        public bool ExecuteGitCommit(string overrideMessage = null)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return false;
            string msg = overrideMessage ?? (scmCommitInput.Text ?? "").Trim();
            if (TryCommitWithParityOptions(msg, out bool amendHandled)) return amendHandled;
            if (string.IsNullOrEmpty(msg))
            {
                SetStatusMessage("Please enter a commit message.");
                return false;
            }

            // Commit each repository that has staged changes; if nothing is staged anywhere, stage everything first
            // (VS Code "smart commit").
            var entries = currentGitEntries.ToList();
            bool anyStaged = entries.Any(e => e.IsStaged);
            var repos = anyStaged
                ? entries.Where(e => e.IsStaged).Select(e => e.RepoRoot).Distinct().ToList()
                : entries.Select(e => e.RepoRoot).Distinct().ToList();
            repos = repos.Where(r => !string.IsNullOrEmpty(r)).ToList();
            if (repos.Count == 0) repos = RepoRootsForScm().ToList();
            if (repos.Count == 0)
            {
                SetStatusMessage("No git repository found for this workspace.");
                return false;
            }

            string msgFile = Path.Combine(Path.GetTempPath(), "codeless-commit-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(msgFile, msg + "\n", new UTF8Encoding(false));
            scmCommitInput.Text = "";

            void work()
            {
                var results = new List<(string repo, int code, string output)>();
                foreach (string repo in repos)
                {
                    if (!anyStaged) RunGit(repo, 60000, out _, "add", "-A");
                    // Generous timeout: pre-commit hooks (lint, tests) can take a while.
                    string res = RunGit(repo, 180000, out int code, "commit", "-F", msgFile);
                    results.Add((repo, code, res));
                }
                try { File.Delete(msgFile); } catch { }
                void apply()
                {
                    foreach (var r in results)
                    {
                        AppendOutputLog($"[Git Commit] {Path.GetFileName(r.repo)}: {r.output}");
                        if (r.code != 0) ReportGitFailure("Commit", $"{Path.GetFileName(r.repo)}: {r.output}");
                    }
                    if (results.All(r => r.code == 0)) SetStatusMessage("Committed.");
                    RefreshGitStatusInBackground();
                }
                if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) MainUi.Post(apply);
                else apply();
            }

            if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) _ = Task.Run(work);
            else work();
            return true;
        }

        public void OpenGitFileDiffInEditor(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
            var tab = OpenFileInTab(fullPath, preview: false);
            if (tab != null && !string.IsNullOrEmpty(workspaceRoot))
            {
                tab.GitHeadContent = LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(workspaceRoot, fullPath);
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

        /// <summary>Test hook: chooses the folder for a new terminal in a multi-root workspace (null = cancel).</summary>
        public Func<IReadOnlyList<string>, string> TerminalFolderPickerOverride { get; set; }

        /// <summary>
        /// User-initiated "New Terminal". With two or more folders in the workspace, ask which folder the terminal
        /// should start in (VS Code behaviour); with one folder, open it there directly.
        /// </summary>
        public void RequestNewTerminal(string shellProfile = null)
        {
            var roots = new List<string>();
            foreach (string r in GetWorkspaceRootFolders())
            {
                if (!string.IsNullOrEmpty(r) && Directory.Exists(r) && !roots.Contains(r, PathKeyComparer))
                    roots.Add(r);
            }
            if (roots.Count < 2)
            {
                CreateNewTerminalSession(shellProfile);
                return;
            }
            if (TerminalFolderPickerOverride != null)
            {
                string pick = TerminalFolderPickerOverride(roots);
                if (!string.IsNullOrEmpty(pick)) CreateNewTerminalSession(shellProfile, pick);
                return;
            }

            ShowWorkspaceFolderPicker("terminal-folder-picker", "New Terminal", "Select the folder for the new terminal:", roots,
                root => CreateNewTerminalSession(shellProfile, root));
        }

        /// <summary>VS Code-style workspace folder pick list (number keys 1-9, Up/Down, Esc).</summary>
        private void ShowWorkspaceFolderPicker(string dialogId, string title, string prompt, List<string> roots, Action<string> onPick)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = prompt,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(textPrimary)
            });
            var buttons = new List<Button>();
            for (int i = 0; i < roots.Count; i++)
            {
                string root = roots[i];
                string name = Path.GetFileName(root.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = root;
                var content = new StackPanel { Spacing = 1 };
                content.Children.Add(new TextBlock
                {
                    Text = (i < 9 ? (i + 1) + "   " : "    ") + name,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(textPrimary)
                });
                content.Children.Add(new TextBlock
                {
                    Text = "      " + root,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(textSecondary)
                });
                var btn = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 6)
                };
                btn.Click += (s, e) =>
                {
                    ModalHost.CloseModal();
                    onPick(root);
                };
                buttons.Add(btn);
                stack.Children.Add(btn);
            }
            // Number keys 1-9 pick directly; Up/Down move between folders; Esc closes (global handler).
            stack.AddHandler(KeyDownEvent, (s, e) =>
            {
                int idx = -1;
                if (e.Key >= Key.D1 && e.Key <= Key.D9) idx = e.Key - Key.D1;
                else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) idx = e.Key - Key.NumPad1;
                if (idx >= 0 && idx < roots.Count)
                {
                    e.Handled = true;
                    ModalHost.CloseModal();
                    onPick(roots[idx]);
                    return;
                }
                if (e.Key == Key.Down || e.Key == Key.Up)
                {
                    int cur = buttons.FindIndex(b => b.IsFocused);
                    int next = e.Key == Key.Down ? Math.Min(buttons.Count - 1, cur + 1) : Math.Max(0, cur - 1);
                    buttons[Math.Max(0, next)].Focus();
                    e.Handled = true;
                }
            }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            ModalHost.ShowModal(dialogId, title, stack, 520);
            MainUi.Post(() => buttons[0].Focus(), DispatcherPriority.Loaded);
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
            // Cmd/Ctrl+Click file links: relative paths resolve against the shell's cwd, then the workspace root.
            termCtrl.WorkspaceRootProvider = () => workspaceRoot;
            termCtrl.FileLinkActivated += (path, line, col) =>
            {
                if (OpenFileInTab(path, preview: false) != null && line > 0)
                    PrimaryEditor.GoToLine(line, Math.Max(1, col));
            };
            termCtrl.SessionExited += ctrl =>
            {
                // Automatically remove session when user types 'exit' or shell process terminates
                RemoveExitedTerminalSession(entry);
            };

            bool started = false;
            try
            {
                started = termCtrl.StartShell(workDir, resolvedShell);
                // VS Code-style Python venv activation, typed once the shell's first prompt is up.
                if (started && (settings == null || settings.ActivatePythonEnvironmentInTerminal))
                {
                    try { PythonVenvActivator.ScheduleActivation(termCtrl.Session, workspaceRoot); } catch { }
                }
            }
            catch { started = false; }

            // Only seed synthetic VT100 prompt into FallbackBuffer when no live OS shell was started (e.g., headless tests)
            string folderLeaf = Path.GetFileName(workDir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderLeaf)) folderLeaf = "workspace";
            if (!started)
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
            // Dispose the exited PTY session (master fd, reader thread, grid + scrollback) instead of leaking it.
            try { entry.Control?.StopShell(notifyExit: false); } catch { }
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

                // VS Code terminal tabs list: 22 px rows, terminal glyph + name, calm selection, trash on the right.
                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Height = UiTokens.ListRowHeight
                };
                var glyph = new VectorIconControl
                {
                    IconName = isSplitRight ? "split-right" : "terminal",
                    Width = 14,
                    Height = 14,
                    StrokeWidth = 1.3f,
                    IconColor = isActive ? textPrimary : textSecondary,
                    Margin = new Thickness(10, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new LiteralTextBlock($"{i + 1}: {session.Title}")
                {
                    FontSize = UiTokens.FontSmall + 0.5,
                    Foreground = new SolidColorBrush(isActive ? textPrimary : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(label, 1);
                var killBtn = WorkbenchStyles.IconButton("trash", "Kill Terminal", () => KillTerminalSession(session), currentTheme, 20, 13);
                killBtn.Margin = new Thickness(0, 0, 6, 0);
                killBtn.Opacity = isActive ? 1 : 0;
                Grid.SetColumn(killBtn, 2);
                rowGrid.Children.Add(glyph);
                rowGrid.Children.Add(label);
                rowGrid.Children.Add(killBtn);

                var itemBorder = new Border
                {
                    Child = rowGrid,
                    Background = isActive
                        ? WorkbenchStyles.B(UiTokens.ListInactiveSelection(currentTheme, currentTheme.SidebarBg))
                        : Brushes.Transparent,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                };
                var hoverBrush = WorkbenchStyles.B(UiTokens.Hover(currentTheme, currentTheme.SidebarBg));
                itemBorder.PointerEntered += (s, e) => { killBtn.Opacity = 1; if (!isActive) itemBorder.Background = hoverBrush; };
                itemBorder.PointerExited += (s, e) => { killBtn.Opacity = isActive ? 1 : 0; if (!isActive) itemBorder.Background = Brushes.Transparent; };
                itemBorder.PointerPressed += (s, e) => { if (!e.Handled) ActivateTerminalSession(session); };
                ToolTip.SetTip(itemBorder, session.Title);
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
                if (ext == "py") cmd = !string.IsNullOrEmpty(activeTab.FilePath) ? BuildPythonRunCommand(activeTab.FilePath, activeTerminalSession) : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"python {target}" : $"python3 {target}");
                else if (ext == "js") cmd = $"node {target}";
                else if (ext == "ps1") cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"powershell -ExecutionPolicy Bypass -File {target}" : $"pwsh -File {target}";
                else if (ext == "sh" || ext == "zsh") cmd = $"sh {target}";
                else if (ext == "rs") cmd = "cargo run";
            }
            // Brand-new terminal: send its pending venv activation first so the file runs inside the venv.
            string pendingVenv = PythonVenvActivator.TakePendingActivation(activeTerminalSession?.Control?.Session);
            if (pendingVenv != null) activeTerminalSession.Control.WriteAutomatedInput(pendingVenv + "\r");
            activeTerminalSession?.Control?.WriteAutomatedInput(cmd + "\r");
            AppendOutputLog("[Runner] Executed in terminal: " + cmd);
        }

        public void RunWorkspaceBuildInTerminal()
        {
            RunBuildTask();
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
                kv.Value.Background = active ? WorkbenchStyles.B(UiTokens.ListActiveSelection(currentTheme, UiTokens.InputBackground(currentTheme))) : Brushes.Transparent;
                kv.Value.Foreground = new SolidColorBrush(active ? textPrimary : textSecondary);
                kv.Value.BorderThickness = new Thickness(1);
                kv.Value.BorderBrush = active ? new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)) : Brushes.Transparent;
            }
        }

        public void SubmitChatMessage(string overridePrompt = null)
        {
            string prompt = (overridePrompt ?? chatPromptInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            if (aiEngine.IsTurnRunning)
            {
                // Keep the prompt in the composer: one request at a time per chat (VS Code), Stop is on the send button.
                SetStatusMessage(LlmHarnessAndAgentEngine.BusyReply);
                return;
            }
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
            if (HandleAgentTrustDiffDecision(item, accepted)) { RefreshChangesetBanner(); return; }
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
            var seen = new HashSet<string>(PathKeyComparer);
            PaletteEntryItem makeFileEntry(string full, bool recent)
            {
                string rel = !string.IsNullOrEmpty(workspaceRoot) && IsSameOrUnder(full, workspaceRoot)
                    ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, full)
                    : full;
                return new PaletteEntryItem
                {
                    Id = "file:" + full,
                    Category = "File",
                    Title = Path.GetFileName(full),
                    stringSubtitleOrPath = rel,
                    FilePath = full,
                    IsRecent = recent,
                    Execute = () => OpenFileAtLocation(full, 0, 1, 0, preview: false, focusEditor: true)
                };
            }
            // Recently opened first (VS Code lists editor history when the query is empty): open tabs, then recent files.
            var recent = new List<string>();
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) recent.Add(activeTab.FilePath);
            recent.AddRange(openTabs.Where(t => !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath));
            if (settings?.RecentFiles != null)
            {
                bool hasRoots = !string.IsNullOrEmpty(workspaceRoot);
                recent.AddRange(settings.RecentFiles.Where(f => !string.IsNullOrEmpty(f) &&
                    (!hasRoots || GetWorkspaceRootFolders().Any(r => IsSameOrUnder(f, r))) && File.Exists(f)));
            }
            foreach (string f in recent)
            {
                if (seen.Add(f)) entries.Add(makeFileEntry(f, true));
            }
            // Every indexed workspace file (was capped at the first 250, so most files could not be found).
            if (workspaceFiles.Count > QuickOpenInlineFileLimit)
            {
                // Big workspace: don't materialise 200k palette entries; search the lower-cased index per keystroke.
                var qoIndex = GetQuickOpenIndex();
                var skip = new HashSet<string>(seen, PathKeyComparer);
                var commands = BuildAllPaletteCommands();
                entries.AddRange(commands);
                CommandPalette.Open("", entries, currentTheme?.Id);
                CommandPalette.QuickOpenFileSearch = (q, max) => qoIndex.Search(q, max, skip)
                    .Select(h => (makeFileEntry(qoIndex.GetFullPath(h.Index), false), h.Score)).ToList();
                CommandPalette.RefreshFilter();
                return;
            }
            foreach (string file in workspaceFiles)
            {
                if (seen.Add(file)) entries.Add(makeFileEntry(file, false));
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
                            Execute = () =>
                            {
                                if (!string.IsNullOrEmpty(activeTab?.FilePath))
                                    OpenFileAtLocation(activeTab.FilePath, captured.LineNumber, 1, 0, preview: false, focusEditor: true);
                                else
                                    PrimaryEditor.GoToLine(captured.LineNumber);
                            }
                        });
                    }
                }
                CommandPalette.Open("@", symEntries, currentTheme?.Id);
                return;
            }
            if (prefix == "#")
            {
                EnsureWorkspaceIndexed(); // lazy index: built on first workspace-symbol search
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
                            Execute = () => OpenFileAtLocation(captured.FilePath, captured.LineNumber, 1, 0, preview: false, focusEditor: true)
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
                        AnalyzeBufferSoon(activeTab, OnBufferAnalyzed);
                        UpdateStatusBar();
                        PrimaryEditor.Canvas.InvalidateVisual();
                    }
                }
            }).ToList();
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        public List<PaletteEntryItem> BuildAllPaletteCommands()
        {
            var all = new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "File", Title = "New Text File", Shortcut = "Ctrl+N", Execute = () => CreateNewUntitledTab() },
                new PaletteEntryItem { Category = "File", Title = "New File in Workspace...", Shortcut = "Ctrl+Alt+N", Execute = PromptCreateNewFileInWorkspace },
                new PaletteEntryItem { Category = "File", Title = "New Folder in Workspace...", Execute = PromptCreateNewFolderInWorkspace },
                new PaletteEntryItem { Category = "File", Title = "Open File...", Shortcut = "Ctrl+O", Execute = () => _ = PromptOpenFileDialogAsync() },
                new PaletteEntryItem { Category = "File", Title = "Open Folder...", Shortcut = "Ctrl+K Ctrl+O", Execute = () => _ = PromptOpenFolderDialogAsync() },
                new PaletteEntryItem { Category = "File", Title = "Save", Shortcut = "Ctrl+S", Execute = () => SaveActiveTab() },
                new PaletteEntryItem { Category = "Workspaces", Title = "Add Folder to Workspace...", Execute = () => _ = PromptAddFolderToWorkspaceAsync() },
                new PaletteEntryItem { Category = "Workspaces", Title = "Remove Folder from Workspace...", Execute = PromptRemoveFolderFromWorkspace },
                new PaletteEntryItem { Category = "File", Title = "Save As...", Shortcut = "Ctrl+Shift+S", Execute = () => _ = PromptSaveAsAsync() },
                new PaletteEntryItem { Category = "File", Title = "Save All", Shortcut = "Ctrl+K S", Execute = SaveAllTabs },
                new PaletteEntryItem { Category = "File", Title = "Revert File", Execute = RevertActiveTab },
                new PaletteEntryItem { Category = "File", Title = "Close Editor", Shortcut = "Ctrl+W", Execute = () => { if (activeTab != null) CloseTab(activeTab); } },
                new PaletteEntryItem { Category = "File", Title = "Close All Editors", Shortcut = "Ctrl+K W", Execute = CloseAllTabs },
                new PaletteEntryItem { Category = "File", Title = "Close Folder", Shortcut = "Ctrl+K F", Execute = CloseWorkspaceFolder },
                new PaletteEntryItem { Category = "Edit", Title = "Find", Shortcut = "Ctrl+F", Execute = () => PrimaryEditor.ShowFindBar(false) },
                new PaletteEntryItem { Category = "Edit", Title = "Replace", Shortcut = "Ctrl+H", Execute = () => PrimaryEditor.ShowFindBar(true) },
                new PaletteEntryItem { Category = "Edit", Title = "Find in Files", Shortcut = "Ctrl+Shift+F", Execute = () => ShowSearchView(false) },
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
                new PaletteEntryItem { Category = "View", Title = "Show Network Activity", Execute = ShowNetworkActivity },
                new PaletteEntryItem { Category = "Preferences", Title = "Toggle Offline Mode", Execute = ToggleOfflineMode },
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
                new PaletteEntryItem { Category = "Terminal", Title = "Create New Terminal", Shortcut = "Ctrl+Shift+`", Execute = () => RequestNewTerminal() },
                new PaletteEntryItem { Category = "Help", Title = "Check for Updates...", Execute = CheckForUpdatesInteractive },
                new PaletteEntryItem { Category = "Terminal", Title = "Split Terminal", Shortcut = "Ctrl+Shift+5", Execute = ToggleSplitTerminal },
                new PaletteEntryItem { Category = "Terminal", Title = "Rename Active Terminal...", Execute = PromptRenameActiveTerminalSession },
                new PaletteEntryItem { Category = "Terminal", Title = "Kill Active Terminal", Execute = () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); } },
                new PaletteEntryItem { Category = "AI", Title = "Inline AI Edit / Generate", Shortcut = "Ctrl+K", Execute = () => PrimaryEditor.ShowInlineAiBar() },
                new PaletteEntryItem { Category = "AI", Title = "Configure BYOK AI Providers & Keys...", Execute = ShowByokAndHarnessModal },
                new PaletteEntryItem { Category = "Preferences", Title = "Color Theme", Shortcut = "Ctrl+K Ctrl+T", Execute = OpenThemePickerPalette },
                new PaletteEntryItem { Category = "Preferences", Title = "Open Settings", Shortcut = "Ctrl+,", Execute = ShowSettingsModal },
                new PaletteEntryItem { Category = "Preferences", Title = "Open Keyboard Shortcuts", Shortcut = "Ctrl+K Ctrl+S", Execute = ShowKeyboardShortcutsModal },
                BuildDebugPaletteCommands(),
                new PaletteEntryItem { Category = "Help", Title = "Welcome", Execute = OpenWelcomeTab },
                new PaletteEntryItem { Category = "Help", Title = "About codeLess", Execute = ShowAboutModal }
            };
            all.AddRange(BuildLanguageServerPaletteCommands());
            all.AddRange(BuildEditorNicetiesPaletteCommands());
            all.AddRange(BuildTerminalFeelPaletteCommands());
            all.AddRange(BuildDevToolsPaletteCommands());
            all.AddRange(BuildParityPaletteCommands());
            all.AddRange(BuildTaskPaletteCommands());
            all.AddRange(BuildAgentTrustPaletteCommands());
            all.AddRange(BuildWorkspaceTrustPaletteCommands());
            all.AddRange(BuildExtensionPaletteCommands());
            return all;
        }

        /// <summary>
        /// Returns true when the caller may proceed immediately. When an Unsaved Changes modal has to be shown it
        /// returns false; <paramref name="proceedAfterDecision"/> then runs once the user picks Save or Don't Save,
        /// so the original action (quit, close tab, open folder) completes instead of silently being dropped.
        /// </summary>
        public bool ConfirmSaveOrDiscardDirtyTabs(IEnumerable<EditorTabBuffer> candidates, Action proceedAfterDecision = null)
        {
            var dirty = (candidates ?? Enumerable.Empty<EditorTabBuffer>()).Where(UntitledFileDefaults.NeedsSavePrompt).ToList();
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
            var view = AvaloniaDialogBuilder.BuildUnsavedChangesView(dirty, currentTheme, async decision =>
            {
                ModalHost.CloseModal();
                if (decision == UnsavedPromptResult.SaveAll)
                {
                    foreach (var d in dirty)
                    {
                        // Untitled editors need a Save As dialog: finish it before closing / quitting, and stop if the
                        // user cancels it (the text must not be thrown away behind an open dialog).
                        bool ok = UntitledFileDefaults.IsUntitledLike(d) && StorageProvider != null && AvaloniaTerminalEmulatorControl.IsDesktopRuntime
                            ? await PromptSaveAsAsync(d)
                            : SaveBufferToDisk(d);
                        if (!ok) return;
                    }
                }
                else if (decision == UnsavedPromptResult.DontSave)
                {
                    foreach (var d in dirty)
                    {
                        d.IsDirty = false;
                        CloseTab(d, forceWithoutPrompt: true);
                    }
                }
                if (decision != UnsavedPromptResult.Cancel && proceedAfterDecision != null)
                    MainUi.Post(proceedAfterDecision);
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
                    ApplyEditorIndentAndWrapSettings(updated);
                    ApplyGitParitySettings();
                    ApplyEditorNicetySettings();
                    UpdateStatusBar();
                },
                () => ModalHost.CloseModal(),
                BuildSettingsTrustSection());
            ModalHost.ShowModal("settings", "Preferences: Settings — codeLess", view, 680);
        }

        private Control BuildSettingsTrustSection()
        {
            var host = new ContentControl();
            Action rebuild = null;
            rebuild = () => host.Content = BuildWorkspaceTrustManager(currentTheme, rebuild);
            rebuild();
            return host;
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
            // The old modal was a static mock-up ("Status: Ready" for two hard-coded models); the Arena now runs a real
            // Best-of-N comparison with verification.
            ShowBestOfNModal();
        }

        public void ShowAboutModal()
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = "codeLess v3.3.5",
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
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) AddFolderToWorkspace(p);
            }
        }

        /// <summary>File → Add Folder to Workspace (remembered for this workspace, like VS Code).</summary>
        public void AddFolderToWorkspace(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) { LoadWorkspace(folder); return; }
            if (string.Equals(folder, workspaceRoot.TrimEnd('\\', '/'), PathKeyComparison) ||
                additionalWorkspaceFolders.Any(f => string.Equals(f.TrimEnd('\\', '/'), folder, PathKeyComparison)))
                return;
            additionalWorkspaceFolders.Add(folder);
            PersistWorkspaceFolders();
            LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
        }

        public IReadOnlyList<string> AdditionalWorkspaceFolders => additionalWorkspaceFolders;

        private void PersistWorkspaceFolders()
        {
            WorkspaceFolders.Store(settings, workspaceRoot, additionalWorkspaceFolders);
            try { settings.Save(); } catch { }
        }

        /// <summary>
        /// Remove Folder from Workspace (Explorer root context menu / File menu / palette). Open editors from that folder
        /// stay open, like VS Code; removing the primary folder promotes the next one.
        /// </summary>
        public void RemoveFolderFromWorkspace(string folder)
        {
            if (!WorkspaceFolders.Remove(workspaceRoot, additionalWorkspaceFolders, folder, out string newPrimary, out List<string> newExtras)) return;
            if (newPrimary == null) return; // the only folder: use File → Close Folder
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            bool primaryChanged = !string.Equals(newPrimary, workspaceRoot, PathKeyComparison);
            if (primaryChanged)
            {
                WorkspaceFolders.Store(settings, workspaceRoot, new string[0]); // forget the old grouping
                additionalWorkspaceFolders.Clear();
                additionalWorkspaceFolders.AddRange(newExtras);
                WorkspaceFolders.Store(settings, newPrimary, newExtras);
                try { settings.Save(); } catch { }
                LoadWorkspace(newPrimary, keepCurrentTabs: true);
            }
            else
            {
                additionalWorkspaceFolders.Clear();
                additionalWorkspaceFolders.AddRange(newExtras);
                PersistWorkspaceFolders();
                LoadWorkspace(workspaceRoot, keepCurrentTabs: true);
            }
            SetStatusMessage($"Removed '{name}' from the workspace.");
        }

        /// <summary>Workspaces: Remove Folder from Workspace... (asks which folder when there are several).</summary>
        public void PromptRemoveFolderFromWorkspace()
        {
            var roots = GetWorkspaceRootFolders().ToList();
            if (roots.Count < 2) { SetStatusMessage("The workspace has only one folder (use File → Close Folder)."); return; }
            if (RemoveFolderPickerOverride != null)
            {
                string pick = RemoveFolderPickerOverride(roots);
                if (!string.IsNullOrEmpty(pick)) RemoveFolderFromWorkspace(pick);
                return;
            }
            ShowWorkspaceFolderPicker("remove-workspace-folder", "Remove Folder from Workspace", "Select the folder to remove from the workspace:", roots, RemoveFolderFromWorkspace);
        }

        /// <summary>Test hook for <see cref="PromptRemoveFolderFromWorkspace"/> (null = cancel).</summary>
        public Func<List<string>, string> RemoveFolderPickerOverride;

        private Task<bool> PromptSaveAsAsync() => PromptSaveAsAsync(activeTab);

        /// <summary>
        /// Save As (also Save on an untitled editor): proposes "Untitled-N.&lt;ext&gt;" with the extension of the editor's
        /// language / the workspace's main language, starting in the workspace folder. True when the file was saved.
        /// </summary>
        public async Task<bool> PromptSaveAsAsync(EditorTabBuffer tab)
        {
            if (tab == null || StorageProvider == null) return false;
            bool untitled = UntitledFileDefaults.IsUntitledLike(tab);
            string suggested = UntitledFileDefaults.SuggestSaveFileName(tab, workspaceRoot, workspaceFiles);
            string ext = Path.GetExtension(suggested).TrimStart('.');
            var options = new FilePickerSaveOptions
            {
                Title = "Save File As — codeLess",
                SuggestedFileName = suggested,
                DefaultExtension = string.IsNullOrEmpty(ext) ? null : ext,
                ShowOverwritePrompt = true
            };
            try
            {
                string startDir = untitled || string.IsNullOrEmpty(tab.FilePath) ? workspaceRoot : Path.GetDirectoryName(tab.FilePath);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startDir);
            }
            catch { }
            var file = await StorageProvider.SaveFilePickerAsync(options);
            string p = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) return false;
            string oldPath = tab.FilePath;
            tab.FilePath = p;
            tab.FileName = Path.GetFileName(p);
            tab.RelativePath = string.IsNullOrEmpty(workspaceRoot) ? tab.FileName : Path.GetRelativePath(workspaceRoot, p);
            string chosenExt = Path.GetExtension(p).TrimStart('.');
            if (!string.IsNullOrEmpty(chosenExt)) tab.Language = chosenExt.ToLowerInvariant();
            tab.IsUntitled = false;
            bool saved = SaveBufferToDisk(tab);
            if (!saved && untitled) { tab.FilePath = oldPath; tab.IsUntitled = true; }
            if (saved)
            {
                settings.AddRecentFile(p);
                if (ReferenceEquals(tab, activeTab)) PrimaryEditor?.LoadBuffer(tab); // grammar / language for the new name
                RefreshTabStrip();
                RefreshOpenEditorsSection();
            }
            return saved;
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

            // Ensure the default (Savatarr Graphite) theme & clean workspace
            ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: false);
            // An explicit --workspace wins; otherwise the codeLess repo in the current directory, else the current directory.
            string repoDir = !string.IsNullOrEmpty(App.StartupWorkspacePath) && Directory.Exists(App.StartupWorkspacePath)
                ? App.StartupWorkspacePath
                : workspaceRoot;
            if (string.IsNullOrEmpty(App.StartupWorkspacePath) && File.Exists(Path.Combine(Environment.CurrentDirectory, "src", "codeLess.Core", "CoreModels.cs")))
                repoDir = Environment.CurrentDirectory;
            else if (string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir))
                repoDir = Environment.CurrentDirectory;
            // Automation: trust the fixture workspace for this run only (a throwaway trust store, never the user's list)
            // so the Workspace Trust prompt doesn't cover the captures.
            WorkspaceTrust.StorePathOverride = Path.Combine(Path.GetTempPath(), "codeless-suite-trust-" + Environment.ProcessId + ".json");
            WorkspaceTrust.Trust(repoDir);
            if (ModalHost.IsVisible) ModalHost.CloseModal();
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

            // 1b. r14_empty_editor_watermark.png (Welcome closed with no files open: VS Code's empty editor group)
            CloseWelcomeTab();
            await PumpAndRenderAsync("r14_empty_editor_watermark.png");
            OpenWelcomeTab();

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
                outlineHeaderBtn.Content = new SectionHeaderLabel("▾ OUTLINE");
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
            DismissLanguageServerHintToast();
            if (activeTab != null) activeTab.GhostTextSuggestion = "";

            // 4. r10b_avalonia_terminal.png (Terminal panel with 2 active sessions in session list + side-by-side split + ANSI colors)
            SwitchBottomTab("terminal");
            // Platform-true staging (website screenshots): zsh/bash on macOS/Linux, PowerShell on Windows.
            bool winHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string sh1 = winHost ? "pwsh" : (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "zsh" : "bash");
            string sh2 = winHost ? "powershell" : sh1;
            while (terminalSessions.Count < 2)
            {
                CreateNewTerminalSession(winHost ? "powershell" : null);
            }
            if (!isTerminalSplit)
                ToggleSplitTerminal();
            if (terminalSessions.Count >= 1)
            {
                terminalSessions[0].Title = sh1;
                terminalSessions[0].Control.FeedAnsiPreview(
                    "\r\n\x1b[1;32m✓ Build succeeded:\x1b[0m \x1b[36mcodeLess.Core -> bin/Release/net8.0/codeLess.Core.dll\x1b[0m\r\n" +
                    "\x1b[1;34m" + sh1 + "\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36mcodeLess\x1b[0m \x1b[33mgit:(main)\x1b[0m $ dotnet test --nologo\r\n" +
                    "\x1b[32mPassed!  - Failed:     0, Passed:    42, Skipped:     0\x1b[0m\r\n");
            }
            if (terminalSessions.Count >= 2)
            {
                terminalSessions[1].Title = sh2;
                terminalSessions[1].Control.FeedAnsiPreview(
                    "\r\n\x1b[1;35m" + sh2 + "\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36mcodeLess\x1b[0m $ git status -sb\r\n" +
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
            ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: false);

            await RunExtendedScreenshotScreensAsync(outputDir, repoDir);
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

        /// <summary>Test hook: decides agent tool approvals without UI.</summary>
        public Func<string, string, bool> AgentToolApprovalOverride { get; set; }

        /// <summary>
        /// Approval gate for agent tools (previously never wired on macOS/Linux, so run_terminal executed any
        /// LLM-chosen command; repo files like AGENTS.md / .cursorrules are injected into the prompt, so an opened
        /// repository could steer it). Shell commands always need explicit approval unless the user disabled
        /// "Require agent tool approval"; file writes/edits are staged as diffs for review, so they pass.
        /// Called on the agent worker thread; blocks until the user decides.
        /// </summary>
        private bool RequestAgentToolApproval(string toolName, string detail)
        {
            if (AgentToolApprovalOverride != null) return AgentToolApprovalOverride(toolName, detail);
            bool isShell = string.Equals(toolName, "run_terminal", StringComparison.OrdinalIgnoreCase);
            if (!isShell) return true;
            // Restricted Mode: agent shell commands always ask (never auto-approved, never run unattended).
            bool restricted = WorkspaceTrust.IsRestricted(workspaceRoot);
            if (settings != null && !settings.RequireAgentToolApproval && !restricted) return true;
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return false; // never run unattended
            if (Dispatcher.UIThread.CheckAccess()) return false; // cannot block the UI thread waiting for a click

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainUi.Post(() =>
            {
                var stack = new StackPanel { Spacing = 10 };
                stack.Children.Add(new TextBlock
                {
                    Text = "The AI agent wants to run a shell command. Review it carefully before allowing it:" +
                           (restricted ? "\n(This folder is in Restricted Mode: the command runs code from a folder you haven't trusted.)" : ""),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary))
                });
                stack.Children.Add(new TextBox
                {
                    Text = detail ?? "",
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"),
                    MaxHeight = 220
                });
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
                var allow = new Button { Content = "Run Command", Padding = new Thickness(14, 5) };
                var deny = new Button
                {
                    Content = "Deny",
                    Padding = new Thickness(14, 5),
                    Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary)),
                    Foreground = Brushes.White
                };
                allow.Click += (s, e) => { ModalHost.CloseModal(); tcs.TrySetResult(true); };
                deny.Click += (s, e) => { ModalHost.CloseModal(); tcs.TrySetResult(false); };
                row.Children.Add(deny);
                row.Children.Add(allow);
                stack.Children.Add(row);
                Action onClosed = null;
                onClosed = () => { ModalHost.Closed -= onClosed; tcs.TrySetResult(false); }; // Esc / dismiss = Deny
                ModalHost.Closed += onClosed;
                ModalHost.ShowModal("agent-tool-approval", "Allow Agent Command?", stack, 560);
                MainUi.Post(() => deny.Focus(), DispatcherPriority.Loaded); // default = safe choice
            });
            // Unanswered for 10 minutes => deny.
            return tcs.Task.Wait(TimeSpan.FromMinutes(10)) && tcs.Task.Result;
        }

        /// <summary>True while a chat turn runs; the send button is a Stop button meanwhile.</summary>
        public bool IsChatTurnRunning => aiEngine.IsTurnRunning;

        /// <summary>Stop: cancels the running chat turn (its provider stream and remaining agent steps).</summary>
        public void StopChatTurn()
        {
            if (!aiEngine.IsTurnRunning) return;
            aiEngine.CancelStreamingRequested = true;
            SetStatusMessage("Stopping the agent…");
        }

        private void SetChatTurnBusy(bool busy)
        {
            if (chatSendBtn?.Content is VectorIconControl glyph)
            {
                glyph.IconName = busy ? "stop" : "send";
                glyph.InvalidateVisual();
            }
            if (chatSendBtn != null) ToolTip.SetTip(chatSendBtn, busy ? "Stop" : "Send (Enter)");
        }

        public async Task<string> SendChatMessageAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return "";
            if (aiEngine.IsTurnRunning)
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = LlmHarnessAndAgentEngine.BusyReply });
                return LlmHarnessAndAgentEngine.BusyReply;
            }

            var userItem = new AvaloniaAgentFeedItem
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now
            };
            AgentFeed.AddMessage(userItem);

            var preferredVendor = aiEngine.Vendors.FirstOrDefault(v =>
                string.Equals(v.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Id, StudioSettings.NormalizeProviderId(settings.ActiveProvider), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Id, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))
                ?? aiEngine.Vendors.FirstOrDefault();

            var pills = new List<string>(attachedContextPills);
            if (!string.IsNullOrEmpty(activeTab?.FileName) && !pills.Contains(activeTab.FileName))
                pills.Add(activeTab.FileName);

            // Run the agent loop (LLM calls, tool execution: shell commands, grep, syntax checks) on a worker
            // thread; previously it ran on the UI thread and froze the window for each tool call.
            string mode = currentAgentMode;
            string harnessId = settings.ActiveHarnessId;
            var tabSnapshot = AgentTurnInputs.SnapshotTab(activeTab); // detached: the turn runs on a worker thread
            string wsSnapshot = workspaceRoot;
            var filesSnapshot = workspaceFiles.ToList();
            string termText = activeTerminalSession?.Control?.GetVisibleScreenText() ?? "";
            var turnRequest = BuildAgentTurnRequest(prompt, mode, preferredVendor, harnessId, tabSnapshot, wsSnapshot, filesSnapshot, pills, termText);
            // Per-turn checkpoint: files changed by the turn's terminal commands are detected and recorded.
            var tracker = agentTrust.BeginTurn(aiEngine, wsSnapshot, prompt);
            userItem.TurnId = tracker.TurnId; // the message's Restore button undoes back to this turn
            List<TurnFileChange> terminalChanges = null;
            var loopTask = Task.Run(async () =>
            {
                try
                {
                    EnsureWorkspaceIndexed(); // lazy index: built on first AI use, off the UI thread
                    return await aiEngine.RunAutonomousAgentLoopAsync(mode, prompt, preferredVendor, harnessId, tabSnapshot, wsSnapshot, filesSnapshot, pills, termText).ConfigureAwait(false);
                }
                finally
                {
                    terminalChanges = agentTrust.EndTurn(aiEngine, tracker);
                }
            });
            SetChatTurnBusy(true);
            AgentExecutionTurnResult turnResult;
            try
            {
                turnResult = AvaloniaTerminalEmulatorControl.IsDesktopRuntime
                    ? await loopTask
                    : loopTask.GetAwaiter().GetResult();
            }
            finally { SetChatTurnBusy(false); }
            if (LlmHarnessAndAgentEngine.IsBusyResult(turnResult))
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = turnResult.AssistantReply });
                return turnResult.AssistantReply;
            }

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

            await PresentAgentTurnAsync(turnResult, turnRequest, tracker.TurnId, terminalChanges);

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
