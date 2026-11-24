using System;
using Avalonia.Input;
using codeLess.Core;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private KeybindingManager keybindings;
        private WindowContextKeyService contextKeyService;

        private void InitializeKeybindings()
        {
            DefaultCommands.Register();
            KeybindingManager.InitializeDefaults();

            // Bind actions to commands
            foreach (var kvp in MenuCommands)
            {
                CommandRegistry.BindAction(kvp.Key, kvp.Value);
            }
            // Add custom mappings not in the menu
            CommandRegistry.BindAction("workbench.action.showCommands", () => OpenCommandPalette(">"));
            CommandRegistry.BindAction("workbench.action.quickOpen", () => OpenQuickOpen());
            CommandRegistry.BindAction("workbench.action.newWindow", () => CreateNewWindowInstance());
            CommandRegistry.BindAction("workbench.action.closeWindow", () => Close());
            CommandRegistry.BindAction("workbench.action.openSettingsJson", () => ShowSettingsModal());
            CommandRegistry.BindAction("workbench.action.openGlobalKeybindings", () => ShowKeyboardShortcutsModal());
            CommandRegistry.BindAction("workbench.action.terminal.toggleTerminal", () => ToggleBottomPanel()); // Simple mapping
            CommandRegistry.BindAction("workbench.action.toggleSidebarVisibility", () => TogglePrimarySidebar());
            CommandRegistry.BindAction("workbench.action.toggleZenMode", () => ToggleZenMode());
            CommandRegistry.BindAction("workbench.action.closeActiveEditor", () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); });
            CommandRegistry.BindAction("workbench.action.nextEditor", () => CycleEditorTab(1));
            CommandRegistry.BindAction("workbench.action.previousEditor", () => CycleEditorTab(-1));
            CommandRegistry.BindAction("workbench.action.openNextRecentlyUsedEditorInGroup", () => OpenOrAdvanceEditorSwitcher(false));
            CommandRegistry.BindAction("editor.action.commentLine", () => PrimaryEditor.Canvas.ToggleLineComment());

            keybindings = new KeybindingManager();
            contextKeyService = new WindowContextKeyService(this);
        }

        private bool DispatchKeybinding(Key key, KeyModifiers modifiers)
        {
            if (keybindings == null) return false;
            string ks = KeyStringParser.FromKey((int)key, (int)modifiers);
            return keybindings.Dispatch(ks, contextKeyService, msg => {
                if (!string.IsNullOrEmpty(msg))
                    statusChordLabel.Text = msg;
                else
                    statusChordLabel.Text = "";
            });
        }
    }
}
