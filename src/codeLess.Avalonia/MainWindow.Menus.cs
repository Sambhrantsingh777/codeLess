using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code menu bar parity (macOS native menu + the in-window menu on Windows / Linux), built from the shared
    /// <see cref="WorkbenchMenuModel"/>. Every item runs the same command as its palette entry / shortcut, shows the
    /// platform shortcut, and checkable items (Word Wrap, Minimap, Auto Save, ...) reflect the live state when a menu
    /// opens. v3.3.1 report: "There is no Word Wrap option" — the macOS native menu had no View → Word Wrap.
    /// </summary>
    public partial class MainWindow
    {
        private Dictionary<string, Action> menuCommands;
        private Dictionary<string, Func<bool>> menuChecks;

        /// <summary>Menu command id → action (tests: every model item must resolve).</summary>
        public IReadOnlyDictionary<string, Action> MenuCommands => menuCommands ??= BuildMenuCommandMap();
        /// <summary>Checkable menu command id → live state.</summary>
        public IReadOnlyDictionary<string, Func<bool>> MenuChecks => menuChecks ??= BuildMenuCheckMap();

        /// <summary>Runs a menu command by id (false when unknown).</summary>
        public bool InvokeMenuCommand(string id)
        {
            if (id != null && MenuCommands.TryGetValue(id, out var a)) { a(); return true; }
            return false;
        }

        public bool? MenuCheckState(string id)
        {
            if (id == null || !MenuChecks.TryGetValue(id, out var f)) return null;
            try { return f(); }
            catch (NullReferenceException) { return false; } // menus are built before the editors exist
        }

        private bool isFullScreenMenuState => WindowState == WindowState.FullScreen;

        private Dictionary<string, Action> BuildMenuCommandMap()
        {
            var ed = new Func<AvaloniaVirtualEditorCanvas>(() => PrimaryEditor.Canvas);
            var m = new Dictionary<string, Action>(StringComparer.Ordinal)
            {
                // app (macOS) / help
                ["app.about"] = ShowAboutModal,
                ["app.checkUpdates"] = CheckForUpdatesInteractive,
                ["app.hide"] = () => MacAppNative.Send("hide:"),
                ["app.hideOthers"] = () => MacAppNative.Send("hideOtherApplications:"),
                ["app.showAll"] = () => MacAppNative.Send("unhideAllApplications:"),
                ["app.quit"] = Close,
                ["pref.settings"] = ShowSettingsModal,
                ["pref.keyboardShortcuts"] = ShowKeyboardShortcutsModal,
                ["pref.colorTheme"] = OpenThemePickerPalette,
                // file
                ["file.newTextFile"] = () => CreateNewUntitledTab(),
                ["file.newFile"] = PromptCreateNewFileInWorkspace,
                ["file.newFolder"] = PromptCreateNewFolderInWorkspace,
                ["file.newWindow"] = CreateNewWindowInstance,
                ["file.open"] = () => _ = PromptOpenFileDialogAsync(),
                ["file.openFolder"] = () => _ = PromptOpenFolderDialogAsync(),
                [WorkbenchMenuModel.OpenRecentId] = () => { var r = RecentFolders().FirstOrDefault(); if (r != null) LoadWorkspace(r); },
                ["file.addFolder"] = () => _ = PromptAddFolderToWorkspaceAsync(),
                ["file.save"] = () => SaveActiveTab(),
                ["file.saveAs"] = () => _ = PromptSaveAsAsync(),
                ["file.saveAll"] = SaveAllTabs,
                ["file.autoSave"] = () =>
                {
                    settings.SetAutoSaveMode(settings.AutoSave ? IdePersistentSettings.AutoSaveOff : IdePersistentSettings.AutoSaveAfterDelay);
                    try { settings.Save(); } catch { }
                    SetStatusMessage("Auto Save: " + (settings.AutoSave ? "On" : "Off"));
                },
                ["file.revert"] = RevertActiveTab,
                ["file.closeEditor"] = () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); },
                ["file.closeFolder"] = CloseWorkspaceFolder,
                ["file.closeWindow"] = Close,
                ["file.exit"] = Close,
                // edit (routed to the focused text box / terminal like the native responders)
                ["edit.undo"] = () => RouteNativeEditCommand("undo"),
                ["edit.redo"] = () => RouteNativeEditCommand("redo"),
                ["edit.cut"] = () => RouteClipboardCommand("cut"),
                ["edit.copy"] = () => RouteClipboardCommand("copy"),
                ["edit.paste"] = () => RouteClipboardCommand("paste"),
                ["edit.find"] = () => PrimaryEditor.ShowFindBar(false),
                ["edit.replace"] = () => PrimaryEditor.ShowFindBar(true),
                ["edit.findInFiles"] = () => ShowSearchView(false),
                ["edit.replaceInFiles"] = () => ShowSearchView(true),
                ["edit.toggleLineComment"] = () => ed().ToggleLineComment(),
                ["edit.toggleBlockComment"] = () => ed().ToggleBlockComment(),
                ["edit.emmetExpand"] = () => { if (!ed().ExpandEmmetAbbreviation()) SetStatusMessage("Emmet: no abbreviation at the cursor"); },
                // selection
                ["sel.selectAll"] = () => RouteNativeEditCommand("selectall"),
                ["sel.expand"] = () => ed().ExpandSelection(),
                ["sel.shrink"] = () => ed().ShrinkSelection(),
                ["sel.copyLineUp"] = () => ed().CopyActiveLine(-1),
                ["sel.copyLineDown"] = () => ed().CopyActiveLine(1),
                ["sel.moveLineUp"] = () => ed().MoveActiveLine(-1),
                ["sel.moveLineDown"] = () => ed().MoveActiveLine(1),
                ["sel.duplicate"] = () => ed().CopyActiveLine(1),
                ["sel.addCursorAbove"] = () => ed().AddCursorVertical(-1),
                ["sel.addCursorBelow"] = () => ed().AddCursorVertical(1),
                ["sel.addCursorsToLineEnds"] = () => ed().AddCursorsToLineEnds(),
                ["sel.addNextOccurrence"] = () => ed().AddNextOccurrenceOrSelectWord(),
                ["sel.selectAllOccurrences"] = () => ed().SelectAllFindOccurrences(),
                ["sel.columnSelectionMode"] = () =>
                {
                    PrimaryEditor.ColumnSelectionMode = !PrimaryEditor.ColumnSelectionMode;
                    SetStatusMessage("Column Selection Mode: " + (PrimaryEditor.ColumnSelectionMode ? "On" : "Off"));
                },
                // view
                ["view.commandPalette"] = () => OpenCommandPalette(">"),
                ["view.fullScreen"] = ToggleFullScreenMode,
                ["view.zenMode"] = ToggleZenMode,
                ["view.toggleSidebar"] = TogglePrimarySidebar,
                ["view.toggleChat"] = ToggleRightChatDock,
                ["view.togglePanel"] = ToggleBottomPanel,
                ["view.minimap"] = () =>
                {
                    PrimaryEditor.ShowMinimap = !PrimaryEditor.ShowMinimap;
                    if (SplitEditor != null) SplitEditor.ShowMinimap = PrimaryEditor.ShowMinimap;
                    settings.ShowMinimap = PrimaryEditor.ShowMinimap;
                    try { settings.Save(); } catch { }
                },
                ["view.breadcrumbs"] = () =>
                {
                    PrimaryEditor.ShowBreadcrumbs = !PrimaryEditor.ShowBreadcrumbs;
                    if (SplitEditor != null) SplitEditor.ShowBreadcrumbs = PrimaryEditor.ShowBreadcrumbs;
                },
                ["view.stickyScroll"] = () => ToggleEditorNicety("Sticky Scroll", () => EditorNicetyOptions.StickyScroll, v => EditorNicetyOptions.StickyScroll = v),
                ["view.zoomIn"] = () => AdjustZoom(1),
                ["view.zoomOut"] = () => AdjustZoom(-1),
                ["view.resetZoom"] = () => AdjustZoom(0),
                ["view.splitEditor"] = ToggleSplitEditor,
                ["view.explorer"] = () => SwitchSidebarView("explorer"),
                ["view.search"] = () => ShowSearchView(false),
                ["view.scm"] = () => SwitchSidebarView("git"),
                ["view.run"] = () => SwitchSidebarView("run"),
                ["view.extensions"] = () => SwitchSidebarView("extensions"),
                ["view.problems"] = () => SwitchBottomTab("problems"),
                ["view.output"] = () => SwitchBottomTab("output"),
                ["view.debugConsole"] = () => { btmDebugConsoleTabBtn.IsVisible = true; SwitchBottomTab("debug"); FocusBottomTabContent("debug"); },
                ["view.terminal"] = () => SwitchBottomTab("terminal"),
                ["view.networkActivity"] = ShowNetworkActivity,
                ["view.offlineMode"] = ToggleOfflineMode,
                ["view.wordWrap"] = ToggleWordWrapCommand,
                // go
                ["go.back"] = NavigateBack,
                ["go.forward"] = NavigateForward,
                ["go.nextEditor"] = () => CycleEditorTab(1),
                ["go.previousEditor"] = () => CycleEditorTab(-1),
                // MRU switching belongs to the Ctrl+Tab handler (quick pick when present): invoke the real binding
                ["go.nextUsedEditor"] = () => SimulateShortcut(Key.Tab, KeyModifiers.Control),
                ["go.previousUsedEditor"] = () => SimulateShortcut(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift),
                ["go.file"] = OpenQuickOpen,
                ["go.symbolInEditor"] = () => OpenCommandPalette("@"),
                ["go.symbolInWorkspace"] = () => OpenCommandPalette("#"),
                ["go.definition"] = GoToDefinitionAtCursor,
                ["go.references"] = FindAllReferencesAtCursor,
                ["go.line"] = PromptGoToLineModal,
                ["go.bracket"] = () => ed().JumpToMatchingBracket(),
                ["go.nextProblem"] = () => JumpDiagnostic(1),
                ["go.previousProblem"] = () => JumpDiagnostic(-1),
                // run
                ["run.start"] = StartOrContinueDebugging,
                ["run.withoutDebugging"] = RunActiveFileInTerminal,
                ["run.stop"] = () => _ = debugController.StopAsync(),
                ["run.restart"] = () => debugController.Restart(),
                ["run.addConfiguration"] = CreateLaunchJson,
                ["run.stepOver"] = () => debugController.StepOver(),
                ["run.stepInto"] = () => debugController.StepInto(),
                ["run.stepOut"] = () => debugController.StepOut(),
                ["run.continue"] = () => debugController.Continue(),
                ["run.toggleBreakpoint"] = ToggleBreakpointAtCursor,
                ["run.conditionalBreakpoint"] = () => { if (activeTab?.FilePath != null) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine, "condition"); else SetStatusMessage("Open a saved file to add a breakpoint."); },
                ["run.logpoint"] = () => { if (activeTab?.FilePath != null) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine, "log"); else SetStatusMessage("Open a saved file to add a logpoint."); },
                ["run.enableAllBreakpoints"] = () => debugController.Breakpoints.SetAllEnabled(true),
                ["run.disableAllBreakpoints"] = () => debugController.Breakpoints.SetAllEnabled(false),
                ["run.removeAllBreakpoints"] = () => debugController.Breakpoints.RemoveAll(),
                // terminal
                ["term.new"] = () => RequestNewTerminal(),
                ["term.split"] = ToggleSplitTerminal,
                ["term.runTask"] = OpenRunTaskPicker,
                ["term.runBuildTask"] = RunWorkspaceBuildInTerminal,
                ["term.runActiveFile"] = RunActiveFileInTerminal,
                ["term.configureTasks"] = ConfigureTasksJson,
                ["term.clear"] = () => activeTerminalSession?.Control?.ClearTerminal(),
                ["term.kill"] = () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); },
                // window (macOS)
                ["win.minimize"] = () => WindowState = WindowState.Minimized,
                ["win.zoom"] = () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
                ["win.bringAllToFront"] = () => { MacAppNative.Send("arrangeInFront:"); Activate(); },
                // help
                ["help.welcome"] = OpenWelcomeTab,
                ["help.showAllCommands"] = () => OpenCommandPalette(">"),
                ["help.documentation"] = () => OpenWithSystemDefaultApp("https://github.com/" + UpdateChecker.Repository + "#readme"),
                ["help.releaseNotes"] = () => OpenWithSystemDefaultApp("https://github.com/" + UpdateChecker.Repository + "/releases"),
                ["help.reportIssue"] = () => OpenWithSystemDefaultApp("https://github.com/" + UpdateChecker.Repository + "/issues/new"),
                ["help.keyboardShortcuts"] = ShowKeyboardShortcutsModal,
                ["help.byok"] = ShowByokAndHarnessModal,
            };
            return m;
        }

        private Dictionary<string, Func<bool>> BuildMenuCheckMap()
        {
            return new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            {
                ["file.autoSave"] = () => settings.AutoSave,
                ["sel.columnSelectionMode"] = () => PrimaryEditor.ColumnSelectionMode,
                ["view.fullScreen"] = () => isFullScreenMenuState,
                ["view.toggleSidebar"] = () => IsSidebarVisible,
                ["view.toggleChat"] = () => IsRightChatVisible,
                ["view.togglePanel"] = () => IsBottomPanelVisible,
                ["view.minimap"] = () => PrimaryEditor.ShowMinimap,
                ["view.breadcrumbs"] = () => PrimaryEditor.ShowBreadcrumbs,
                ["view.stickyScroll"] = () => EditorNicetyOptions.StickyScroll,
                ["view.offlineMode"] = () => NetworkLedger.OfflineMode,
                ["view.wordWrap"] = () => IsActiveEditorWordWrapped,
            };
        }

        private bool suppressNextWindowTextInput;

        /// <summary>Tests: the Settings dialog's apply step for tab size / insert spaces / word wrap.</summary>
        public void ApplyEditorIndentAndWrapSettingsForTest(StudioSettings s) => ApplyEditorIndentAndWrapSettings(s);

        private void InstallMenuKeyHooks()
        {
            lastAppliedWordWrapSetting = settings?.WordWrap; // open editors start with the setting (see ApplyEditorIndentAndWrapSettings)
            AddHandler(KeyDownEvent, OnGlobalMenuKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, (s, e) =>
            {
                // macOS sends the Option character (⌥Z = "Ω") after a shortcut we handled: drop it
                if (suppressNextWindowTextInput) { suppressNextWindowTextInput = false; e.Handled = true; }
            }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// View: Toggle Word Wrap is a workbench command in VS Code (Alt+Z / ⌥Z works from the find box, the explorer,
        /// ...), matched by physical key because Option changes the produced character on macOS.
        /// </summary>
        private void OnGlobalMenuKeyDown(object sender, KeyEventArgs e)
        {
            suppressNextWindowTextInput = false;
            if (e.Handled) return;
            if (AvaloniaVirtualEditorCanvas.IsToggleWordWrapKey(e.Key, e.PhysicalKey, e.KeyModifiers) && !(e.Source is Visual v && IsFromTerminal(v)))
            {
                ToggleWordWrapCommand();
                suppressNextWindowTextInput = true;
                RefreshNativeCheckStates();
                e.Handled = true;
            }
        }

        /// <summary>Word wrap state of the focused editor (View → Word Wrap check mark).</summary>
        public bool IsActiveEditorWordWrapped => (FocusedEditorControl()?.CurrentBuffer ?? activeTab)?.WordWrap ?? false;

        private AvaloniaEditorControl FocusedEditorControl()
        {
            if (SplitEditor != null && isEditorSplit && SplitEditor.Canvas.IsFocused) return SplitEditor;
            return PrimaryEditor;
        }

        /// <summary>View: Toggle Word Wrap (Alt+Z / ⌥Z, View → Word Wrap, command palette) for the focused editor.</summary>
        public void ToggleWordWrapCommand()
        {
            var ctl = FocusedEditorControl();
            if (ctl?.CurrentBuffer == null) { SetStatusMessage("Word Wrap: open a file first"); return; }
            ctl.Canvas.ToggleWordWrap();
            SetStatusMessage("Word Wrap: " + (ctl.CurrentBuffer.WordWrap ? "On" : "Off"));
        }

        private void CycleEditorTab(int step)
        {
            if (openTabs.Count < 2 || activeTab == null) return;
            int cur = openTabs.IndexOf(activeTab);
            ActivateTab(openTabs[(cur + step + openTabs.Count) % openTabs.Count]);
        }

        private List<string> RecentFolders()
        {
            var list = settings?.RecentWorkspaces ?? new List<string>();
            return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(10).ToList();
        }

        /// <summary>Cut / Copy / Paste to the focused text box, terminal, or the editor (same path as the keyboard).</summary>
        private void RouteClipboardCommand(string cmd)
        {
            var focused = FocusManager?.GetFocusedElement();
            if (focused is TextBox tb)
            {
                if (cmd == "cut") tb.Cut(); else if (cmd == "copy") tb.Copy(); else tb.Paste();
                return;
            }
            Key key = cmd == "cut" ? Key.X : cmd == "copy" ? Key.C : Key.V;
            if (focused is Visual v && IsFromTerminal(v))
            {
                activeTerminalSession?.Control?.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = MacKeymap.PrimaryModifier });
                return;
            }
            var ctl = FocusedEditorControl();
            ctl.Canvas.Focus();
            using (MacKeymap.BeginLenientPrimaryMatching())
                ctl.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = MacKeymap.PrimaryModifier });
        }

        // ------------------------------------------------------------------ shortcuts → gestures
        /// <summary>Parses "Ctrl+Shift+F" / "Cmd+Alt+Left" / "Ctrl+`" into a gesture (false for chords and keys a menu cannot bind).</summary>
        public static bool TryParseMenuGesture(string shortcut, out KeyGesture gesture)
        {
            gesture = null;
            if (string.IsNullOrEmpty(shortcut) || shortcut.Contains(' ')) return false;
            var parts = WorkbenchMenuModel.SplitKeys(shortcut);
            if (parts.Count == 0) return false;
            KeyModifiers mods = KeyModifiers.None;
            for (int i = 0; i < parts.Count - 1; i++)
            {
                switch (parts[i])
                {
                    case "Ctrl": mods |= KeyModifiers.Control; break;
                    case "Cmd": case "Meta": mods |= KeyModifiers.Meta; break;
                    case "Alt": case "Option": mods |= KeyModifiers.Alt; break;
                    case "Shift": mods |= KeyModifiers.Shift; break;
                    default: return false;
                }
            }
            Key key = KeyFromName(parts[parts.Count - 1]);
            if (key == Key.None) return false;
            gesture = new KeyGesture(key, mods);
            return true;
        }

        public static Key KeyFromName(string name)
        {
            switch (name)
            {
                case "=": return Key.OemPlus;
                case "-": return Key.OemMinus;
                case "/": return Key.OemQuestion;
                case "\\": return Key.OemPipe;
                case "`": return Key.OemTilde;
                case ",": return Key.OemComma;
                case ".": return Key.OemPeriod;
                case "[": return Key.OemOpenBrackets;
                case "]": return Key.OemCloseBrackets;
                case "Tab": return Key.Tab;
                case "Enter": return Key.Enter;
            }
            if (name.Length == 1 && char.IsLetter(name[0])) return (Key)Enum.Parse(typeof(Key), name.ToUpperInvariant());
            if (name.Length == 1 && char.IsDigit(name[0])) return (Key)Enum.Parse(typeof(Key), "D" + name);
            return Enum.TryParse(name, out Key k) ? k : Key.None;
        }

        // ------------------------------------------------------------------ in-window menu (Windows / Linux)
        private Menu BuildModelTopMenu(bool mac)
        {
            var tops = new List<object>();
            foreach (var menu in WorkbenchMenuModel.Build())
            {
                if (!menu.VisibleOn(mac)) continue;
                var top = new MenuItem { Header = "_" + menu.Title };
                top.ItemsSource = BuildMenuItems(menu.Items, mac);
                top.SubmenuOpened += (s, e) => RefreshMenuItemStates(top);
                tops.Add(top);
            }
            return new Menu { VerticalAlignment = VerticalAlignment.Center, ItemsSource = tops };
        }

        private List<object> BuildMenuItems(List<WorkbenchMenuEntry> entries, bool mac)
        {
            var items = new List<object>();
            foreach (var e in entries)
            {
                if (!e.VisibleOn(mac)) continue;
                if (e.IsSeparator) { if (items.Count > 0 && !(items[items.Count - 1] is Separator)) items.Add(new Separator()); continue; }
                if (e.IsSubmenu)
                {
                    var sub = new MenuItem { Header = new LiteralTextBlock(e.Label) };
                    if (e.Children.Count == 1 && e.Children[0].Id == WorkbenchMenuModel.OpenRecentId)
                    {
                        sub.ItemsSource = new List<object> { new MenuItem { Header = new LiteralTextBlock("(no recent folders)"), IsEnabled = false } };
                        sub.SubmenuOpened += (s, a) => sub.ItemsSource = BuildRecentMenuItems();
                    }
                    else
                    {
                        sub.ItemsSource = BuildMenuItems(e.Children, mac);
                        sub.SubmenuOpened += (s, a) => RefreshMenuItemStates(sub);
                    }
                    items.Add(sub);
                    continue;
                }
                var entry = e;
                var mi = new MenuItem { Header = new LiteralTextBlock(e.Label), Tag = e.Id };
                string sc = e.ShortcutFor(mac);
                if (!string.IsNullOrEmpty(sc))
                {
                    if (!e.DisplayOnly && TryParseMenuGesture(sc, out var g)) mi.InputGesture = g; // display only: keys are handled by the workbench
                    else mi.Header = new LiteralTextBlock(e.Label + "    " + WorkbenchMenuModel.FormatShortcut(sc, mac));
                }
                if (e.Checkable)
                {
                    mi.ToggleType = MenuItemToggleType.CheckBox;
                    mi.IsChecked = MenuCheckState(e.Id) ?? false;
                }
                mi.Click += (s, a) => InvokeMenuCommand(entry.Id);
                items.Add(mi);
            }
            while (items.Count > 0 && items[items.Count - 1] is Separator) items.RemoveAt(items.Count - 1);
            return items;
        }

        private List<object> BuildRecentMenuItems()
        {
            var items = new List<object>();
            foreach (var path in RecentFolders())
            {
                string p = path;
                var mi = new MenuItem { Header = new LiteralTextBlock(PathDisplay(p)) };
                ToolTip.SetTip(mi, p);
                mi.Click += (s, a) => { if (Directory.Exists(p)) LoadWorkspace(p); else SetStatusMessage("Folder not found: " + p); };
                items.Add(mi);
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = new LiteralTextBlock("(no recent folders)"), IsEnabled = false });
            return items;
        }

        private static string PathDisplay(string p)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && p.StartsWith(home, StringComparison.Ordinal)) return "~" + p.Substring(home.Length);
            }
            catch { }
            return p;
        }

        private void RefreshMenuItemStates(MenuItem parent)
        {
            if (parent?.ItemsSource == null) return;
            foreach (var o in parent.ItemsSource)
                if (o is MenuItem mi && mi.ToggleType == MenuItemToggleType.CheckBox && mi.Tag is string id)
                    mi.IsChecked = MenuCheckState(id) ?? false;
        }

        // ------------------------------------------------------------------ macOS native menu
        private readonly List<(NativeMenuItem Item, string Id)> nativeCheckItems = new List<(NativeMenuItem, string)>();

        /// <summary>Builds the native menu bar (app menu, File ... Help) from the shared model.</summary>
        private void BuildModelNativeMenu(NativeMenu rootMenu, bool includeAppMenu)
        {
            const bool mac = true;
            nativeCheckItems.Clear();
            foreach (var menu in WorkbenchMenuModel.Build())
            {
                if (!menu.VisibleOn(mac)) continue;
                var sub = new NativeMenu();
                FillNativeMenu(sub, menu.Items);
                if (menu.Title == "codeLess")
                {
                    if (Application.Current != null)
                    {
                        Application.Current.Name = "codeLess";
                        NativeMenu.SetMenu(Application.Current, sub);
                    }
                    if (!includeAppMenu) continue;
                }
                rootMenu.Add(new NativeMenuItem(menu.Title) { Menu = sub });
            }
        }

        private void FillNativeMenu(NativeMenu target, List<WorkbenchMenuEntry> entries)
        {
            const bool mac = true;
            bool lastWasSep = true;
            foreach (var e in entries)
            {
                if (!e.VisibleOn(mac)) continue;
                if (e.IsSeparator)
                {
                    if (!lastWasSep) { target.Add(new NativeMenuItemSeparator()); lastWasSep = true; }
                    continue;
                }
                lastWasSep = false;
                if (e.IsSubmenu)
                {
                    var sub = new NativeMenu();
                    if (e.Children.Count == 1 && e.Children[0].Id == WorkbenchMenuModel.OpenRecentId)
                    {
                        FillRecentNativeMenu(sub);
                        sub.NeedsUpdate += (s, a) => FillRecentNativeMenu(sub);
                    }
                    else FillNativeMenu(sub, e.Children);
                    target.Add(new NativeMenuItem(e.Label) { Menu = sub });
                    continue;
                }
                var entry = e;
                var item = new NativeMenuItem(e.Label);
                string sc = e.ShortcutFor(mac);
                if (!e.DisplayOnly && TryParseMenuGesture(sc, out var g)) item.Gesture = g;
                if (e.Checkable)
                {
                    item.ToggleType = NativeMenuItemToggleType.CheckBox;
                    item.IsChecked = MenuCheckState(e.Id) ?? false;
                    nativeCheckItems.Add((item, e.Id));
                }
                item.Click += (s, a) => InvokeNativeMenuCommand(entry);
                target.Add(item);
            }
            target.NeedsUpdate += (s, a) => RefreshNativeCheckStates();
        }

        private void FillRecentNativeMenu(NativeMenu sub)
        {
            sub.Items.Clear();
            foreach (var path in RecentFolders())
            {
                string p = path;
                var it = new NativeMenuItem(PathDisplay(p));
                it.Click += (s, a) => { if (Directory.Exists(p)) LoadWorkspace(p); else SetStatusMessage("Folder not found: " + p); };
                sub.Add(it);
            }
            if (sub.Items.Count == 0) sub.Add(new NativeMenuItem("(no recent folders)") { IsEnabled = false });
        }

        /// <summary>Refreshes every checkable native item (called when a menu opens and after a command ran).</summary>
        public void RefreshNativeCheckStates()
        {
            foreach (var (item, id) in nativeCheckItems)
            {
                bool v = MenuCheckState(id) ?? false;
                if (item.IsChecked != v) item.IsChecked = v;
            }
        }

        /// <summary>
        /// Native key equivalents fire before the focused control sees the key. Editor-only commands whose macOS shortcut
        /// has no ⌘ (⌥↑ Move Line Up, ⇧⌥A ...) are text-editing keys in text boxes and the terminal: when one of those has
        /// focus, the key is handed back to it instead of editing the code behind it.
        /// </summary>
        private void InvokeNativeMenuCommand(WorkbenchMenuEntry e)
        {
            string sc = e.ShortcutFor(true);
            bool editorOnly = e.Id.StartsWith("sel.", StringComparison.Ordinal) || e.Id == "edit.toggleBlockComment";
            if (editorOnly && sc != null && !sc.Contains("Cmd+") && TryParseMenuGesture(sc, out var g))
            {
                var focused = FocusManager?.GetFocusedElement();
                if (focused is TextBox || (focused is Visual v && IsFromTerminal(v)))
                {
                    if (focused is global::Avalonia.Interactivity.Interactive target) target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = g.Key, KeyModifiers = g.KeyModifiers });
                    return;
                }
            }
            InvokeMenuCommand(e.Id);
            RefreshNativeCheckStates();
        }
    }

    /// <summary>NSApplication actions for the macOS app / Window menus (Hide, Hide Others, Show All, Bring All to Front).</summary>
    internal static class MacAppNative
    {
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

        public static void Send(string selector)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
            try
            {
                IntPtr app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app != IntPtr.Zero) objc_msgSend_ptr(app, sel_registerName(selector), IntPtr.Zero);
            }
            catch { }
        }
    }
}
