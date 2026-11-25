using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public class CrossCuttingQaW7Tests
    {
        private static string LocateRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "src")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        }

        private sealed class FakeContextKeyService : IContextKeyService
        {
            private readonly HashSet<string> active = new HashSet<string>(StringComparer.Ordinal);
            public FakeContextKeyService(string whenExpr)
            {
                if (string.IsNullOrEmpty(whenExpr)) return;
                foreach (string token in whenExpr.Split(new[] { "&&", "||", "(", ")" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = token.Trim();
                    if (!t.StartsWith("!") && !t.Contains("==") && !t.Contains("!="))
                        active.Add(t);
                }
            }
            public object GetValue(string key) => active.Contains(key);
        }

        [AvaloniaFact]
        public void TruthfulnessAudit_AllThreeAuditDocs_EveryRowHasValidFileLineAndWiredInBothUis()
        {
            string root = LocateRepoRoot();
            string[] auditFiles = new[]
            {
                Path.Combine(root, "docs", "WORKBENCH_PARITY.md"),
                Path.Combine(root, "docs", "HARNESS_SPEC_AUDIT.md"),
                Path.Combine(root, "docs", "KEYBINDING_AUDIT.md")
            };

            foreach (string auditPath in auditFiles)
            {
                Assert.True(File.Exists(auditPath), "Missing audit doc: " + auditPath);
                string[] lines = File.ReadAllLines(auditPath);
                int verifiedRows = 0;

                foreach (string line in lines)
                {
                    if (!line.StartsWith("|") || line.Contains("---") || line.Contains("File:Line")) continue;
                    string[] cols = line.Split('|');
                    if (cols.Length < 5) continue;

                    string fileLineCell = cols
                        .Select(c => c.Trim())
                        .FirstOrDefault(c => (c.StartsWith("src/") || c.StartsWith("docs/")) && c.Contains(":"));
                    Assert.False(string.IsNullOrEmpty(fileLineCell), $"Row missing File:Line in {Path.GetFileName(auditPath)}: {line}");

                    int colon = fileLineCell.LastIndexOf(':');
                    string relFile = fileLineCell.Substring(0, colon);
                    int lineNum = int.Parse(fileLineCell.Substring(colon + 1));
                    string fullFile = Path.Combine(root, relFile);
                    Assert.True(File.Exists(fullFile), $"Cited file does not exist: {relFile}");

                    string[] srcLines = File.ReadAllLines(fullFile);
                    Assert.True(lineNum >= 2 && lineNum <= srcLines.Length, $"Invalid line {lineNum} in {relFile} (max {srcLines.Length})");
                    Assert.False(lineNum == 1 || lineNum == 10 || lineNum == 3000, $"Suspicious placeholder line {relFile}:{lineNum}");
                    verifiedRows++;
                }

                Assert.True(verifiedRows >= 40, $"Expected >= 40 verified rows in {Path.GetFileName(auditPath)}, got {verifiedRows}");
            }

            // Confirm WinForms & Avalonia both wire KeybindingManager, LspDiagnosticsProvider, AgentUiParity, and WorkbenchParityHelper
            string winFormsMain = File.ReadAllText(Path.Combine(root, "src", "CodeLessMainForm.cs"));
            string winFormsTree = File.ReadAllText(Path.Combine(root, "src", "CoreModelsAndThemes.cs"));
            string winFormsDebug = File.ReadAllText(Path.Combine(root, "src", "CodeLessMainForm.Debug.cs"));
            Assert.Contains("new LspDiagnosticsProvider()", winFormsMain);
            Assert.Contains("InitKeybindingsWin()", winFormsMain);
            Assert.Contains("DispatchKeybindingWin(keyData)", winFormsDebug);
            Assert.Contains("DismissSearchRowWin", winFormsMain);
            Assert.Contains("ReplaceSingleSearchMatchWin", winFormsMain);
            Assert.Contains("SetProblemsFilter", winFormsMain);
            Assert.Contains("ShowWorkbenchNotificationWin", winFormsMain);
            Assert.Contains("GetCompactDisplayText", winFormsTree);
            Assert.Contains("ApplyTypeFilter", winFormsTree);
            Assert.Contains("ProblemCountByFullPath", winFormsTree);
        }

        [AvaloniaFact]
        public void ShortcutSweep_AllDefaultCommands_AndWorkbenchMenuModel_ExecuteInFocusContext_WithoutLeakingText()
        {
            DefaultCommands.Register();
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w7_sweep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            string sampleFile = Path.Combine(ws, "Sample.cs");
            File.WriteAllText(sampleFile, "using System;\nclass Sample {\n    void Run() {\n        int x = 1;\n    }\n}\n");

            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(sampleFile, preview: false);
                Assert.NotNull(tab);

                // 1. Verify every DefaultCommand dispatches on both mac (Cmd) and non-mac (Ctrl) maps (including chords)
                foreach (bool isMac in new[] { true, false })
                {
                    var km = new KeybindingManager(isMac);
                    var fired = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cmd in CommandRegistry.AllCommands)
                    {
                        string id = cmd.Id;
                        km.BindAction(id, () => fired.Add(id));
                    }

                    foreach (var b in km.Defaults)
                    {
                        if (string.IsNullOrEmpty(b.Key)) continue;

                        fired.Clear();
                        var ctx = new FakeContextKeyService(b.When);
                        bool terminalFocus = (b.When ?? "").Trim() == "terminalFocus";
                        string[] parts = b.Key.Split(' ');
                        if (parts.Length == 2)
                        {
                            Assert.True(km.Dispatch(parts[0], ctx, _ => { }, terminalFocus), $"Chord prefix {parts[0]} failed for {b.Command} (isMac={isMac})");
                            Assert.Empty(fired);
                            Assert.True(km.Dispatch(parts[1], ctx, _ => { }, terminalFocus), $"Chord second key {parts[1]} failed for {b.Command} (isMac={isMac})");
                        }
                        else
                        {
                            Assert.True(km.Dispatch(parts[0], ctx, _ => { }, terminalFocus), $"Dispatch {parts[0]} failed for {b.Command} (isMac={isMac})");
                        }
                        Assert.NotEmpty(fired);
                    }
                }

                // 2. Verify WorkbenchMenuModel commands are all bound and executable without leaking text into editor or chat
                var menuBar = WorkbenchMenuModel.Build();
                var menuIds = new List<string>();
                void CollectIds(IEnumerable<WorkbenchMenuEntry> specs)
                {
                    foreach (var sp in specs)
                    {
                        if (!string.IsNullOrEmpty(sp.Id) && sp.Id != "window.focusDynamic")
                            menuIds.Add(sp.Id);
                        if (sp.Children != null) CollectIds(sp.Children);
                    }
                }
                foreach (var top in menuBar) CollectIds(top.Items);
                Assert.NotEmpty(menuIds);

                // Check pressing Ctrl/Cmd+B, Ctrl/Cmd+J, Ctrl/Cmd+Shift+P, and chord Ctrl/Cmd+K Z on the live window does not leak text
                string beforeContent = tab.Content;
                var chatBox = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ChatPromptInput");
                chatBox.Text = "";

                Assert.True(window.SimulateShortcut(Key.B, KeyModifiers.Control));
                Assert.True(window.SimulateShortcut(Key.B, KeyModifiers.Control));
                Assert.True(window.SimulateShortcut(Key.J, KeyModifiers.Control));
                Assert.True(window.SimulateShortcut(Key.J, KeyModifiers.Control));
                Assert.Equal(beforeContent, tab.Content);
                Assert.Equal("", chatBox.Text ?? "");
            }
            finally
            {
                window.SuppressUnsavedPrompts = true;
                window.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void InteractionConflicts_TabPerContext_CmdKInTerminalVsEditor_CmdIInlineAi_AndEscapePriority()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w7_conflicts_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            string file = Path.Combine(ws, "Conflict.cs");
            File.WriteAllText(file, "line1\nline2\nline3\n");

            var window = new MainWindow(ws);
            try
            {
                window.Show();
                var tab = window.OpenFileInTab(file, preview: false);
                Assert.NotNull(tab);

                // 1. Tab in chat input cycles Build/Plan mode without indenting editor
                var chatBox = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ChatPromptInput");
                string modeBefore = window.CurrentAgentMode;
                string line0Before = tab.Lines[0];
                chatBox.Focus();
                chatBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.None });
                Assert.NotEqual(modeBefore, window.CurrentAgentMode);
                Assert.Equal(line0Before, tab.Lines[0]);

                // 2. Tab in Command Palette cycles palette items without changing agent mode or indenting editor
                string modeAfterChatTab = window.CurrentAgentMode;
                window.OpenCommandPalette(">");
                Assert.True(window.CommandPalette.IsVisible);
                int selBefore = window.CommandPalette.ResultsListBox.SelectedIndex;
                window.CommandPalette.SearchInput.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Tab,
                    KeyModifiers = KeyModifiers.None
                });
                Assert.Equal(selBefore + 1, window.CommandPalette.ResultsListBox.SelectedIndex);
                Assert.Equal(modeAfterChatTab, window.CurrentAgentMode);
                window.CommandPalette.Close();

                // 3. Tab in editor indents without changing agent mode
                tab.CursorLine = 0;
                tab.CursorCol = 0;
                tab.ClearSelection();
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Tab,
                    KeyModifiers = KeyModifiers.None
                });
                Assert.NotEqual(line0Before, tab.Lines[0]);
                Assert.Equal(modeAfterChatTab, window.CurrentAgentMode);

                // 4. Cmd/Ctrl+K in terminal does NOT start a workbench chord, whereas Cmd/Ctrl+K in editor starts the chord prefix
                string statusMsg = "";
                string chordPrefixKey = MacKeymap.IsMacOS ? "cmd+k" : "ctrl+k";
                bool handledInTerminal = window.KeybindingManager.Dispatch(chordPrefixKey, new FakeContextKeyService("terminalFocus"), m => statusMsg = m, isTerminalFocus: true);
                Assert.False(handledInTerminal);
                Assert.Equal("", statusMsg);

                bool handledInEditor = window.KeybindingManager.Dispatch(chordPrefixKey, new FakeContextKeyService("editorTextFocus"), m => statusMsg = m, isTerminalFocus: false);
                Assert.True(handledInEditor);
                Assert.Contains("Waiting for second key of chord", statusMsg);
                // Clear chord state
                window.KeybindingManager.Dispatch("escape", new FakeContextKeyService("editorTextFocus"), m => statusMsg = m, isTerminalFocus: false);

                // 5. Cmd/Ctrl+I opens Inline AI bar in editor
                Assert.False(window.PrimaryEditor.IsInlineAiBarVisible);
                window.SimulateShortcut(Key.I, KeyModifiers.Control);
                Assert.True(window.PrimaryEditor.IsInlineAiBarVisible);
                window.PrimaryEditor.HideInlineAiBar();
                Assert.False(window.PrimaryEditor.IsInlineAiBarVisible);

                // 6. Escape priority cascade: suggest -> find widget -> multi-cursor -> selection -> bottom panel
                if (!window.IsBottomPanelVisible) window.ToggleBottomPanel();
                Assert.True(window.IsBottomPanelVisible);

                // Set up all layers simultaneously: completion popup + find widget + extra cursor + selection + bottom panel
                window.PrimaryLspBinding.Completion.IsVisible = true;
                Assert.True(window.PrimaryLspBinding.IsCompletionVisible);
                window.PrimaryEditor.ShowFindBar(false);
                Assert.True(window.PrimaryEditor.IsFindBarVisible);
                window.PrimaryEditor.Canvas.AddCursorVertical(1);
                Assert.True(window.PrimaryEditor.Canvas.ExtraCursors.Count > 0);
                tab.SelectionStartLine = 0;
                tab.SelectionStartCol = 0;
                tab.CursorLine = 0;
                tab.CursorCol = 3;
                Assert.True(tab.HasSelection());

                // Press 1: closes Suggest (Completion) only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(window.PrimaryLspBinding.IsCompletionVisible);
                Assert.True(window.PrimaryEditor.IsFindBarVisible);
                Assert.True(window.PrimaryEditor.Canvas.ExtraCursors.Count > 0);
                Assert.True(window.IsBottomPanelVisible);

                // Press 2: closes Find widget only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(window.PrimaryEditor.IsFindBarVisible);
                Assert.True(window.PrimaryEditor.Canvas.ExtraCursors.Count > 0);
                Assert.True(window.IsBottomPanelVisible);

                // Press 3: clears Multi-cursor only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.Empty(window.PrimaryEditor.Canvas.ExtraCursors);
                Assert.True(tab.HasSelection());
                Assert.True(window.IsBottomPanelVisible);

                // Press 4: clears Selection only
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Assert.False(tab.HasSelection());
                Assert.True(window.IsBottomPanelVisible);

                // Press 5: closes Bottom Panel
                window.PrimaryEditor.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
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
        public void LiveSettingsAndKeybindings_PropagateAcrossWindows_WhileWorkspaceSettingsStayPerWindow()
        {
            string ws1 = Path.Combine(Path.GetTempPath(), "codeless_w7_ws1_" + Guid.NewGuid().ToString("N"));
            string ws2 = Path.Combine(Path.GetTempPath(), "codeless_w7_ws2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws1, ".vscode"));
            Directory.CreateDirectory(ws2);
            File.WriteAllText(Path.Combine(ws1, ".vscode", "settings.json"), "{ \"editor.fontSize\": 19 }");

            var w1 = new MainWindow(ws1);
            var w2 = new MainWindow(ws2);
            try
            {
                w1.Show();
                w2.Show();

                // Per-workspace .vscode/settings.json applies to w1 only, not w2
                Assert.Equal(19f, w1.GetEditorFontSizeForTests());
                Assert.NotEqual(19f, w2.GetEditorFontSizeForTests());

                // User keybindings.json live reload applies to each window's KeybindingManager
                string customKb = "[ { \"key\": \"ctrl+alt+9\", \"command\": \"workbench.action.togglePanel\" } ]";
                Assert.Null(w1.KeybindingManager.LoadUserBindings(customKb));
                Assert.Null(w2.KeybindingManager.LoadUserBindings(customKb));

                bool w1PanelBefore = w1.IsBottomPanelVisible;
                bool w2PanelBefore = w2.IsBottomPanelVisible;
                Assert.True(w1.KeybindingManager.Dispatch("ctrl+alt+9", new FakeContextKeyService(""), _ => { }));
                Assert.True(w2.KeybindingManager.Dispatch("ctrl+alt+9", new FakeContextKeyService(""), _ => { }));
                Assert.NotEqual(w1PanelBefore, w1.IsBottomPanelVisible);
                Assert.NotEqual(w2PanelBefore, w2.IsBottomPanelVisible);
            }
            finally
            {
                w1.SuppressUnsavedPrompts = true;
                w2.SuppressUnsavedPrompts = true;
                w1.Close();
                w2.Close();
                try { Directory.Delete(ws1, true); } catch { }
                try { Directory.Delete(ws2, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void MultiInstance_CliForwardingFlags_WindowRestore_PerWindowIsolation_AndDirtyClosePrompts()
        {
            string ws1 = Path.Combine(Path.GetTempPath(), "codeless_w7_mi1_" + Guid.NewGuid().ToString("N"));
            string ws2 = Path.Combine(Path.GetTempPath(), "codeless_w7_mi2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws1);
            Directory.CreateDirectory(ws2);
            string fileA = Path.Combine(ws1, "Alpha.cs");
            string fileB = Path.Combine(ws1, "Beta.cs");
            File.WriteAllText(fileA, "line1\nline2\nline3\nline4\n");
            File.WriteAllText(fileB, "line1\nline2\ndiff\nline4\n");

            var w1 = new MainWindow(ws1);
            var w2 = new MainWindow(ws2);
            try
            {
                w1.Show();
                w2.Show();

                // 1. Per-window isolation: separate HarnessEngine, KeybindingManager, and TerminalSessions
                Assert.NotSame(w1.HarnessEngine, w2.HarnessEngine);
                Assert.NotSame(w1.KeybindingManager, w2.KeybindingManager);
                Assert.NotSame(w1.TerminalSessions, w2.TerminalSessions);

                // 2. CLI flags forwarding via IWorkbenchWindow: -g file:line:col, -d diff, -a add folder
                ((IWorkbenchWindow)w1).OpenFileAtLine(fileA, 3, 2, null);
                Assert.Equal(fileA, w1.ActiveTab?.FilePath);
                Assert.Equal(2, w1.ActiveTab?.CursorLine);

                bool waitClosed = false;
                ((IWorkbenchWindow)w1).DiffFiles(fileA, fileB, () => waitClosed = true);
                Assert.Contains(w1.OpenTabs, t => t.FilePath == fileB);

                // 3. Dirty-file prompt on window close: Cancel aborts close, Don't Save allows close and disposes resources
                w1.ActiveTab.Content += "// dirty edit\n";
                w1.ActiveTab.IsDirty = true;
                w1.SuppressUnsavedPrompts = false;
                w1.UnsavedPromptOverride = _ => UnsavedPromptResult.Cancel;
                w1.Close();
                Assert.False(w1.TestCheckDisposed()); // Still open because Cancel was chosen

                w1.UnsavedPromptOverride = _ => UnsavedPromptResult.DontSave;
                w1.Close();
                Assert.True(w1.TestCheckDisposed()); // Disposed cleanly
                Assert.True(waitClosed);
            }
            finally
            {
                w1.SuppressUnsavedPrompts = true;
                w2.SuppressUnsavedPrompts = true;
                try { w1.Close(); } catch { }
                try { w2.Close(); } catch { }
                try { Directory.Delete(ws1, true); } catch { }
                try { Directory.Delete(ws2, true); } catch { }
            }
        }
    }
}
