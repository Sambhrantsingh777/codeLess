using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Headless wiring tests for Run and Debug + zero-setup Python on the Avalonia workbench.</summary>
    [Collection("SequentialWorkbench")]
    public class DebugWorkbenchHeadlessTests : IDisposable
    {
        private readonly string settingsPath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;

        public DebugWorkbenchHeadlessTests()
        {
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        }

        public void Dispose()
        {
            try
            {
                if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
        }

        private static string MakeWorkspace(out string py, out string cs)
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_dbgui_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            TestTrust.Trust(dir); // workspace-local tools / git / tasks need a trusted folder
            py = Path.Combine(dir, "prog.py");
            File.WriteAllText(py, "import sys\n\ndef main():\n    x = 42\n    return x\n\nmain()\n\nprint('bye')\n\nz = 1\n");
            cs = Path.Combine(dir, "Calc.cs");
            File.WriteAllText(cs, "public class Calc { }\n");
            return dir;
        }

        private static bool Pump(Func<bool> cond, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return true;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            return cond();
        }

        private static Point CanvasPoint(MainWindow w, double x, double y) =>
            w.PrimaryEditor.Canvas.TranslatePoint(new Point(x, y), w).Value;

        [AvaloniaFact]
        public void GlyphMargin_TogglesBreakpoints_LineNumbersStillSelect_F9_AndBreakpointsView()
        {
            string ws = MakeWorkspace(out string py, out _);
            var w = new MainWindow(ws);
            try
            {
                w.Show();
                var buf = w.OpenFileInTab(py);
                Dispatcher.UIThread.RunJobs();
                var canvas = w.PrimaryEditor.Canvas;
                double lh = canvas.LineHeight;

                // Left-most margin toggles a breakpoint on line 4 (0-based 3)...
                var p = CanvasPoint(w, 8, lh * 3.5);
                w.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
                w.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(3, buf.Breakpoints);
                Assert.Single(w.Debugger.Breakpoints.All());
                Assert.False(buf.HasSelection());

                // ...while the line-number area still selects the whole line (no breakpoint).
                var num = CanvasPoint(w, 30, lh * 1.5);
                w.MouseDown(num, MouseButton.Left, RawInputModifiers.None);
                w.MouseUp(num, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(1, buf.Breakpoints);
                Assert.True(buf.HasSelection());

                // F9 toggles at the caret line.
                buf.ClearSelection();
                buf.CursorLine = 6;
                Assert.True(w.SimulateShortcut(Key.F9, KeyModifiers.None));
                Assert.Contains(6, buf.Breakpoints);
                Assert.Equal(2, w.Debugger.Breakpoints.All().Count);

                // BREAKPOINTS view lists both; disabling turns the glyph gray.
                w.SwitchSidebarView("run");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, w.DebugSidebar.BreakpointsHost.Children.OfType<Grid>().Count());
                var bp = w.Debugger.Breakpoints.Find(py, 6);
                w.Debugger.SetBreakpointEnabled(bp, false);
                Assert.Equal(DebugBreakpointGlyph.Disabled, buf.GetBreakpointGlyph(6));

                // Clicking the glyph again removes it.
                w.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
                w.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(3, buf.Breakpoints);
                Assert.Single(w.Debugger.Breakpoints.All());
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void RunAndDebugView_ConfigPicker_CreateLaunchJson_PythonStatusItem_AndEditorRunButton()
        {
            string ws = MakeWorkspace(out string py, out string cs);
            var w = new MainWindow(ws);
            try
            {
                w.Show();
                w.SwitchSidebarView("run");
                Dispatcher.UIThread.RunJobs();
                var combo = w.DebugSidebar.ConfigCombo;
                Assert.Contains(DebugController.ActiveFileConfigName, combo.ItemsSource.Cast<string>());
                Assert.True(w.DebugSidebar.CreateLaunchJsonLink.IsVisible);

                w.OpenFileInTab(py);
                Dispatcher.UIThread.RunJobs();
                w.CreateLaunchJson();
                string launch = Path.Combine(ws, ".vscode", "launch.json");
                Assert.True(File.Exists(launch));
                Assert.Contains("debugpy", File.ReadAllText(launch));
                Assert.Contains("Python Debugger: Current File", combo.ItemsSource.Cast<string>());
                Assert.False(w.DebugSidebar.CreateLaunchJsonLink.IsVisible);

                // Python file: interpreter item + ▶ visible; C# file: hidden.
                w.OpenFileInTab(py);
                Dispatcher.UIThread.RunJobs();
                Assert.True(w.StatusPythonButton.IsVisible);
                Assert.True(w.EditorRunPythonButton.IsVisible);
                w.OpenFileInTab(cs);
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.StatusPythonButton.IsVisible);
                Assert.False(w.EditorRunPythonButton.IsVisible);

                // Selecting an interpreter persists it per workspace and updates the label.
                w.OpenFileInTab(py);
                string venvDir = Path.Combine(ws, ".venv");
                Directory.CreateDirectory(Path.Combine(venvDir, "bin"));
                File.WriteAllText(Path.Combine(venvDir, "pyvenv.cfg"), "home = /usr/bin\nversion = 3.12.4\n");
                string venvPy = Path.Combine(venvDir, "bin", "python");
                File.WriteAllText(venvPy, "#!");
                w.SelectPythonInterpreter(PythonInterpreterDiscovery.FromPath(venvPy, null));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(venvPy, w.CurrentSettings.GetPythonInterpreterFor(ws));
                Assert.Contains("3.12.4 ('.venv')", ((TextBlock)w.StatusPythonButton.Content).Text);
                Assert.Equal(venvDir, PythonInterpreterService.GetSelectedEnvironmentPath(ws));

                // Palette has the debug / python commands.
                var titles = w.BuildAllPaletteCommands().Select(c => c.DisplayLabel).ToList();
                Assert.Contains("Python: Select Interpreter", titles);
                Assert.Contains("Debug: Start Debugging", titles);
                Assert.Contains("Debug: Create launch.json", titles);
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void FullSession_WithScriptedAdapter_DrivesToolbarStatusColorVariablesConsoleHoverAndStepping()
        {
            string ws = MakeWorkspace(out string py, out _);
            using var fake = new FakeDapAdapter(py);
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "launch.json"),
                "{ // fake adapter over TCP\n \"configurations\": [ { \"name\": \"Fake\", \"type\": \"codeless\", \"request\": \"launch\", \"program\": \"${file}\", \"debugServer\": " + fake.Port + ", \"console\": \"internalConsole\", }, ], }");
            var w = new MainWindow(ws);
            try
            {
                w.Show();
                var buf = w.OpenFileInTab(py);
                w.SwitchSidebarView("run");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Fake", w.DebugSidebar.SelectedConfiguration);
                Color normal = ((ISolidColorBrush)GetStatusBar(w).Background).Color;

                Assert.True(w.SimulateShortcut(Key.F5, KeyModifiers.None));
                Assert.True(Pump(() => w.Debugger.IsStopped && w.Debugger.SelectedFrame != null, 8000), "never stopped");
                Assert.True(w.IsStatusBarInDebugColor);
                Assert.Equal(MainWindow.DebuggingStatusBarColor(w.CurrentTheme), ((ISolidColorBrush)GetStatusBar(w).Background).Color);
                Assert.True(w.DebugToolbar.IsVisible);
                Assert.Equal(2, buf.DebugStoppedLine);
                Assert.Equal("debug", w.ActiveBottomTab);

                // VARIABLES: first scope auto-expanded with x = 42; CALL STACK lists the frames.
                Assert.True(Pump(() => AllText(w.DebugSidebar.VariablesHost).Contains("x: 42"), 3000), AllText(w.DebugSidebar.VariablesHost));
                Assert.Contains("main", AllText(w.DebugSidebar.CallStackHost));

                // DEBUG CONSOLE REPL.
                w.DebugConsole.Input.Text = "x";
                w.DebugConsole.Input.Focus();
                w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.True(Pump(() => w.DebugConsole.Text.Contains("42\n"), 3000), w.DebugConsole.Text);
                Assert.Contains("hello from fake", w.DebugConsole.Text);

                // Hover over "x" on line 4 ("    x = 42") while stopped.
                var canvas = w.PrimaryEditor.Canvas;
                double xPx = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 4.5 * canvas.CharWidth;
                var t = w.ShowDebugHoverAsync(new Point(xPx, canvas.LineHeight * 3.5));
                Assert.True(Pump(() => t.IsCompleted, 3000));
                Assert.True(w.IsDebugHoverVisible);
                Assert.Equal("x = 42", w.DebugHoverText);

                // F10 steps (from the stopped frame line 3 to line 4).
                Assert.True(w.SimulateShortcut(Key.F10, KeyModifiers.None));
                Assert.True(Pump(() => w.Debugger.IsStopped && w.Debugger.SelectedFrame?.Line == 4, 5000));
                Assert.False(w.IsDebugHoverVisible);

                // Shift+F5 stops: status bar color and toolbar revert.
                Assert.True(w.SimulateShortcut(Key.F5, KeyModifiers.Shift));
                Assert.True(Pump(() => w.Debugger.State == DebugSessionState.Terminated, 5000));
                Assert.False(w.IsStatusBarInDebugColor);
                Assert.Equal(normal, ((ISolidColorBrush)GetStatusBar(w).Background).Color);
                Assert.False(w.DebugToolbar.IsVisible);
                Assert.Equal(-1, buf.DebugStoppedLine);
            }
            finally
            {
                try { w.Debugger.Session?.Dispose(); } catch { }
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void MissingDebugpy_ShowsNonModalHintWithInstallCommand()
        {
            string ws = MakeWorkspace(out string py, out _);
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "launch.json"),
                "{ \"configurations\": [ { \"name\": \"Py\", \"type\": \"debugpy\", \"request\": \"launch\", \"program\": \"${file}\", \"python\": \"" + Path.Combine(ws, "no-python").Replace("\\", "\\\\") + "\" } ] }");
            var w = new MainWindow(ws);
            try
            {
                w.Show();
                w.OpenFileInTab(py);
                w.StartDebugging();
                Assert.True(Pump(() => w.IsDebugNotificationVisible, 8000));
                Assert.Contains("debugpy is not installed", w.DebugNotificationText);
                Assert.EndsWith("-m pip install debugpy", w.DebugNotificationInstallCommand);
                Assert.False(w.IsModalOpen); // non-modal
                Assert.False(w.Debugger.IsActive);
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        private static Border GetStatusBar(MainWindow w) =>
            (Border)typeof(MainWindow).GetField("statusBarBorder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);

        private static string AllText(Control root)
        {
            var sb = new System.Text.StringBuilder();
            void Walk(object o)
            {
                switch (o)
                {
                    case TextBlock tb:
                        if (tb.Inlines != null && tb.Inlines.Count > 0) foreach (var r in tb.Inlines.OfType<global::Avalonia.Controls.Documents.Run>()) sb.Append(r.Text);
                        else sb.Append(tb.Text);
                        sb.Append('\n');
                        break;
                    case Panel p: foreach (var c in p.Children) Walk(c); break;
                    case ContentControl cc: Walk(cc.Content); break;
                    case Decorator d: Walk(d.Child); break;
                }
            }
            Walk(root);
            return sb.ToString();
        }
    }
}
