using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Run and Debug for the macOS/Linux workbench: DAP debugging through the shared DebugController (sidebar view,
    /// floating toolbar, DEBUG CONSOLE, gutter breakpoints, stopped-line markers, hover values, debugging status
    /// bar color) and zero-setup Python (interpreter status item + picker, Run / Debug Python File, editor ▶).
    /// </summary>
    public partial class MainWindow
    {
        private DebugController debugController;
        private AvaloniaDebugSidebar debugSidebar;
        private AvaloniaDebugConsole debugConsolePanel;
        private AvaloniaDebugToolbar debugToolbar;
        private Border debugNotification;
        private Border debugHoverBox;
        private TextBlock debugHoverText;
        private DispatcherTimer debugHoverTimer;
        private Point debugHoverPoint;
        private int debugHoverGeneration;
        private Button statusPythonBtn;
        private Button editorRunPythonBtn;
        private TerminalSessionEntry debugTerminal;
        private UiPost debugUiPost;
        private string selectedDebugConfigName;
        private bool debugWorkbenchInitialized;
        private bool pythonDiscoveryRequested;
        private EditorTabBuffer lastDebugDecoratedTab;

        /// <summary>When a debug session runs, F5/F10/F11 reach the workbench even from the terminal (VS Code skipShell).</summary>
        internal static volatile bool DebugKeysSkipShell;

        public DebugController Debugger => debugController;
        public AvaloniaDebugSidebar DebugSidebar => debugSidebar;
        public AvaloniaDebugConsole DebugConsole => debugConsolePanel;
        public AvaloniaDebugToolbar DebugToolbar => debugToolbar;
        public Button StatusPythonButton => statusPythonBtn;
        public Button EditorRunPythonButton => editorRunPythonBtn;
        public bool IsDebugHoverVisible => debugHoverBox != null && debugHoverBox.IsVisible;
        public string DebugHoverText => debugHoverText?.Text ?? "";
        public bool IsDebugNotificationVisible => debugNotification != null && debugNotification.IsVisible;
        public string DebugNotificationText { get; private set; } = "";
        public string DebugNotificationInstallCommand { get; private set; }

        private sealed class AvaloniaDebugHost : IDebugUiHost, IDebugTaskHost
        {
            private readonly MainWindow w;
            public AvaloniaDebugHost(MainWindow w) { this.w = w; }
            /// <summary>launch.json preLaunchTask / postDebugTask through the shared task chain runner.</summary>
            public void RunTaskByLabel(string label, Action<bool, string> completed) => w.RunTaskByLabel(label, completed);
            public void Post(Action action) => w.debugUiPost.Post(action);
            public Task<int?> RunInTerminalAsync(string title, string cwd, List<string> args, Dictionary<string, string> env) => Task.FromResult(w.RunDebuggeeInTerminal(title, cwd, args, env));
            public void RevealLocation(string path, int line)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var tab = w.OpenFileInTab(path, preview: false);
                if (tab != null && line > 0) w.PrimaryEditor.GoToLine(line);
            }
            public IList<EditorTabBuffer> GetOpenBuffers() => w.openTabs;
            public void RefreshEditors()
            {
                w.PrimaryEditor?.Canvas?.InvalidateVisual();
                w.SplitEditor?.Canvas?.InvalidateVisual();
            }
            public void ShowStatus(string message) => w.SetStatusMessage(message);
            public void ShowDebugError(string message, string hint, string installCommand) => w.ShowDebugNotification(message, hint, installCommand);
            public string WorkspaceRoot => string.IsNullOrEmpty(w.workspaceRoot) ? null : w.workspaceRoot;
            public IdePersistentSettings Settings => w.settings;
        }

        private void EnsureDebugController()
        {
            if (debugController != null) return;
            debugUiPost = UiPost.CaptureCurrent();
            debugController = new DebugController(new AvaloniaDebugHost(this));
            debugController.StateChanged += OnDebugStateChanged;
            PythonInterpreterService.BindSettings(settings);
        }

        /// <summary>Hook from BuildPrimarySidebar: the RUN AND DEBUG view.</summary>
        private Grid BuildDebugSidebarView()
        {
            EnsureDebugController();
            var grid = new Grid { IsVisible = false };
            debugSidebar = new AvaloniaDebugSidebar(debugController);
            debugSidebar.StartRequested += StartDebugging;
            debugSidebar.ConfigurationChosen += name =>
            {
                selectedDebugConfigName = name;
                if (!string.IsNullOrEmpty(workspaceRoot)) { settings.SetLastDebugConfiguration(workspaceRoot, name); try { settings.Save(); } catch { } }
            };
            debugSidebar.CreateLaunchJsonRequested += CreateLaunchJson;
            debugSidebar.OpenLaunchJsonRequested += () =>
            {
                string p = DebugLaunchConfigurations.LaunchJsonPath(workspaceRoot);
                if (p != null && File.Exists(p)) OpenFileInTab(p, preview: false); else CreateLaunchJson();
            };
            debugSidebar.EditBreakpointRequested += bp => PromptBreakpointOptions(bp.FilePath, bp.Line, null);
            debugSidebar.OpenLocationRequested += (path, line) => { if (OpenFileInTab(path, preview: false) != null) PrimaryEditor.GoToLine(line); };
            grid.Children.Add(debugSidebar);
            grid.PropertyChanged += (s, e) => { if (e.Property == IsVisibleProperty && grid.IsVisible) RefreshDebugConfigurations(); };
            return grid;
        }

        /// <summary>Hook from BuildBottomPanel: the DEBUG CONSOLE body.</summary>
        private Control BuildDebugConsolePanel()
        {
            EnsureDebugController();
            debugConsolePanel = new AvaloniaDebugConsole(debugController);
            return debugConsolePanel;
        }

        /// <summary>Hook from the constructor (after layout + theme): toolbar, status items, editor ▶, hover, keys.</summary>
        private void InitDebugWorkbench()
        {
            if (debugWorkbenchInitialized) return;
            debugWorkbenchInitialized = true;
            EnsureDebugController();

            debugToolbar = new AvaloniaDebugToolbar(debugController, () => debugController.Restart(), () => _ = debugController.StopAsync());
            Grid.SetColumnSpan(debugToolbar, 2);
            editorSplitGrid.Children.Add(debugToolbar);

            debugHoverText = new TextBlock { FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
            debugHoverBox = new Border
            {
                Child = debugHoverText,
                IsVisible = false,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(8, 4),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                ZIndex = 60
            };
            Grid.SetColumnSpan(debugHoverBox, 2);
            editorSplitGrid.Children.Add(debugHoverBox);
            debugHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            debugHoverTimer.Tick += (s, e) => { debugHoverTimer.Stop(); _ = ShowDebugHoverAsync(); };
            PrimaryEditor.Canvas.PointerMoved += OnEditorPointerMovedForDebugHover;
            PrimaryEditor.Canvas.PointerExited += (s, e) => { debugHoverTimer.Stop(); HideDebugHover(); };
            PrimaryEditor.BreakpointGutterClicked += OnBreakpointGutterClicked;
            SplitEditor.BreakpointGutterClicked += OnBreakpointGutterClicked;

            // Status bar: Python interpreter item, left of the language mode (like VS Code).
            if (statusLanguageBtn?.Parent is StackPanel rightRow)
            {
                statusPythonBtn = MakeStatusBarItem("Select Interpreter", null);
                statusPythonBtn.IsVisible = false;
                ToolTip.SetTip(statusPythonBtn, "Select Python Interpreter");
                statusPythonBtn.Click += (s, e) => OpenPythonInterpreterPicker();
                rightRow.Children.Insert(Math.Max(0, rightRow.Children.IndexOf(statusLanguageBtn)), statusPythonBtn);
            }

            // Editor title area: ▶ Run Python File (dropdown: Run / Debug), visible for .py files.
            if (tabStripBorder?.Child is Grid tabBarGrid)
            {
                var actions = tabBarGrid.Children.OfType<StackPanel>().FirstOrDefault(p => Grid.GetColumn(p) == 1);
                if (actions != null)
                {
                    var glyph = new DebugGlyph { Kind = "start", Width = 14, Height = 14 };
                    editorRunPythonBtn = new Button
                    {
                        Content = glyph,
                        Width = 26,
                        Height = 24,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent,
                        IsVisible = false
                    };
                    ToolTip.SetTip(editorRunPythonBtn, "Run Python File (right-click: Debug Python File)");
                    editorRunPythonBtn.Click += (s, e) => RunPythonFile();
                    editorRunPythonBtn.ContextMenu = new ContextMenu
                    {
                        ItemsSource = new object[]
                        {
                            MakeCtxItem("Run Python File", RunPythonFile),
                            MakeCtxItem("Debug Python File", DebugPythonFile)
                        }
                    };
                    actions.Children.Insert(0, editorRunPythonBtn);
                }
            }

            Opened += (s, e) =>
            {
                PythonInterpreterService.Changed -= OnPythonInterpretersChanged;
                PythonInterpreterService.Changed += OnPythonInterpretersChanged;
                PythonInterpreterService.BindSettings(settings);
            };
            Closed += (s, e) =>
            {
                PythonInterpreterService.Changed -= OnPythonInterpretersChanged;
                try { debugController.Session?.Dispose(); } catch { }
                DebugKeysSkipShell = false;
            };
            ApplyDebugTheme(currentTheme);
            UpdateDebugStatusItems();
        }

        private void OnPythonInterpretersChanged(string ws)
        {
            debugUiPost?.Post(UpdateDebugStatusItems);
        }

        /// <summary>Hook from ApplyTheme.</summary>
        private void ApplyDebugTheme(StudioTheme theme)
        {
            if (theme == null) return;
            debugSidebar?.ApplyTheme(theme);
            debugConsolePanel?.ApplyTheme(theme);
            debugToolbar?.ApplyTheme(theme);
            if (debugHoverBox != null)
            {
                debugHoverBox.Background = AvaloniaColorHelper.ToBrush(theme.FloatingBg);
                debugHoverBox.BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle);
                debugHoverText.Foreground = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            }
            if (statusPythonBtn != null) statusPythonBtn.Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.StatusBarForeground(theme)));
            if (debugNotification != null) WorkbenchStyles.ThemeNotification(debugNotification, theme);
            UpdateDebugStatusBarColor();
        }

        /// <summary>VS Code's debugging status bar (orange), tuned per light / dark theme.</summary>
        public static Color DebuggingStatusBarColor(StudioTheme theme) =>
            theme != null && theme.IsLight ? Color.FromRgb(214, 110, 52) : Color.FromRgb(204, 102, 51);

        public bool IsStatusBarInDebugColor { get; private set; }

        private void UpdateDebugStatusBarColor()
        {
            if (statusBarBorder == null || currentTheme == null) return;
            bool debugging = debugController != null && debugController.IsActive;
            IsStatusBarInDebugColor = debugging;
            statusBarBorder.Background = debugging ? new SolidColorBrush(DebuggingStatusBarColor(currentTheme)) : AvaloniaColorHelper.ToBrush(UiTokens.StatusBarBackground(currentTheme));
            if (debugging)
            {
                foreach (var b in new Control[] { statusBranchBtn, statusProblemsBtn, statusLnColBtn, statusSpacesBtn, statusEncodingBtn, statusEolBtn, statusLanguageBtn, statusAiModelBtn, statusPythonBtn })
                    if (b is TemplatedControl tc) tc.Foreground = Brushes.White;
                if (statusMessageLabel != null) statusMessageLabel.Foreground = Brushes.White;
            }
            else
            {
                var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.StatusBarForeground(currentTheme)));
                foreach (var b in new Control[] { statusBranchBtn, statusProblemsBtn, statusLnColBtn, statusSpacesBtn, statusEncodingBtn, statusEolBtn, statusLanguageBtn, statusAiModelBtn, statusPythonBtn })
                    if (b is TemplatedControl tc) tc.Foreground = fg;
                if (statusMessageLabel != null) statusMessageLabel.Foreground = fg;
            }
        }

        private void OnDebugStateChanged()
        {
            DebugKeysSkipShell = debugController.IsActive;
            UpdateDebugStatusBarColor();
            debugToolbar?.Refresh();
            HideDebugHover(); // any state change (step, continue, new stop) invalidates the shown value
            if (!string.IsNullOrEmpty(debugController.StatusText)) SetStatusMessage(debugController.StatusText);
            else if (!debugController.IsActive && statusMessageLabel != null && (statusMessageLabel.Text ?? "").StartsWith("Paused", StringComparison.Ordinal)) SetStatusMessage("");
        }

        /// <summary>Hook from UpdateStatusBar: interpreter item, editor ▶ and decorations of a newly active tab.</summary>
        private void UpdateDebugStatusItems()
        {
            if (!debugWorkbenchInitialized) return;
            bool isPy = activeTab != null && IsPythonBuffer(activeTab);
            if (editorRunPythonBtn != null) editorRunPythonBtn.IsVisible = isPy;
            if (statusPythonBtn != null)
            {
                statusPythonBtn.IsVisible = isPy;
                if (isPy)
                {
                    if (!pythonDiscoveryRequested || PythonInterpreterService.GetCached(workspaceRoot) == null)
                    {
                        if (!pythonDiscoveryRequested) { pythonDiscoveryRequested = true; PythonInterpreterService.RefreshInBackground(workspaceRoot); }
                    }
                    var sel = PythonInterpreterService.GetSelected(workspaceRoot);
                    statusPythonBtn.Content = new LiteralTextBlock(sel != null ? sel.DisplayLabel : "Select Interpreter") { FontSize = 11 };
                    ToolTip.SetTip(statusPythonBtn, sel != null ? sel.Path : "Select Python Interpreter");
                }
            }
            if (!ReferenceEquals(activeTab, lastDebugDecoratedTab))
            {
                lastDebugDecoratedTab = activeTab;
                if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) debugController.ApplyDecorations(activeTab);
            }
        }

        private static bool IsPythonBuffer(EditorTabBuffer b)
        {
            string lang = (b.Language ?? "").TrimStart('.').ToLowerInvariant();
            return lang == "py" || lang == "pyw" || lang == "python" || (b.FilePath ?? "").EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Workspace switch: rediscover interpreters lazily, refresh configurations.</summary>
        private void OnDebugWorkspaceChanged()
        {
            pythonDiscoveryRequested = false;
            selectedDebugConfigName = settings?.GetLastDebugConfiguration(workspaceRoot);
            RefreshDebugConfigurations();
            UpdateDebugStatusItems();
        }

        // ---- Configurations ----

        public void RefreshDebugConfigurations()
        {
            if (debugSidebar == null || debugController == null) return;
            if (selectedDebugConfigName == null && !string.IsNullOrEmpty(workspaceRoot)) selectedDebugConfigName = settings.GetLastDebugConfiguration(workspaceRoot);
            var cfgs = debugController.GetConfigurations(out string err);
            var names = cfgs.Select(c => c.Name).Distinct().ToList();
            names.Add(DebugController.ActiveFileConfigName);
            bool hasJson = !string.IsNullOrEmpty(workspaceRoot) && File.Exists(DebugLaunchConfigurations.LaunchJsonPath(workspaceRoot));
            debugSidebar.SetConfigurations(names, selectedDebugConfigName ?? names[0], hasJson, err);
            if (err == null)
            {
                debugSidebar.HintText.Text = hasJson
                    ? ""
                    : "Debug the active file (Python, Go, C/C++/Rust/Swift, C#) with F5, or customize Run and Debug with a launch.json.";
            }
        }

        public void CreateLaunchJson()
        {
            string fam = activeTab != null ? DebugLaunchConfigurations.FamilyForFile(activeTab.FilePath) : null;
            string path = DebugLaunchConfigurations.CreateLaunchJson(workspaceRoot, fam, out string err);
            if (path == null) { ShowDebugNotification(err, null, null); return; }
            OpenFileInTab(path, preview: false);
            RefreshDebugConfigurations();
        }

        // ---- Start / control ----

        /// <summary>F5 / Start: continue when stopped, else start the selected configuration (or Debug Active File).</summary>
        public void StartOrContinueDebugging()
        {
            if (debugController.IsActive) { debugController.ContinueOrPause(); return; }
            StartDebugging();
        }

        public void StartDebugging()
        {
            if (debugController.IsActive) { SetStatusMessage("A debug session is already running."); return; }
            if (debugSidebar?.SelectedConfiguration != null) selectedDebugConfigName = debugSidebar.SelectedConfiguration;
            var cfg = debugController.PickConfiguration(selectedDebugConfigName, activeTab?.FilePath, out string err);
            if (debugController.ShouldRunWithoutDebugging(cfg, activeTab?.FilePath))
            {
                RunActiveFileInTerminal();
                SetStatusMessage("No debugger for " + Path.GetExtension(activeTab?.FilePath ?? "") + " files: ran it in the terminal instead");
                return;
            }
            if (cfg == null) { ShowDebugNotification(err ?? "Nothing to debug.", null, null); return; }
            StartDebugConfiguration(cfg);
        }

        private void StartDebugConfiguration(DebugLaunchConfig cfg)
        {
            // Save the active file first (VS Code: debug.saveBeforeStart).
            if (activeTab != null && activeTab.IsDirty && !activeTab.IsUntitled && !string.IsNullOrEmpty(activeTab.FilePath)) SaveBufferToDisk(activeTab);
            HideDebugNotification();
            btmDebugConsoleTabBtn.IsVisible = true;
            string console = DapJson.Str(cfg.Properties, "console") ?? (cfg.AdapterFamily == "python" ? "integratedTerminal" : "internalConsole");
            if (console != "integratedTerminal") SwitchBottomTab("debug");
            if (sidebarBorder.IsVisible && activeSidebarView != "run") SwitchSidebarView("run");
            _ = debugController.StartAsync(cfg, activeTab?.FilePath);
        }

        public void DebugPythonFile()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath)) { SetStatusMessage("Save the file first to debug it."); return; }
            var cfg = DebugLaunchConfigurations.CreateDefaultForFile(activeTab.FilePath, workspaceRoot, out string err);
            if (cfg == null) { ShowDebugNotification(err, null, null); return; }
            if (debugController.IsActive) { SetStatusMessage("A debug session is already running."); return; }
            StartDebugConfiguration(cfg);
        }

        /// <summary>Workbench keys: F5/Shift+F5/Ctrl+Shift+F5/F6/F9/F10/F11/Shift+F11 (hook in HandleWorkbenchKeyEvent).</summary>
        private bool HandleDebugFunctionKey(Key key, KeyModifiers mods)
        {
            if (debugController == null) return false;
            bool shift = (mods & KeyModifiers.Shift) != 0;
            bool primary = MacKeymap.IsPrimary(mods) || (mods & KeyModifiers.Control) != 0;
            bool alt = (mods & KeyModifiers.Alt) != 0;
            if (alt) return false;
            bool active = debugController.IsActive;
            switch (key)
            {
                case Key.F5:
                    if (primary && shift) { if (active || debugController.LastConfig != null) debugController.Restart(); return true; }
                    if (shift) { if (active) _ = debugController.StopAsync(); return true; }
                    if (primary) { RunActiveFileInTerminal(); return true; } // Run Without Debugging
                    StartOrContinueDebugging();
                    return true;
                case Key.F6:
                    if (active && !shift && !primary) { debugController.Pause(); return true; }
                    return false;
                case Key.F9:
                    if (shift || primary) return false;
                    ToggleBreakpointAtCursor();
                    return true;
                case Key.F10:
                    if (active && !shift && !primary) { debugController.StepOver(); return true; }
                    return false;
                case Key.F11:
                    if (!active || primary) return false;
                    if (shift) debugController.StepOut(); else debugController.StepInto();
                    return true;
            }
            return false;
        }

        public void ToggleBreakpointAtCursor()
        {
            if (activeTab == null) { SetStatusMessage("No active editor for breakpoint"); return; }
            if (string.IsNullOrEmpty(activeTab.FilePath)) { SetStatusMessage("Save the file to set breakpoints"); return; }
            debugController.ToggleBreakpoint(activeTab, activeTab.CursorLine);
            PrimaryEditor.Canvas.InvalidateVisual();
            SplitEditor.Canvas.InvalidateVisual();
        }

        private void OnBreakpointGutterClicked(EditorTabBuffer buf, int line, bool context)
        {
            if (buf == null) return;
            if (string.IsNullOrEmpty(buf.FilePath))
            {
                if (!context) { if (!buf.Breakpoints.Remove(line)) buf.Breakpoints.Add(line); }
                return;
            }
            if (!context) { debugController.ToggleBreakpoint(buf, line); return; }
            var existing = debugController.Breakpoints.Find(buf.FilePath, line);
            var items = new List<object>();
            if (existing == null)
            {
                items.Add(MakeCtxItem("Add Breakpoint", () => debugController.ToggleBreakpoint(buf, line)));
                items.Add(MakeCtxItem("Add Conditional Breakpoint...", () => PromptBreakpointOptions(buf.FilePath, line, "condition")));
                items.Add(MakeCtxItem("Add Logpoint...", () => PromptBreakpointOptions(buf.FilePath, line, "log")));
            }
            else
            {
                items.Add(MakeCtxItem("Remove Breakpoint", () => debugController.RemoveBreakpoint(existing)));
                items.Add(MakeCtxItem("Edit Breakpoint...", () => PromptBreakpointOptions(buf.FilePath, line, null)));
                items.Add(MakeCtxItem(existing.Enabled ? "Disable Breakpoint" : "Enable Breakpoint", () => debugController.SetBreakpointEnabled(existing, !existing.Enabled)));
            }
            new ContextMenu { ItemsSource = items }.Open(PrimaryEditor.Canvas);
        }

        /// <summary>Condition / hit count / log message editor (VS Code's inline breakpoint widget, as a small modal).</summary>
        public void PromptBreakpointOptions(string path, int line, string focus)
        {
            var bp = debugController.Breakpoints.Find(path, line);
            Color fg = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            var stack = new StackPanel { Spacing = 6 };
            TextBox Field(string label, string value, string watermark)
            {
                stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(fg) });
                var tb = new TextBox { Text = value ?? "", Watermark = watermark, FontSize = 12 };
                stack.Children.Add(tb);
                return tb;
            }
            var cond = Field("Expression (break when true)", bp?.Condition, "e.g. x > 5");
            var hits = Field("Hit count", bp?.HitCondition, "e.g. 3 (or >= 3 / %2, adapter-specific)");
            var log = Field("Log message (makes it a logpoint; {expr} is interpolated)", bp?.LogMessage, "e.g. x is {x}");
            var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 4), Margin = new Thickness(0, 6, 0, 0) };
            void Apply()
            {
                ModalHost.CloseModal();
                debugController.SetBreakpointOptions(path, line, cond.Text, hits.Text, log.Text);
            }
            ok.Click += (s, e) => Apply();
            foreach (var tb in new[] { cond, hits, log })
                tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
            stack.Children.Add(ok);
            ModalHost.ShowModal("breakpoint-options", "Breakpoint at " + Path.GetFileName(path) + ":" + (line + 1), stack, 460);
            var target = focus == "log" ? log : cond;
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Loaded);
        }

        // ---- Notifications (non-modal, only after the user asked to debug) ----

        public void ShowDebugNotification(string message, string hint, string installCommand)
        {
            if (string.IsNullOrEmpty(message)) return;
            DebugNotificationText = message + (string.IsNullOrEmpty(hint) ? "" : "\n" + hint);
            DebugNotificationInstallCommand = installCommand;
            AppendOutputLog("[Debug] " + DebugNotificationText.Replace("\n", " "));
            SetStatusMessage(message);
            if (editorSplitGrid == null) return;
            if (debugNotification != null) editorSplitGrid.Children.Remove(debugNotification);
            var buttons = new List<Button>();
            if (!string.IsNullOrEmpty(installCommand))
            {
                var install = new Button { Content = "Install in Terminal" };
                ToolTip.SetTip(install, installCommand);
                install.Click += (s, e) => { HideDebugNotification(); RunCommandInTerminal(installCommand); };
                buttons.Add(install);
            }
            Control detail = string.IsNullOrEmpty(hint) ? null : new SelectableTextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall };
            debugNotification = WorkbenchStyles.BuildNotification(currentTheme, "error", new TextBlock { Text = message }, detail, buttons.ToArray(), true, HideDebugNotification);
            debugNotification.Margin = new Thickness(0, 0, 12, 12);
            debugNotification.ZIndex = 70;
            Grid.SetColumnSpan(debugNotification, 2);
            editorSplitGrid.Children.Add(debugNotification);
        }

        public void HideDebugNotification()
        {
            if (debugNotification != null) debugNotification.IsVisible = false;
        }

        /// <summary>Types a command into the active terminal (after a brand-new terminal's pending venv activation).</summary>
        public void RunCommandInTerminal(string cmd)
        {
            SwitchBottomTab("terminal");
            EnsureInitialTerminalSession();
            var ctrl = activeTerminalSession?.Control;
            if (ctrl == null) return;
            string pendingVenv = PythonVenvActivator.TakePendingActivation(ctrl.Session);
            if (pendingVenv != null) ctrl.WriteAutomatedInput(pendingVenv + "\r");
            ctrl.WriteAutomatedInput(cmd + "\r");
        }

        private TerminalShellFlavor FlavorOf(TerminalSessionEntry entry)
        {
            var s = entry?.Control?.Session;
            string shell = s != null ? (!string.IsNullOrEmpty(s.ShellExecutable) ? s.ShellExecutable : s.CommandLine) : entry?.ShellCommand;
            var f = PythonVenvActivator.DetectShellFlavor(shell ?? entry?.ShellCommand ?? "", s?.ShellName ?? entry?.Title ?? "");
            if (f == TerminalShellFlavor.Unknown || f == TerminalShellFlavor.Python)
                f = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TerminalShellFlavor.PowerShell : TerminalShellFlavor.Posix;
            return f;
        }

        /// <summary>runInTerminal: runs the debuggee in a dedicated, reused terminal named after the session.</summary>
        private int? RunDebuggeeInTerminal(string title, string cwd, List<string> args, Dictionary<string, string> env)
        {
            if (args == null || args.Count == 0) return null;
            if (debugTerminal == null || !terminalSessions.Contains(debugTerminal) || (debugTerminal.Control?.Session != null && !debugTerminal.Control.Session.IsRunning))
            {
                debugTerminal = CreateNewTerminalSession(null, !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd) ? cwd : null);
                RenameTerminalSession(debugTerminal, string.IsNullOrEmpty(title) ? "Debug Console" : title);
            }
            else ActivateTerminalSession(debugTerminal);
            SwitchBottomTab("terminal");
            var ctrl = debugTerminal.Control;
            string pendingVenv = PythonVenvActivator.TakePendingActivation(ctrl?.Session);
            if (pendingVenv != null) ctrl.WriteAutomatedInput(pendingVenv + "\r");
            string cmd = DebugTerminalCommands.Build(FlavorOf(debugTerminal), cwd, args, env);
            ctrl?.WriteAutomatedInput(cmd + "\r");
            return null;
        }

        // ---- Zero-setup Python ----

        /// <summary>Command line for "Run Python File" in the given terminal with the selected interpreter.</summary>
        private string BuildPythonRunCommand(string file, TerminalSessionEntry terminal)
        {
            var sel = PythonInterpreterService.GetSelected(workspaceRoot);
            string exe = sel != null ? sel.Path : PythonInterpreterService.FallbackCommand();
            return DebugTerminalCommands.BuildRun(FlavorOf(terminal), exe, file);
        }

        public void RunPythonFile()
        {
            if (activeTab == null) return;
            if (string.IsNullOrEmpty(activeTab.FilePath)) { SetStatusMessage("Save the file first to run it."); return; }
            if (activeTab.IsDirty) SaveBufferToDisk(activeTab);
            SwitchBottomTab("terminal");
            EnsureInitialTerminalSession();
            RunCommandInTerminal(BuildPythonRunCommand(activeTab.FilePath, activeTerminalSession));
        }

        public void OpenPythonInterpreterPicker()
        {
            var all = PythonInterpreterService.GetCached(workspaceRoot);
            var entries = new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Python", Title = "Enter interpreter path...", Execute = PromptInterpreterPath },
                new PaletteEntryItem { Category = "Python", Title = "Refresh interpreter list", Execute = () => { PythonInterpreterService.RefreshInBackground(workspaceRoot); SetStatusMessage("Discovering Python interpreters..."); } }
            };
            var selected = PythonInterpreterService.GetSelected(workspaceRoot);
            if (all == null)
            {
                pythonDiscoveryRequested = true;
                PythonInterpreterService.RefreshInBackground(workspaceRoot);
                SetStatusMessage("Discovering Python interpreters... (reopen the picker in a moment)");
            }
            else
            {
                foreach (var i in all)
                {
                    var info = i;
                    bool isSel = selected != null && selected.Path == info.Path;
                    entries.Add(new PaletteEntryItem
                    {
                        Category = (isSel ? "★ " : "") + (info.Kind ?? "Python"),
                        Title = "Python " + info.DisplayLabel,
                        stringSubtitleOrPath = info.Path,
                        Execute = () => SelectPythonInterpreter(info)
                    });
                }
                if (all.Count == 0)
                    entries.Add(new PaletteEntryItem { Category = "Python", Title = "No interpreters found — install Python 3 or enter a path", Execute = PromptInterpreterPath });
            }
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        public void SelectPythonInterpreter(PythonInterpreterInfo info)
        {
            if (info == null) return;
            PythonInterpreterService.Select(workspaceRoot, info);
            SetStatusMessage("Python interpreter: " + info.DisplayLabel + "  (" + info.Path + ")");
            UpdateDebugStatusItems();
        }

        private void PromptInterpreterPath()
        {
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                "Path to a Python interpreter (e.g. /usr/local/bin/python3 or .venv/bin/python):",
                "",
                currentTheme,
                path =>
                {
                    ModalHost.CloseModal();
                    string p = (path ?? "").Trim().Trim('"');
                    if (p.Length == 0) return;
                    if (!Path.IsPathRooted(p) && !string.IsNullOrEmpty(workspaceRoot)) p = Path.Combine(workspaceRoot, p);
                    if (!File.Exists(p)) { ShowDebugNotification("Interpreter not found: " + p, null, null); return; }
                    var env = PythonDiscoveryEnvironment.CreateDefault();
                    env.ProbeVersions = false;
                    SelectPythonInterpreter(PythonInterpreterDiscovery.FromPath(p, env));
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("python-interpreter-path", "Python: Select Interpreter", view, 520);
            tb.Focus();
        }

        // ---- Hover while stopped (does not interfere with editor / LSP hover otherwise) ----

        private void OnEditorPointerMovedForDebugHover(object sender, PointerEventArgs e)
        {
            if (debugController == null || !debugController.IsStopped) return;
            var pt = e.GetPosition(PrimaryEditor.Canvas);
            if (IsDebugHoverVisible && Math.Abs(pt.X - debugHoverPoint.X) < 4 && Math.Abs(pt.Y - debugHoverPoint.Y) < 4) return;
            debugHoverPoint = pt;
            HideDebugHover();
            debugHoverTimer.Stop();
            debugHoverTimer.Start();
        }

        /// <summary>Evaluates the expression under the given canvas point and shows the value (tests call it directly).</summary>
        public async Task ShowDebugHoverAsync(Point? canvasPoint = null)
        {
            if (canvasPoint.HasValue) debugHoverPoint = canvasPoint.Value;
            var buf = PrimaryEditor.CurrentBuffer;
            if (buf == null || !debugController.IsStopped) return;
            if (!PrimaryEditor.Canvas.TryGetDocPositionAt(debugHoverPoint, out int line, out int col)) return;
            string expr = DebugController.ExpressionAt(buf.Lines[line], col);
            if (string.IsNullOrEmpty(expr)) return;
            int gen = ++debugHoverGeneration;
            string value = await debugController.EvaluateForHoverAsync(expr);
            if (gen != debugHoverGeneration || value == null || !debugController.IsStopped) return;
            debugHoverText.Text = expr + " = " + value;
            var origin = PrimaryEditor.Canvas.TranslatePoint(new Point(0, 0), editorSplitGrid) ?? new Point(0, 0);
            double top = PrimaryEditor.Canvas.LineTopY(line) ?? debugHoverPoint.Y;
            double y = origin.Y + top + PrimaryEditor.Canvas.LineHeight + 2;
            debugHoverBox.Margin = new Thickness(Math.Max(0, origin.X + debugHoverPoint.X - 10), Math.Max(0, y), 0, 0);
            debugHoverBox.IsVisible = true;
        }

        private void HideDebugHover()
        {
            debugHoverGeneration++;
            if (debugHoverBox != null) debugHoverBox.IsVisible = false;
        }

        // ---- Command palette ----

        private List<PaletteEntryItem> BuildDebugPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Debug", Title = "Start Debugging", Shortcut = "F5", Execute = StartOrContinueDebugging },
                new PaletteEntryItem { Category = "Debug", Title = "Run Without Debugging", Shortcut = "Ctrl+F5", Execute = RunActiveFileInTerminal },
                new PaletteEntryItem { Category = "Debug", Title = "Stop", Shortcut = "Shift+F5", Execute = () => _ = debugController.StopAsync() },
                new PaletteEntryItem { Category = "Debug", Title = "Restart", Shortcut = "Ctrl+Shift+F5", Execute = () => debugController.Restart() },
                new PaletteEntryItem { Category = "Debug", Title = "Pause", Shortcut = "F6", Execute = () => debugController.Pause() },
                new PaletteEntryItem { Category = "Debug", Title = "Step Over", Shortcut = "F10", Execute = () => debugController.StepOver() },
                new PaletteEntryItem { Category = "Debug", Title = "Step Into", Shortcut = "F11", Execute = () => debugController.StepInto() },
                new PaletteEntryItem { Category = "Debug", Title = "Step Out", Shortcut = "Shift+F11", Execute = () => debugController.StepOut() },
                new PaletteEntryItem { Category = "Debug", Title = "Toggle Breakpoint", Shortcut = "F9", Execute = ToggleBreakpointAtCursor },
                new PaletteEntryItem { Category = "Debug", Title = "Add Conditional Breakpoint...", Execute = () => { if (activeTab?.FilePath != null) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine, "condition"); } },
                new PaletteEntryItem { Category = "Debug", Title = "Add Logpoint...", Execute = () => { if (activeTab?.FilePath != null) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine, "log"); } },
                new PaletteEntryItem { Category = "Debug", Title = "Remove All Breakpoints", Execute = () => debugController.Breakpoints.RemoveAll() },
                new PaletteEntryItem { Category = "Debug", Title = "Debug Active File", Execute = () => { selectedDebugConfigName = DebugController.ActiveFileConfigName; StartDebugging(); } },
                new PaletteEntryItem { Category = "Debug", Title = "Create launch.json", Execute = CreateLaunchJson },
                new PaletteEntryItem { Category = "Debug", Title = "Open Debug Console", Shortcut = "Ctrl+Shift+Y", Execute = () => { btmDebugConsoleTabBtn.IsVisible = true; SwitchBottomTab("debug"); } },
                new PaletteEntryItem { Category = "Python", Title = "Select Interpreter", Execute = OpenPythonInterpreterPicker },
                new PaletteEntryItem { Category = "Python", Title = "Run Python File", Execute = RunPythonFile },
                new PaletteEntryItem { Category = "Python", Title = "Debug Python File", Execute = DebugPythonFile },
            };
        }
    }

    /// <summary>Lets feature partials append command groups inside the palette's collection initializer.</summary>
    internal static class PaletteEntryListExtensions
    {
        public static void Add(this List<PaletteEntryItem> list, IEnumerable<PaletteEntryItem> items)
        {
            if (items != null) list.AddRange(items);
        }
    }
}
