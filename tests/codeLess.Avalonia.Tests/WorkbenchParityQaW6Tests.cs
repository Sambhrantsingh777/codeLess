using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class WorkbenchParityQaW6Tests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();
        private readonly List<MainWindow> _openWindows = new List<MainWindow>();

        private string CreateWorkspace(out string fileA, out string fileB)
        {
            string root = Path.Combine(Path.GetTempPath(), "codeLess_W6_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            fileA = Path.Combine(root, "a", "index.ts");
            fileB = Path.Combine(root, "b", "index.ts");
            File.WriteAllText(fileA, "export const alpha = 1;\nexport const targetToken = 'hello';\n");
            File.WriteAllText(fileB, "export const beta = 2;\nexport const targetToken = 'world';\n");
            _tempDirs.Add(root);
            WorkspaceTrust.Trust(root);
            return root;
        }

        private MainWindow CreateWindow(string ws)
        {
            var w = new MainWindow(ws) { Width = 1400, Height = 900 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            _openWindows.Add(w);
            return w;
        }

        public void Dispose()
        {
            foreach (var w in _openWindows)
            {
                try
                {
                    w.SuppressUnsavedPrompts = true;
                    if (!w.IsClosed) w.Close();
                }
                catch { }
            }
            Dispatcher.UIThread.RunJobs();
            foreach (var d in _tempDirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Explorer_FileCopyCutPaste_AndDragDropMove_WorkCleanly()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            var w = CreateWindow(ws);

            var rootNode = w.ExplorerTree.Nodes[0];
            var dirA = rootNode.Nodes.First(n => n.Text == "a");
            var dirB = rootNode.Nodes.First(n => n.Text == "b");
            w.EnsureDirectoryNodePopulated(dirA);
            dirA.IsExpanded = true;
            w.EnsureDirectoryNodePopulated(dirB);
            dirB.IsExpanded = true;
            w.ExplorerTree.RebuildTreeLayout(false);

            var nodeA = dirA.Nodes.First(n => n.Text == "index.ts");

            // Copy & Paste in same directory -> creates "index copy.ts"
            w.CopyExplorerNode(nodeA);
            string copiedSameDir = w.PasteExplorerNode(dirA);
            Assert.NotNull(copiedSameDir);
            Assert.Equal("index copy.ts", Path.GetFileName(copiedSameDir));
            Assert.True(File.Exists(copiedSameDir));

            // Cut & Paste into directory b -> moves "index copy.ts" into b/ and updates open tab path
            var openCopyTab = w.OpenFileInTab(copiedSameDir, preview: false);
            var copyNode = w.ExplorerTree.FindNodeByPath(copiedSameDir);
            Assert.NotNull(copyNode);
            w.CutExplorerNode(copyNode);
            string movedPath = w.PasteExplorerNode(dirB);
            Assert.NotNull(movedPath);
            Assert.Equal(Path.Combine(ws, "b", "index copy.ts"), movedPath);
            Assert.False(File.Exists(copiedSameDir));
            Assert.True(File.Exists(movedPath));
            Assert.Equal(movedPath, openCopyTab.FilePath);

            // Drag & drop move back to directory a via MoveNodeToDirectory
            var movedNode = w.ExplorerTree.FindNodeByPath(movedPath);
            var refreshedDirA = w.ExplorerTree.FindNodeByPath(Path.Combine(ws, "a"));
            w.ExplorerTree.MoveNodeToDirectory(movedNode, refreshedDirA);
            Assert.True(File.Exists(copiedSameDir));
            Assert.False(File.Exists(movedPath));
        }

        [AvaloniaFact]
        public void Explorer_TypeToFilter_CompactFolders_AndProblemDecorations_WorkCleanly()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            string nestedLeafDir = Path.Combine(ws, "pkg", "sub", "leaf");
            Directory.CreateDirectory(nestedLeafDir);
            string uniqueFile = Path.Combine(ws, "unique_needle.py");
            File.WriteAllText(uniqueFile, "x = 1\n");

            var w = CreateWindow(ws);

            // Type-to-filter narrows VisibleFlatRows to matching items and Escape clears it
            int countBefore = w.ExplorerTree.VisibleFlatRows.Count;
            Assert.True(countBefore >= 3);
            w.ExplorerTree.ApplyTypeFilter("unique_needle");
            Assert.Single(w.ExplorerTree.VisibleFlatRows);
            Assert.Equal("unique_needle.py", w.ExplorerTree.SelectedNode?.Text);

            w.ExplorerTree.ApplyTypeFilter("");
            Assert.Equal(countBefore, w.ExplorerTree.VisibleFlatRows.Count);

            // Compact folders combines single-child folder chain
            var pkgNode = new ExplorerTreeNode("pkg") { IsDirectory = true };
            var subNode = new ExplorerTreeNode("sub") { IsDirectory = true, Parent = pkgNode };
            var leafNode = new ExplorerTreeNode("leaf") { IsDirectory = true, Parent = subNode };
            pkgNode.Nodes.Add(subNode);
            subNode.Nodes.Add(leafNode);

            w.ExplorerTree.CompactFoldersEnabled = false;
            Assert.Equal("pkg", w.ExplorerTree.GetCompactDisplayText(pkgNode));
            w.ExplorerTree.CompactFoldersEnabled = true;
            Assert.Equal("pkg/sub/leaf", w.ExplorerTree.GetCompactDisplayText(pkgNode));

            // Problem decorations populate ProblemCountByFullPath
            var tab = w.OpenFileInTab(fileA, preview: false);
            tab.Diagnostics = new List<DiagnosticItem>
            {
                new DiagnosticItem { Severity = "Error", Line = 1, Message = "Syntax error" }
            };
            w.RefreshOutlineAndProblems(tab);
            Assert.True(w.ExplorerTree.ProblemCountByFullPath.TryGetValue(fileA, out int probCount));
            Assert.Equal(1, probCount);
        }

        [AvaloniaFact]
        public void Tabs_TitleDisambiguation_AndDirtyDotHoverTurnsIntoCloseButton()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            var w = CreateWindow(ws);

            var tabA = w.OpenFileInTab(fileA, preview: false);
            var tabB = w.OpenFileInTab(fileB, preview: false);

            // Both tabs are named index.ts in folders a and b -> disambiguator returns "a" and "b"
            var map = WorkbenchParityHelper.ComputeDisambiguatedFolders(w.OpenTabs);
            Assert.Equal("a", map[fileA]);
            Assert.Equal("b", map[fileB]);

            // Dirty dot is visible when not hovered, and swaps to the close button on hover
            tabB.IsDirty = true;
            w.RefreshTabStrip();
            Assert.True(w.IsTabDirtyDotVisible(tabB));
            Assert.False(w.IsTabCloseButtonVisible(tabB));

            w.SetTabHoverState(tabB, true);
            Assert.False(w.IsTabDirtyDotVisible(tabB));
            Assert.True(w.IsTabCloseButtonVisible(tabB));

            w.SetTabHoverState(tabB, false);
            Assert.True(w.IsTabDirtyDotVisible(tabB));
            Assert.False(w.IsTabCloseButtonVisible(tabB));
        }

        [AvaloniaFact]
        public void QuickOpen_HelpPrefix_CtrlPCycles_CtrlEnterOpensToSide_AndRecentCommandsFirst()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            var w = CreateWindow(ws);

            // 1. '?' help prefix shows prefix help entries
            w.OpenCommandPalette("?");
            Assert.True(w.IsCommandPaletteOpen);
            Assert.Contains(w.CommandPalette.FilteredEntries, e => e.Category == ">" && e.Title == "Show and Run Commands");
            Assert.Contains(w.CommandPalette.FilteredEntries, e => e.Category == "@" && e.Title == "Go to Symbol in Editor");
            Assert.Contains(w.CommandPalette.FilteredEntries, e => e.Category == ":" && e.Title == "Go to Line/Column");
            w.CommandPalette.Close();

            // 2. Calling OpenQuickOpen while already open cycles selection
            w.OpenQuickOpen();
            Assert.Equal(0, w.CommandPalette.ResultsListBox.SelectedIndex);
            w.OpenQuickOpen();
            Assert.Equal(1, w.CommandPalette.ResultsListBox.SelectedIndex);

            // 3. Ctrl+Enter (ExecuteSelected(openToSide: true)) opens the file and splits the editor
            Assert.False(w.IsEditorSplit);
            w.CommandPalette.ExecuteSelected(openToSide: true);
            Assert.True(w.IsEditorSplit);
            Assert.NotNull(w.SplitEditor.CurrentBuffer);

            // 4. Executing a command in '>' mode records it in RecentCommandTitles and lists it first next time
            w.OpenCommandPalette(">");
            int idx = w.CommandPalette.FilteredEntries.FindIndex(e => e.Title == "Toggle Minimap");
            Assert.True(idx > 0);
            w.CommandPalette.ResultsListBox.SelectedIndex = idx;
            w.CommandPalette.ExecuteSelected();

            w.OpenCommandPalette(">");
            Assert.Equal("Toggle Minimap", w.CommandPalette.FilteredEntries[0].Title);
            w.CommandPalette.Close();
        }

        [AvaloniaFact]
        public void Search_DismissResultAndGroup_AndReplaceSingleMatch_WorkCleanly()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            var w = CreateWindow(ws);

            w.ShowSearchView(showReplace: true);
            w.ExecuteWorkspaceSearch("targetToken");
            Assert.Equal(2, w.WorkspaceSearchGroups.Count);

            // Replace single match in fileA
            var firstMatchRow = w.WorkspaceSearchRows.First(r => !r.IsFileHeader && r.Match.FilePath == fileA);
            Assert.True(w.ReplaceSingleSearchMatch(firstMatchRow, "replacedToken"));
            Assert.Contains("replacedToken", File.ReadAllText(fileA));
            Assert.Contains("targetToken", File.ReadAllText(fileB));
            Assert.Single(w.WorkspaceSearchGroups);

            // Dismiss remaining file group
            var remainingHeader = w.WorkspaceSearchRows.First(r => r.IsFileHeader);
            Assert.True(w.DismissSearchRow(remainingHeader));
            Assert.Empty(w.WorkspaceSearchGroups);
            Assert.Equal("No results found.", w.WorkspaceSearchSummaryText);
        }

        [AvaloniaFact]
        public void StatusBar_SelectionCount_ProblemsFilter_Notifications_AndEmojiMenu_WorkCleanly()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB);
            var w = CreateWindow(ws);

            var tab = w.OpenFileInTab(fileA, preview: false);
            tab.SelectionAnchorLine = 0;
            tab.SelectionAnchorCol = 0;
            tab.CursorLine = 0;
            tab.CursorCol = 6;
            w.UpdateStatusBar();
            Assert.Contains("(6 selected)", w.StatusBarLineColText);

            // Problems filtering
            tab.Diagnostics = new List<DiagnosticItem>
            {
                new DiagnosticItem { Severity = "Error", Line = 1, Message = "Alpha error" },
                new DiagnosticItem { Severity = "Warning", Line = 2, Message = "Beta warning" }
            };
            w.SetProblemsFilter("Alpha");
            var itemsAfterFilter = ((IEnumerable<Control>)w.ProblemsListItemsForTest()).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Single(itemsAfterFilter);
            Assert.Contains("Alpha error", itemsAfterFilter[0]);
            w.SetProblemsFilter("");

            // Notifications with action & "Do Not Show Again" + Notification Center
            bool actionRan = false;
            Assert.True(w.ShowWorkbenchNotification("test.notif.1", "warning", "Sample warning toast", "Fix Now", () => actionRan = true, canDoNotShowAgain: true));
            Assert.NotNull(w.ActiveNotificationToast);
            var dnsBtn = w.ActiveNotificationToast.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "NotificationDoNotShowAgainBtn");
            Assert.NotNull(dnsBtn);
            dnsBtn.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null(w.ActiveNotificationToast);
            // Subsequent notification with same id is suppressed
            Assert.False(w.ShowWorkbenchNotification("test.notif.1", "warning", "Should be suppressed"));

            // Notification center toggle
            w.ToggleNotificationsCenter();
            Assert.True(w.IsNotificationCenterOpen);
            Assert.Equal("notifications-center", w.ActiveModalId);
            w.ToggleNotificationsCenter();
            Assert.False(w.IsNotificationCenterOpen);
            Assert.False(actionRan);

            // Emoji & Symbols menu item exists in Edit menu and executes cleanly
            Assert.True(w.MenuCommands.ContainsKey("edit.emojiSymbols"));
            Assert.True(w.InvokeMenuCommand("edit.emojiSymbols"));
        }
    }
}
