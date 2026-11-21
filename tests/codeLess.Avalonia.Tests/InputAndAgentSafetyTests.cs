using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class InputAndAgentSafetyTests
    {
        private static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [Fact]
        public void TerminalKeys_FollowXtermAndMacConventions()
        {
            // macOS editing conventions
            Assert.Equal("\x1b" + "b", AvaloniaTerminalGridCanvasProbe(Key.Left, KeyModifiers.Alt, false, true));
            Assert.Equal("\x1b" + "f", AvaloniaTerminalGridCanvasProbe(Key.Right, KeyModifiers.Alt, false, true));
            Assert.Equal("\x01", AvaloniaTerminalGridCanvasProbe(Key.Left, KeyModifiers.Meta, false, true));
            Assert.Equal("\x05", AvaloniaTerminalGridCanvasProbe(Key.Right, KeyModifiers.Meta, false, true));
            Assert.Equal("\x15", AvaloniaTerminalGridCanvasProbe(Key.Back, KeyModifiers.Meta, false, true));
            Assert.Null(AvaloniaTerminalGridCanvasProbe(Key.P, KeyModifiers.Meta, false, true));
            // xterm modifier encoding / keys that were missing
            Assert.Equal("\x1b[1;5D", AvaloniaTerminalGridCanvasProbe(Key.Left, KeyModifiers.Control, false, false));
            Assert.Equal("\x1b[1;2A", AvaloniaTerminalGridCanvasProbe(Key.Up, KeyModifiers.Shift, false, false));
            Assert.Equal("\x1bOH", AvaloniaTerminalGridCanvasProbe(Key.Home, KeyModifiers.None, true, false));
            Assert.Equal("\x1b[15~", AvaloniaTerminalGridCanvasProbe(Key.F5, KeyModifiers.None, false, false));
            Assert.Equal("\x1bOP", AvaloniaTerminalGridCanvasProbe(Key.F1, KeyModifiers.None, false, false));
            Assert.Equal("\x1b[2~", AvaloniaTerminalGridCanvasProbe(Key.Insert, KeyModifiers.None, false, false));
            Assert.Equal("\0", AvaloniaTerminalGridCanvasProbe(Key.Space, KeyModifiers.Control, false, false));
            Assert.Equal("\x03", AvaloniaTerminalGridCanvasProbe(Key.C, KeyModifiers.Control, false, true));
        }

        private static string AvaloniaTerminalGridCanvasProbe(Key key, KeyModifiers mods, bool appCursor, bool isMac)
        {
            Type ct = typeof(AvaloniaTerminalEmulatorControl).Assembly.GetType("codeLess.AvaloniaApp.AvaloniaTerminalGridCanvas");
            var m = ct.GetMethod("TranslateKeyToVtSequence", new[] { typeof(Key), typeof(KeyModifiers), typeof(bool), typeof(bool) });
            return (string)m.Invoke(null, new object[] { key, mods, appCursor, isMac });
        }

        [Fact]
        public void TerminalFocus_OnlyAllowlistedWorkbenchShortcutsAreIntercepted()
        {
            try
            {
                MacKeymap.ForceMacKeymapOverride = true;
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.P, KeyModifiers.Control)); // shell history
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.W, KeyModifiers.Control)); // delete word
                Assert.True(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.P, KeyModifiers.Meta));
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.C, KeyModifiers.Meta)); // terminal copy
                Assert.True(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.OemTilde, KeyModifiers.Control));

                MacKeymap.ForceMacKeymapOverride = false;
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.W, KeyModifiers.Control));
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.E, KeyModifiers.Control));
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.K, KeyModifiers.Control));
                Assert.True(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
                Assert.True(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.P, KeyModifiers.Control));
                Assert.False(MainWindow.IsWorkbenchShortcutAllowedFromTerminal(Key.V, KeyModifiers.Control | KeyModifiers.Shift));
            }
            finally { MacKeymap.ForceMacKeymapOverride = null; }
        }

        [Fact]
        public void Paste_NormalisesNewlines_AndCannotEscapeBracketedPaste()
        {
            Assert.Equal("a\rb\rc", Vt100TerminalBuffer.PreparePasteText("a\r\nb\nc", false));
            string br = Vt100TerminalBuffer.PreparePasteText("ls\x1b[201~rm -rf ~\n", true);
            Assert.StartsWith("\x1b[200~", br);
            Assert.EndsWith("\x1b[201~", br);
            Assert.Equal(1, br.Split(new[] { "\x1b[201~" }, StringSplitOptions.None).Length - 1);
        }

        [AvaloniaFact]
        public void TypingExit_DoesNotKillTerminalSession()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            bool exited = false;
            term.SessionExited += c => exited = true;
            term.WriteAutomatedInput("exit");
            term.WriteAutomatedInput("exit\r");
            Assert.False(exited);
        }

        [Fact]
        public void AgentSandbox_RejectsSymlinkEscape_AndNoWorkspace()
        {
            Assert.False(LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(null, "/etc/hosts"));
            if (!IsUnix) return;
            string root = Path.Combine(Path.GetTempPath(), "codeLess_sbx_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string outside = Path.Combine(Path.GetTempPath(), "codeLess_out_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
                Assert.True(LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(root, Path.Combine(root, "a", "new.txt")));
                Assert.False(LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(root, Path.Combine(root, "link", "secret.txt")));
                Assert.False(LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(root, Path.Combine(root, "..", Path.GetFileName(outside), "secret.txt")));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
        }

        [Fact]
        public void ProcessArgumentQuoting_RoundTripsTrickyShellCommands()
        {
            if (!IsUnix) return;
            string cmd = "printf '%s|' \"a b\" 'c\"d' \"e\\\\\"; echo";
            int code; bool to;
            string outp = LlmHarnessAndAgentEngine.RunProcessCaptureWithExitCode("/bin/sh", "-c " + LlmHarnessAndAgentEngine.QuoteProcessArgument(cmd), Path.GetTempPath(), 10000, out code, out to);
            Assert.Equal(0, code);
            Assert.Equal("a b|c\"d|e\\|", outp.Trim());
        }

        [Fact]
        public void ProcessCapture_KeepsPartialOutputOnTimeout_AndKillsTree()
        {
            if (!IsUnix) return;
            int code; bool timedOut;
            string outp = LlmHarnessAndAgentEngine.RunProcessCaptureWithExitCode("/bin/sh", "-c " + LlmHarnessAndAgentEngine.QuoteProcessArgument("echo PARTIAL_OUTPUT; sleep 30 & wait"), Path.GetTempPath(), 1500, out code, out timedOut);
            Assert.True(timedOut);
            Assert.Contains("PARTIAL_OUTPUT", outp);
        }

        [AvaloniaFact]
        public void AgentRunTerminal_IsNeverAutoApprovedHeadless_AndOverrideIsHonoured()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeLess_ap_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "a.txt"), "a");
            try
            {
                var w = new MainWindow(ws);
                w.Show();
                Assert.NotNull(w.HarnessEngine.OnRequestToolApproval);
                Assert.False(w.HarnessEngine.OnRequestToolApproval("run_terminal", "rm -rf ~"));
                Assert.True(w.HarnessEngine.OnRequestToolApproval("write_file", "staged diff"));
                w.AgentToolApprovalOverride = (t, d) => true;
                Assert.True(w.HarnessEngine.OnRequestToolApproval("run_terminal", "ls"));
                w.Close();
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [AvaloniaFact]
        public void MacEditor_CtrlEmacsKeys_MoveCaret_WhileCmdRemainsPrimary()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeLess_mk_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(ws);
            string f = Path.Combine(ws, "a.txt");
            File.WriteAllText(f, "hello world\nsecond\n");
            try
            {
                MacKeymap.ForceMacKeymapOverride = true;
                var w = new MainWindow(ws);
                w.SuppressUnsavedPrompts = true;
                w.Show();
                var tab = w.OpenFileInTab(f);
                tab.CursorLine = 0; tab.CursorCol = 3; tab.ClearSelection();
                var canvas = w.PrimaryEditor.Canvas;
                canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.E, KeyModifiers = KeyModifiers.Control });
                Assert.Equal(11, tab.CursorCol);                 // Ctrl+E = end of line (not Quick Open / select-all)
                canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
                Assert.Equal(0, tab.CursorCol);                  // Ctrl+A = line start
                Assert.False(tab.HasSelection());
                canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.K, KeyModifiers = KeyModifiers.Control });
                Assert.Equal("", tab.Lines[0]);                  // Ctrl+K = kill to end of line
                w.Close();
            }
            finally
            {
                MacKeymap.ForceMacKeymapOverride = null;
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }
}
