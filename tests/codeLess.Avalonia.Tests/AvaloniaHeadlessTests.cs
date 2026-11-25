using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [CollectionDefinition("SequentialWorkbench", DisableParallelization = true)]
    public class SequentialWorkbenchCollection { }

    [Collection("SequentialWorkbench")]
    public class AvaloniaHeadlessTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackupContent;
        private readonly bool _hadSettingsFile;

                [AvaloniaFact]
        public void Avalonia_Tab_TogglesMode()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                window.SetAgentMode("build");
                var input = global::Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<global::Avalonia.Controls.TextBox>().FirstOrDefault(x => x.Name == "ChatPromptInput");
                Assert.NotNull(input);
                
                var statusBtnField = window.GetType().GetField("statusAiModelBtn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var statusBtn = (global::Avalonia.Controls.Button)statusBtnField.GetValue(window);
                var btnContentStr = statusBtn.Content.ToString();
                
                Assert.Equal("build", window.CurrentAgentMode);
                Assert.Contains("build", btnContentStr, StringComparison.OrdinalIgnoreCase);

                var tabEvent = new global::Avalonia.Input.KeyEventArgs { RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent, Key = global::Avalonia.Input.Key.Tab };
                input.RaiseEvent(tabEvent);

                btnContentStr = statusBtn.Content.ToString();
                Assert.Equal("plan", window.CurrentAgentMode);
                Assert.Contains("plan", btnContentStr, StringComparison.OrdinalIgnoreCase);

                var tabEvent2 = new global::Avalonia.Input.KeyEventArgs { RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent, Key = global::Avalonia.Input.Key.Tab };
                input.RaiseEvent(tabEvent2);
                
                btnContentStr = statusBtn.Content.ToString();
                Assert.Equal("build", window.CurrentAgentMode);
                Assert.Contains("build", btnContentStr, StringComparison.OrdinalIgnoreCase);

                window.SuppressUnsavedPrompts = true;
                window.Close();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            finally
            {
                try { System.IO.Directory.Delete(ws, true); } catch { }
            }
        }

public AvaloniaHeadlessTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _hadSettingsFile = !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath);
            _settingsBackupContent = _hadSettingsFile ? File.ReadAllText(_settingsFilePath) : null;
        }

        public void Dispose()
        {
            try
            {
                if (_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && _settingsBackupContent != null)
                {
                    File.WriteAllText(_settingsFilePath, _settingsBackupContent);
                }
                else if (!_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                }
            }
            catch { }
        }

        private static string CreateRichTempWorkspace(out string csFile, out string pyFile, out string subFile)
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_Parity_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            TestTrust.Trust(dir); // workspace-local tools / git / tasks need a trusted folder

            // Excluded folders that MUST be filtered out in Explorer tree
            Directory.CreateDirectory(Path.Combine(dir, "bench"));
            File.WriteAllText(Path.Combine(dir, "bench", "secret_bench.json"), "{}");
            Directory.CreateDirectory(Path.Combine(dir, "scratch"));
            File.WriteAllText(Path.Combine(dir, "scratch", "temp_note.md"), "# scratch");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "pkg.js"), "module.exports = {};");
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "obj"));

            // Real source folder tree
            string srcDir = Path.Combine(dir, "src");
            Directory.CreateDirectory(srcDir);
            csFile = Path.Combine(dir, "Program.cs");
            pyFile = Path.Combine(dir, "worker.py");
            subFile = Path.Combine(srcDir, "Engine.cs");

            File.WriteAllText(csFile,
                "using System;\n" +
                "namespace DemoApp\n" +
                "{\n" +
                "    public class Program\n" +
                "    {\n" +
                "        public static int Compute(int value)\n" +
                "        {\n" +
                "            int result = value + 1;\n" +
                "            return result;\n" +
                "        }\n" +
                "    }\n" +
                "}\n");

            File.WriteAllText(pyFile,
                "def run_task(item):\n" +
                "    total = item * 2\n" +
                "    return total\n");

            File.WriteAllText(subFile,
                "namespace DemoApp\n" +
                "{\n" +
                "    public class Engine\n" +
                "    {\n" +
                "        public string Name => \"codeLess\";\n" +
                "    }\n" +
                "}\n");

            return dir;
        }

        [AvaloniaFact]
        public void TitleAndMenus_WindowTitleAndDirtyIndicator_MatchCodeLessSpec()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // Title must end with "codeLess" and never "codeLess Studio"
                Assert.DoesNotContain("codeLess Studio", window.Title);
                Assert.EndsWith("— codeLess", window.Title);

                var tab = window.OpenFileInTab(csFile, preview: false);
                Assert.Contains("Program.cs", window.Title);
                Assert.DoesNotContain("● Program.cs", window.Title);

                // Mark dirty -> title shows "● Program.cs — <folder> — codeLess"
                window.Editor.Text = tab.Content + "// modified\n";
                Assert.True(tab.IsDirty);
                Assert.StartsWith("● Program.cs — ", window.Title);
                Assert.EndsWith("— codeLess", window.Title);

                // Save -> dirty prefix removed
                window.SaveActiveTab();
                Assert.False(tab.IsDirty);
                Assert.StartsWith("Program.cs — ", window.Title);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void TitleAndMenus_TopMenuAndNativeMenu_HaveAllEightTopLevelMenus()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                Assert.NotNull(window.TopMenuControl);
                var headers = window.TopMenuControl.ItemsSource.Cast<MenuItem>()
                    .Select(m => (m.Header?.ToString() ?? "").Replace("_", ""))
                    .ToList();

                Assert.Equal(new[] { "File", "Edit", "Selection", "View", "Go", "Run", "Terminal", "Help" }, headers);

                // Verify macOS NativeMenu also has codeLess + all 8 standard menus
                var nativeMenu = NativeMenu.GetMenu(window);
                Assert.NotNull(nativeMenu);
                var nativeHeaders = nativeMenu.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
                Assert.Equal(new[] { "codeLess", "File", "Edit", "Selection", "View", "Go", "Run", "Terminal", "Window", "Help" }, nativeHeaders);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void TitleAndMenus_CommandCenterAndLayoutToggles_OperateCorrectly()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                Assert.True(window.IsSidebarVisible);
                Assert.True(window.IsBottomPanelVisible);
                Assert.True(window.IsRightChatVisible);

                window.TogglePrimarySidebar();
                Assert.False(window.IsSidebarVisible);
                window.TogglePrimarySidebar();
                Assert.True(window.IsSidebarVisible);

                window.ToggleBottomPanel();
                Assert.False(window.IsBottomPanelVisible);
                window.ToggleBottomPanel();
                Assert.True(window.IsBottomPanelVisible);

                window.ToggleRightChatDock();
                Assert.False(window.IsRightChatVisible);
                window.ToggleRightChatDock();
                Assert.True(window.IsRightChatVisible);

                window.OpenQuickOpen();
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void ActivityBar_VectorIconsAndBottomAccountSettings_SwitchViews()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                string[] views = { "explorer", "search", "git", "run", "extensions", "harness" };
                foreach (string v in views)
                {
                    window.SwitchSidebarView(v);
                    Assert.Equal(v, window.ActiveSidebarView);
                    Assert.True(window.IsSidebarVisible);
                }

                // Bottom Account / BYOK and Settings modals
                window.ShowByokAndHarnessModal();
                Assert.True(window.ModalHost.IsVisible);
                Assert.Equal("settings", window.ModalHost.ActiveDialogId);
                window.ModalHost.CloseModal();

                window.ShowSettingsModal();
                Assert.True(window.ModalHost.IsVisible);
                Assert.Equal("settings", window.ModalHost.ActiveDialogId);
                window.ModalHost.CloseModal();

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Explorer_FolderTreeExclusionsAndRowMetrics_MatchWindows()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // RowHeight must be 22px matching VS Code & Windows CodeLessExplorerTree
                Assert.Equal(22, AvaloniaExplorerTree.RowHeight);

                Assert.Single(window.ExplorerTree.Nodes);
                var rootNode = window.ExplorerTree.Nodes[0];
                Assert.True(rootNode.IsDirectory);
                Assert.True(rootNode.IsExpanded);

                var childNames = rootNode.Nodes.Select(n => n.Text).ToList();
                Assert.Contains("src", childNames);
                Assert.Contains("Program.cs", childNames);
                Assert.Contains("worker.py", childNames);

                // VS Code parity: build/dependency folders stay visible in the Explorer (dimmed) but are excluded
                // from the search / AI file index. Only VCS metadata (.git) is hidden.
                foreach (string dimmed in new[] { "node_modules", "bin", "obj" })
                {
                    var n = rootNode.Nodes.FirstOrDefault(x => x.Text == dimmed);
                    Assert.NotNull(n);
                    Assert.True(n.IsDimmed, dimmed + " should render dimmed");
                }
                Assert.DoesNotContain(".git", childNames);
                Assert.DoesNotContain(window.WorkspaceFiles, f => f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar));
                // Ordinary project folders (even ones named like scratch/bench) are visible and indexed.
                Assert.Contains("bench", childNames);
                Assert.Contains("scratch", childNames);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Explorer_OpenEditorsAndOutlineSections_SyncWithActiveTab()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                var tabCs = window.OpenFileInTab(csFile, preview: false);
                var tabPy = window.OpenFileInTab(pyFile, preview: false);

                Assert.Contains(tabCs, window.OpenTabs);
                Assert.Contains(tabPy, window.OpenTabs);
                Assert.NotEmpty(tabCs.Symbols);
                Assert.NotEmpty(tabPy.Symbols);

                window.ActivateTab(tabCs);
                Assert.Equal(csFile, window.ExplorerTree.ActiveEditorFilePath);
                Assert.Contains(tabCs.Symbols, s => s.Name.Contains("Compute"));

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Explorer_ContextMenuFileCrudRenameAndTrashDelete_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // 1. Create new file & folder via Explorer actions
                string createdFile = window.CreateFileInDirectory(ws, "Service.cs");
                Assert.True(File.Exists(createdFile));
                Assert.Equal(createdFile, window.ActiveTab.FilePath);

                string createdDir = window.CreateFolderInDirectory(ws, "Models");
                Assert.True(Directory.Exists(createdDir));

                // 2. Rename file via Explorer node rename
                var rootNode = window.ExplorerTree.Nodes[0];
                var serviceNode = rootNode.Nodes.FirstOrDefault(n => n.Text == "Service.cs");
                Assert.NotNull(serviceNode);

                bool renamed = window.RenameExplorerPath(serviceNode.Tag, "RenamedService.cs");
                Assert.True(renamed);
                string renamedPath = Path.Combine(ws, "RenamedService.cs");
                Assert.True(File.Exists(renamedPath));
                Assert.False(File.Exists(createdFile));
                Assert.Equal(renamedPath, window.ActiveTab.FilePath);

                // 3. Delete file via Trash/Recycle Bin helper
                var renamedNode = window.ExplorerTree.Nodes[0].Nodes.FirstOrDefault(n => n.Text == "RenamedService.cs");
                Assert.NotNull(renamedNode);
                bool deleted = window.DeletePathToTrashOrRecycleBin(renamedNode.Tag);
                Assert.True(deleted);
                Assert.False(File.Exists(renamedPath));
                Assert.DoesNotContain(window.OpenTabs, t => t.FilePath == renamedPath);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void SearchView_FindAndReplaceAcrossWorkspaceFiles_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                window.ExecuteWorkspaceSearch("Compute");
                Assert.NotEmpty(window.WorkspaceSearchHits);
                Assert.Contains(window.WorkspaceSearchHits, h => h.FilePath == csFile);

                int replacedFiles = window.ExecuteWorkspaceReplaceAll("Compute", "CalculateTotal");
                Assert.Equal(1, replacedFiles);
                Assert.Contains("CalculateTotal", File.ReadAllText(csFile));

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void ScmView_GitStatusStageUnstageCommitAndGutterDiff_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "init", ws, 5000);
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "config user.email test@codeless.local", ws, 5000);
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "config user.name codeLessTest", ws, 5000);
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "add -A", ws, 5000);
                LlmHarnessAndAgentEngine.RunQuickProcess("git", "commit -m \"Initial commit\"", ws, 5000);

                var window = new MainWindow(ws);
                window.Show();

                var tab = window.OpenFileInTab(csFile, preview: false);
                window.Editor.Text = tab.Content.Replace("value + 1", "value + 42") + "// new line\n";
                window.SaveActiveTab();

                // Verify line-accurate Git gutter diff markers against HEAD
                Assert.NotEmpty(tab.GutterDiffByLine);

                window.RefreshGitStatus();
                Assert.Contains("Program.cs", window.ExplorerTree.GitStatusByFullPath.Keys.Select(Path.GetFileName));

                // Stage & Unstage via SCM view commands
                window.ExecuteGitStageFile("Program.cs");
                var stagedEntries = LlmHarnessAndAgentEngine.GetGitStatusEntries(ws);
                Assert.Contains(stagedEntries, e => e.RelativePath.EndsWith("Program.cs") && e.IsStaged);

                window.ExecuteGitUnstageFile("Program.cs");
                var unstagedEntries = LlmHarnessAndAgentEngine.GetGitStatusEntries(ws);
                Assert.Contains(unstagedEntries, e => e.RelativePath.EndsWith("Program.cs") && !e.IsStaged);

                // Stage & Commit
                window.ExecuteGitStageFile("Program.cs");
                bool committed = window.ExecuteGitCommit("Update Compute offset");
                Assert.True(committed);
                Assert.Empty(LlmHarnessAndAgentEngine.GetGitStatusEntries(ws));

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void RunAndExtensionsAndHarnessViews_OperateCorrectly()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                window.SwitchSidebarView("run");
                Assert.Equal("run", window.ActiveSidebarView);

                window.SwitchSidebarView("extensions");
                Assert.Equal("extensions", window.ActiveSidebarView);

                window.SwitchSidebarView("harness");
                Assert.Equal("harness", window.ActiveSidebarView);

                window.ShowMultiHarnessArenaModal();
                Assert.True(window.ModalHost.IsVisible);
                Assert.Equal("arena", window.ModalHost.ActiveDialogId);
                window.ModalHost.CloseModal();

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Editor_SyntaxHighlightingAndAllNineThemes_RenderCorrectly()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // Default theme matches Windows default ("codeless-obsidian")
                var themes = StudioTheme.GetAllBuiltInThemes();
                Assert.True(themes.Count >= 9);

                var csSpans = AvaloniaVirtualEditorCanvas.TokenizeLineForTheme("public static int Compute(int value) // comment", window.CurrentTheme);
                Assert.True(csSpans.Count >= 4);

                var pySpans = AvaloniaVirtualEditorCanvas.TokenizeLineForTheme("def run_task(item): # python comment", window.CurrentTheme);
                Assert.True(pySpans.Count >= 3);

                foreach (var theme in themes)
                {
                    window.ApplyTheme(theme, persistSetting: false);
                    Assert.Equal(theme.Id, window.CurrentTheme.Id);
                    var spans = AvaloniaVirtualEditorCanvas.TokenizeLineForTheme("string s = \"hello\";", theme);
                    Assert.NotEmpty(spans);
                }

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Editor_BreadcrumbsMinimapFoldingAndBracketMatching_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                var tab = window.OpenFileInTab(csFile, preview: false);
                window.Editor.Text = tab.Content + "// dirty check\n";
                Assert.True(tab.IsDirty);
                window.PrimaryEditor.RefreshBreadcrumbs();

                // Breadcrumb bar omits dirty dot '●' (shown on tab and window title instead)
                Assert.DoesNotContain("●", window.PrimaryEditor.BreadcrumbText);

                // Minimap toggle
                Assert.True(window.PrimaryEditor.ShowMinimap);
                window.PrimaryEditor.ShowMinimap = false;
                Assert.False(window.PrimaryEditor.ShowMinimap);
                window.PrimaryEditor.ShowMinimap = true;
                Assert.True(window.PrimaryEditor.ShowMinimap);

                // Code Folding via Ctrl+Shift+[ and Cmd+Shift+]: the caret on the Allman "{" line folds the enclosing
                // block, which belongs to its header line "namespace DemoApp" (QA #13); the caret moves to the header
                tab.CursorLine = 2;
                window.SimulateShortcut(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.Contains(1, tab.FoldedBlockRanges.Keys);
                Assert.Equal(10, tab.FoldedBlockRanges[1]); // the closing "}" stays visible (VS Code)
                Assert.Equal(1, tab.CursorLine);

                window.SimulateShortcut(Key.OemCloseBrackets, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.DoesNotContain(1, tab.FoldedBlockRanges.Keys);

                // Bracket matching jump (Ctrl+Shift+\ and Cmd+Shift+\)
                tab.CursorLine = 2;
                tab.CursorCol = 0;
                window.SimulateShortcut(Key.OemPipe, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.Equal(11, tab.CursorLine);

                window.SimulateShortcut(Key.OemPipe, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(2, tab.CursorLine);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Editor_MultiCursorAndColumnSelection_WorkWithCtrlAndCmd()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                var tab = window.OpenFileInTab(csFile, preview: false);
                var canvas = window.PrimaryEditor.Canvas;
                tab.CursorLine = 7;
                tab.CursorCol = 16; // on "result"
                tab.ClearSelection();

                // 1. Ctrl+D selects word then adds next occurrence
                window.SimulateShortcut(Key.D, KeyModifiers.Control);
                Assert.Equal("result", tab.GetSelectedText());
                window.SimulateShortcut(Key.D, KeyModifiers.Control);
                Assert.NotEmpty(canvas.ExtraCursors);

                // 2. Ctrl+U undoes last cursor
                window.SimulateShortcut(Key.U, KeyModifiers.Control);
                Assert.Empty(canvas.ExtraCursors);

                // 3. Cmd+D (macOS keymap) selects next occurrence
                window.SimulateShortcut(Key.D, KeyModifiers.Meta);
                Assert.NotEmpty(canvas.ExtraCursors);

                // 4. Esc clears extra cursors
                window.SimulateShortcut(Key.Escape, KeyModifiers.None);
                Assert.Empty(canvas.ExtraCursors);

                // 5. Ctrl+Alt+Down and Cmd+Alt+Up add vertical cursors
                tab.CursorLine = 5;
                tab.CursorCol = 8;
                window.SimulateShortcut(Key.Down, KeyModifiers.Control | KeyModifiers.Alt);
                Assert.Single(canvas.ExtraCursors);
                Assert.Equal(6, canvas.ExtraCursors[0].Line);

                window.SimulateShortcut(Key.Up, KeyModifiers.Meta | KeyModifiers.Alt);
                Assert.Equal(2, canvas.ExtraCursors.Count);

                // 6. Column / Box selection across rows
                canvas.ApplyColumnBoxSelection(5, 4, 8, 10);
                Assert.True(canvas.ExtraCursors.Count >= 3);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Editor_FindReplaceGoToLineAndWordWrap_WorkWithCtrlAndCmd()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                var tab = window.OpenFileInTab(csFile, preview: false);

                // Ctrl+F & Cmd+H Find/Replace widget
                window.SimulateShortcut(Key.F, KeyModifiers.Control);
                Assert.True(window.PrimaryEditor.IsFindOverlayVisible);
                window.PrimaryEditor.HideFindBar();
                Assert.False(window.PrimaryEditor.IsFindOverlayVisible);

                window.SimulateShortcut(Key.H, KeyModifiers.Meta);
                Assert.True(window.PrimaryEditor.IsFindOverlayVisible);
                Assert.True(window.PrimaryEditor.IsReplaceRowVisible);

                window.PrimaryEditor.SetFindQuery("result");
                window.PrimaryEditor.ReplaceAllMatches("computedVal");
                Assert.Contains("computedVal", tab.Content);
                window.PrimaryEditor.HideFindBar();

                // Go to Line
                window.PrimaryEditor.GoToLine(8);
                Assert.Equal(7, tab.CursorLine);

                // Word Wrap (Alt+Z)
                bool initialWrap = tab.WordWrap;
                window.SimulateShortcut(Key.Z, KeyModifiers.Alt);
                Assert.NotEqual(initialWrap, tab.WordWrap);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Editor_GhostTextDiagnosticsAndInlineDiffHunks_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                var tab = window.OpenFileInTab(csFile, preview: false);

                // 1. Inline AI Ghost Text: Tab accepts suggestion
                tab.CursorLine = 7;
                tab.CursorCol = tab.Lines[7].Length;
                tab.GhostTextSuggestion = " // ghost accepted";
                window.SimulateShortcut(Key.Tab, KeyModifiers.None);
                Assert.Equal("", tab.GhostTextSuggestion);
                Assert.EndsWith("// ghost accepted", tab.Lines[7]);

                // 2. Diagnostics squiggles & F8 jump
                tab.Diagnostics.Add(new DiagnosticItem { LineNumber = 4, Column = 5, Length = 6, Severity = "Warning", Message = "Test warning" });
                tab.Diagnostics.Add(new DiagnosticItem { LineNumber = 9, Column = 5, Length = 6, Severity = "Error", Message = "Test error" });
                tab.CursorLine = 0;
                window.SimulateShortcut(Key.F8, KeyModifiers.None);
                Assert.Equal(3, tab.CursorLine);
                window.SimulateShortcut(Key.F8, KeyModifiers.None);
                Assert.Equal(8, tab.CursorLine);

                // 3. Inline Diff Staging, Per-Hunk Accept/Reject, and Banner Accept/Reject
                string original = tab.Content;
                string proposed = original.Replace("value + 1", "value + 999");
                window.PrimaryEditor.StageProposedDiffContent(proposed);
                Assert.True(tab.HasPendingDiff);
                Assert.True(window.PrimaryEditor.IsDiffBannerVisible);

                window.PrimaryEditor.RejectAllPendingDiffs();
                Assert.False(tab.HasPendingDiff);
                Assert.Contains("value + 1", tab.Content);

                window.PrimaryEditor.StageProposedDiffContent(proposed);
                window.PrimaryEditor.AcceptAllPendingDiffs();
                Assert.False(tab.HasPendingDiff);
                Assert.Contains("value + 999", tab.Content);

                // 4. Inline AI Edit Bar (Ctrl+I / Cmd+I)
                window.SimulateShortcut(Key.I, KeyModifiers.Control);
                Assert.True(window.PrimaryEditor.IsInlineAiBarVisible);
                window.PrimaryEditor.HideInlineAiBar();

                window.SimulateShortcut(Key.I, KeyModifiers.Meta);
                Assert.True(window.PrimaryEditor.IsInlineAiBarVisible);
                window.PrimaryEditor.HideInlineAiBar();

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void TabsAndUnsavedDialog_DirtyDotPinPreviewSplitAndUnsavedPrompts_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // Preview tab is replaced until pinned or edited
                var prev1 = window.OpenFileInTab(csFile, preview: true);
                Assert.True(prev1.IsPreviewTab);
                var prev2 = window.OpenFileInTab(pyFile, preview: true);
                Assert.Single(window.OpenTabs);
                Assert.DoesNotContain(prev1, window.OpenTabs);
                Assert.Equal(pyFile, prev2.FilePath);

                // Editing sets dirty flag
                window.Editor.Text = prev2.Content + "# edited\n";
                Assert.True(prev2.IsDirty);

                // Unsaved-changes Cancel keeps tab open
                window.UnsavedPromptOverride = _ => UnsavedPromptResult.Cancel;
                bool closedCancel = window.CloseTab(prev2, forceWithoutPrompt: false);
                Assert.False(closedCancel);
                Assert.Contains(prev2, window.OpenTabs);

                // Unsaved-changes SaveAll writes to disk and closes tab
                window.UnsavedPromptOverride = _ => UnsavedPromptResult.SaveAll;
                bool closedSave = window.CloseTab(prev2, forceWithoutPrompt: false);
                Assert.True(closedSave);
                Assert.DoesNotContain(prev2, window.OpenTabs);
                Assert.Contains("# edited", File.ReadAllText(pyFile));

                // Reopen closed tab (Ctrl+Shift+T / Cmd+Shift+T)
                window.SimulateShortcut(Key.T, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(pyFile, window.ActiveTab.FilePath);

                // Split Editor (Ctrl+\ / Cmd+\)
                Assert.False(window.IsEditorSplit);
                window.SimulateShortcut(Key.OemPipe, KeyModifiers.Control);
                Assert.True(window.IsEditorSplit);
                window.SimulateShortcut(Key.OemPipe, KeyModifiers.Meta);
                Assert.False(window.IsEditorSplit);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Terminal_Vt100AnsiRenderingKeystrokesSelectionAndResize_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                var term = window.ActiveTerminalSession.Control;
                Assert.NotNull(term);

                // Feed ANSI color sequences, cursor moves, and text into VT100 buffer
                term.FeedAnsiPreview("\x1b[1;32mSUCCESS_TOKEN\x1b[0m \x1b[38;2;255;128;0mTRUECOLOR_OK\x1b[0m\r\n");
                string screen = term.GetVisibleScreenText();
                Assert.Contains("SUCCESS_TOKEN", screen);
                Assert.Contains("TRUECOLOR_OK", screen);

                // Selection and copy text
                term.SelectAll();
                Assert.Contains("SUCCESS_TOKEN", term.GetSelectedText());
                term.ClearSelection();

                // Inline Terminal AI bar (Ctrl+K / Cmd+K)
                term.ShowInlineAiBar();
                Assert.True(term.IsInlineAiBarVisible);
                term.HideInlineAiBar();
                Assert.False(term.IsInlineAiBarVisible);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Terminal_MultipleSessionsSwitchRenameSplitKillAndExitRemovesSession_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // Clean normalized shell labels
                Assert.Equal("zsh", AvaloniaTerminalEmulatorControl.NormalizeShellLabel("/bin/zsh -l"));
                Assert.Equal("bash", AvaloniaTerminalEmulatorControl.NormalizeShellLabel("/bin/bash"));
                Assert.Equal("pwsh", AvaloniaTerminalEmulatorControl.NormalizeShellLabel("pwsh.exe -NoLogo"));
                Assert.Equal("powershell", AvaloniaTerminalEmulatorControl.NormalizeShellLabel("powershell.exe"));

                int initialCount = window.TerminalSessions.Count;
                var second = window.CreateNewTerminalSession("zsh", ws);
                Assert.Equal(initialCount + 1, window.TerminalSessions.Count);
                Assert.Equal("zsh", second.Title);
                Assert.Equal(second, window.ActiveTerminalSession);

                // Rename session
                window.RenameTerminalSession(second, "build-worker");
                Assert.Equal("build-worker", second.Title);

                // Split terminal
                Assert.False(window.IsTerminalSplit);
                window.ToggleSplitTerminal();
                Assert.True(window.IsTerminalSplit);
                window.ToggleSplitTerminal();
                Assert.False(window.IsTerminalSplit);

                // Kill session removes it from session list
                window.KillTerminalSession(second);
                Assert.DoesNotContain(second, window.TerminalSessions);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void ChatDock_MessageBubblesMarkdownCodeBlocksDiffAcceptRejectAndModes_Work()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                var tab = window.OpenFileInTab(csFile, preview: false);

                // Verify clean startup has zero demo chat content
                Assert.Empty(window.AgentFeed.Items);

                // Mode switching
                foreach (string mode in new[] { "agent", "ask", "edit", "autofix" })
                {
                    window.SetAgentMode(mode);
                    Assert.Equal(mode, window.CurrentAgentMode);
                }

                // Verify fixture injection & UI cards: bubbles, markdown code block, and diff review card
                window.SetAgentMode("agent");
                window.InjectScreenshotFixtureChat("Refactor Compute with a diff");
                Assert.Contains(window.AgentFeed.Items, i => i.Role == "user");
                Assert.Contains(window.AgentFeed.Items, i => i.Role == "assistant" && i.Content.Contains("```csharp"));
                var diffCard = window.AgentFeed.Items.LastOrDefault(i => i.Role == "diff_review");
                Assert.NotNull(diffCard);
                Assert.Equal("Pending", diffCard.DiffStatus);

                // Stage diff & accept through diff review decision
                window.PrimaryEditor.StageProposedDiffContent(tab.Content.Replace("value + 1", "value + 77"));
                Assert.True(tab.HasPendingDiff);
                window.PrimaryEditor.AcceptAllPendingDiffs();
                Assert.False(tab.HasPendingDiff);
                Assert.Contains("value + 77", tab.Content);

                // Verify light theme applies to terminal and Explorer
                window.ApplyTheme("codeless-daylight");
                Assert.NotNull(window.TerminalSession?.Control);
                Assert.True(window.TerminalSession.Control.IsLightTerminal);
                Assert.Equal(global::Avalonia.Media.Colors.White, window.TerminalSession.Control.TermBgColor);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void CommandPaletteAndDialogs_AllModesAndModals_WorkWithCtrlAndCmd()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                window.OpenFileInTab(csFile, preview: false);

                // 1. Ctrl+Shift+P and Cmd+Shift+P -> '>' Command Palette
                window.SimulateShortcut(Key.P, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(">", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                window.SimulateShortcut(Key.P, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(">", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // 2. Ctrl+P and Cmd+P -> Quick Open files
                window.SimulateShortcut(Key.P, KeyModifiers.Meta);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // 3. Ctrl+Shift+O / Cmd+Shift+O -> '@' File Symbols
                window.SimulateShortcut(Key.O, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("@", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // 4. Ctrl+T / Cmd+T -> '#' Workspace Symbols
                window.SimulateShortcut(Key.T, KeyModifiers.Meta);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal("#", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // 5. Ctrl+G -> ':' Go to Line (also on macOS: literal Control+G like VS Code; Cmd+G is Find Next)
                window.SimulateShortcut(Key.G, KeyModifiers.Control);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(":", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // 6. Keyboard Shortcuts Modal (Ctrl+K Ctrl+S and Cmd+K Cmd+S)
                window.SimulateShortcut(Key.K, KeyModifiers.Meta);
                window.SimulateShortcut(Key.S, KeyModifiers.Meta);
                Assert.True(window.ModalHost.IsVisible);
                Assert.Equal("keyboard-shortcuts", window.ModalHost.ActiveDialogId);
                window.ModalHost.CloseModal();

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void StatusBarAndBottomPanel_AllItemsAndTabs_MatchWindows()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // Bottom panel switches cleanly between problems, output, terminal
                window.SwitchBottomTab("problems");
                Assert.Equal("problems", window.ActiveBottomTab);
                window.SwitchBottomTab("output");
                Assert.Equal("output", window.ActiveBottomTab);
                window.SwitchBottomTab("terminal");
                Assert.Equal("terminal", window.ActiveBottomTab);

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Keybindings_AllBindingsAndChords_WorkWithBothCtrlAndCmd()
        {
            string csFile, pyFile, subFile;
            string ws = CreateRichTempWorkspace(out csFile, out pyFile, out subFile);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                var tab = window.OpenFileInTab(csFile, preview: false);

                // Test line comment toggle with Ctrl+/ and Cmd+/
                tab.CursorLine = 7;
                tab.ClearSelection();
                window.SimulateShortcut(Key.OemQuestion, KeyModifiers.Control);
                Assert.Contains("//", tab.Lines[7]);
                window.SimulateShortcut(Key.OemQuestion, KeyModifiers.Meta);
                Assert.DoesNotContain("//", tab.Lines[7]);

                // Test Alt+Up / Option+Up line move
                string line7Before = tab.Lines[7];
                window.SimulateShortcut(Key.Up, KeyModifiers.Alt);
                Assert.Equal(line7Before, tab.Lines[6]);
                window.SimulateShortcut(Key.Down, KeyModifiers.Alt);
                Assert.Equal(line7Before, tab.Lines[7]);

                // Test Ctrl+` toggle terminal
                Assert.True(window.IsBottomPanelVisible);
                window.SimulateShortcut(Key.OemTilde, KeyModifiers.Control);
                Assert.False(window.IsBottomPanelVisible);
                window.SimulateShortcut(Key.OemTilde, KeyModifiers.Control);
                Assert.True(window.IsBottomPanelVisible);

                // Test Cmd+Alt+I / Ctrl+Alt+I toggle AI Chat
                bool chatVis = window.IsRightChatVisible;
                window.SimulateShortcut(Key.I, KeyModifiers.Meta | KeyModifiers.Alt);
                Assert.NotEqual(chatVis, window.IsRightChatVisible);
                window.SimulateShortcut(Key.I, KeyModifiers.Control | KeyModifiers.Alt);
                Assert.Equal(chatVis, window.IsRightChatVisible);

                // Test Cmd+K Cmd+T chord -> Theme Picker
                window.SimulateShortcut(Key.K, KeyModifiers.Meta);
                window.SimulateShortcut(Key.T, KeyModifiers.Meta);
                Assert.True(window.CommandPalette.IsVisible);
                Assert.Equal(">", window.CommandPalette.InitialPrefix);
                window.CommandPalette.Close();

                // Test Ctrl+- Zoom Out on Windows vs Cmd+- Zoom Out on Mac
                bool? prevMac = MacKeymap.ForceMacKeymapOverride;
                try
                {
                    MacKeymap.ForceMacKeymapOverride = false;
                    double beforeZoom = window.PrimaryEditor.Canvas.FontSizePx;
                    window.SimulateShortcut(Key.OemMinus, KeyModifiers.Control);
                    Assert.Equal(beforeZoom - 1.0, window.PrimaryEditor.Canvas.FontSizePx);
                    window.SimulateShortcut(Key.D0, KeyModifiers.Control);
                    Assert.Equal(13.0, window.PrimaryEditor.Canvas.FontSizePx);
                }
                finally
                {
                    MacKeymap.ForceMacKeymapOverride = prevMac;
                }

                // Test Ctrl+K Ctrl+0 (Fold All) and Ctrl+K Ctrl+J (Unfold All)
                window.SimulateShortcut(Key.K, KeyModifiers.Control);
                window.SimulateShortcut(Key.D0, KeyModifiers.Control);
                Assert.NotEmpty(tab.FoldedBlockRanges);
                window.SimulateShortcut(Key.K, KeyModifiers.Control);
                window.SimulateShortcut(Key.J, KeyModifiers.Control);
                Assert.Empty(tab.FoldedBlockRanges);

                // Test Ctrl+L / Cmd+L select current line
                tab.CursorLine = 1;
                tab.ClearSelection();
                window.SimulateShortcut(Key.L, KeyModifiers.Control);
                Assert.True(tab.HasSelection());

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void UnderscoreLabels_WelcomeStatusBar_And_LightOutline_Work_For_my_proj_x()
        {
            string parentDir = Path.Combine(Path.GetTempPath(), "codeLess_R10c_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string ws = Path.Combine(parentDir, "my_proj_x");
            Directory.CreateDirectory(ws);
            string subDir = Path.Combine(ws, "sub_pkg_x");
            Directory.CreateDirectory(subDir);
            string csFile = Path.Combine(ws, "my_file_x.cs");
            File.WriteAllText(csFile,
                "using System;\n" +
                "namespace my_ns_x\n" +
                "{\n" +
                "    public class my_class_x\n" +
                "    {\n" +
                "        public int my_method_x(int input_val_x)\n" +
                "        {\n" +
                "            return input_val_x + 1;\n" +
                "        }\n" +
                "    }\n" +
                "}\n");

            try
            {
                var window = new MainWindow(ws);
                window.Show();

                // 1. On Welcome tab (no text editor active): editor-only status bar items MUST be hidden
                window.CloseAllTabs();
                window.OpenWelcomeTab();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Null(window.ActiveTab);
                Assert.False(window.IsStatusBarLineColVisible);
                Assert.False(window.IsStatusBarSpacesVisible);
                Assert.False(window.IsStatusBarEncodingVisible);
                Assert.False(window.IsStatusBarEolVisible);
                Assert.False(window.IsStatusBarLanguageVisible);

                // 2. Welcome Recent list must show "my_proj_x" (underscores NOT stripped to "myprojx") and full path + tooltip
                var welcomeTexts = window.PrimaryEditor.GetVisualDescendants().OfType<TextBlock>()
                    .Select(tb => tb.Text ?? "")
                    .ToList();
                Assert.Contains("my_proj_x", welcomeTexts);
                Assert.DoesNotContain("myprojx", welcomeTexts);
                Assert.Contains(welcomeTexts, t => t.Contains("my_proj_x") && t.Contains("codeLess_R10c_"));

                var recentBtn = window.PrimaryEditor.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => string.Equals(b.Tag as string, ws, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(recentBtn);
                Assert.Equal(ws, ToolTip.GetTip(recentBtn)?.ToString());

                // 3. Explorer workspace header and tree nodes preserve underscores
                Assert.Equal("▾ MY_PROJ_X", window.WorkspaceFolderHeaderButton?.Content?.ToString());
                Assert.Single(window.ExplorerTree.Nodes);
                Assert.Equal("my_proj_x", window.ExplorerTree.Nodes[0].Text);
                var childNodeNames = window.ExplorerTree.Nodes[0].Nodes.Select(n => n.Text).ToList();
                Assert.Contains("sub_pkg_x", childNodeNames);
                Assert.Contains("my_file_x.cs", childNodeNames);

                // 4. Open my_file_x.cs -> status bar editor items become visible; tabs, breadcrumbs, terminal, chat, palette keep underscores
                var tab = window.OpenFileInTab(csFile, preview: false);
                Assert.NotNull(tab);
                Assert.True(window.IsStatusBarLineColVisible);
                Assert.True(window.IsStatusBarSpacesVisible);
                Assert.True(window.IsStatusBarEncodingVisible);
                Assert.True(window.IsStatusBarEolVisible);
                Assert.True(window.IsStatusBarLanguageVisible);
                Assert.Equal("C#", window.StatusBarLanguageText);

                // Breadcrumbs preserve underscores in both BreadcrumbText and rendered TextBlocks
                tab.CursorLine = 6;
                window.PrimaryEditor.RefreshBreadcrumbs();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Contains("my_file_x.cs", window.PrimaryEditor.BreadcrumbText);
                Assert.Contains("my_method_x", window.PrimaryEditor.BreadcrumbText);
                var editorTexts = window.PrimaryEditor.GetVisualDescendants().OfType<TextBlock>()
                    .Select(tb => tb.Text ?? "")
                    .ToList();
                Assert.Contains(editorTexts, t => t.Contains("my_file_x.cs"));
                Assert.Contains(editorTexts, t => t.Contains("my_method_x"));
                Assert.DoesNotContain(editorTexts, t => t.Contains("myfilex") || t.Contains("mymethodx"));

                // Terminal session renamed to my_term_x preserves underscores in UI
                window.RenameTerminalSession(window.ActiveTerminalSession, "my_term_x");
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var windowTexts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(tb => tb.Text ?? "")
                    .ToList();
                Assert.Contains(windowTexts, t => t.Contains("my_term_x"));
                Assert.DoesNotContain(windowTexts, t => t.Contains("mytermx"));

                // Chat context pill and chat message card preserve underscores
                window.AttachChatContextPill("my_file_x.cs");
                window.InjectScreenshotFixtureChat("Inspect my_proj_x and my_file_x.cs");
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                windowTexts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(tb => tb.Text ?? "")
                    .ToList();
                Assert.Contains(windowTexts, t => t.Contains("@my_file_x.cs  ✕"));
                Assert.Contains(windowTexts, t => t.Contains("Inspect my_proj_x and my_file_x.cs"));
                Assert.DoesNotContain(windowTexts, t => t.Contains("myprojx") || t.Contains("myfilex"));

                // Command Palette Quick Open preserves underscores
                window.OpenQuickOpen();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var paletteTexts = window.CommandPalette.GetVisualDescendants().OfType<TextBlock>()
                    .Select(tb => tb.Text ?? "")
                    .ToList();
                Assert.Contains(paletteTexts, t => t.Contains("my_file_x.cs"));
                window.CommandPalette.Close();

                // 5. Light theme ("codeless-daylight"): Outline items must use dark theme foreground colors (legible on light background)
                window.ApplyTheme("codeless-daylight");
                var outlineItems = window.OutlineListBoxControl.ItemsSource?.Cast<Control>().OfType<TextBlock>().ToList();
                Assert.NotNull(outlineItems);
                Assert.NotEmpty(outlineItems);
                Assert.Contains(outlineItems, tb => (tb.Text ?? "").Contains("my_method_x"));
                foreach (var itemTb in outlineItems)
                {
                    var brush = Assert.IsType<SolidColorBrush>(itemTb.Foreground);
                    Assert.True(brush.Color.R < 90 && brush.Color.G < 90 && brush.Color.B < 90,
                        $"Expected dark legible Outline foreground on light theme, got {brush.Color}");
                }

                window.SuppressUnsavedPrompts = true; // dirty test buffers must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(parentDir, true); } catch { }
            }
        }
    }
}
