using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// v3.3.1 macOS report: "There is no case-sensitive and whole-word option in Find &amp; Replace", "There is no Word Wrap
    /// option", plus VS Code menu parity (every menu item wired, shortcuts equal to the real key bindings).
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class FindWrapAndMenusHeadlessTests : IDisposable
    {
        private readonly string dir;
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;

        public FindWrapAndMenusHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_FindMenus_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            EditorNicetyOptions.ApplyFrom(new IdePersistentSettings());
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static KeyModifiers OptionMods => MacKeymap.IsMacOS ? KeyModifiers.Meta | KeyModifiers.Alt : KeyModifiers.Alt;

        private static void Key(Interactive target, Key key, KeyModifiers mods, PhysicalKey physical = PhysicalKey.None)
        {
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, PhysicalKey = physical, Source = target });
        }

        private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        private (MainWindow, EditorTabBuffer) Open(string name, string content)
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, content);
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            return (w, tab);
        }

        private static void CloseWindow(MainWindow w)
        {
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        // ------------------------------------------------------------------ find widget
        [AvaloniaFact]
        public void FindWidget_ToggleButtons_ClickAndShortcuts_ChangeMatching()
        {
            var (w, tab) = Open("find.txt", "Foo foo food FOO\nfoo.bar foo-bar\nlast foo line\n");
            var ed = w.PrimaryEditor;
            ed.ShowFindBar(false);
            ed.SetFindQuery("foo");
            Assert.Equal(7, ed.FindMatches.Count);
            Assert.Equal("1 of 7", ed.FindCountText);

            // the toggles are real buttons with a visible ON state and platform tooltips
            var mc = ed.FindOptionButton(FindOptionToggles.MatchCase);
            Assert.Equal(UiTokens.InputOptionSize, (int)mc.Width);
            Assert.Contains(MacKeymap.IsMacOS ? "Match Case (⌥⌘C)" : "Match Case (Alt+C)", ToolTip.GetTip(mc)?.ToString());
            Assert.Contains("Match Whole Word (", ToolTip.GetTip(ed.FindOptionButton(FindOptionToggles.WholeWord))?.ToString());
            Assert.Contains("Use Regular Expression (", ToolTip.GetTip(ed.FindOptionButton(FindOptionToggles.Regex))?.ToString());
            Assert.False(FindOptionToggles.IsOn(mc));

            Click(mc);
            Assert.True(ed.IsFindOptionOn(FindOptionToggles.MatchCase));
            Assert.True(FindOptionToggles.IsOn(mc));
            Assert.Equal("filled", Brushes(mc));
            Assert.Equal(5, ed.FindMatches.Count); // foo, foo(d), foo.bar, foo-bar, foo line

            // shortcut while the find input has focus: whole word (Alt+W / ⌥⌘W)
            Key(ed.FindInputBox, global::Avalonia.Input.Key.W, OptionMods);
            Assert.True(ed.IsFindOptionOn(FindOptionToggles.WholeWord));
            Assert.Equal(4, ed.FindMatches.Count); // not "food"

            // shortcut while the editor has focus: match case off (by physical key: Option changes the character)
            Key(ed.Canvas, global::Avalonia.Input.Key.None, OptionMods, PhysicalKey.C);
            Assert.False(ed.IsFindOptionOn(FindOptionToggles.MatchCase));
            Assert.Equal(6, ed.FindMatches.Count); // whole words, any case: Foo foo FOO, foo.bar, foo-bar, foo line

            // regex (+ whole word keeps working with regex), invalid pattern message
            Click(ed.FindOptionButton(FindOptionToggles.WholeWord));
            Click(ed.FindOptionButton(FindOptionToggles.Regex));
            ed.SetFindQuery("fo+\\.bar");
            Assert.Single(ed.FindMatches);
            ed.SetFindQuery("fo(o");
            Assert.Empty(ed.FindMatches);
            Assert.Equal("Invalid regex", ed.FindCountText);
            Assert.NotNull(ed.FindErrorText);
            Click(ed.FindOptionButton(FindOptionToggles.WholeWord));
            ed.SetFindQuery("fo+");
            Assert.Equal(6, ed.FindMatches.Count); // regex + whole word: Foo foo FOO, foo.bar, foo-bar, foo line (not food)
            Click(ed.FindOptionButton(FindOptionToggles.Regex));
            Click(ed.FindOptionButton(FindOptionToggles.WholeWord));

            // find in selection (Alt+L / ⌥⌘L): only line 2
            ed.SetFindQuery("foo");
            tab.SelectionStartLine = 1; tab.SelectionStartCol = 0; tab.CursorLine = 1; tab.CursorCol = tab.Lines[1].Length;
            Key(ed.FindInputBox, global::Avalonia.Input.Key.L, OptionMods);
            Assert.True(ed.IsFindOptionOn(FindOptionToggles.InSelection));
            Assert.Equal(2, ed.FindMatches.Count);
            Assert.All(ed.FindMatches, m => Assert.Equal(1, m.Line));
            Click(ed.FindOptionButton(FindOptionToggles.InSelection));
            Assert.Equal(7, ed.FindMatches.Count);

            // preserve case on the replace input (Alt+P / ⌥⌘P)
            ed.ShowFindBar(true);
            ed.SetFindQuery("foo");
            Key(ed.ReplaceInputBox, global::Avalonia.Input.Key.P, OptionMods);
            Assert.True(ed.IsFindOptionOn(FindOptionToggles.PreserveCase));
            ed.ReplaceAllMatches("bar");
            Assert.Equal("Bar bar bard BAR", tab.Lines[0]);
            CloseWindow(w);
        }

        private static string Brushes(Button b) => (b.Background as global::Avalonia.Media.ISolidColorBrush)?.Color.A == 0 ? "transparent" : "filled";

        [AvaloniaFact]
        public void SearchView_ToggleShortcuts_ChangeResults()
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "Foo foo FOO foobar\n");
            var w = new MainWindow(dir);
            w.Show();
            w.ShowSearchView(false);
            w.ExecuteWorkspaceSearch("foo");
            Assert.Equal(4, w.WorkspaceSearchGroups[0].Matches.Count);
            Key(w.WorkspaceSearchQueryInput, global::Avalonia.Input.Key.C, OptionMods);
            Assert.True(w.IsWorkspaceSearchOptionOn(FindOptionToggles.MatchCase));
            Assert.True(FindOptionToggles.IsOn(w.WorkspaceSearchOptionButton(FindOptionToggles.MatchCase)));
            Assert.Equal(2, w.WorkspaceSearchGroups[0].Matches.Count);
            Key(w.WorkspaceSearchQueryInput, global::Avalonia.Input.Key.W, OptionMods);
            Assert.Single(w.WorkspaceSearchGroups[0].Matches);
            Click(w.WorkspaceSearchOptionButton(FindOptionToggles.MatchCase));
            Assert.Equal(3, w.WorkspaceSearchGroups[0].Matches.Count);
            CloseWindow(w);
        }

        [Fact]
        public void FindEngine_WholeWordRegexPreserveCaseAndScope()
        {
            var lines = new List<string> { "foo(x) foo-bar xfoo", "FOO foo" };
            string err;
            Assert.Equal(4, EditorFindEngine.FindAll(lines, "foo", false, true, false, null, out err).Count); // foo(x) foo-bar FOO foo, not xfoo
            Assert.Single(EditorFindEngine.FindAll(lines, "foo(", false, false, false, null, out err));
            Assert.Single(EditorFindEngine.FindAll(lines, "fo+\\(", false, true, true, null, out err));
            Assert.Empty(EditorFindEngine.FindAll(lines, "[", false, false, true, null, out err));
            Assert.NotNull(err);
            Assert.Single(EditorFindEngine.FindAll(lines, "foo", true, false, false, new EditorFindScope(1, 0, 1, 7), out err));
            Assert.Equal("BAR", EditorFindEngine.ApplyPreserveCase("FOO", "bar"));
            Assert.Equal("Bar", EditorFindEngine.ApplyPreserveCase("Foo", "bar"));
            Assert.Equal("bar", EditorFindEngine.ApplyPreserveCase("foo", "Bar"));
            Assert.Equal("Bar-Baz", EditorFindEngine.ApplyPreserveCase("Foo-Qux", "bar-baz"));
        }

        // ------------------------------------------------------------------ word wrap
        [AvaloniaFact]
        public void WordWrap_MenuItem_OptionZ_PhysicalKey_NoOmegaInserted_AndSettingsSync()
        {
            var (w, tab) = Open("wrap.txt", "a long line\nsecond\n");
            var canvas = w.PrimaryEditor.Canvas;
            canvas.Focus();
            if (tab.WordWrap) w.InvokeMenuCommand("view.wordWrap"); // the user's settings may enable wrapping
            Assert.False(tab.WordWrap);

            // View → Word Wrap exists in the native (macOS) menu with ⌥Z and a check mark, and in the in-window menu
            var native = NativeMenu.GetMenu(w);
            var view = native.Items.OfType<NativeMenuItem>().First(i => i.Header == "View").Menu;
            var wrapItem = view.Items.OfType<NativeMenuItem>().First(i => i.Header == "Word Wrap");
            Assert.Equal(NativeMenuItemToggleType.CheckBox, wrapItem.ToggleType);
            Assert.Equal(new KeyGesture(global::Avalonia.Input.Key.Z, KeyModifiers.Alt), wrapItem.Gesture);
            var inWindowView = w.TopMenuControl.ItemsSource.Cast<MenuItem>().First(m => (m.Header?.ToString() ?? "").Replace("_", "") == "View");
            var inWindowWrap = inWindowView.ItemsSource.OfType<MenuItem>().First(m => m.Tag as string == "view.wordWrap");
            Assert.Equal(MenuItemToggleType.CheckBox, inWindowWrap.ToggleType);

            // menu command toggles and the check mark follows
            w.InvokeMenuCommand("view.wordWrap");
            Assert.True(tab.WordWrap);
            Assert.True(w.MenuCheckState("view.wordWrap"));
            w.RefreshNativeCheckStates();
            Assert.True(wrapItem.IsChecked);

            // ⌥Z on macOS: Option makes the key report another symbol; matched by physical key, and the "Ω" text input
            // that macOS sends afterwards is swallowed
            string before = tab.Lines[0];
            Key(canvas, global::Avalonia.Input.Key.None, KeyModifiers.Alt, PhysicalKey.Z);
            Assert.False(tab.WordWrap);
            canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "Ω", Source = canvas });
            Assert.Equal(before, tab.Lines[0]);
            // Alt+Z from the find input toggles too (workbench command)
            w.PrimaryEditor.ShowFindBar(false);
            Key(w.PrimaryEditor.FindInputBox, global::Avalonia.Input.Key.Z, KeyModifiers.Alt);
            Assert.True(tab.WordWrap);
            // the next real character is typed normally
            canvas.Focus();
            Key(canvas, global::Avalonia.Input.Key.X, KeyModifiers.None);
            canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x", Source = canvas });
            Assert.StartsWith("x", tab.Lines[tab.CursorLine].Substring(Math.Max(0, tab.CursorCol - 1)));

            // the command palette has "View: Toggle Word Wrap"
            w.OpenCommandPalette(">");
            Assert.Contains(w.CommandPalette.AllEntries, e => e.Category == "View" && e.Title == "Toggle Word Wrap" && e.Shortcut == "Alt+Z");
            w.CommandPalette.IsVisible = false;

            // saving Settings without touching Word wrap keeps the per-editor toggle; changing it applies to every editor
            var s = w.CurrentSettings;
            bool baseWrap = s.WordWrap;
            if (tab.WordWrap == baseWrap) w.InvokeMenuCommand("view.wordWrap"); // editor differs from the setting
            bool toggled = tab.WordWrap;
            w.ApplyEditorIndentAndWrapSettingsForTest(s); // unrelated save
            Assert.Equal(toggled, tab.WordWrap);
            s.WordWrap = !baseWrap;
            w.ApplyEditorIndentAndWrapSettingsForTest(s);
            Assert.Equal(!baseWrap, tab.WordWrap);
            s.WordWrap = baseWrap;
            w.ApplyEditorIndentAndWrapSettingsForTest(s);
            Assert.Equal(baseWrap, tab.WordWrap);
            CloseWindow(w);
        }

        // ------------------------------------------------------------------ menus
        /// <summary>Menu items VS Code also lists twice with the same key (same command).</summary>
        private static readonly HashSet<string> Aliases = new HashSet<string>
        {
            "edit.findInFiles|view.search", "view.commandPalette|help.showAllCommands",
            "view.fullScreen|run.stepInto" // Windows / Linux F11: Step Into while debugging, Full Screen otherwise (VS Code)
        };

        [Fact]
        public void MenuModel_EveryShortcutParses_AndNoTwoCommandsShareAGesture()
        {
            foreach (bool mac in new[] { true, false })
            {
                var seen = new Dictionary<string, string>();
                foreach (var e in WorkbenchMenuModel.AllCommands(mac))
                {
                    Assert.False(string.IsNullOrWhiteSpace(e.Label), e.Id);
                    string sc = e.ShortcutFor(mac);
                    if (string.IsNullOrEmpty(sc) || e.DisplayOnly) continue;
                    Assert.True(MainWindow.TryParseMenuGesture(sc, out var g), e.Id + ": " + sc);
                    string key = g.ToString();
                    if (seen.TryGetValue(key, out string other)) Assert.True(other == e.Id || Aliases.Contains(other + "|" + e.Id), key + " is bound to both " + other + " and " + e.Id + (mac ? " (mac)" : ""));
                    else seen[key] = e.Id;
                }
                var tops = WorkbenchMenuModel.Build().Where(m => m.VisibleOn(mac)).Select(m => m.Title).ToArray();
                Assert.Equal(mac
                    ? new[] { "codeLess", "File", "Edit", "Selection", "View", "Go", "Run", "Terminal", "Window", "Help" }
                    : new[] { "File", "Edit", "Selection", "View", "Go", "Run", "Terminal", "Window", "Help" }, tops);
            }
            Assert.Equal("⌥⌘F", WorkbenchMenuModel.FormatShortcut("Cmd+Alt+F", true));
            Assert.Equal("⌘K ⌘O", WorkbenchMenuModel.FormatShortcut("Cmd+K Cmd+O", true));
        }

        /// <summary>Commands not executed headlessly (quit, new window, OS dialogs, browser, network, processes).</summary>
        private static readonly HashSet<string> Unsafe = new HashSet<string>
        {
            "app.quit", "file.exit", "file.closeWindow", "file.newWindow", "file.open", "file.openFolder", "file.addFolder",
            "file.saveAs", "file.revert", "app.checkUpdates", "app.hide", "app.hideOthers", "app.showAll", "win.minimize",
            "win.zoom", "win.bringAllToFront", "help.documentation", "help.releaseNotes", "help.reportIssue", "term.new",
            "term.split", "term.runActiveFile", "term.runBuildTask", "run.start", "run.withoutDebugging", "run.restart",
            "view.fullScreen", "file.closeFolder", WorkbenchMenuModel.OpenRecentId, "file.closeEditor", "go.definition",
            "go.references", "term.kill", "view.networkActivity", "view.zenMode"
        };

        [AvaloniaFact]
        public void MenuModel_NoDeadItems_CommandsRun_ChecksAreLive_AndShortcutsAreTheRealBindings()
        {
            var (w, tab) = Open("menu.cs", "class A\n{\n    void M() { int x = 1; }\n}\n");
            foreach (bool mac in new[] { true, false })
                foreach (var e in WorkbenchMenuModel.AllCommands(mac))
                {
                    Assert.True(w.MenuCommands.ContainsKey(e.Id), "menu item without a command: " + e.Id + " (" + e.Label + ")");
                    if (e.Checkable) Assert.True(w.MenuChecks.ContainsKey(e.Id), "checkable item without state: " + e.Id);
                }

            // every safe command runs without throwing; toggles flip their check state
            var ids = WorkbenchMenuModel.AllCommands(true).Concat(WorkbenchMenuModel.AllCommands(false)).Select(e => e.Id).Distinct().ToList();
            foreach (string id in ids)
            {
                if (Unsafe.Contains(id)) continue;
                bool? before = w.MenuCheckState(id);
                w.InvokeMenuCommand(id);
                if (w.IsModalOpen) w.ModalHost.CloseModal();
                if (w.IsCommandPaletteOpen) w.CommandPalette.IsVisible = false;
                if (before.HasValue && id != "view.zenMode") Assert.True(before.Value != w.MenuCheckState(id), "check state did not change: " + id);
                if (before.HasValue) w.InvokeMenuCommand(id); // restore
                if (w.IsModalOpen) w.ModalHost.CloseModal();
            }

            // palette shortcuts equal the menu's (same command, same key)
            w.OpenCommandPalette(">");
            var palette = w.CommandPalette.AllEntries.Where(p => !string.IsNullOrEmpty(p.Shortcut)).ToList();
            w.CommandPalette.IsVisible = false;
            foreach (var e in WorkbenchMenuModel.AllCommands(false))
            {
                var p = palette.FirstOrDefault(x => string.Equals(x.Title, e.Label, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Title, "Toggle " + e.Label, StringComparison.OrdinalIgnoreCase));
                if (p == null || string.IsNullOrEmpty(e.Shortcut)) continue;
                Assert.True(string.Equals(p.Shortcut.Replace(" ", ""), e.Shortcut.Replace(" ", ""), StringComparison.OrdinalIgnoreCase), e.Id + ": menu " + e.Shortcut + " vs palette " + p.Shortcut);
            }

            // pressing the menu's shortcut does what the menu item does
            Assert.False(w.IsZenMode);
            if (!w.IsSidebarVisible) w.InvokeMenuCommand("view.toggleSidebar");
            w.SwitchSidebarView("git");
            Assert.True(w.SimulateShortcut(global::Avalonia.Input.Key.E, KeyModifiers.Control | KeyModifiers.Shift));
            Assert.Equal("explorer", w.ActiveSidebarView);
            bool side = w.IsSidebarVisible;
            w.SimulateShortcut(global::Avalonia.Input.Key.B, KeyModifiers.Control);
            Assert.NotEqual(side, w.IsSidebarVisible);
            w.InvokeMenuCommand("view.toggleSidebar");
            Assert.Equal(side, w.IsSidebarVisible);
            w.SimulateShortcut(global::Avalonia.Input.Key.M, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.Equal("problems", w.ActiveBottomTab);
            w.SimulateShortcut(global::Avalonia.Input.Key.U, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.Equal("output", w.ActiveBottomTab);
            var cur = w.OpenFileInTab(tab.FilePath, preview: false); // the loop opened Welcome / a new text file
            bool wrap = cur.WordWrap;
            w.PrimaryEditor.Canvas.Focus();
            w.SimulateShortcut(global::Avalonia.Input.Key.Z, KeyModifiers.Alt);
            Assert.NotEqual(wrap, cur.WordWrap);
            w.InvokeMenuCommand("view.wordWrap");
            Assert.Equal(wrap, cur.WordWrap);
            w.SimulateShortcut(global::Avalonia.Input.Key.F, KeyModifiers.Control);
            Assert.True(w.PrimaryEditor.IsFindBarVisible);
            CloseWindow(w);
        }
    }
}
