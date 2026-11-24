using System;
namespace codeLess.Core
{
    public static class DefaultCommands
    {
        public static void Register()
        {
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.showCommands", "Show Command Palette", "View", "Cmd+Shift+P", "Ctrl+Shift+P", "Ctrl+Shift+P", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.quickOpen", "Go to File...", "File", "Cmd+P", "Ctrl+P", "Ctrl+P", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openSettingsJson", "Preferences: Open User Settings (JSON)", "Preferences", "Cmd+,", "Ctrl+,", "Ctrl+,", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.openGlobalKeybindings", "Preferences: Open Keyboard Shortcuts", "Preferences", "Cmd+K Cmd+S", "Ctrl+K Ctrl+S", "Ctrl+K Ctrl+S", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.terminal.toggleTerminal", "Toggle Terminal", "View", "Cmd+`", "Ctrl+`", "Ctrl+`", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleSidebarVisibility", "Toggle Side Bar", "View", "Cmd+B", "Ctrl+B", "Ctrl+B", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("workbench.action.toggleZenMode", "Zen Mode", "View", "Cmd+K Z", "Ctrl+K Z", "Ctrl+K Z", ""));
            CommandRegistry.RegisterCommand(new CommandDefinition("editor.action.copyLinesDownAction", "Copy line down", "Editor", "Shift+Alt+Down", "Shift+Alt+Down", "Shift+Alt+Down", "editorTextFocus"));
            // Add more as needed
        }
    }
}

