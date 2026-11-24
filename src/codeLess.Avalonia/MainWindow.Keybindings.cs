using System;
using Avalonia.Input;
using codeLess.Core;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private KeybindingManager keybindings;
        private KeybindingsFileWatcher keybindingsWatcher;
        private Avalonia.Controls.Border keybindingsNotification;
        private WindowContextKeyService contextKeyService;

        private void InitializeKeybindings()
        {
            DefaultCommands.Register();
            keybindings = new KeybindingManager();

            // Bind actions to commands
            foreach (var kvp in MenuCommands)
            {
                keybindings.BindAction(kvp.Key, kvp.Value);
            }
            // Add custom mappings not in the menu
            keybindings.BindAction("workbench.action.showCommands", () => OpenCommandPalette(">"));
            keybindings.BindAction("workbench.action.quickOpen", () => OpenQuickOpen());
            keybindings.BindAction("workbench.action.newWindow", () => CreateNewWindowInstance());
            keybindings.BindAction("workbench.action.closeWindow", () => Close());
            keybindings.BindAction("workbench.action.openSettingsJson", () => OpenFileInTab(VscodeSettingsManager.GetUserSettingsPath()));
            keybindings.BindAction("workbench.action.openWorkspaceSettingsFile", () => OpenWorkspaceSettingsFile());
            keybindings.BindAction("workbench.action.openGlobalKeybindings", () => ShowKeyboardShortcutsModal());
            keybindings.BindAction("workbench.action.openGlobalKeybindingsFile", () => OpenFileInTab(codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath()));
            keybindings.BindAction("workbench.action.openDefaultKeybindingsFile", () => OpenDefaultKeybindingsFile());
            keybindings.BindAction("workbench.action.terminal.toggleTerminal", () => ToggleBottomPanel()); // Simple mapping
            keybindings.BindAction("workbench.action.toggleSidebarVisibility", () => TogglePrimarySidebar());
            keybindings.BindAction("workbench.action.toggleZenMode", () => ToggleZenMode());
            keybindings.BindAction("workbench.action.closeActiveEditor", () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); });
            keybindings.BindAction("workbench.action.nextEditor", () => CycleEditorTab(1));
            keybindings.BindAction("workbench.action.previousEditor", () => CycleEditorTab(-1));
            keybindings.BindAction("workbench.action.openNextRecentlyUsedEditorInGroup", () => OpenOrAdvanceEditorSwitcher(false));
            keybindings.BindAction("editor.action.commentLine", () => PrimaryEditor.Canvas.ToggleLineComment());

            contextKeyService = new WindowContextKeyService(this);
            InitializeVscodeSettingsWatcher();

            string path = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            keybindingsWatcher = new KeybindingsFileWatcher(path, content => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    string err = keybindings.LoadUserBindings(content);
                    if (err != null) {
                        ShowKeybindingsNotification("Keybindings parse error: " + err);
                    } else {
                        HideKeybindingsNotification();
                    }
                });
            });
            // Initial load
            try { 
                string err = keybindings.LoadUserBindings(System.IO.File.ReadAllText(path)); 
                if (err != null) ShowKeybindingsNotification("Keybindings parse error: " + err);
            } catch { }

        }

        private bool DispatchKeybinding(Key key, KeyModifiers modifiers, bool isTerminalFocus)
        {
            if (keybindings == null) return false;
            string ks = KeyStringParser.FromKey((int)key, (int)modifiers);
            return keybindings.Dispatch(ks, contextKeyService, msg => {
                if (!string.IsNullOrEmpty(msg))
                    statusChordLabel.Text = msg;
                else
                    statusChordLabel.Text = "";
            }, isTerminalFocus);
        }
        private void ShowKeybindingsNotification(string message)
        {
            HideKeybindingsNotification();
            keybindingsNotification = WorkbenchStyles.BuildNotification(currentTheme, "error", new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, null, new Avalonia.Controls.Button[0], false, HideKeybindingsNotification);
            keybindingsNotification.Margin = new Avalonia.Thickness(0, 0, 12, 12);
            keybindingsNotification.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            keybindingsNotification.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            keybindingsNotification.ZIndex = 70;
            Avalonia.Controls.Grid.SetColumnSpan(keybindingsNotification, 2);
            editorSplitGrid.Children.Add(keybindingsNotification);
        }

        private void HideKeybindingsNotification()
        {
            if (keybindingsNotification != null)
            {
                editorSplitGrid.Children.Remove(keybindingsNotification);
                keybindingsNotification = null;
            }
        }

        private void OpenDefaultKeybindingsFile()
        {
            var defaults = codeLess.Core.CommandRegistry.AllCommands;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[");
            bool isMac = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
            bool isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
            bool first = true;
            foreach (var cmd in defaults)
            {
                string key = isMac ? cmd.DefaultKeyMac : (isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin);
                if (string.IsNullOrEmpty(key) && !isMac) key = cmd.DefaultKeyWin;
                if (string.IsNullOrEmpty(key)) continue;
                
                if (!first) sb.AppendLine(",");
                first = false;
                
                sb.AppendLine("  {");
                sb.AppendLine("    \"key\": \"" + key.ToLowerInvariant() + "\",");
                sb.AppendLine("    \"command\": \"" + cmd.Id + "\"" + (!string.IsNullOrEmpty(cmd.When) ? "," : ""));
                if (!string.IsNullOrEmpty(cmd.When))
                {
                    sb.AppendLine("    \"when\": \"" + cmd.When + "\"");
                }
                sb.Append("  }");
            }
            sb.AppendLine();
            sb.AppendLine("]");
            
            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "defaultKeybindings.json");
            try {
                System.IO.File.WriteAllText(tempPath, sb.ToString());
            } catch { }
            var tab = OpenFileInTab(tempPath, preview: false);
            if (tab != null) {
                tab.IsReadOnlyDiffPreview = true;
                tab.RelativePath = "Default Keybindings";
                tab.FileName = "Default Keybindings";
            }
        }
        private System.IO.FileSystemWatcher userSettingsWatcher;
        
        private void InitializeVscodeSettingsWatcher()
        {
            string path = VscodeSettingsManager.GetUserSettingsPath();
            if (!System.IO.File.Exists(path)) {
                try { System.IO.File.WriteAllText(path, "{\n}"); } catch { }
            }
            userSettingsWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileName(path));
            userSettingsWatcher.NotifyFilter = System.IO.NotifyFilters.LastWrite;
            userSettingsWatcher.Changed += (s, e) => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    try {
                        System.Threading.Thread.Sleep(50); // debounce file write
                        string content = System.IO.File.ReadAllText(path);
                        ApplyVscodeSettings(content);
                    } catch { }
                });
            };
            userSettingsWatcher.EnableRaisingEvents = true;
            
            try { ApplyVscodeSettings(System.IO.File.ReadAllText(path)); } catch { }
        }

        private void ApplyVscodeSettings(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            VscodeSettingsManager.ApplySettings(content, settings);
            try { settings.Save(); } catch { }
            
            var studioSettings = settings as StudioSettings;
            if (studioSettings != null)
            {
                ApplyTheme(StudioTheme.GetById(studioSettings.ActiveThemeId), persistSetting: true);
                PrimaryEditor.ShowMinimap = studioSettings.ShowMinimap;
                PrimaryEditor.ShowBreadcrumbs = studioSettings.ShowBreadcrumbs;
                PrimaryEditor.Canvas.FontSizePx = studioSettings.EditorFontSize;
                ApplyEditorIndentAndWrapSettings(studioSettings);
                ApplyGitParitySettings();
                ApplyEditorNicetySettings();
                UpdateStatusBar();
            }
        }

        private void OpenWorkspaceSettingsFile()
        {
            string path = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
            if (path == null) return;
            if (!System.IO.File.Exists(path))
            {
                try {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, "{\n}");
                } catch { }
            }
            OpenFileInTab(path);
        }
    }
}
