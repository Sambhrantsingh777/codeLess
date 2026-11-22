using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// v3.3.1 macOS report: "Ctrl+Tab does not change the file opened" and "autocomplete while typing a filename for
    /// import". Ctrl+Tab = VS Code recently-used-editors picker from any focus; path / module completion in the suggest widget.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorSwitchAndImportCompletionHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private readonly string dir;
        private MainWindow window;

        public EditorSwitchAndImportCompletionHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_SwitchImport_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            ImportCompletionEngine.InvalidateCaches();
        }

        public void Dispose()
        {
            try
            {
                if (window != null)
                {
                    window.SuppressUnsavedPrompts = true;
                    window.Close();
                }
            }
            catch { }
            MainWindow.LanguageServersEnabledOverride = null;
            PythonModuleCompletion.LayoutOverride = null;
            ImportCompletionEngine.InvalidateCaches();
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void Pump(Func<bool> cond, int ms = 8000, string what = "condition")
        {
            ms = PerfBudget.Wait(ms);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return;
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(cond(), "Timed out waiting for " + what);
        }

        private string Write(string rel, string content = "")
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return p;
        }

        private EditorTabBuffer[] OpenFour()
        {
            var paths = new[] { "a.txt", "b.txt", "c.txt", "d.txt" }.Select(n => Write(n, n + "\n")).ToArray();
            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(dir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = paths.Select(p => window.OpenFileInTab(p, preview: false)).ToArray();
            Dispatcher.UIThread.RunJobs();
            return tabs;
        }

        private void CtrlTab(bool shift = false) =>
            window.KeyPress(Key.Tab, RawInputModifiers.Control | (shift ? RawInputModifiers.Shift : RawInputModifiers.None), PhysicalKey.Tab, null);

        private void ReleaseCtrl() => window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);

        // ------------------------------------------------------------------ Ctrl+Tab

        [AvaloniaFact]
        public void Ctrl_Tab_Tap_From_The_Editor_Switches_To_The_Most_Recently_Used_Editor()
        {
            var t = OpenFour();
            window.ActivateTab(t[0]); // MRU: a, d, c, b   (tab strip: a, b, c, d)
            Assert.Equal(new[] { t[0], t[3], t[2], t[1] }, window.EditorMruOrder.ToArray());
            window.PrimaryEditor.Canvas.Focus();
            string before = t[0].Lines[0];

            CtrlTab();
            Assert.True(window.IsEditorSwitcherOpen);
            Assert.Same(t[3], window.EditorSwitcherSelection);
            Assert.Equal(before, t[0].Lines[0]); // the editor never indents on Ctrl+Tab
            ReleaseCtrl();
            Assert.False(window.IsEditorSwitcherOpen);
            Assert.Same(t[3], window.ActiveTab); // MRU previous, not the tab-strip neighbour (b)

            // a second quick tap toggles back
            CtrlTab();
            ReleaseCtrl();
            Assert.Same(t[0], window.ActiveTab);
        }

        [AvaloniaFact]
        public void Holding_Ctrl_Cycles_The_Picker_Release_Opens_Shift_Goes_Back_Escape_Cancels()
        {
            var t = OpenFour(); // MRU: d, c, b, a
            window.PrimaryEditor.Canvas.Focus();
            CtrlTab();
            CtrlTab();
            Assert.Same(t[1], window.EditorSwitcherSelection);
            CtrlTab(shift: true);
            Assert.Same(t[2], window.EditorSwitcherSelection);
            window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
            Assert.Same(t[1], window.EditorSwitcherSelection);
            Assert.Same(t[3], window.ActiveTab); // nothing opens while Ctrl is held
            ReleaseCtrl();
            Assert.Same(t[1], window.ActiveTab);

            // Ctrl+Shift+Tab starts at the least recently used editor
            CtrlTab(shift: true);
            Assert.Same(t[0], window.EditorSwitcherSelection);
            window.KeyPress(Key.Escape, RawInputModifiers.Control, PhysicalKey.Escape, null);
            Assert.False(window.IsEditorSwitcherOpen);
            ReleaseCtrl();
            Assert.Same(t[1], window.ActiveTab);
        }

        [AvaloniaFact]
        public void Ctrl_Tab_Works_From_The_Terminal_And_From_Text_Boxes()
        {
            var t = OpenFour(); // active d, MRU previous c
            var term = window.ActiveTerminalSession?.Control;
            Assert.NotNull(term);
            // Key events that originate in the terminal (the shell gets every other Ctrl key)
            term.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control, Source = term });
            Assert.True(window.IsEditorSwitcherOpen);
            term.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.LeftCtrl, KeyModifiers = KeyModifiers.None, Source = term });
            Assert.Same(t[2], window.ActiveTab);

            // focus in a text box of the side bar / palette
            window.CommandPalette.SearchInput.Focus();
            Dispatcher.UIThread.RunJobs();
            CtrlTab();
            ReleaseCtrl();
            Assert.Same(t[3], window.ActiveTab);
        }

        [AvaloniaFact]
        public void Tab_Strip_Order_Shortcuts_Are_Unchanged_And_Close_Focuses_The_Recent_Editor()
        {
            var t = OpenFour();
            window.ActivateTab(t[0]);
            window.ActivateTab(t[2]); // MRU: c, a, d, b
            window.SimulateShortcut(Key.PageDown, KeyModifiers.Control);
            Assert.Same(t[3], window.ActiveTab); // tab strip: c -> d
            window.SimulateShortcut(Key.PageUp, KeyModifiers.Control);
            Assert.Same(t[2], window.ActiveTab);
            window.SimulateShortcut(Key.Right, KeyModifiers.Meta | KeyModifiers.Alt);
            Assert.Same(t[3], window.ActiveTab);
            Assert.False(window.IsEditorSwitcherOpen);

            // closing the active editor activates the most recently used one (VS Code), not its neighbour
            window.ActivateTab(t[0]); // MRU: a, d, c, b
            window.CloseTab(t[0], forceWithoutPrompt: true);
            Assert.Same(t[3], window.ActiveTab);
        }

        // ------------------------------------------------------------------ import completion

        private (EditorTabBuffer Tab, LspEditorBinding Binding, AvaloniaVirtualEditorCanvas Canvas) OpenEditor(string path)
        {
            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(dir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tab = window.OpenFileInTab(path, preview: false);
            Dispatcher.UIThread.RunJobs();
            window.PrimaryEditor.Canvas.Focus();
            return (tab, window.PrimaryLspBinding, window.PrimaryEditor.Canvas);
        }

        [AvaloniaFact]
        public void Typing_In_An_Import_String_Lists_Files_Folder_Accept_Keeps_The_Popup_Open()
        {
            Write("src/components/Button.tsx", "export const Button = 1;\n");
            Write("src/components/Card.tsx", "");
            Write("src/utils/index.ts", "");
            string main = Write("src/main.ts", "import B from '';\n");
            var (tab, binding, canvas) = OpenEditor(main);
            tab.CursorLine = 0;
            tab.CursorCol = "import B from '".Length;

            window.KeyTextInput(".");
            window.KeyTextInput("/");
            Pump(() => binding.IsCompletionVisible, what: "path completion after './'");
            Assert.Equal(new[] { "components/", "utils/" }, binding.Session.Visible.Select(i => i.Label).ToArray());

            window.KeyTextInput("c");
            Pump(() => binding.Session != null && binding.Session.Visible.Count == 1, what: "filtered to components/");
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("import B from './components/';", tab.Lines[0]);
            Pump(() => binding.IsCompletionVisible && binding.Session.Visible.Any(i => i.Label == "Button"), what: "folder accept re-opens with its contents");
            Assert.Equal(new[] { "Button", "Card" }, binding.Session.Visible.Select(i => i.Label).ToArray()); // .tsx omitted

            window.SimulateShortcut(Key.Down, KeyModifiers.None);
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("import B from './components/Card';", tab.Lines[0]);
            Assert.False(binding.IsCompletionVisible);
        }

        [AvaloniaFact]
        public void Switching_Editors_Closes_The_Suggest_List_Of_The_Previous_File()
        {
            Write("lib/util.ts", "");
            string other = Write("other.ts", "const x = 1;\n");
            string main = Write("main.ts", "import u from './';\n");
            var (tab, binding, canvas) = OpenEditor(other);
            tab = window.OpenFileInTab(main, preview: false);
            window.PrimaryEditor.Canvas.Focus();
            tab.CursorLine = 0;
            tab.CursorCol = "import u from './".Length;
            window.SimulateShortcut(Key.Space, KeyModifiers.Control); // Ctrl+Space inside the import string
            Pump(() => binding.IsCompletionVisible, what: "path completion via Ctrl+Space");
            Assert.Contains(binding.Session.Visible, i => i.Label == "lib/");
            CtrlTab();
            ReleaseCtrl();
            Assert.EndsWith("other.ts", window.ActiveTab.FilePath);
            Assert.False(binding.IsCompletionVisible);
            // Enter in the other file is a plain newline, not a stale completion
            int lines = window.ActiveTab.Lines.Count;
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal(lines + 1, window.ActiveTab.Lines.Count);
            Assert.DoesNotContain("lib/", string.Join("\n", window.ActiveTab.Lines));
        }

        [AvaloniaFact]
        public void Opening_Quote_Lists_Packages_And_Comments_Or_Plain_Strings_Never_Do()
        {
            Write("node_modules/react/package.json", "{}");
            Write("node_modules/@scope/kit/package.json", "{}");
            string main = Write("main.js", "\n// see './\nconst s = 'x/';\n");
            var (tab, binding, canvas) = OpenEditor(main);
            tab.CursorLine = 0;
            tab.CursorCol = 0;
            window.KeyTextInput("import React from '");
            Pump(() => binding.IsCompletionVisible, what: "packages after the opening quote");
            Assert.Contains(binding.Session.Visible, i => i.Label == "react");
            Assert.Contains(binding.Session.Visible, i => i.Label == "@scope/kit");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None);

            tab.CursorLine = 1;
            tab.CursorCol = tab.Lines[1].Length;
            window.KeyTextInput("s");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
            Assert.False(binding.IsCompletionVisible && binding.Session?.ImportContext != null);

            tab.CursorLine = 2;
            tab.CursorCol = tab.Lines[2].IndexOf("x/") + 2;
            window.KeyTextInput("a");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
            Assert.False(binding.IsCompletionVisible && binding.Session?.ImportContext != null);
        }

        [AvaloniaFact]
        public void Python_Import_Lists_Workspace_Site_Packages_And_Stdlib_Modules_And_From_Import_Names()
        {
            Write("mypkg/__init__.py", "");
            Write("mypkg/core.py", "class Engine:\n    pass\n\ndef start():\n    pass\n");
            Write("site/requests/__init__.py", "");
            string site = Path.Combine(dir, "site");
            PythonModuleCompletion.LayoutOverride = ws => new PythonEnvironmentLayout { Version = "3.12", SitePackages = new System.Collections.Generic.List<string> { site } };
            string main = Write("main.py", "\n");
            var (tab, binding, canvas) = OpenEditor(main);
            tab.CursorLine = 0;
            tab.CursorCol = 0;
            window.KeyTextInput("import ");
            Pump(() => binding.IsCompletionVisible, what: "module list after 'import '");
            var labels = binding.Session.Visible.Select(i => i.Label).ToList();
            Assert.Contains("mypkg", labels);
            Assert.Contains("requests", labels);
            Assert.Contains("os", labels);
            window.KeyTextInput("myp");
            Pump(() => binding.Session != null && binding.Session.Selected?.Label == "mypkg", what: "filtered to mypkg");
            window.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("import mypkg", tab.Lines[0]);

            tab.Lines[0] = "";
            tab.CursorCol = 0;
            window.KeyTextInput("from mypkg.core import ");
            Pump(() => binding.IsCompletionVisible && binding.Session.Visible.Any(i => i.Label == "Engine"), what: "names of mypkg.core");
            Assert.Equal(new[] { "Engine", "start" }, binding.Session.Visible.Select(i => i.Label).ToArray());
        }
    }
}
