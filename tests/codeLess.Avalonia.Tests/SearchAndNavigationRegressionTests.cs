using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Regressions for "Search in folder does not open the file" and the VS Code parity gaps of the Search view,
    /// Quick Open, Explorer reveal and navigation history.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class SearchAndNavigationRegressionTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackup;
        private readonly string _root;

        public SearchAndNavigationRegressionTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _settingsBackup = File.Exists(_settingsFilePath) ? File.ReadAllText(_settingsFilePath) : null;
            _root = Path.Combine(Path.GetTempPath(), "codeLess_Search_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (_settingsBackup != null) File.WriteAllText(_settingsFilePath, _settingsBackup);
                else if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);
            }
            catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Write(string rel, string content)
        {
            string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
            return full;
        }

        private MainWindow OpenWindow()
        {
            var w = new MainWindow(_root) { Width = 1400, Height = 900 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return w;
        }

        private static void Click(MainWindow w, Control target, int clicks = 1)
        {
            Dispatcher.UIThread.RunJobs();
            var p = target.TranslatePoint(new Point(Math.Min(30, target.Bounds.Width / 2), target.Bounds.Height / 2), w);
            Assert.True(p.HasValue, "target is not in the visual tree");
            for (int i = 0; i < clicks; i++)
            {
                w.MouseDown(p.Value, MouseButton.Left);
                w.MouseUp(p.Value, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
        }

        private static ListBoxItem ContainerFor(MainWindow w, SearchResultRow row)
        {
            var lb = w.WorkspaceSearchResultsListControl;
            lb.ScrollIntoView(row);
            Dispatcher.UIThread.RunJobs();
            var item = lb.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, row));
            Assert.NotNull(item);
            return item;
        }

        [AvaloniaFact]
        public void ClickingSearchResult_OpensFile_SelectsMatch_AndReclickWorks()
        {
            Write("a.txt", "alpha\nbeta\n");
            string b = Write("sub/b.txt", "one\ntwo\nthree needle here\nfour\n");
            var w = OpenWindow();
            w.SwitchActivityView("search");
            w.ExecuteWorkspaceSearch("needle");

            var group = Assert.Single(w.WorkspaceSearchGroups);
            Assert.Equal(b, group.FilePath);
            var matchRow = group.MatchRows[0];

            Click(w, ContainerFor(w, matchRow));
            Assert.NotNull(w.ActiveTab);
            Assert.Equal(b, w.ActiveTab.FilePath);
            Assert.Equal(2, w.ActiveTab.CursorLine);
            Assert.True(w.ActiveTab.HasSelection());
            Assert.Equal("needle", w.ActiveTab.GetSelectedText());
            Assert.True(w.ActiveTab.IsPreviewTab); // single click = preview, like VS Code

            // Switch away, then click the SAME (still selected) result again: it must re-open (SelectionChanged never fired here).
            var other = w.OpenFileInTab(Path.Combine(_root, "a.txt"), preview: false);
            Assert.Same(other, w.ActiveTab);
            Click(w, ContainerFor(w, matchRow));
            Assert.Equal(b, w.ActiveTab.FilePath);
            Assert.Equal("needle", w.ActiveTab.GetSelectedText());

            // Double click pins the tab.
            Click(w, ContainerFor(w, matchRow), clicks: 2);
            Assert.False(w.ActiveTab.IsPreviewTab);
            w.Close();
        }

        [AvaloniaFact]
        public void FileHeaderClick_TogglesCollapse_AndKeyboardEnterOpens()
        {
            string f = Write("x.cs", "class A { int needle; int needle2; }\n// needle\n");
            var w = OpenWindow();
            w.SwitchActivityView("search");
            w.ExecuteWorkspaceSearch("needle");
            var g = Assert.Single(w.WorkspaceSearchGroups);
            Assert.Equal(3, g.Matches.Count); // one row per occurrence, two on line 1
            Assert.Equal(4, w.WorkspaceSearchRows.Count);
            Assert.Equal(1, g.Matches[0].LineNumber);
            Assert.Equal(15, g.Matches[0].Column);
            Assert.Equal(27, g.Matches[1].Column);

            Click(w, ContainerFor(w, g.HeaderRow));
            Assert.True(g.IsCollapsed);
            Assert.Single(w.WorkspaceSearchRows);
            Click(w, ContainerFor(w, g.HeaderRow));
            Assert.False(g.IsCollapsed);
            Assert.Equal(4, w.WorkspaceSearchRows.Count);

            // Down from the query box focuses the first match; Enter opens it pinned with the editor focused.
            w.WorkspaceSearchQueryInput.Focus();
            w.WorkspaceSearchQueryInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
            Dispatcher.UIThread.RunJobs();
            Assert.Same(g.MatchRows[0], w.WorkspaceSearchResultsListControl.SelectedItem);
            w.WorkspaceSearchResultsListControl.SelectedItem = g.MatchRows[2];
            w.WorkspaceSearchResultsListControl.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f, w.ActiveTab.FilePath);
            Assert.Equal(1, w.ActiveTab.CursorLine);
            Assert.Equal("needle", w.ActiveTab.GetSelectedText());
            Assert.False(w.ActiveTab.IsPreviewTab);
            w.Close();
        }

        [AvaloniaFact]
        public void F4_And_ShiftF4_Cycle_Through_Results()
        {
            string a = Write("a.txt", "needle\n");
            string b = Write("b.txt", "x\nneedle\n");
            var w = OpenWindow();
            w.ExecuteWorkspaceSearch("needle");
            Assert.Equal(2, w.WorkspaceSearchGroups.Count);

            Assert.True(w.SimulateShortcut(Key.F4, KeyModifiers.None));
            Assert.Equal(a, w.ActiveTab.FilePath);
            Assert.True(w.SimulateShortcut(Key.F4, KeyModifiers.None));
            Assert.Equal(b, w.ActiveTab.FilePath);
            Assert.Equal(1, w.ActiveTab.CursorLine);
            Assert.True(w.SimulateShortcut(Key.F4, KeyModifiers.None)); // wraps
            Assert.Equal(a, w.ActiveTab.FilePath);
            Assert.True(w.SimulateShortcut(Key.F4, KeyModifiers.Shift)); // previous wraps back
            Assert.Equal(b, w.ActiveTab.FilePath);
            w.Close();
        }

        [AvaloniaFact]
        public void FindInFolder_ScopesIncludeToFolder_AndClickOpens()
        {
            Write("root.txt", "needle at root\n");
            string inner = Write("pkg/inner/deep.txt", "a\nneedle deep\n");
            Write("pkg/other.txt", "needle other\n");
            var w = OpenWindow();
            w.ExecuteWorkspaceSearch("needle");
            Assert.Equal(3, w.WorkspaceSearchGroups.Count);

            w.FindInFolder(Path.Combine(_root, "pkg", "inner"));
            Assert.Equal("./pkg/inner", w.WorkspaceSearchIncludeInput.Text);
            var g = Assert.Single(w.WorkspaceSearchGroups);
            Assert.Equal(inner, g.FilePath);

            Click(w, ContainerFor(w, g.MatchRows[0]));
            Assert.Equal(inner, w.ActiveTab.FilePath);
            Assert.Equal(1, w.ActiveTab.CursorLine);
            // Explorer auto-reveals the opened file (ancestors expanded + selected).
            Assert.NotNull(w.ExplorerTree.SelectedNode);
            Assert.Equal(inner, w.ExplorerTree.SelectedNode.Tag);
            Assert.Contains(w.ExplorerTree.SelectedNode, w.ExplorerTree.VisibleFlatRows);

            // Find in Folder on the workspace root clears the scope.
            w.FindInFolder(_root);
            Assert.Equal("", w.WorkspaceSearchIncludeInput.Text);
            Assert.Equal(3, w.WorkspaceSearchGroups.Count);
            w.Close();
        }

        [AvaloniaFact]
        public void IncludeExcludeGlobs_And_Toggles_Work()
        {
            Write("src/A.cs", "Foo foo FOO foobar\n");
            Write("src/B.ts", "foo\n");
            Write("gen/C.cs", "foo\n");
            var w = OpenWindow();

            w.WorkspaceSearchIncludeInput.Text = "*.cs";
            w.ExecuteWorkspaceSearch("foo");
            Assert.All(w.WorkspaceSearchGroups, g => Assert.EndsWith(".cs", g.FilePath));
            Assert.Equal(2, w.WorkspaceSearchGroups.Count);

            w.WorkspaceSearchExcludeInput.Text = "gen";
            w.ExecuteWorkspaceSearch();
            var g1 = Assert.Single(w.WorkspaceSearchGroups);
            Assert.Equal(4, g1.Matches.Count);

            w.SetWorkspaceSearchFlags(matchCase: true);
            w.ExecuteWorkspaceSearch();
            Assert.Equal(2, w.WorkspaceSearchGroups[0].Matches.Count); // foo + foo(bar)

            w.SetWorkspaceSearchFlags(matchCase: false, wholeWord: true);
            w.ExecuteWorkspaceSearch();
            Assert.Equal(3, w.WorkspaceSearchGroups[0].Matches.Count); // Foo foo FOO, not foobar

            w.SetWorkspaceSearchFlags(wholeWord: false, useRegex: true);
            w.ExecuteWorkspaceSearch("fo+bar");
            Assert.Single(w.WorkspaceSearchGroups[0].Matches);
            Assert.Equal(6, w.WorkspaceSearchGroups[0].Matches[0].MatchLength);

            w.ExecuteWorkspaceSearch("fo(o");
            Assert.Empty(w.WorkspaceSearchGroups);
            Assert.Contains("Invalid regular expression", w.WorkspaceSearchSummaryText);
            w.Close();
        }

        [AvaloniaFact]
        public void ReplaceAll_HonoursRegexGroups_LiteralDollar_AndDirtyEditors()
        {
            string a = Write("a.txt", "id = 1;\nid = 22;\n");
            string b = Write("b.txt", "price id\n");
            var w = OpenWindow();

            w.SetWorkspaceSearchFlags(useRegex: true);
            w.ExecuteWorkspaceSearch(@"id = (\d+)");
            Assert.Equal(1, w.ExecuteWorkspaceReplaceAll(null, "key = $1"));
            Assert.Equal("key = 1;\nkey = 22;\n", File.ReadAllText(a).Replace("\r\n", "\n"));

            // Plain mode: "$1" is literal text.
            w.SetWorkspaceSearchFlags(useRegex: false);
            Assert.Equal(1, w.ExecuteWorkspaceReplaceAll("price", "$1"));
            Assert.Equal("$1 id\n", File.ReadAllText(b).Replace("\r\n", "\n"));

            // A dirty open editor is edited in memory (not overwritten on disk, unsaved edits preserved).
            var tab = w.OpenFileInTab(b, preview: false);
            tab.Content = "$1 id\nunsaved id\n";
            tab.SyncLinesFromContent();
            tab.IsDirty = true;
            Assert.Equal(1, w.ExecuteWorkspaceReplaceAll("id", "ident"));
            Assert.Equal("$1 ident\nunsaved ident\n", tab.Content.Replace("\r\n", "\n"));
            Assert.True(tab.IsDirty);
            Assert.Equal("$1 id\n", File.ReadAllText(b).Replace("\r\n", "\n"));
            w.Close();
        }

        [AvaloniaFact]
        public void QuickOpen_FindsFilesBeyondFirst250_LineSuffix_AndSingleClick()
        {
            for (int i = 0; i < 300; i++) Write($"many/f{i:D3}.txt", "x\n");
            string target = Write("zz/TargetWidget.cs", "l1\nl2\nl3 here\nl4\n");
            var w = OpenWindow();

            w.OpenQuickOpen();
            w.CommandPalette.SearchInput.Text = "TargetWidget";
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(w.CommandPalette.FilteredEntries);
            Assert.Equal(target, w.CommandPalette.FilteredEntries[0].FilePath);
            // Quick Open lists files only (commands need '>').
            Assert.All(w.CommandPalette.FilteredEntries, e => Assert.False(string.IsNullOrEmpty(e.FilePath)));

            w.CommandPalette.SearchInput.Text = "tgtwid:3";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(target, w.CommandPalette.FilteredEntries[0].FilePath);
            w.CommandPalette.ExecuteSelected();
            Assert.False(w.CommandPalette.IsVisible);
            Assert.Equal(target, w.ActiveTab.FilePath);
            Assert.Equal(2, w.ActiveTab.CursorLine);

            // Single click on a row opens it.
            w.OpenQuickOpen();
            w.CommandPalette.SearchInput.Text = "f299";
            Dispatcher.UIThread.RunJobs();
            var item = w.CommandPalette.ResultsListBox.GetVisualDescendants().OfType<ListBoxItem>().First();
            Click(w, item);
            Assert.False(w.CommandPalette.IsVisible);
            Assert.Equal(Path.Combine(_root, "many", "f299.txt"), w.ActiveTab.FilePath);
            w.Close();
        }

        [AvaloniaFact]
        public void SearchJump_RecordsHistory_SoBackReturnsToPreviousCaret()
        {
            string a = Write("a.txt", string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i)) + "\n");
            string b = Write("b.txt", "x\ny\nneedle\n");
            var w = OpenWindow();
            var ta = w.OpenFileInTab(a, preview: false);
            w.PrimaryEditor.GoToLine(30);

            w.ExecuteWorkspaceSearch("needle");
            w.ActivateSearchMatch(w.WorkspaceSearchGroups[0].Matches[0], preview: true, focusEditor: true);
            Assert.Equal(b, w.ActiveTab.FilePath);
            Assert.Equal(2, w.ActiveTab.CursorLine);

            w.NavigateBack();
            Assert.Equal(a, w.ActiveTab.FilePath);
            Assert.Equal(29, w.ActiveTab.CursorLine);

            w.NavigateForward();
            Assert.Equal(b, w.ActiveTab.FilePath);
            Assert.Equal(2, w.ActiveTab.CursorLine);
            w.Close();
        }

        [Fact]
        public void GlobPathFilter_And_QuickOpenSuffix_Parse_Like_VSCode()
        {
            string root = Path.Combine(Path.GetTempPath(), "ws");
            var inc = GlobPathFilter.Parse("./src, *.md", root);
            Assert.True(inc.IsMatch(Path.Combine(root, "src", "a", "b.cs"), "src/a/b.cs"));
            Assert.True(inc.IsMatch(Path.Combine(root, "docs", "x.md"), "docs/x.md"));
            Assert.False(inc.IsMatch(Path.Combine(root, "lib", "src", "b.cs"), "lib/src/b.cs"));

            var any = GlobPathFilter.Parse("node_modules, **/gen/**, *.{min.js,map}", root);
            Assert.True(any.IsMatch("", "a/node_modules/x.js"));
            Assert.True(any.IsMatch("", "a/gen/b/c.ts"));
            Assert.True(any.IsMatch("", "dist/app.min.js"));
            Assert.True(any.IsMatch("", "dist/app.map"));
            Assert.False(any.IsMatch("", "dist/app.js"));

            var abs = GlobPathFilter.Parse(Path.Combine(root, "src"), root);
            Assert.True(abs.IsMatch(Path.Combine(root, "src", "k.cs"), "src/k.cs"));
            Assert.False(abs.IsMatch(Path.Combine(root, "test", "k.cs"), "test/k.cs"));

            Assert.Equal("Main.cs", WorkspaceSearchEngine.SplitQuickOpenLineSuffix("Main.cs:12:4", out int l, out int c));
            Assert.Equal(12, l);
            Assert.Equal(4, c);
            Assert.Equal("Main.cs", WorkspaceSearchEngine.SplitQuickOpenLineSuffix("Main.cs", out l, out c));
            Assert.Equal(0, l);
        }
    }
}
