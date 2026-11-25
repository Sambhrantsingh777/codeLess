using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    [Collection("SequentialWorkbench")]
    public class FreshEyesRegressionHuntW11Tests
    {
        private static string CreateTempWorkspace(out string fileA, out string fileB)
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w11_hunt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            fileA = Path.Combine(ws, "Service.cs");
            fileB = Path.Combine(ws, "Consumer.cs");
            File.WriteAllText(fileA, "using System;\npublic class Service {\n    public int ComputeValue(int x) {\n        return x * 2;\n    }\n}\n");
            File.WriteAllText(fileB, "using System;\npublic class Consumer {\n    internal void Run() {\n        var s = new Service();\n        int v = s.ComputeValue(21);\n    }\n}\n");
            return ws;
        }

        [AvaloniaFact]
        public void Flow01_OpenFolder_LoadsExplorerWorkspaceSettingsAndClosesFolderCleanly()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "settings.json"), "{ \"editor.tabSize\": 2 }");
            var window = new MainWindow(null);
            try
            {
                window.Show();
                window.LoadWorkspace(ws);
                Assert.Equal(ws, window.WorkspacePath);
                Assert.True(window.ExplorerTree.VisibleFlatRows.Count > 0);
                var tab = window.OpenFileInTab(fileA, preview: false);
                Assert.NotNull(tab);

                window.CloseWorkspaceFolder();
                Assert.Equal("", window.WorkspacePath);
                Assert.Empty(window.OpenTabs);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow02_QuickOpen_FuzzyFileSearchLineJumpAndOpenToSide()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                window.OpenQuickOpen();
                Assert.True(window.CommandPalette.IsVisible);
                window.CommandPalette.SearchInput.Text = "Service.cs:3:5";
                window.CommandPalette.RefreshFilter();
                Assert.NotEmpty(window.CommandPalette.FilteredEntries);

                // Enter jumps to Service.cs line 3, col 5
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = KeyModifiers.None
                });
                Assert.Equal(fileA, window.ActiveTab?.FilePath);
                Assert.Equal(2, window.ActiveTab?.CursorLine);
                Assert.Equal(4, window.ActiveTab?.CursorCol);

                // Cmd/Ctrl+Enter opens Consumer.cs to the side
                window.OpenQuickOpen();
                window.CommandPalette.SearchInput.Text = "Consumer.cs";
                window.CommandPalette.RefreshFilter();
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = KeyModifiers.Control
                });
                Assert.True(window.SplitEditor.IsVisible);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow03_MultiCursor_AddNextOccurrenceSkipUndoAndMultiCaretEdit()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            File.WriteAllText(fileA, "alpha = 1;\nalpha = 2;\nalpha = 3;\n");
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                tab.CursorLine = 0;
                tab.CursorCol = 2;
                tab.ClearSelection();

                // First Ctrl+D selects "alpha"; second & third add occurrences on lines 1 and 2
                window.PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord();
                Assert.Equal("alpha", tab.GetSelectedText());
                window.PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord();
                window.PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord();
                Assert.Equal(2, window.PrimaryEditor.Canvas.ExtraCursors.Count);

                // Cursor Undo removes last added caret
                window.PrimaryEditor.Canvas.UndoLastCursor();
                Assert.Single(window.PrimaryEditor.Canvas.ExtraCursors);

                // Re-add third caret and type replacement across all 3 carets
                window.PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord();
                window.PrimaryEditor.Canvas.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = "beta"
                });
                Assert.Equal("beta = 1;", tab.Lines[0]);
                Assert.Equal("beta = 2;", tab.Lines[1]);
                Assert.Equal("beta = 3;", tab.Lines[2]);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow04_FindAndReplaceInFiles_SearchDismissSingleReplaceAndReplaceAll()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                window.ShowSearchView(true);
                window.ExecuteWorkspaceSearch("ComputeValue");
                Assert.True(window.WorkspaceSearchRows.Count >= 3);

                // Single-match replace in first match row
                var firstMatchRow = window.WorkspaceSearchRows.First(r => !r.IsFileHeader);
                Assert.True(window.ReplaceSingleSearchMatch(firstMatchRow, "CalculateTotal"));

                // Replace remaining occurrences across workspace
                int replaced = window.ExecuteWorkspaceReplaceAll("ComputeValue", "CalculateTotal");
                Assert.True(replaced >= 1);
                Assert.Contains("CalculateTotal", File.ReadAllText(fileA));
                Assert.Contains("CalculateTotal", File.ReadAllText(fileB));
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow05_SplitEditors_SplitRightSyncAndClosingLastTabAutoClosesSplit()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                window.ToggleSplitEditor();
                Assert.True(window.IsEditorSplit);
                Assert.Same(tab, window.SplitEditor.CurrentBuffer);

                // Closing the only open tab automatically closes the empty split editor pane (HUNT-01 fix)
                window.CloseTab(tab, forceWithoutPrompt: true);
                Assert.Empty(window.OpenTabs);
                Assert.False(window.IsEditorSplit);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow06_Terminal_CreateSplitRenameSwitchAndKillSessions()
        {
            string ws = CreateTempWorkspace(out _, out _);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                int initialCount = window.TerminalSessions.Count;
                var s1 = window.CreateNewTerminalSession();
                var s2 = window.CreateNewTerminalSession();
                Assert.Equal(initialCount + 2, window.TerminalSessions.Count);

                window.RenameTerminalSession(s2, "BuildShell");
                Assert.Equal("BuildShell", s2.Title);

                window.ToggleSplitTerminal();
                Assert.True(window.IsTerminalSplit);
                window.ToggleSplitTerminal();
                Assert.False(window.IsTerminalSplit);

                window.KillTerminalSession(s2);
                Assert.Equal(initialCount + 1, window.TerminalSessions.Count);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow07_ScmStageCommit_StageUnstageCommitAndAmendInGitRepo()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            WorkspaceTrust.Trust(ws);
            LlmHarnessAndAgentEngine.RunProcessCapture("git", "init", ws);
            LlmHarnessAndAgentEngine.RunProcessCapture("git", "config user.email \"qa@codeless.test\"", ws);
            LlmHarnessAndAgentEngine.RunProcessCapture("git", "config user.name \"QA\"", ws);
            LlmHarnessAndAgentEngine.RunProcessCapture("git", "add -A", ws);
            LlmHarnessAndAgentEngine.RunProcessCapture("git", "commit -m \"Initial commit\"", ws);

            var window = new MainWindow(ws);
            try
            {
                window.Show();
                File.AppendAllText(fileA, "// modified\n");
                window.RefreshGitStatus();
                var entry = window.GitEntries.FirstOrDefault(e => Path.GetFileName(e.FullPath) == "Service.cs");
                Assert.NotNull(entry);

                window.ExecuteGitStageFile(entry);
                Assert.Contains(window.GitEntries, e => Path.GetFileName(e.FullPath) == "Service.cs" && e.IsStaged);

                window.ExecuteGitUnstageFile(entry);
                Assert.Contains(window.GitEntries, e => Path.GetFileName(e.FullPath) == "Service.cs" && !e.IsStaged);

                window.ExecuteGitStageFile(entry);
                Assert.True(window.ExecuteGitCommit("Second commit"));
                window.RefreshGitStatus();
                Assert.DoesNotContain(window.GitEntries, e => Path.GetFileName(e.FullPath) == "Service.cs");
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow08_ProblemsPanel_FilterIncludesTaskProblemsAndF8JumpsToLineAndColumn()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                tab.Diagnostics.Clear();
                tab.Diagnostics.Add(new DiagnosticItem { Severity = "Error", Line = 4, Column = 9, Message = "CS1002 ; expected", FilePath = fileA });
                TaskProblemStore.Set("build", new List<DiagnosticItem>
                {
                    new DiagnosticItem { Severity = "Warning", Line = 3, Column = 5, Message = "Unused variable", FilePath = fileB, RelativePath = "Consumer.cs", Source = "csc" }
                });

                window.RefreshOutlineAndProblems(tab);
                // Filter to "CS1002" excludes the task problem from Consumer.cs (HUNT-02 fix)
                window.SetProblemsFilter("CS1002");
                var listItems = ((IEnumerable<Control>)window.ProblemsListItemsForTest()).ToList();
                Assert.Single(listItems);

                window.SetProblemsFilter("");
                tab.CursorLine = 0;
                tab.CursorCol = 0;
                window.JumpDiagnostic(1);
                Assert.Equal(3, tab.CursorLine);
                Assert.Equal(8, tab.CursorCol);
            }
            finally
            {
                TaskProblemStore.ClearAll();
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow09_GoToDefinition_JumpsAcrossWorkspaceFilesAndNavigatesBackAndForward()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tabB = window.OpenFileInTab(fileB, preview: false);
                // Place cursor on "ComputeValue" in Consumer.cs (line 4, col 18)
                tabB.CursorLine = 4;
                tabB.CursorCol = 18;

                window.GoToDefinitionAtCursor();
                Assert.Equal(fileA, window.ActiveTab?.FilePath);
                Assert.Equal(2, window.ActiveTab?.CursorLine);

                window.NavigateBack();
                Assert.Equal(fileB, window.ActiveTab?.FilePath);

                window.NavigateForward();
                Assert.Equal(fileA, window.ActiveTab?.FilePath);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow10_RenameSymbol_RenamesAcrossOpenBuffersWithSingleStepUndo()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tabA = window.OpenFileInTab(fileA, preview: false);
                var tabB = window.OpenFileInTab(fileB, preview: false);
                window.ActivateTab(tabA);
                tabA.CursorLine = 2;
                tabA.CursorCol = 18; // on "ComputeValue"

                int count = window.RenameSymbolFallback(tabA, "EvaluateMetric");
                Assert.Equal(2, count);
                Assert.Contains("EvaluateMetric", tabA.Content);
                Assert.Contains("EvaluateMetric", tabB.Content);

                // Single Undo on each buffer restores original symbol name
                Assert.True(tabA.Undo());
                Assert.True(tabB.Undo());
                Assert.Contains("ComputeValue", tabA.Content);
                Assert.Contains("ComputeValue", tabB.Content);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow11_SettingsJson_WorkspaceSettingsPropagateSaveOptionsToWindowSettings()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "settings.json"),
                "{\n  \"editor.formatOnSave\": true,\n  \"files.trimTrailingWhitespace\": true,\n  \"files.insertFinalNewline\": true\n}");

            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                // Verify HUNT-04 fix: workspace settings copied FormatOnSave & TrimTrailingWhitespaceOnSave onto window settings
                tab.Lines[0] = "using System;   ";
                tab.SyncContentFromLines();
                tab.IsDirty = true;
                Assert.True(window.SaveActiveTab());
                Assert.Equal("using System;", tab.Lines[0]);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow12_ZenMode_PreservesPriorPanelVisibilityAndExitsOnDoubleEscape()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                window.OpenFileInTab(fileA, preview: false);
                // User hides bottom panel before entering Zen Mode
                if (window.IsBottomPanelVisible) window.ToggleBottomPanel();
                Assert.True(window.IsSidebarVisible);
                Assert.False(window.IsBottomPanelVisible);

                window.ToggleZenMode();
                Assert.True(window.IsZenMode);
                Assert.False(window.IsSidebarVisible);
                Assert.False(window.IsBottomPanelVisible);

                // Double-Escape exits Zen Mode and restores sidebar=true, bottomPanel=false (HUNT-05 fix)
                window.SimulateShortcut(Key.Escape, KeyModifiers.None);
                Assert.True(window.IsZenMode);
                window.SimulateShortcut(Key.Escape, KeyModifiers.None);
                Assert.False(window.IsZenMode);
                Assert.True(window.IsSidebarVisible);
                Assert.False(window.IsBottomPanelVisible);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow13_NewWindow_CreatesIsolatedWindowInstanceAndDisposesCleanly()
        {
            string ws = CreateTempWorkspace(out _, out _);
            var w1 = new MainWindow(ws);
            MainWindow w2 = null;
            try
            {
                w1.Show();
                w2 = w1.CreateNewWindowInstance();
                Assert.NotNull(w2);
                Assert.NotSame(w1, w2);
                Assert.NotSame(w1.HarnessEngine, w2.HarnessEngine);

                w2.SuppressUnsavedPrompts = true;
                w2.Close();
                Assert.True(w2.TestCheckDisposed());
                Assert.False(w1.TestCheckDisposed());
            }
            finally
            {
                w1.SuppressUnsavedPrompts = true;
                w1.Close();
                if (w2 != null) { w2.SuppressUnsavedPrompts = true; try { w2.Close(); } catch { } }
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow14_CloseAndReopenTab_RestoresCursorAndScrollPositionAndWorksAfterCloseAll()
        {
            string ws = CreateTempWorkspace(out string fileA, out string fileB);
            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tabA = window.OpenFileInTab(fileA, preview: false);
                tabA.CursorLine = 3;
                tabA.CursorCol = 8;
                tabA.ScrollTopLine = 2;

                window.CloseTab(tabA, forceWithoutPrompt: true);
                Assert.Empty(window.OpenTabs);

                // Reopen Closed Tab restores exact CursorLine, CursorCol, and ScrollTopLine (HUNT-06 fix)
                window.ReopenClosedTab();
                Assert.Equal(fileA, window.ActiveTab?.FilePath);
                Assert.Equal(3, window.ActiveTab?.CursorLine);
                Assert.Equal(8, window.ActiveTab?.CursorCol);
                Assert.Equal(2, window.ActiveTab?.ScrollTopLine);

                // CloseAllTabs also records closedTabHistory so ReopenClosedTab works after Close All
                window.OpenFileInTab(fileB, preview: false);
                window.CloseAllTabs();
                Assert.Empty(window.OpenTabs);
                window.ReopenClosedTab();
                Assert.Single(window.OpenTabs);
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Flow15_FormatOnSave_AppliesBuiltInFallbackFormattingAndSaveAllHonoursFormatOnSave()
        {
            string ws = CreateTempWorkspace(out string fileA, out _);
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "settings.json"),
                "{\n  \"editor.formatOnSave\": true,\n  \"editor.tabSize\": 4,\n  \"editor.insertSpaces\": true\n}");

            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(fileA, preview: false);
                // Mix tabs and trailing spaces; FormatOnSave normalizes indentation to 4 spaces and trims trailing whitespace (HUNT-07 fix)
                tab.Lines[2] = "\tpublic int ComputeValue(int x) {   ";
                tab.SyncContentFromLines();
                tab.IsDirty = true;

                window.SaveAllTabs();
                Assert.False(tab.IsDirty);
                Assert.Equal("    public int ComputeValue(int x) {", tab.Lines[2]);
                Assert.Contains("    public int ComputeValue(int x) {", File.ReadAllText(fileA));
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }
}
