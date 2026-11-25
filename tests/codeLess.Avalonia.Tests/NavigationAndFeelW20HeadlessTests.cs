using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// W20 Area D (Navigation &amp; Feel) headless verification suite covering D01..D35
    /// against VS Code reference behaviour via real key, mouse, and command dispatch.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class NavigationAndFeelW20HeadlessTests
    {
        private static string CreateWorkspace(out string fileA, out string fileB, out string nestedFile)
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w20_area_d_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            string subDir = Path.Combine(ws, "sub");
            Directory.CreateDirectory(subDir);

            fileA = Path.Combine(ws, "AlphaService.cs");
            File.WriteAllText(fileA, string.Join("\n", new[]
            {
                "using System;",
                "namespace Demo",
                "{",
                "    public class AlphaService",
                "    {",
                "        private int counter;",
                "        public int ComputeAlpha()",
                "        {",
                "            return ++counter;",
                "        }",
                "        public int GetCounter()",
                "        {",
                "            return counter;",
                "        }",
                "    }",
                "}"
            }));

            fileB = Path.Combine(ws, "BetaHelper.cs");
            File.WriteAllText(fileB, string.Join("\n", new[]
            {
                "using System;",
                "namespace Demo",
                "{",
                "    public class BetaHelper",
                "    {",
                "        public static string FormatBeta(string input)",
                "        {",
                "            return input ?? string.Empty;",
                "        }",
                "    }",
                "}"
            }));

            nestedFile = Path.Combine(subDir, "NestedWorker.cs");
            File.WriteAllText(nestedFile, string.Join("\n", new[]
            {
                "namespace Demo.Sub",
                "{",
                "    public class NestedWorker",
                "    {",
                "        public int RunWork() { return 1; }",
                "    }",
                "}"
            }));

            return ws;
        }

        [AvaloniaFact]
        public void D01_to_D05_CommandPalette_Shortcuts_FuzzySubsequence_RecentMru_And_PrefixTransitions()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                window.OpenFileInTab(fileA);

                // D01: Cmd/Ctrl+Shift+P opens Command Palette with '>' prefilled; Escape closes it
                window.SimulateShortcut(Key.P, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(">", window.CommandPalette.SearchInput.Text);

                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape
                });
                Assert.False(window.CommandPalette.IsVisible);

                // F1 also opens Command Palette with '>'
                window.SimulateShortcut(Key.F1, KeyModifiers.None);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(">", window.CommandPalette.SearchInput.Text);

                // D02: Fuzzy subsequence & word-boundary matching in '>' mode (e.g. '>tlc' -> Toggle Line Comment, '>gtd' -> Go to Definition)
                window.CommandPalette.SearchInput.Text = ">tlc";
                Assert.Contains(window.CommandPalette.FilteredEntries, e => e.Title == "Toggle Line Comment");

                window.CommandPalette.SearchInput.Text = ">gtd";
                Assert.Contains(window.CommandPalette.FilteredEntries, e => e.Title == "Go to Definition");

                // D03: Recently used commands appear at top of '>' when query is empty and get MRU boost
                window.CommandPalette.RecentCommandTitles.Clear();
                window.CommandPalette.SearchInput.Text = ">Toggle Word Wrap";
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                window.CommandPalette.ExecuteSelected();
                Assert.False(window.CommandPalette.IsVisible);
                Assert.Contains("Toggle Word Wrap", window.CommandPalette.RecentCommandTitles);

                window.OpenCommandPalette(">");
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                Assert.Equal("Toggle Word Wrap", window.CommandPalette.FilteredEntries[0].Title);
                Assert.True(window.CommandPalette.FilteredEntries[0].IsRecent);

                // D04: Deleting '>' in Command Palette switches dynamically to Quick Open file mode
                window.CommandPalette.SearchInput.Text = "";
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                Assert.Contains(window.CommandPalette.FilteredEntries, e => !string.IsNullOrEmpty(e.FilePath) && e.Title == "AlphaService.cs");

                // D05: Typing '?' shows Quick Open Help rows and selecting '@' switches mode
                window.CommandPalette.SearchInput.Text = "?";
                Assert.Equal(5, window.CommandPalette.FilteredEntries.Count);
                int symRowIdx = window.CommandPalette.FilteredEntries.FindIndex(e => e.Category == "@");
                Assert.True(symRowIdx >= 0);
                window.CommandPalette.ResultsListBox.SelectedIndex = symRowIdx;
                window.CommandPalette.ExecuteSelected();
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("@", window.CommandPalette.SearchInput.Text);
                window.CommandPalette.Close();
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D06_to_D10_QuickOpen_MruEmptyQuery_FuzzyPathRanking_LineColSuffix_OpenToSide_And_Cycle()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                // Open in order: fileA, fileB, nestedFile, then activate fileB so MRU is: fileB, nestedFile, fileA
                window.OpenFileInTab(fileA, preview: false);
                window.OpenFileInTab(fileB, preview: false);
                window.OpenFileInTab(nestedFile, preview: false);
                window.OpenFileInTab(fileB, preview: false);

                // D06: Cmd/Ctrl+P with empty query lists open editors in MRU order
                window.OpenQuickOpen();
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("", window.CommandPalette.SearchInput.Text);
                Assert.True(window.CommandPalette.FilteredEntries.Count >= 3);
                Assert.Equal("BetaHelper.cs", window.CommandPalette.FilteredEntries[0].Title);
                Assert.Equal("NestedWorker.cs", window.CommandPalette.FilteredEntries[1].Title);
                Assert.Equal("AlphaService.cs", window.CommandPalette.FilteredEntries[2].Title);

                // D10: Tab / Shift+Tab and Ctrl+P cycle selection in Quick Open
                Assert.Equal(0, window.CommandPalette.ResultsListBox.SelectedIndex);
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Tab
                });
                Assert.Equal(1, window.CommandPalette.ResultsListBox.SelectedIndex);
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Tab,
                    KeyModifiers = KeyModifiers.Shift
                });
                Assert.Equal(0, window.CommandPalette.ResultsListBox.SelectedIndex);
                window.OpenQuickOpen(); // repeated Ctrl+P while open cycles selection
                Assert.Equal(1, window.CommandPalette.ResultsListBox.SelectedIndex);

                // D07: Fuzzy file matching by filename and path segment ('sub/nested')
                window.CommandPalette.SearchInput.Text = "sub/nested";
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                Assert.Equal("NestedWorker.cs", window.CommandPalette.FilteredEntries[0].Title);

                // D08: 'AlphaService.cs:7:9' opens file at 1-based line 7, col 9
                window.CommandPalette.SearchInput.Text = "AlphaService.cs:7:9";
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                Assert.Equal("AlphaService.cs", window.CommandPalette.FilteredEntries[0].Title);
                window.CommandPalette.ExecuteSelected(openToSide: false);
                Assert.False(window.CommandPalette.IsVisible);
                Assert.Equal(fileA, window.ActiveTab.FilePath);
                Assert.Equal(6, window.ActiveTab.CursorLine);
                Assert.Equal(8, window.ActiveTab.CursorCol);

                // D09: Cmd/Ctrl+Enter in Quick Open opens selected file to the side (Split Editor)
                Assert.False(window.IsEditorSplit);
                window.OpenQuickOpen();
                window.CommandPalette.SearchInput.Text = "BetaHelper.cs";
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = KeyModifiers.Control
                });
                Assert.True(window.IsEditorSplit);
                Assert.Equal(fileB, window.SplitEditor.CurrentBuffer.FilePath);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D11_to_D15_Symbols_And_GoToLineCol_Preview_CategorySort_EscapeRestore_And_History()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                tab.CursorLine = 1;
                tab.CursorCol = 4;

                // Ensure symbols are populated on activeTab
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                Assert.NotNull(tab.Symbols);
                Assert.NotEmpty(tab.Symbols);

                // D11: '@' Go to Symbol in Editor live-previews on Down arrow and restores original line/col on Escape
                window.OpenCommandPalette("@");
                Assert.True(window.CommandPalette.IsVisible);
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                if (window.CommandPalette.FilteredEntries.Count > 1)
                {
                    window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Down
                    });
                    int previewedLine = window.CommandPalette.FilteredEntries[window.CommandPalette.ResultsListBox.SelectedIndex].PreviewLine;
                    Assert.Equal(previewedLine - 1, tab.CursorLine);
                }

                // Escape restores original (line 1, col 4)
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Escape
                });
                Assert.False(window.CommandPalette.IsVisible);
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(4, tab.CursorCol);

                // D12: '@:' groups/sorts symbols by category (Kind) and Enter commits jump + records Back history
                window.OpenCommandPalette("@:");
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);
                var categories = window.CommandPalette.FilteredEntries.Select(e => e.Category ?? "").ToList();
                var sortedCategories = categories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                Assert.Equal(sortedCategories, categories);

                var computeSymIdx = window.CommandPalette.FilteredEntries.FindIndex(e => e.Title.Contains("ComputeAlpha"));
                if (computeSymIdx >= 0)
                    window.CommandPalette.ResultsListBox.SelectedIndex = computeSymIdx;
                int expectedSymLine = window.CommandPalette.FilteredEntries[window.CommandPalette.ResultsListBox.SelectedIndex].PreviewLine;
                window.CommandPalette.ExecuteSelected();
                Assert.Equal(expectedSymLine - 1, tab.CursorLine);

                // NavigateBack returns to pre-preview position (line 1, col 4)!
                window.NavigateBack();
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(4, tab.CursorCol);

                // D14: ':line:col' in palette shows prompt entry, jumps to 1-based line & col, and records nav history
                window.OpenCommandPalette(":");
                window.CommandPalette.SearchInput.Text = ":11:15";
                Assert.Single(window.CommandPalette.FilteredEntries);
                Assert.Contains("line 11", window.CommandPalette.FilteredEntries[0].Title);
                Assert.Contains("column 15", window.CommandPalette.FilteredEntries[0].Title);
                window.CommandPalette.ExecuteSelected();
                Assert.Equal(10, tab.CursorLine);
                Assert.Equal(14, tab.CursorCol);

                window.NavigateBack();
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(4, tab.CursorCol);

                // D13 & D15: Switching prefixes inside a single open overlay ('' -> '@' -> '#' -> ':8:5' -> '>')
                window.OpenQuickOpen();
                Assert.Contains(window.CommandPalette.FilteredEntries, e => !string.IsNullOrEmpty(e.FilePath));
                window.CommandPalette.SearchInput.Text = "@Compute";
                Assert.Contains(window.CommandPalette.FilteredEntries, e => e.Title.Contains("ComputeAlpha"));
                window.CommandPalette.SearchInput.Text = "#";
                Assert.Contains("Workspace", window.CommandPalette.CommandPaletteModeHintOrText());
                window.CommandPalette.SearchInput.Text = ">Toggle Line Comment";
                Assert.Contains(window.CommandPalette.FilteredEntries, e => e.Title == "Toggle Line Comment");
                window.CommandPalette.Close();
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D16_to_D19_Breadcrumbs_And_Outline_Sync_SymbolPicker_And_NavigationHistory()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);

                // D16: Moving caret updates BreadcrumbText with file and active symbol
                tab.CursorLine = 8; // inside ComputeAlpha
                tab.CursorCol = 12;
                window.PrimaryEditor.RefreshBreadcrumbs();
                Assert.Contains("AlphaService.cs", window.PrimaryEditor.BreadcrumbText);
                Assert.Contains("ComputeAlpha", window.PrimaryEditor.BreadcrumbText);

                // D18: Jump via JumpToLineAndColumnWithHistory (used by Breadcrumb symbol menu & Outline click) records Back/Forward history
                tab.CursorLine = 1;
                tab.CursorCol = 2;
                window.JumpToLineAndColumnWithHistory(11, 9);
                Assert.Equal(10, tab.CursorLine);
                Assert.Equal(8, tab.CursorCol);

                window.NavigateBack();
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(2, tab.CursorCol);

                window.NavigateForward();
                Assert.Equal(10, tab.CursorLine);
                Assert.Equal(8, tab.CursorCol);

                // D19: Modifying buffer re-analyzes symbols and updates Breadcrumbs
                tab.Lines.Insert(14, "        public int NewlyAddedMethod() { return 0; }");
                tab.SyncContentFromLines();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                Assert.Contains(tab.Symbols, s => s.Name.Contains("NewlyAddedMethod"));
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D20_to_D23_NavigationHistory_BackForward_ExactLineAndColumn_And_LastEditLocation()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();

                // D20: Cross-file and intra-file jumps preserve both 1-based Line and Column
                window.OpenFileAtLocation(fileA, 4, 11);
                Assert.Equal(fileA, window.ActiveTab.FilePath);
                Assert.Equal(3, window.ActiveTab.CursorLine);
                Assert.Equal(10, window.ActiveTab.CursorCol);

                window.OpenFileAtLocation(fileB, 6, 15);
                Assert.Equal(fileB, window.ActiveTab.FilePath);
                Assert.Equal(5, window.ActiveTab.CursorLine);
                Assert.Equal(14, window.ActiveTab.CursorCol);

                window.NavigateBack();
                Assert.Equal(fileA, window.ActiveTab.FilePath);
                Assert.Equal(3, window.ActiveTab.CursorLine);
                Assert.Equal(10, window.ActiveTab.CursorCol);

                window.NavigateForward();
                Assert.Equal(fileB, window.ActiveTab.FilePath);
                Assert.Equal(5, window.ActiveTab.CursorLine);
                Assert.Equal(14, window.ActiveTab.CursorCol);

                // D21: Jumping to nestedFile after NavigateBack truncates the forward branch (fileB)
                window.NavigateBack();
                Assert.Equal(fileA, window.ActiveTab.FilePath);
                window.OpenFileAtLocation(nestedFile, 3, 5);
                Assert.Equal(nestedFile, window.ActiveTab.FilePath);
                window.NavigateForward(); // should be a no-op since forward history was truncated
                Assert.Equal(nestedFile, window.ActiveTab.FilePath);

                // D22: Go to Last Edit Location (Ctrl+K Ctrl+Q / workbench.action.navigateToLastEditLocation)
                window.OpenFileAtLocation(fileA, 9, 13);
                window.PrimaryEditor.Canvas.Focus();
                window.PrimaryEditor.Canvas.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = "x"
                });
                int editedLine1 = window.ActiveTab.CursorLine + 1;
                int editedCol1 = window.ActiveTab.CursorCol + 1;

                // Navigate away to fileB
                window.OpenFileAtLocation(fileB, 2, 1);
                Assert.Equal(fileB, window.ActiveTab.FilePath);

                // Dispatch Ctrl+K Ctrl+Q chord to jump back to last edit location in fileA!
                window.SimulateShortcut(Key.K, KeyModifiers.Control);
                window.SimulateShortcut(Key.Q, KeyModifiers.Control);
                Assert.Equal(fileA, window.ActiveTab.FilePath);
                Assert.Equal(editedLine1 - 1, window.ActiveTab.CursorLine);
                Assert.Equal(editedCol1 - 1, window.ActiveTab.CursorCol);

                // D23: MRU Editor Switcher (EditorMruOrder tracks most recently activated tabs)
                Assert.Equal(fileA, window.EditorMruOrder[0].FilePath);
                Assert.Equal(fileB, window.EditorMruOrder[1].FilePath);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D24_to_D27_EscapeChain_And_PanelCloseFocusRestoration()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                if (!window.IsBottomPanelVisible) window.ToggleBottomPanel();
                Assert.True(window.IsBottomPanelVisible);

                // D24: Editor Escape priority cascade:
                // 1. Suggest/hover/signature popup -> 2. Find bar -> 3. Secondary cursors -> 4. Selection -> 5. Stops (bottom panel stays visible!)
                window.PrimaryLspBinding.Completion.IsVisible = true;
                window.PrimaryEditor.ShowFindBar(false);
                window.PrimaryEditor.Canvas.AddCursorVertical(1);
                tab.SelectionStartLine = 0;
                tab.SelectionStartCol = 0;
                tab.CursorLine = 0;
                tab.CursorCol = 5;
                window.UpdateStatusBar();

                // Press 1: closes completion popup only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(window.PrimaryLspBinding.IsCompletionVisible);
                Assert.True(window.PrimaryEditor.IsFindBarVisible);
                Assert.NotEmpty(window.PrimaryEditor.Canvas.ExtraCursors);
                Assert.True(tab.HasSelection());

                // Press 2: closes Find bar only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(window.PrimaryEditor.IsFindBarVisible);
                Assert.NotEmpty(window.PrimaryEditor.Canvas.ExtraCursors);
                Assert.True(tab.HasSelection());

                // Press 3: clears secondary cursors AND updates Status Bar immediately
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.Empty(window.PrimaryEditor.Canvas.ExtraCursors);
                Assert.True(tab.HasSelection());
                Assert.DoesNotContain("cursors", window.StatusBarLineColText);
                Assert.Contains("selected", window.StatusBarLineColText);

                // Press 4: clears selection AND updates Status Bar immediately
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(tab.HasSelection());
                Assert.DoesNotContain("selected", window.StatusBarLineColText);
                Assert.True(window.IsBottomPanelVisible);

                // Press 5: does NOT close the bottom panel (VS Code editor Escape chain stops after clearing selection)
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.True(window.IsBottomPanelVisible);

                // D25, D26, D27: Closing Quick Open, Find bar, Sidebar, Bottom Panel, or Right Chat Dock restores visibility/focus cleanly
                window.TogglePrimarySidebar();
                Assert.False(window.IsSidebarVisible);
                window.ToggleBottomPanel();
                Assert.False(window.IsBottomPanelVisible);
                if (window.IsRightChatVisible) window.ToggleRightChatDock();
                Assert.False(window.IsRightChatVisible);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void D28_to_D35_StatusBar_VisualColOnTabs_MultiCursorSelectionCount_And_Items()
        {
            string ws = CreateWorkspace(out string fileA, out string fileB, out string nestedFile);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                tab.CustomTabSize = 4;
                tab.Lines[0] = "\t\tabcdef";

                // D28: Caret after 2 tabs (CursorCol == 2) with TabSize == 4 is at 1-based visual column 9 ("Ln 1, Col 9")
                tab.ClearSelection();
                tab.CursorLine = 0;
                tab.CursorCol = 2;
                window.PrimaryEditor.Canvas.ExtraCursors.Clear();
                window.UpdateStatusBar();
                Assert.Equal("Ln 1, Col 9", window.StatusBarLineColText);

                // D29: Multi-cursor with selections on both primary and secondary cursors sums total selected characters
                tab.SelectionStartLine = 0;
                tab.SelectionStartCol = 2;
                tab.CursorLine = 0;
                tab.CursorCol = 5; // 3 chars selected on primary
                window.PrimaryEditor.Canvas.AddCursorVertical(1);
                var ec0 = window.PrimaryEditor.Canvas.ExtraCursors[0];
                ec0.AnchorLine = 1;
                ec0.AnchorCol = 0;
                ec0.Line = 1;
                ec0.Col = 4; // 4 chars selected on secondary
                window.PrimaryEditor.Canvas.ExtraCursors[0] = ec0;
                window.UpdateStatusBar();
                Assert.Equal("Ln 1, Col 12 (7 selected) (2 cursors)", window.StatusBarLineColText);

                // D30..D35: Indentation, Encoding, EOL, Language Mode
                Assert.Contains("4", window.StatusIndentationText);
                Assert.Equal("UTF-8", tab.StatusEncodingDisplayName);
                Assert.Equal("C#", MainWindow.MapLanguageDisplayName(tab.Language));
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }

    internal static class PaletteTestExtensions
    {
        public static string CommandPaletteModeHintOrText(this AvaloniaCommandPaletteOverlay overlay)
        {
            return overlay?.ModeHintLabel?.Text ?? "";
        }
    }
}
