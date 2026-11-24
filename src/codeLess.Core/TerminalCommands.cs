using System.Collections.Generic;

namespace codeLess.Core
{
    public static class TerminalCommands
    {
        public static readonly HashSet<string> CommandsToSkipShell = new HashSet<string>
        {
            "workbench.action.showCommands",
            "workbench.action.quickOpen",
            "workbench.action.terminal.toggleTerminal",
            "workbench.action.terminal.kill",
            "workbench.action.terminal.focus",
            "workbench.action.toggleSidebarVisibility",
            "workbench.action.toggleZenMode",
            "workbench.action.closeWindow",
            "workbench.action.closeActiveEditor",
            "workbench.action.nextEditor",
            "workbench.action.previousEditor",
            "workbench.action.openNextRecentlyUsedEditorInGroup",
            "workbench.action.files.save",
            "workbench.action.files.saveAll",
            "workbench.action.findInFiles",
            "workbench.action.replaceInFiles",
            "workbench.action.openGlobalKeybindings",
            "workbench.action.openSettingsJson",
            "workbench.action.focusActiveEditorGroup",
            "workbench.action.focusPrimarySideBar",
            "workbench.action.focusPanel",
            // Debugging
            "workbench.action.debug.start",
            "workbench.action.debug.run",
            "workbench.action.debug.continue",
            "workbench.action.debug.stepOver",
            "workbench.action.debug.stepInto",
            "workbench.action.debug.stepOut",
            "workbench.action.debug.restart",
            "workbench.action.debug.stop",
            "editor.action.toggleBreakpoint"
        };
    }
}
