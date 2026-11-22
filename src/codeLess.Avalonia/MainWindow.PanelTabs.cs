using Avalonia.Controls;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Bottom panel tab behaviour (VS Code parity): the selected tab is always visible and highlighted, the toolbar only
    /// shows the actions of the active view (terminal actions on TERMINAL, "Clear" on OUTPUT / DEBUG CONSOLE), and
    /// choosing a tab explicitly (click, Ctrl+Shift+Y) moves keyboard focus into it — while a debug session start
    /// reveals the DEBUG CONSOLE without stealing focus from the editor.
    /// </summary>
    public partial class MainWindow
    {
        private Button[] terminalOnlyPanelButtons = new Button[0];
        private Button panelClearButton;

        /// <summary>Id of the active bottom panel view ("problems", "output", "debug", "terminal", "ports").</summary>
        public string ActiveBottomTabId => activeBottomTab;

        private Button BottomTabButtonFor(string id)
        {
            switch (id)
            {
                case "problems": return btmProblemsTabBtn;
                case "output": return btmOutputTabBtn;
                case "debug": return btmDebugConsoleTabBtn;
                case "terminal": return btmTerminalTabBtn;
                case "ports": return btmPortsTabBtn;
            }
            return null;
        }

        private void UpdatePanelToolbarForActiveTab()
        {
            // A view that is shown must have its tab visible (the debug console used to appear with no tab selected).
            var tab = BottomTabButtonFor(activeBottomTab);
            if (tab != null) tab.IsVisible = true;
            bool terminal = activeBottomTab == "terminal";
            foreach (var b in terminalOnlyPanelButtons) b.IsVisible = terminal;
            if (panelClearButton != null)
            {
                bool clearable = activeBottomTab == "output" || activeBottomTab == "debug";
                panelClearButton.IsVisible = clearable;
                ToolTip.SetTip(panelClearButton, activeBottomTab == "debug" ? "Clear Console" : "Clear Output");
            }
        }

        private void ClearActiveBottomTabContent()
        {
            if (activeBottomTab == "debug") debugController?.ClearConsole();
            else if (activeBottomTab == "output" && outputStreamBox != null) outputStreamBox.Text = "";
        }

        /// <summary>Focus the content of an explicitly chosen tab (REPL input for DEBUG CONSOLE, terminal canvas).</summary>
        private void FocusBottomTabContent(string id)
        {
            if (id == "debug") debugConsolePanel?.Input?.Focus();
            else if (id == "problems") problemsListBox?.Focus();
        }
    }
}
