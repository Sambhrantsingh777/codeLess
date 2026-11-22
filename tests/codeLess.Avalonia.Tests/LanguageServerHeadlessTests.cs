using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>End-to-end LSP UI flow in the Avalonia workbench against the deterministic fake server.</summary>
    [Collection("SequentialWorkbench")]
    public class LanguageServerHeadlessTests : IDisposable
    {
        private readonly string settingsFilePath;
        private readonly string settingsBackup;
        private readonly bool hadSettingsFile;
        private readonly string dir;
        private MainWindow window;

        public LanguageServerHeadlessTests()
        {
            settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            hadSettingsFile = !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath);
            settingsBackup = hadSettingsFile ? File.ReadAllText(settingsFilePath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_LspUi_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try
            {
                if (window != null)
                {
                    window.SuppressUnsavedPrompts = true;
                    window.Close(); // OnClosed shuts the servers down
                }
            }
            catch { }
            MainWindow.LanguageServersEnabledOverride = null;
            MainWindow.LanguageServerSettingsOverride = null;
            try
            {
                if (hadSettingsFile && settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (!hadSettingsFile && !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void Pump(Func<bool> cond, int ms = 15000, string what = "condition")
        {
            ms = PerfBudget.Wait(ms); // slow shared CI runners need longer waits (this is a timeout, not a perf assertion)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(cond(), "Timed out waiting for " + what);
        }

        private bool Setup(string overrides = null)
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return false; // no python3 on this machine
            MainWindow.LanguageServersEnabledOverride = true;
            MainWindow.LanguageServerSettingsOverride = () => LanguageServerUserSettings.Parse(overrides ?? ("python = " + line), false);
            window = new MainWindow(dir) { Width = 1300, Height = 860 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return true;
        }

        [AvaloniaFact]
        public void Diagnostics_Completion_Hover_Signature_Definition_Rename_Format_And_Status_Work_In_The_Editor()
        {
            string main = Path.Combine(dir, "main.py");
            File.WriteAllText(main, "def helper(a, b):\n    return a\n\nx = helper(1, 2)   \nBAD = 2\n");
            string other = Path.Combine(dir, "other.py");
            File.WriteAllText(other, "from main import helper\n");
            if (!Setup()) return;
            Assert.NotNull(window.LanguageServers);
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            // Status bar item: starting -> running server name.
            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "status bar shows the running server");

            // Diagnostics merged with the built-in ones, exact range, survive the analyzer re-run on edits.
            Pump(() => tab.Diagnostics.Any(d => d.IsLanguageServer), what: "LSP diagnostics in the buffer");
            var diag = tab.Diagnostics.First(d => d.IsLanguageServer);
            Assert.Equal(5, diag.Line);
            Assert.Equal(4, diag.EndColumn);
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
            Assert.Contains(tab.Diagnostics, d => d.IsLanguageServer && d.Message == "BAD is not allowed");

            // As-you-type completion: typing "al" on a new last line opens the popup filtered to alpha/alphabet.
            canvas.Focus();
            tab.CursorLine = tab.Lines.Count - 1;
            tab.CursorCol = 0;
            window.KeyTextInput("a");
            window.KeyTextInput("l");
            Pump(() => binding.IsCompletionVisible && binding.Session.Visible.Count == 2, what: "completion popup");
            Assert.Equal(new[] { "alpha", "alphabet" }, binding.Session.Visible.Select(i => i.Label).ToArray());
            // Resolved documentation appears for the selected item.
            Pump(() => binding.Completion.DocumentationText.Contains("Resolved docs for alpha"), what: "resolved docs");
            // Down + Enter inserts the snippet with the placeholder selected; the popup closes.
            window.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal("alphabet", binding.Session.Selected.Label);
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Pump(() => tab.Lines[tab.CursorLine] == "alphabet(x)", what: "completion accepted");
            Assert.False(binding.IsCompletionVisible);
            Assert.True(tab.HasSelection());
            Assert.True(tab.IsDirty);

            // Escape closes an open popup; Ctrl+Space reopens it.
            tab.ClearSelection();
            tab.CursorCol = tab.Lines[tab.CursorLine].Length;
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            canvas.Focus();
            window.SimulateShortcut(Key.Space, KeyModifiers.Control);
            Pump(() => binding.IsCompletionVisible, what: "Ctrl+Space completion");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None);
            Assert.False(binding.IsCompletionVisible);

            // Signature help on '(' with the active parameter following ','.
            window.KeyTextInput("helper(");
            Pump(() => binding.SignatureText.StartsWith("func(a, b)"), what: "signature help");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None);
            Pump(() => binding.SignatureText == "", what: "signature hidden");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None); // close completion if it opened for "helper"

            // Hover over "helper" in "x = helper(1, 2)".
            int xLine = tab.Lines.FindIndex(l => l.StartsWith("x = helper"));
            double cx = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + (6 + 0.5) * canvas.CharWidth;
            double cy = (xLine + 0.5) * canvas.LineHeight;
            var hoverTask = binding.RequestHoverAsync(new Point(cx, cy));
            Pump(() => hoverTask.IsCompleted && binding.HoverText.Contains("Docs for helper"), what: "hover tip");
            Assert.Contains("def helper()", binding.HoverText);
            // Hover over the BAD diagnostic shows its message.
            int badLine = tab.Lines.FindIndex(l => l.StartsWith("BAD"));
            var diagHover = binding.RequestHoverAsync(new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 1.5 * canvas.CharWidth, (badLine + 0.5) * canvas.LineHeight));
            Pump(() => diagHover.IsCompleted && binding.HoverText.Contains("BAD is not allowed"), what: "diagnostic hover");

            // F12 goes through the server (LocationLink) to the definition.
            tab.ClearSelection();
            tab.CursorLine = xLine;
            tab.CursorCol = 6;
            window.GoToDefinitionAtCursor();
            Pump(() => ReferenceEquals(window.ActiveTab, tab) && tab.CursorLine == 0 && tab.CursorCol == 4, what: "definition navigation");

            // Rename across the open buffer and a file on disk; one undo step in the buffer.
            var renameTask = window.ApplyRenameAsync(tab, main, 0, 5, "assist");
            Pump(() => renameTask.IsCompleted, what: "rename");
            Assert.NotNull(renameTask.Result);
            Assert.True(renameTask.Result.Success, renameTask.Result.Summary);
            Assert.Equal("def assist(a, b):", tab.Lines[0]);
            Assert.Contains("x = assist(1, 2)   ", tab.Lines);
            Assert.Equal("from main import assist\n", File.ReadAllText(other));
            Assert.True(tab.Undo());
            Assert.Equal("def helper(a, b):", tab.Lines[0]);
            Assert.True(tab.Redo());
            canvas.CommitExternalEdit();

            // Format Document strips trailing whitespace through the server.
            window.FormatDocumentOrSelection(false);
            Pump(() => tab.Lines.Contains("x = assist(1, 2)") && !tab.Lines.Any(l => l.EndsWith(" ")), what: "format document");

            // Outline symbols come from the server.
            var symTask = binding.RefreshSymbolsNowAsync();
            Pump(() => symTask.IsCompleted, what: "document symbols");
            Assert.Contains(tab.Symbols, s => s.Name == "assist" && s.Kind == "fn");

            // Closing the tab closes the document on the server and clears its diagnostics.
            window.SuppressUnsavedPrompts = true;
            window.CloseTab(tab, forceWithoutPrompt: true);
            Pump(() => LspDiagnosticsStore.Count(main) == 0, what: "diagnostics cleared on close");
            Assert.Equal("", window.LanguageServerStatusText);
        }

        [AvaloniaFact]
        public void Missing_Server_Shows_Status_And_One_Install_Hint_And_F12_Falls_Back_To_Builtin_Resolver()
        {
            string main = Path.Combine(dir, "app.py");
            File.WriteAllText(main, "def run():\n    pass\n\nrun()\n");
            if (!Setup("python = codeless-no-such-language-server --stdio")) return;
            var tab = window.OpenFileInTab(main, preview: false);
            Pump(() => window.LanguageServerStatusText.StartsWith("{ } No Python server"), what: "not-installed status");
            Pump(() => window.LanguageServerHintText.Contains("No Python language server found"), what: "install hint");
            // F12 still works through the built-in resolver.
            tab.CursorLine = 3;
            tab.CursorCol = 1;
            window.GoToDefinitionAtCursor();
            Pump(() => tab.CursorLine == 0, what: "fallback definition");
        }
    }
}
