using System;
using System.Collections.Generic;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Terminal settings applied to every open terminal, and their command-palette entries.</summary>
    public partial class MainWindow
    {
        /// <summary>Pushes <see cref="TerminalOptions"/> into all open terminals (font, cursor, contrast, scrolling).</summary>
        internal void ApplyTerminalFeelSettings()
        {
            foreach (var ts in terminalSessions)
                ts.Control?.ApplyTerminalOptions();
        }

        private List<PaletteEntryItem> BuildTerminalFeelPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Terminal", Title = "Terminal: Toggle Cursor Blinking", Execute = () =>
                    ToggleTerminalFeel(() => { TerminalOptions.CursorBlinking = !TerminalOptions.CursorBlinking; return "Terminal cursor blinking: " + (TerminalOptions.CursorBlinking ? "on" : "off"); }) },
                new PaletteEntryItem { Category = "Terminal", Title = "Terminal: Cycle Cursor Style (Block / Line / Underline)", Execute = () =>
                    ToggleTerminalFeel(() => "Terminal cursor style: " + TerminalOptions.CycleCursorStyle()) },
                new PaletteEntryItem { Category = "Terminal", Title = "Terminal: Toggle Smooth Scrolling", Execute = () =>
                    ToggleTerminalFeel(() => { TerminalOptions.SmoothScrolling = !TerminalOptions.SmoothScrolling; return "Terminal smooth scrolling: " + (TerminalOptions.SmoothScrolling ? "on" : "off"); }) },
            };
        }

        /// <summary>Applies a terminal setting change from the palette, persists it and reports it in the status bar.</summary>
        private void ToggleTerminalFeel(Func<string> change)
        {
            string msg = change();
            TerminalOptions.CopyTo(settings);
            try { settings.Save(); } catch { }
            ApplyTerminalFeelSettings();
            PrimaryEditor?.PublishStatus(msg);
        }
    }
}
