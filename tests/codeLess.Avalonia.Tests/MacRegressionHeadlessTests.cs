using System;
using System.IO;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class MacRegressionHeadlessTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackupContent;
        private readonly bool _hadSettingsFile;

        public MacRegressionHeadlessTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _hadSettingsFile = !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath);
            _settingsBackupContent = _hadSettingsFile ? File.ReadAllText(_settingsFilePath) : null;
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = null;
            try
            {
                if (_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && _settingsBackupContent != null)
                    File.WriteAllText(_settingsFilePath, _settingsBackupContent);
                else if (!_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath))
                    File.Delete(_settingsFilePath);
            }
            catch { }
        }

        [AvaloniaFact]
        public void Editor_MacCmdShiftArrows_SelectToLineAndDocumentBoundaries()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_MacSel_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Sel.cs");
            File.WriteAllText(file, "class A\n{\n    int result = value + 1;\n\tint tabbed = 2;\n}\n");
            MacKeymap.ForceMacKeymapOverride = true;
            try
            {
                var window = new MainWindow(dir);
                window.Show();
                var tab = window.OpenFileInTab(file, preview: false);
                string line = tab.Lines[2];

                // Cmd+Shift+Right selects from the caret to the end of the logical line (not one word).
                tab.CursorLine = 2;
                tab.CursorCol = 8;
                tab.ClearSelection();
                window.SimulateShortcut(Key.Right, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(2, tab.CursorLine);
                Assert.Equal(line.Length, tab.CursorCol);
                Assert.Equal(line.Substring(8), tab.GetSelectedText());

                // Cmd+Shift+Left keeps the anchor and goes to the first non-whitespace char, then column 0.
                window.SimulateShortcut(Key.Left, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(4, tab.CursorCol);
                Assert.Equal(line.Substring(4, 4), tab.GetSelectedText());
                window.SimulateShortcut(Key.Left, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(0, tab.CursorCol);
                Assert.Equal(line.Substring(0, 8), tab.GetSelectedText());

                // Cmd+Right without Shift moves to line end and clears the selection.
                window.SimulateShortcut(Key.Right, KeyModifiers.Meta);
                Assert.Equal(line.Length, tab.CursorCol);
                Assert.False(tab.HasSelection());

                // Tab-indented line: smart home lands on the real first non-whitespace column (1, not 4).
                tab.CursorLine = 3;
                tab.CursorCol = tab.Lines[3].Length;
                string tabbed = tab.Lines[3];
                int firstNonWs = tabbed.Length - tabbed.TrimStart(' ', '	').Length;
                window.SimulateShortcut(Key.Left, KeyModifiers.Meta);
                Assert.Equal(firstNonWs, tab.CursorCol);

                // Option+Shift+Right is a word selection on macOS (not expand-selection, not navigate-forward).
                tab.CursorLine = 2;
                tab.CursorCol = 4;
                tab.ClearSelection();
                window.SimulateShortcut(Key.Right, KeyModifiers.Alt | KeyModifiers.Shift);
                Assert.Equal(2, tab.CursorLine);
                Assert.True(tab.CursorCol > 4 && tab.CursorCol <= 11);
                Assert.StartsWith("int", tab.GetSelectedText());

                // Cmd+Shift+Down / Cmd+Shift+Up select to document end / start.
                tab.CursorLine = 1;
                tab.CursorCol = 0;
                tab.ClearSelection();
                window.SimulateShortcut(Key.Down, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(tab.Lines.Count - 1, tab.CursorLine);
                Assert.True(tab.HasSelection());
                window.SimulateShortcut(Key.Up, KeyModifiers.Meta | KeyModifiers.Shift);
                Assert.Equal(0, tab.CursorLine);
                Assert.Equal(0, tab.CursorCol);
                Assert.True(tab.HasSelection());

                window.Close();
            }
            finally
            {
                MacKeymap.ForceMacKeymapOverride = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void Terminal_ShiftOrOptionEnter_SendsEscCr_PlainEnterSendsCr()
        {
            Assert.Equal("\x1b\r", AvaloniaTerminalGridCanvasProbe.Translate(Key.Enter, KeyModifiers.Shift));
            Assert.Equal("\x1b\r", AvaloniaTerminalGridCanvasProbe.Translate(Key.Enter, KeyModifiers.Alt));
            Assert.Equal("\r", AvaloniaTerminalGridCanvasProbe.Translate(Key.Enter, KeyModifiers.None));
            Assert.Equal("\x1b[C", AvaloniaTerminalGridCanvasProbe.Translate(Key.Right, KeyModifiers.None));
            Assert.Equal("\x03", AvaloniaTerminalGridCanvasProbe.Translate(Key.C, KeyModifiers.Control));
        }

        [AvaloniaFact]
        public void Terminal_RowsMatchRenderedArea_AndTextBoxIsCentredInCell()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            var window = new Window { Width = 640, Height = 80, Content = term };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            object canvas = typeof(AvaloniaTerminalEmulatorControl)
                .GetField("gridCanvas", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(term);
            Type ct = canvas.GetType();
            double cellH = (double)ct.GetField("CellH").GetValue(null);
            double padTop = (double)ct.GetField("PadTop").GetValue(null);
            int rows = (int)ct.GetMethod("CalcRows").Invoke(canvas, null);
            double top = (double)ct.GetProperty("TextTopOffset").GetValue(canvas);
            double lineH = (double)ct.GetProperty("TextLineHeight").GetValue(canvas);

            // A small (80px) panel must report the rows that actually fit, not a 24-row default,
            // and the buffer the PTY/renderer uses must match it.
            int expectedRows = Math.Max(4, (int)((80 - padTop * 2) / cellH));
            Assert.Equal(expectedRows, rows);
            Assert.Equal(expectedRows, term.ActiveBuffer.Rows);

            // Glyph box (also the cursor box) is vertically centred in the cell row.
            Assert.True(top >= 0);
            Assert.True(lineH > 0 && lineH <= cellH);
            Assert.True(Math.Abs((cellH - lineH) - 2 * top) < 0.001);

            window.Close();
        }

        [AvaloniaFact]
        public void Terminal_Scrollback_IsCappedAndTrimmed()
        {
            var vt = new Vt100TerminalBuffer(80, 24, false);
            var sb = new StringBuilder();
            for (int i = 0; i < 7000; i++) sb.Append("line ").Append(i).Append("\r\n");
            vt.FeedText(sb.ToString());
            Assert.True(vt.ScrollbackCount <= vt.MaxScrollbackLines);
            Assert.True(vt.MaxScrollbackLines <= 5000);
            string tail = vt.GetScrollbackAndScreenText(40);
            Assert.Contains("line 6999", tail);
        }

        [AvaloniaFact]
        public void Terminal_CharacterWidths_KeepCursorInSyncWithTuiApps()
        {
            var vt = new Vt100TerminalBuffer(80, 24, false);
            // Zero-width: variation selector and combining accent occupy no cell.
            vt.FeedText("⚠️x");
            Assert.Equal(2, vt.CursorCol);
            vt.FeedText("\r\né");
            Assert.Equal(1, vt.CursorCol);
            // Wide CJK glyph occupies two cells.
            vt.FeedText("\r\n你好");
            Assert.Equal(4, vt.CursorCol);
            Assert.Equal('你', vt.GetCell(2, 0).Char);
            // Astral emoji (surrogate pair) occupies two cells and keeps both halves.
            vt.FeedText("\r\n\U0001F680!");
            Assert.Equal(3, vt.CursorCol);
            Assert.True(char.IsHighSurrogate(vt.GetCell(3, 0).Char));
            Assert.True(char.IsLowSurrogate(vt.GetCell(3, 1).Char));
            // A wide glyph that does not fit in the last column wraps to the next line instead of splitting.
            var narrow = new Vt100TerminalBuffer(20, 6, false);
            narrow.FeedText(new string('a', 19) + "你");
            Assert.Equal(1, narrow.CursorRow);
            Assert.Equal(2, narrow.CursorCol);
            Assert.Equal('你', narrow.GetCell(1, 0).Char);
        }

        private static class AvaloniaTerminalGridCanvasProbe
        {
            public static string Translate(Key key, KeyModifiers mods)
            {
                Type ct = typeof(AvaloniaTerminalEmulatorControl).Assembly.GetType("codeLess.AvaloniaApp.AvaloniaTerminalGridCanvas");
                return (string)ct.GetMethod("TranslateKeyToVtSequence").Invoke(null, new object[] { key, mods, false });
            }
        }
    }
}
