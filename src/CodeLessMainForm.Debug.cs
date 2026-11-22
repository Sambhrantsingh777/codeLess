using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Run and Debug for the Windows (WinForms) workbench, on the shared DebugController: RUN AND DEBUG sidebar
    /// (configurations, VARIABLES / WATCH / CALL STACK / BREAKPOINTS), floating debug toolbar, DEBUG CONSOLE with REPL,
    /// debug keys, debugging status bar color, hover values while stopped, and zero-setup Python (interpreter status
    /// item + picker, Run / Debug Python File). Strict C# 5 (legacy csc).
    /// </summary>
    public partial class CodeLessMainForm
    {
        private DebugController debugController;
        private Panel debugSidebarPanel;
        private ComboBox debugConfigCombo;
        private Label debugHintLabel;
        private LinkLabel debugCreateLaunchLink;
        private TreeView debugVariablesTree;
        private TreeView debugWatchTree;
        private TextBox debugWatchInput;
        private TreeView debugCallStackTree;
        private ListView debugBreakpointsList;
        private Panel debugToolbarPanel;
        private Panel debugConsolePanel;
        private RichTextBox debugConsoleOutput;
        private TextBox debugConsoleInput;
        private Label statusPythonItem;
        private ToolTip debugHoverTip;
        private Timer debugHoverTimer;
        private Point debugHoverPoint;
        private int debugHoverGeneration;
        private TerminalSessionState debugTerminalState;
        private string selectedDebugConfigName;
        private int debugReplHistoryIndex = -1;
        private bool suppressDebugListEvents;
        private bool pythonDiscoveryRequested;
        private EditorTabBuffer lastDebugDecoratedTab;
        private DapStackFrameInfo variablesShownForFrame;
        private readonly HashSet<string> debugExpandedPaths = new HashSet<string>(StringComparer.Ordinal);
        private static bool debugStatusBarActive;

        public DebugController Debugger { get { return debugController; } }

        private sealed class WinFormsDebugHost : IDebugUiHost, IDebugTaskHost
        {
            private readonly CodeLessMainForm f;
            public WinFormsDebugHost(CodeLessMainForm f) { this.f = f; }

            /// <summary>launch.json preLaunchTask / postDebugTask through the shared task chain runner.</summary>
            public void RunTaskByLabel(string label, Action<bool, string> completed)
            {
                f.RunTaskByLabelWin(label, completed);
            }

            public void Post(Action action)
            {
                if (action == null || f.IsDisposed) return;
                try
                {
                    if (f.IsHandleCreated) f.BeginInvoke(action);
                    else action();
                }
                catch { }
            }

            public Task<int?> RunInTerminalAsync(string title, string cwd, List<string> args, Dictionary<string, string> env)
            {
                return Task.FromResult(f.RunDebuggeeInTerminal(title, cwd, args, env));
            }

            public void RevealLocation(string path, int line)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                EditorTabBuffer tab = f.OpenFileInTab(path);
                if (tab != null && line > 0) f.editorControl.JumpToLine(line);
            }

            public IList<EditorTabBuffer> GetOpenBuffers() { return f.openTabs; }

            public void RefreshEditors()
            {
                if (f.editorControl != null) f.editorControl.Invalidate(true);
                if (f.splitEditorControl != null) f.splitEditorControl.Invalidate(true);
            }

            public void ShowStatus(string message)
            {
                if (f.statusLeftLabel != null) f.statusLeftLabel.Text = message ?? "";
            }

            public void ShowDebugError(string message, string hint, string installCommand)
            {
                f.ShowDebugNotification(message, hint, installCommand);
            }

            public string WorkspaceRoot { get { return string.IsNullOrEmpty(f.workspaceRoot) ? null : f.workspaceRoot; } }
            public IdePersistentSettings Settings { get { return f.settings; } }
        }

        /// <summary>Hook from the constructor (after BuildWorkbenchLayout).</summary>
        private void InitDebugWorkbench()
        {
            if (debugController != null) return;
            debugController = new DebugController(new WinFormsDebugHost(this));
            PythonInterpreterService.BindSettings(settings);
            PythonInterpreterService.Changed += OnPythonInterpretersChangedWinForms;
            EditorDebugHooks.BreakpointToggled += OnEditorBreakpointToggledWinForms;
            this.FormClosed += delegate
            {
                PythonInterpreterService.Changed -= OnPythonInterpretersChangedWinForms;
                EditorDebugHooks.BreakpointToggled -= OnEditorBreakpointToggledWinForms;
                try { if (debugController.Session != null) debugController.Session.Dispose(); } catch { }
            };

            BuildDebugSidebarPanel();
            BuildDebugConsolePanel();
            BuildDebugToolbar();
            InstallDebugActivityButton();
            BuildPythonStatusItem();

            debugHoverTip = new ToolTip { UseAnimation = false, UseFading = false, ShowAlways = true };
            debugHoverTimer = new Timer { Interval = 350 };
            debugHoverTimer.Tick += delegate { debugHoverTimer.Stop(); ShowDebugHover(); };
            if (editorControl != null && editorControl.Canvas != null)
            {
                editorControl.Canvas.MouseMove += OnEditorMouseMoveForDebugHover;
                editorControl.Canvas.MouseLeave += delegate { debugHoverTimer.Stop(); HideDebugHover(); };
            }

            debugController.StateChanged += OnDebugStateChangedWinForms;
            debugController.CallStackChanged += delegate { RebuildDebugCallStack(); RebuildDebugVariables(false); };
            debugController.BreakpointsChanged += RebuildDebugBreakpoints;
            debugController.WatchesChanged += RebuildDebugWatches;
            debugController.ConsoleAppended += AppendDebugConsoleEntry;
            debugController.ConsoleCleared += delegate { if (debugConsoleOutput != null) debugConsoleOutput.Clear(); };
            ApplyDebugTheme(activeTheme);
        }

        private void OnPythonInterpretersChangedWinForms(string ws)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { UpdateDebugStatusItems(activeTab); }));
            }
            catch { }
        }

        private void OnEditorBreakpointToggledWinForms(EditorTabBuffer buf, int line)
        {
            if (buf == null || !openTabs.Contains(buf)) return;
            if (string.IsNullOrEmpty(buf.FilePath)) return;
            debugController.OnBufferBreakpointToggled(buf, line);
        }

        // ---------------- Theme ----------------

        public static Color DebuggingStatusBarColor(StudioTheme theme)
        {
            return theme != null && theme.IsLight ? Color.FromArgb(214, 110, 52) : Color.FromArgb(204, 102, 51);
        }

        /// <summary>Hook from ResolveStatusBarBackColor: the orange debugging status bar while a session runs.</summary>
        private static bool TryGetDebugStatusBarColor(StudioTheme theme, out Color color)
        {
            color = DebuggingStatusBarColor(theme);
            return debugStatusBarActive;
        }

        private void ApplyDebugTheme(StudioTheme t)
        {
            if (t == null || debugSidebarPanel == null) return;
            Color bg = t.BgSidebar, fg = t.FgPrimary, fg2 = t.FgSecondary;
            foreach (Control c in new Control[] { debugSidebarPanel, debugVariablesTree, debugWatchTree, debugCallStackTree, debugBreakpointsList, debugHintLabel })
            {
                if (c == null) continue;
                c.BackColor = bg;
                c.ForeColor = c == debugHintLabel ? fg2 : fg;
            }
            foreach (Control c in debugSidebarPanel.Controls)
                ApplyDebugThemeRecursive(c, t);
            if (debugCreateLaunchLink != null) { debugCreateLaunchLink.LinkColor = t.AccentPrimary; debugCreateLaunchLink.ActiveLinkColor = t.AccentPrimary; }
            if (debugConsolePanel != null)
            {
                debugConsolePanel.BackColor = t.BgWorkbench;
                debugConsoleOutput.BackColor = t.BgWorkbench;
                debugConsoleOutput.ForeColor = t.FgPrimary;
                debugConsoleInput.BackColor = t.BgElevated;
                debugConsoleInput.ForeColor = t.FgPrimary;
            }
            if (debugToolbarPanel != null) { debugToolbarPanel.BackColor = t.BgElevated; debugToolbarPanel.Invalidate(true); }
            if (debugWatchInput != null) { debugWatchInput.BackColor = t.BgElevated; debugWatchInput.ForeColor = t.FgPrimary; }
            if (debugConfigCombo != null) { debugConfigCombo.BackColor = t.BgElevated; debugConfigCombo.ForeColor = t.FgPrimary; }
        }

        private void ApplyDebugThemeRecursive(Control c, StudioTheme t)
        {
            if (c is Label && "debug_section".Equals(c.Tag)) { c.ForeColor = t.FgSecondary; c.BackColor = t.BgSidebar; }
            foreach (Control child in c.Controls) ApplyDebugThemeRecursive(child, t);
        }

        // ---------------- Sidebar ----------------

        private void InstallDebugActivityButton()
        {
            if (activityBar == null) return;
            foreach (Control c in activityBar.Controls)
            {
                // Make room after Source Control (VS Code order: Explorer, Search, SCM, Run and Debug, Extensions).
                if ("activity_icon".Equals(c.Tag) && c.Top >= 140 && c.Top < 390) c.Top += 44;
            }
            Button runBtn = CreateActivityButton("run", "Run and Debug (Ctrl+Shift+D)", 140, "run");
            runBtn.Click += delegate
            {
                if (leftSidebar.Visible && activeSidebarView == "run") { ToggleLeftSidebarVisibility(); return; }
                ShowDebugSidebar();
            };
            activityBar.Controls.Add(runBtn);
        }

        public void ShowDebugSidebar()
        {
            leftSidebar.Visible = true;
            if (leftSplitter != null) leftSplitter.Visible = true;
            SwitchSidebarView("run");
        }

        /// <summary>Hook from SwitchSidebarView.</summary>
        private void OnSidebarViewSwitchedForDebug(string view)
        {
            if (debugSidebarPanel == null) return;
            debugSidebarPanel.Visible = view == "run";
            if (view == "run")
            {
                sidebarHeaderLabel.Text = "RUN AND DEBUG";
                debugSidebarPanel.BringToFront();
                RefreshDebugConfigurations();
            }
        }

        private Label MakeDebugSectionHeader(string title, Control body)
        {
            // Section header: drawn chevron + 11 px semibold uppercase title (no text-glyph chevrons).
            Label header = new Label
            {
                Text = "▾ " + title,
                Dock = DockStyle.Top,
                Height = UiTokens.SectionHeaderHeight,
                Padding = new Padding(6, 4, 0, 0),
                Font = new Font("Segoe UI Semibold", UiTokens.Pt(UiTokens.FontHeader), FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = "debug_section"
            };
            header.Paint += (s, e) =>
            {
                e.Graphics.Clear(header.BackColor);
                bool open = !(header.Text ?? "").StartsWith("▸", StringComparison.Ordinal);
                Color fg = header.ForeColor;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(fg, 1.5f))
                {
                    int cx = 12, cy = header.Height / 2;
                    if (open) e.Graphics.DrawLines(p, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
                    else e.Graphics.DrawLines(p, new[] { new Point(cx - 2, cy - 4), new Point(cx + 2, cy), new Point(cx - 2, cy + 4) });
                }
                e.Graphics.SmoothingMode = SmoothingMode.Default;
                TextRenderer.DrawText(e.Graphics, title.ToUpperInvariant(), header.Font, new Rectangle(22, 0, Math.Max(10, header.Width - 24), header.Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            header.Click += delegate
            {
                body.Visible = !body.Visible;
                header.Text = (body.Visible ? "▾ " : "▸ ") + title;
                header.Invalidate();
            };
            return header;
        }

        private TreeView MakeDebugTree(int height)
        {
            TreeView tv = new TreeView
            {
                Dock = DockStyle.Top,
                Height = height,
                BorderStyle = BorderStyle.None,
                ShowLines = false,
                ShowRootLines = true,
                HideSelection = false,
                FullRowSelect = true,
                Font = new Font("Consolas", 8.6f),
                ItemHeight = 18
            };
            return tv;
        }

        private void BuildDebugSidebarPanel()
        {
            debugSidebarPanel = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true };

            Panel top = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(8, 4, 8, 2) };
            Button start = new Button { Text = "▶", Width = 28, Height = 24, Location = new Point(8, 4), FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(56, 170, 90), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            start.FlatAppearance.BorderSize = 0;
            start.Click += delegate { StartDebugging(); };
            debugConfigCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(40, 5), Width = 150, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            debugConfigCombo.SelectedIndexChanged += delegate
            {
                if (suppressDebugListEvents) return;
                selectedDebugConfigName = debugConfigCombo.SelectedItem as string;
                if (!string.IsNullOrEmpty(workspaceRoot) && settings != null)
                {
                    settings.SetLastDebugConfiguration(workspaceRoot, selectedDebugConfigName);
                    try { settings.Save(); } catch { }
                }
            };
            Button gear = new Button { Text = "", Width = 26, Height = 24, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            gear.FlatAppearance.BorderSize = 0;
            gear.Paint += (s, e) => CodeLessVectorIcons.DrawIcon(e.Graphics, "gear", new Rectangle((gear.Width - 16) / 2, (gear.Height - 16) / 2, 16, 16), gear.ForeColor, 1.4f);
            gear.Click += delegate
            {
                string p = DebugLaunchConfigurations.LaunchJsonPath(workspaceRoot);
                if (p != null && File.Exists(p)) OpenFileInTab(p); else CreateLaunchJson();
            };
            top.Resize += delegate
            {
                gear.Location = new Point(Math.Max(200, top.Width - 34), 4);
                debugConfigCombo.Width = Math.Max(80, gear.Left - 44);
            };
            ToolTip tt = new ToolTip();
            tt.SetToolTip(start, "Start Debugging (F5)");
            tt.SetToolTip(gear, "Open launch.json");
            debugHintLabel = new Label { Location = new Point(8, 32), AutoSize = false, Height = 22, Width = 220, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, Font = new Font("Segoe UI", 8f), UseMnemonic = false };
            debugCreateLaunchLink = new LinkLabel { Text = "create a launch.json file", Location = new Point(8, 54), AutoSize = true, Font = new Font("Segoe UI", 8f) };
            debugCreateLaunchLink.LinkClicked += delegate { CreateLaunchJson(); };
            top.Controls.AddRange(new Control[] { start, debugConfigCombo, gear, debugHintLabel, debugCreateLaunchLink });

            debugVariablesTree = MakeDebugTree(190);
            debugVariablesTree.BeforeExpand += OnDebugTreeBeforeExpand;
            debugVariablesTree.AfterExpand += delegate (object s, TreeViewEventArgs e) { debugExpandedPaths.Add(e.Node.FullPath); };
            debugVariablesTree.AfterCollapse += delegate (object s, TreeViewEventArgs e) { debugExpandedPaths.Remove(e.Node.FullPath); };

            Panel watchBody = new Panel { Dock = DockStyle.Top, Height = 120 };
            debugWatchTree = MakeDebugTree(96);
            debugWatchTree.Dock = DockStyle.Fill;
            debugWatchTree.BeforeExpand += OnDebugTreeBeforeExpand;
            debugWatchTree.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete && debugWatchTree.SelectedNode != null && debugWatchTree.SelectedNode.Tag is DebugWatchItem)
                {
                    debugController.RemoveWatch((DebugWatchItem)debugWatchTree.SelectedNode.Tag);
                    e.Handled = true;
                }
            };
            debugWatchInput = new TextBox { Dock = DockStyle.Bottom, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8.6f) };
            SetCueBanner(debugWatchInput, "Add expression to watch (Enter)");
            debugWatchInput.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string text = debugWatchInput.Text;
                    debugWatchInput.Text = "";
                    debugController.AddWatch(text);
                }
            };
            watchBody.Controls.Add(debugWatchTree);
            watchBody.Controls.Add(debugWatchInput);

            debugCallStackTree = MakeDebugTree(130);
            debugCallStackTree.NodeMouseClick += delegate (object s, TreeNodeMouseClickEventArgs e)
            {
                DapStackFrameInfo fr = e.Node.Tag as DapStackFrameInfo;
                if (fr != null) debugController.SelectFrame(fr);
                DapThreadInfo th = e.Node.Tag as DapThreadInfo;
                if (th != null && !debugController.FramesByThread.ContainsKey(th.Id)) debugController.LoadThreadFrames(th.Id);
            };

            debugBreakpointsList = new ListView
            {
                Dock = DockStyle.Top,
                Height = 130,
                View = View.Details,
                CheckBoxes = true,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 8.4f)
            };
            debugBreakpointsList.Columns.Add("bp", 230);
            debugBreakpointsList.Resize += delegate { if (debugBreakpointsList.Columns.Count > 0) debugBreakpointsList.Columns[0].Width = Math.Max(120, debugBreakpointsList.ClientSize.Width - 4); };
            debugBreakpointsList.ItemChecked += delegate (object s, ItemCheckedEventArgs e)
            {
                if (suppressDebugListEvents) return;
                DebugSourceBreakpoint bp = e.Item.Tag as DebugSourceBreakpoint;
                if (bp != null) { debugController.SetBreakpointEnabled(bp, e.Item.Checked); return; }
                DapExceptionFilter ef = e.Item.Tag as DapExceptionFilter;
                if (ef != null) debugController.SetExceptionFilter(ef, e.Item.Checked);
            };
            debugBreakpointsList.DoubleClick += delegate
            {
                if (debugBreakpointsList.SelectedItems.Count == 0) return;
                DebugSourceBreakpoint bp = debugBreakpointsList.SelectedItems[0].Tag as DebugSourceBreakpoint;
                if (bp != null && OpenFileInTab(bp.FilePath) != null) editorControl.JumpToLine(bp.Line + 1);
            };
            debugBreakpointsList.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                ListViewItem hit = debugBreakpointsList.GetItemAt(e.X, e.Y);
                DebugSourceBreakpoint bp = hit != null ? hit.Tag as DebugSourceBreakpoint : null;
                ContextMenuStrip menu = CreateAutoDisposingContextMenu();
                if (bp != null)
                {
                    menu.Items.Add(CreateMenuItem("Edit Condition / Hit Count / Log Message...", "", delegate { PromptBreakpointOptions(bp.FilePath, bp.Line); }));
                    menu.Items.Add(CreateMenuItem("Remove Breakpoint", "", delegate { debugController.RemoveBreakpoint(bp); }));
                    menu.Items.Add(new ToolStripSeparator());
                }
                menu.Items.Add(CreateMenuItem("Enable All Breakpoints", "", delegate { debugController.Breakpoints.SetAllEnabled(true); }));
                menu.Items.Add(CreateMenuItem("Disable All Breakpoints", "", delegate { debugController.Breakpoints.SetAllEnabled(false); }));
                menu.Items.Add(CreateMenuItem("Remove All Breakpoints", "", delegate { debugController.Breakpoints.RemoveAll(); }));
                menu.Show(debugBreakpointsList, e.Location);
            };

            // Dock=Top stacks by reverse index: add the visually last section first.
            List<Control> visualOrder = new List<Control>
            {
                top,
                MakeDebugSectionHeader("VARIABLES", debugVariablesTree), debugVariablesTree,
                MakeDebugSectionHeader("WATCH", watchBody), watchBody,
                MakeDebugSectionHeader("CALL STACK", debugCallStackTree), debugCallStackTree,
                MakeDebugSectionHeader("BREAKPOINTS", debugBreakpointsList), debugBreakpointsList
            };
            for (int i = visualOrder.Count - 1; i >= 0; i--) debugSidebarPanel.Controls.Add(visualOrder[i]);
            leftSidebar.Controls.Add(debugSidebarPanel);
            debugSidebarPanel.BringToFront();
            RebuildDebugBreakpoints();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessageCue(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox tb, string text)
        {
            tb.HandleCreated += delegate
            {
                try { SendMessageCue(tb.Handle, 0x1501, (IntPtr)1, text); } catch { }
            };
        }

        public void RefreshDebugConfigurations()
        {
            if (debugConfigCombo == null) return;
            if (selectedDebugConfigName == null && !string.IsNullOrEmpty(workspaceRoot) && settings != null)
                selectedDebugConfigName = settings.GetLastDebugConfiguration(workspaceRoot);
            string err;
            List<DebugLaunchConfig> cfgs = debugController.GetConfigurations(out err);
            List<string> names = new List<string>();
            foreach (DebugLaunchConfig c in cfgs) if (!names.Contains(c.Name)) names.Add(c.Name);
            names.Add(DebugController.ActiveFileConfigName);
            suppressDebugListEvents = true;
            debugConfigCombo.Items.Clear();
            foreach (string n in names) debugConfigCombo.Items.Add(n);
            int idx = selectedDebugConfigName != null ? names.IndexOf(selectedDebugConfigName) : -1;
            debugConfigCombo.SelectedIndex = idx >= 0 ? idx : 0;
            suppressDebugListEvents = false;
            bool hasJson = !string.IsNullOrEmpty(workspaceRoot) && File.Exists(DebugLaunchConfigurations.LaunchJsonPath(workspaceRoot));
            debugCreateLaunchLink.Visible = !hasJson;
            debugHintLabel.Text = err ?? (hasJson ? "" : "Debug the active file with F5, or customize:");
        }

        public void CreateLaunchJson()
        {
            string fam = activeTab != null ? DebugLaunchConfigurations.FamilyForFile(activeTab.FilePath) : null;
            string err;
            string path = DebugLaunchConfigurations.CreateLaunchJson(workspaceRoot, fam, out err);
            if (path == null) { ShowDebugNotification(err, null, null); return; }
            OpenFileInTab(path);
            RefreshDebugConfigurations();
        }

        // ---------------- Tree population ----------------

        private static TreeNode LoadingNode() { return new TreeNode("Loading...") { Tag = "loading" }; }

        private TreeNode MakeVariableNode(DapVariableInfo v)
        {
            string val = (v.Value ?? "").Replace("\n", "⏎");
            if (val.Length > 300) val = val.Substring(0, 300) + "…";
            TreeNode n = new TreeNode(v.Name + ": " + val) { Tag = v, Name = v.Name };
            n.ToolTipText = string.IsNullOrEmpty(v.Type) ? v.Value : v.Type + ": " + v.Value;
            if (v.VariablesReference > 0) n.Nodes.Add(LoadingNode());
            return n;
        }

        private void OnDebugTreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode node = e.Node;
            if (node.Nodes.Count != 1 || !"loading".Equals(node.Nodes[0].Tag)) return;
            int reference = 0;
            DapScopeInfo sc = node.Tag as DapScopeInfo;
            DapVariableInfo v = node.Tag as DapVariableInfo;
            DebugWatchItem w = node.Tag as DebugWatchItem;
            if (sc != null) reference = sc.VariablesReference;
            else if (v != null) reference = v.VariablesReference;
            else if (w != null && w.Result != null) reference = w.Result.VariablesReference;
            if (reference <= 0) { node.Nodes.Clear(); return; }
            debugController.GetVariablesAsync(reference).ContinueWith(delegate (Task<List<DapVariableInfo>> t)
            {
                List<DapVariableInfo> vars = t.IsFaulted ? new List<DapVariableInfo>() : t.Result;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (node.TreeView == null) return;
                        node.TreeView.BeginUpdate();
                        node.Nodes.Clear();
                        int count = 0;
                        foreach (DapVariableInfo child in vars)
                        {
                            if (count++ >= 500) { node.Nodes.Add(new TreeNode("... " + (vars.Count - 500) + " more")); break; }
                            node.Nodes.Add(MakeVariableNode(child));
                        }
                        if (vars.Count == 0) node.Nodes.Add(new TreeNode("(empty)"));
                        node.TreeView.EndUpdate();
                        foreach (TreeNode child in node.Nodes)
                            if (debugExpandedPaths.Contains(child.FullPath) && child.Nodes.Count > 0) child.Expand();
                    }));
                }
                catch { }
            });
        }

        private void RebuildDebugVariables(bool force)
        {
            if (debugVariablesTree == null) return;
            DapStackFrameInfo frame = debugController.IsStopped ? debugController.SelectedFrame : null;
            if (!force && frame != null && ReferenceEquals(frame, variablesShownForFrame) && debugVariablesTree.Nodes.Count > 0) return;
            variablesShownForFrame = frame;
            debugVariablesTree.Nodes.Clear();
            if (frame == null)
            {
                if (debugController.IsActive) debugVariablesTree.Nodes.Add(new TreeNode(debugController.IsStopped ? "No frame selected." : "Running..."));
                return;
            }
            DapStackFrameInfo requested = frame;
            debugController.GetScopesAsync(frame).ContinueWith(delegate (Task<List<DapScopeInfo>> t)
            {
                List<DapScopeInfo> scopes = t.IsFaulted ? new List<DapScopeInfo>() : t.Result;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (!ReferenceEquals(requested, variablesShownForFrame)) return;
                        debugVariablesTree.Nodes.Clear();
                        for (int i = 0; i < scopes.Count; i++)
                        {
                            DapScopeInfo sc = scopes[i];
                            TreeNode n = new TreeNode(sc.Name) { Tag = sc, Name = sc.Name, NodeFont = new Font("Segoe UI", 8.4f, FontStyle.Bold) };
                            if (sc.VariablesReference > 0) n.Nodes.Add(LoadingNode());
                            debugVariablesTree.Nodes.Add(n);
                            bool expand = !sc.Expensive && (i == 0 || debugExpandedPaths.Contains(n.FullPath));
                            if (expand && n.Nodes.Count > 0) n.Expand();
                        }
                        if (scopes.Count == 0) debugVariablesTree.Nodes.Add(new TreeNode("No variables."));
                    }));
                }
                catch { }
            });
        }

        private void RebuildDebugWatches()
        {
            if (debugWatchTree == null) return;
            debugWatchTree.BeginUpdate();
            debugWatchTree.Nodes.Clear();
            foreach (DebugWatchItem w in debugController.Watches)
            {
                string val = w.Result == null ? (debugController.IsStopped ? "…" : "not available")
                    : (w.Result.Success ? w.Result.Result : w.Result.Error);
                TreeNode n = new TreeNode(w.Expression + ": " + (val ?? "").Replace("\n", "⏎")) { Tag = w };
                if (w.Result != null && w.Result.Success && w.Result.VariablesReference > 0) n.Nodes.Add(LoadingNode());
                debugWatchTree.Nodes.Add(n);
            }
            debugWatchTree.EndUpdate();
        }

        private void RebuildDebugCallStack()
        {
            if (debugCallStackTree == null) return;
            debugCallStackTree.BeginUpdate();
            debugCallStackTree.Nodes.Clear();
            if (debugController.IsActive)
            {
                if (!debugController.IsStopped)
                {
                    debugCallStackTree.Nodes.Add(new TreeNode((debugController.SessionName ?? "Session") + "   RUNNING"));
                }
                else
                {
                    foreach (DapThreadInfo th in debugController.Threads)
                    {
                        List<DapStackFrameInfo> frames;
                        bool has = debugController.FramesByThread.TryGetValue(th.Id, out frames);
                        string state = th.Id == debugController.SelectedThreadId
                            ? "   PAUSED ON " + ((debugController.Session != null ? debugController.Session.StopReason : null) ?? "breakpoint").ToUpperInvariant()
                            : "   PAUSED";
                        TreeNode tn = new TreeNode(th.Name + state) { Tag = th };
                        if (has)
                        {
                            foreach (DapStackFrameInfo f in frames)
                            {
                                string loc = f.HasSource ? (f.SourceName ?? Path.GetFileName(f.SourcePath)) + ":" + f.Line : (f.SourceName ?? "unknown source");
                                TreeNode fn = new TreeNode((f.Name ?? "?") + "   " + loc) { Tag = f, ToolTipText = f.HasSource ? f.SourcePath + ":" + f.Line : null };
                                if (ReferenceEquals(f, debugController.SelectedFrame)) fn.NodeFont = new Font("Consolas", 8.6f, FontStyle.Bold);
                                if (!f.HasSource) fn.ForeColor = activeTheme != null ? activeTheme.FgMuted : Color.Gray;
                                tn.Nodes.Add(fn);
                            }
                        }
                        debugCallStackTree.Nodes.Add(tn);
                        if (has) tn.Expand();
                    }
                }
            }
            debugCallStackTree.EndUpdate();
        }

        private void RebuildDebugBreakpoints()
        {
            if (debugBreakpointsList == null) return;
            suppressDebugListEvents = true;
            debugBreakpointsList.BeginUpdate();
            debugBreakpointsList.Items.Clear();
            foreach (DapExceptionFilter ef in debugController.ExceptionFilters)
            {
                ListViewItem it = new ListViewItem(ef.Label) { Tag = ef, Checked = ef.Enabled };
                debugBreakpointsList.Items.Add(it);
            }
            foreach (DebugSourceBreakpoint bp in debugController.Breakpoints.All())
            {
                ListViewItem it = new ListViewItem(DebugBreakpointStore.Describe(bp)) { Tag = bp, Checked = bp.Enabled };
                it.ToolTipText = bp.FilePath + ":" + (bp.Line + 1) + (string.IsNullOrEmpty(bp.VerifyMessage) ? "" : "\n" + bp.VerifyMessage);
                if (bp.Verified.HasValue && !bp.Verified.Value) it.ForeColor = activeTheme != null ? activeTheme.FgMuted : Color.Gray;
                debugBreakpointsList.Items.Add(it);
            }
            debugBreakpointsList.EndUpdate();
            suppressDebugListEvents = false;
        }

        /// <summary>Condition / hit count / log message editor.</summary>
        public void PromptBreakpointOptions(string path, int line)
        {
            DebugSourceBreakpoint bp = debugController.Breakpoints.Find(path, line);
            using (Form dlg = new Form())
            {
                dlg.Text = "Breakpoint at " + Path.GetFileName(path) + ":" + (line + 1);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(440, 196);
                if (activeTheme != null) { dlg.BackColor = activeTheme.BgSidebar; dlg.ForeColor = activeTheme.FgPrimary; }
                string[] labels = new string[] { "Expression (break when true):", "Hit count (e.g. 3):", "Log message ({expr} interpolated; makes a logpoint):" };
                string[] values = new string[] { bp != null ? bp.Condition : "", bp != null ? bp.HitCondition : "", bp != null ? bp.LogMessage : "" };
                TextBox[] boxes = new TextBox[3];
                for (int i = 0; i < 3; i++)
                {
                    dlg.Controls.Add(new Label { Text = labels[i], Location = new Point(12, 10 + i * 52), AutoSize = true, UseMnemonic = false });
                    boxes[i] = new TextBox { Text = values[i] ?? "", Location = new Point(12, 28 + i * 52), Width = 414 };
                    if (activeTheme != null) { boxes[i].BackColor = activeTheme.BgElevated; boxes[i].ForeColor = activeTheme.FgPrimary; }
                    dlg.Controls.Add(boxes[i]);
                }
                Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(270, 164), Width = 75 };
                Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(351, 164), Width = 75 };
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    debugController.SetBreakpointOptions(path, line, boxes[0].Text, boxes[1].Text, boxes[2].Text);
            }
        }

        // ---------------- Debug console ----------------

        private void BuildDebugConsolePanel()
        {
            if (bottomDockPanel == null) return;
            debugConsolePanel = new Panel { Dock = DockStyle.Fill, Visible = false };
            debugConsoleOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                DetectUrls = false,
                WordWrap = true
            };
            debugConsoleInput = new TextBox { Dock = DockStyle.Bottom, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f) };
            SetCueBanner(debugConsoleInput, "Evaluate expression in the selected frame (Enter). Up/Down: history");
            debugConsoleInput.KeyDown += OnDebugConsoleInputKey;
            debugConsolePanel.Controls.Add(debugConsoleOutput);
            debugConsolePanel.Controls.Add(debugConsoleInput);
            bottomDockPanel.Controls.Add(debugConsolePanel);
            debugConsolePanel.BringToFront();
        }

        private void OnDebugConsoleInputKey(object sender, KeyEventArgs e)
        {
            List<string> hist = debugController.ReplHistory;
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                string text = debugConsoleInput.Text;
                debugConsoleInput.Text = "";
                debugReplHistoryIndex = -1;
                debugController.SubmitRepl(text);
            }
            else if (e.KeyCode == Keys.Up && hist.Count > 0)
            {
                e.SuppressKeyPress = true;
                debugReplHistoryIndex = debugReplHistoryIndex < 0 ? hist.Count - 1 : Math.Max(0, debugReplHistoryIndex - 1);
                debugConsoleInput.Text = hist[debugReplHistoryIndex];
                debugConsoleInput.SelectionStart = debugConsoleInput.Text.Length;
            }
            else if (e.KeyCode == Keys.Down && debugReplHistoryIndex >= 0)
            {
                e.SuppressKeyPress = true;
                debugReplHistoryIndex++;
                if (debugReplHistoryIndex >= hist.Count) { debugReplHistoryIndex = -1; debugConsoleInput.Text = ""; }
                else debugConsoleInput.Text = hist[debugReplHistoryIndex];
                debugConsoleInput.SelectionStart = debugConsoleInput.Text.Length;
            }
        }

        private Color DebugConsoleColor(string category)
        {
            StudioTheme t = activeTheme;
            if (t == null) return Color.Gainsboro;
            switch (category)
            {
                case "stderr":
                case "error": return Color.FromArgb(241, 76, 76);
                case "console":
                case "important": return t.AccentPrimary;
                case "input": return t.FgSecondary;
                case "result": return t.TokenString;
                default: return t.FgPrimary;
            }
        }

        private void AppendDebugConsoleEntry(DebugConsoleEntry e)
        {
            if (debugConsoleOutput == null || debugConsoleOutput.IsDisposed) return;
            if (debugConsoleOutput.TextLength > 400000)
            {
                debugConsoleOutput.Select(0, 100000);
                debugConsoleOutput.SelectedText = "";
            }
            debugConsoleOutput.SelectionStart = debugConsoleOutput.TextLength;
            debugConsoleOutput.SelectionLength = 0;
            debugConsoleOutput.SelectionColor = DebugConsoleColor(e.Category);
            debugConsoleOutput.AppendText(e.Text.Replace("\r\n", "\n"));
            debugConsoleOutput.ScrollToCaret();
        }

        /// <summary>Hook from SwitchBottomDockTab.</summary>
        private void OnBottomDockTabSwitchedForDebug(string tabId)
        {
            if (debugConsolePanel == null) return;
            debugConsolePanel.Visible = tabId == "debug";
            if (tabId == "debug")
            {
                debugConsolePanel.BringToFront();
                if (debugConsoleBox != null) debugConsoleBox.Visible = false;
            }
        }

        public void ShowDebugConsole()
        {
            bottomDockPanel.Visible = true;
            SwitchBottomDockTab("debug");
            if (debugConsoleInput != null) debugConsoleInput.Focus();
        }

        // ---------------- Toolbar ----------------

        private void BuildDebugToolbar()
        {
            Control host = editorsSplitContainer;
            if (host == null) return;
            debugToolbarPanel = new Panel { Size = new Size(6 * 28 + 8, 28), Visible = false, Anchor = AnchorStyles.Top };
            string[] kinds = new string[] { "continue", "stepOver", "stepInto", "stepOut", "restart", "stop" };
            string[] tips = new string[] { "Continue / Pause (F5)", "Step Over (F10)", "Step Into (F11)", "Step Out (Shift+F11)", "Restart (Ctrl+Shift+F5)", "Stop (Shift+F5)" };
            ToolTip tt = new ToolTip();
            for (int i = 0; i < kinds.Length; i++)
            {
                string kind = kinds[i];
                Button b = new Button { Size = new Size(26, 24), Location = new Point(6 + i * 28, 2), FlatStyle = FlatStyle.Flat, Text = "", Tag = kind, Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0;
                b.Paint += delegate (object s, PaintEventArgs pe) { PaintDebugToolbarGlyph(pe.Graphics, (Button)s); };
                b.Click += delegate { OnDebugToolbarClick(kind); };
                tt.SetToolTip(b, tips[i]);
                debugToolbarPanel.Controls.Add(b);
            }
            host.Controls.Add(debugToolbarPanel);
            EventHandler place = delegate
            {
                debugToolbarPanel.Location = new Point(Math.Max(0, (host.ClientSize.Width - debugToolbarPanel.Width) / 2), 2);
            };
            host.Resize += place;
            place(null, EventArgs.Empty);
        }

        private void OnDebugToolbarClick(string kind)
        {
            switch (kind)
            {
                case "continue": debugController.ContinueOrPause(); break;
                case "stepOver": debugController.StepOver(); break;
                case "stepInto": debugController.StepInto(); break;
                case "stepOut": debugController.StepOut(); break;
                case "restart": debugController.Restart(); break;
                case "stop": debugController.StopAsync(); break;
            }
        }

        private void PaintDebugToolbarGlyph(Graphics g, Button b)
        {
            StudioTheme t = activeTheme;
            Color bg = t != null ? t.BgElevated : Color.FromArgb(40, 40, 46);
            using (SolidBrush br = new SolidBrush(bg)) g.FillRectangle(br, b.ClientRectangle);
            string kind = (string)b.Tag;
            bool stopped = debugController != null && debugController.IsStopped;
            if (kind == "continue" && !stopped) kind = "pause";
            bool enabled = stopped || kind == "pause" || kind == "restart" || kind == "stop";
            Color blue = enabled ? Color.FromArgb(70, 140, 230) : Color.FromArgb(110, 120, 130);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = b.Width / 2f, cy = b.Height / 2f;
            using (SolidBrush fill = new SolidBrush(blue))
            using (Pen pen = new Pen(blue, 1.6f))
            {
                switch (kind)
                {
                    case "continue":
                        g.FillRectangle(fill, cx - 6, cy - 5, 2.5f, 10);
                        g.FillPolygon(fill, new PointF[] { new PointF(cx - 2, cy - 5), new PointF(cx + 6, cy), new PointF(cx - 2, cy + 5) });
                        break;
                    case "pause":
                        g.FillRectangle(fill, cx - 4.5f, cy - 5, 3, 10);
                        g.FillRectangle(fill, cx + 1.5f, cy - 5, 3, 10);
                        break;
                    case "stepOver":
                        g.DrawArc(pen, cx - 6, cy - 6, 12, 10, 200, 140);
                        g.FillPolygon(fill, new PointF[] { new PointF(cx + 3, cy - 4), new PointF(cx + 7, cy - 1), new PointF(cx + 3, cy + 1) });
                        g.FillEllipse(fill, cx - 1.8f, cy + 3, 3.6f, 3.6f);
                        break;
                    case "stepInto":
                        g.DrawLine(pen, cx, cy - 7, cx, cy);
                        g.FillPolygon(fill, new PointF[] { new PointF(cx - 3.5f, cy - 1), new PointF(cx + 3.5f, cy - 1), new PointF(cx, cy + 3) });
                        g.FillEllipse(fill, cx - 1.8f, cy + 4, 3.6f, 3.6f);
                        break;
                    case "stepOut":
                        g.DrawLine(pen, cx, cy + 1, cx, cy - 5);
                        g.FillPolygon(fill, new PointF[] { new PointF(cx - 3.5f, cy - 3), new PointF(cx + 3.5f, cy - 3), new PointF(cx, cy - 7) });
                        g.FillEllipse(fill, cx - 1.8f, cy + 4, 3.6f, 3.6f);
                        break;
                    case "restart":
                        using (Pen green = new Pen(Color.FromArgb(56, 170, 90), 1.6f))
                            g.DrawArc(green, cx - 5.5f, cy - 5.5f, 11, 11, -30, 300);
                        using (SolidBrush gb = new SolidBrush(Color.FromArgb(56, 170, 90)))
                            g.FillPolygon(gb, new PointF[] { new PointF(cx + 2, cy - 8), new PointF(cx + 7, cy - 6), new PointF(cx + 3, cy - 2) });
                        break;
                    case "stop":
                        using (SolidBrush rb = new SolidBrush(Color.FromArgb(232, 80, 80))) g.FillRectangle(rb, cx - 5, cy - 5, 10, 10);
                        break;
                }
            }
        }

        // ---------------- State ----------------

        private void OnDebugStateChangedWinForms()
        {
            bool active = debugController.IsActive;
            if (debugStatusBarActive != active)
            {
                debugStatusBarActive = active;
                if (statusBar != null)
                {
                    statusBar.BackColor = ResolveStatusBarBackColor(activeTheme, !string.IsNullOrEmpty(workspaceRoot));
                    // White text on the orange debugging bar; the neutral token color otherwise.
                    Color fg = active || activeTheme == null ? Color.White : UiTokens.StatusBarForeground(activeTheme);
                    foreach (Control c in statusBar.Controls) c.ForeColor = fg;
                    statusBar.Invalidate(true);
                }
            }
            if (debugToolbarPanel != null)
            {
                debugToolbarPanel.Visible = active;
                if (active) debugToolbarPanel.BringToFront();
                debugToolbarPanel.Invalidate(true);
            }
            HideDebugHover();
            if (!string.IsNullOrEmpty(debugController.StatusText) && statusLeftLabel != null) statusLeftLabel.Text = debugController.StatusText;
            RebuildDebugCallStack();
            RebuildDebugVariables(false);
            RebuildDebugWatches();
            RebuildDebugBreakpoints();
        }

        /// <summary>Hook from UpdateStatusBarCursorAndLanguage: interpreter item and decorations of a newly active tab.</summary>
        private void UpdateDebugStatusItems(EditorTabBuffer buf)
        {
            if (debugController == null) return;
            bool isPy = buf != null && IsPythonBufferWinForms(buf);
            if (statusPythonItem != null)
            {
                statusPythonItem.Visible = isPy;
                if (isPy)
                {
                    if (!pythonDiscoveryRequested) { pythonDiscoveryRequested = true; PythonInterpreterService.RefreshInBackground(workspaceRoot); }
                    PythonInterpreterInfo sel = PythonInterpreterService.GetSelected(workspaceRoot);
                    statusPythonItem.Text = sel != null ? sel.DisplayLabel : "Select Interpreter";
                    statusBarToolTip.SetToolTip(statusPythonItem, sel != null ? sel.Path : "Select Python Interpreter");
                }
            }
            if (!ReferenceEquals(buf, lastDebugDecoratedTab))
            {
                lastDebugDecoratedTab = buf;
                if (buf != null && !string.IsNullOrEmpty(buf.FilePath)) debugController.ApplyDecorations(buf);
            }
        }

        private static bool IsPythonBufferWinForms(EditorTabBuffer b)
        {
            string lang = (b.Language ?? "").TrimStart('.').ToLowerInvariant();
            return lang == "py" || lang == "pyw" || lang == "python" || (b.FilePath ?? "").EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        }

        private void BuildPythonStatusItem()
        {
            if (statusBar == null) return;
            statusPythonItem = new Label
            {
                AutoSize = true,
                Font = StatusItemFont,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Padding = new Padding(5, 3, 5, 3),
                Margin = new Padding(0),
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Text = "Select Interpreter",
                Visible = false
            };
            statusPythonItem.MouseEnter += delegate { statusPythonItem.BackColor = Color.FromArgb(45, 255, 255, 255); };
            statusPythonItem.MouseLeave += delegate { statusPythonItem.BackColor = Color.Transparent; };
            statusPythonItem.Click += delegate { OpenPythonInterpreterPicker(statusPythonItem); };
            statusBar.Controls.Add(statusPythonItem);
        }

        // ---------------- Start / keys ----------------

        public void StartOrContinueDebugging()
        {
            if (debugController.IsActive) { debugController.ContinueOrPause(); return; }
            StartDebugging();
        }

        public void StartDebugging()
        {
            if (debugController.IsActive) { if (statusLeftLabel != null) statusLeftLabel.Text = "A debug session is already running."; return; }
            if (debugConfigCombo != null && debugConfigCombo.SelectedItem is string) selectedDebugConfigName = (string)debugConfigCombo.SelectedItem;
            string err;
            DebugLaunchConfig cfg = debugController.PickConfiguration(selectedDebugConfigName, activeTab != null ? activeTab.FilePath : null, out err);
            if (activeTab != null && debugController.ShouldRunWithoutDebugging(cfg, activeTab.FilePath))
            {
                Task run = ExecuteActiveFileInTerminalAsync(activeTab);
                if (statusLeftLabel != null) statusLeftLabel.Text = "No debugger for " + Path.GetExtension(activeTab.FilePath ?? "") + " files: ran it in the terminal instead";
                return;
            }
            if (cfg == null) { ShowDebugNotification(err ?? "Nothing to debug.", null, null); return; }
            StartDebugConfiguration(cfg);
        }

        private void StartDebugConfiguration(DebugLaunchConfig cfg)
        {
            if (activeTab != null && activeTab.IsDirty && !activeTab.IsUntitled && !string.IsNullOrEmpty(activeTab.FilePath)) SaveTabBuffer(activeTab);
            string console = DapJson.Str(cfg.Properties, "console") ?? (cfg.AdapterFamily == "python" ? "integratedTerminal" : "internalConsole");
            // Reveal the DEBUG CONSOLE without stealing focus from the editor (VS Code openOnSessionStart).
            if (console != "integratedTerminal") { bottomDockPanel.Visible = true; SwitchBottomDockTab("debug"); }
            if (leftSidebar.Visible && activeSidebarView != "run") SwitchSidebarView("run");
            debugController.StartAsync(cfg, activeTab != null ? activeTab.FilePath : null);
        }

        public void DebugPythonFile()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath)) return;
            if (debugController.IsActive) { if (statusLeftLabel != null) statusLeftLabel.Text = "A debug session is already running."; return; }
            string err;
            DebugLaunchConfig cfg = DebugLaunchConfigurations.CreateDefaultForFile(activeTab.FilePath, workspaceRoot, out err);
            if (cfg == null) { ShowDebugNotification(err, null, null); return; }
            StartDebugConfiguration(cfg);
        }

        public void ToggleBreakpointAtCursor()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath)) return;
            debugController.ToggleBreakpoint(activeTab, activeTab.CursorLine);
            if (editorControl != null) editorControl.Invalidate(true);
        }

        private bool IsTerminalFocusedForDebug()
        {
            return (conPtyTerminalControl != null && conPtyTerminalControl.ContainsFocus) ||
                   (splitConPtyTerminalControl != null && splitConPtyTerminalControl.ContainsFocus);
        }

        /// <summary>Debug keys before menus / controls: F5, Ctrl+F5, Shift+F5, Ctrl+Shift+F5, F6, F9, F10, F11, Shift+F11, Ctrl+Shift+D/Y.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (debugController != null && HandleDebugKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool HandleDebugKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
            if (alt) return false;
            bool active = debugController.IsActive;
            bool term = IsTerminalFocusedForDebug();
            switch (key)
            {
                case Keys.F5:
                    if (ctrl && shift) { if (active || debugController.LastConfig != null) debugController.Restart(); return true; }
                    if (shift) { if (active) { debugController.StopAsync(); return true; } return false; }
                    if (ctrl) { if (activeTab != null) { Task t = ExecuteActiveFileInTerminalAsync(activeTab); } return true; }
                    StartOrContinueDebugging();
                    return true;
                case Keys.F6:
                    if (active && !ctrl && !shift) { debugController.Pause(); return true; }
                    return false;
                case Keys.F9:
                    if (ctrl || shift || term) return false;
                    ToggleBreakpointAtCursor();
                    return true;
                case Keys.F10:
                    if (active && !ctrl && !shift) { debugController.StepOver(); return true; }
                    return false;
                case Keys.F11:
                    if (!active || ctrl) return false;
                    if (shift) debugController.StepOut(); else debugController.StepInto();
                    return true;
                case Keys.D:
                    if (ctrl && shift) { ShowDebugSidebar(); return true; }
                    return false;
                case Keys.Y:
                    if (ctrl && shift) { ShowDebugConsole(); return true; }
                    return false;
            }
            return false;
        }

        /// <summary>Hook from ExecutePaletteCommand.</summary>
        private bool TryExecuteDebugCommand(string cmdId)
        {
            if (debugController == null || string.IsNullOrEmpty(cmdId)) return false;
            switch (cmdId)
            {
                case "debug_start": StartOrContinueDebugging(); return true;
                case "debug_stop": debugController.StopAsync(); return true;
                case "debug_restart": debugController.Restart(); return true;
                case "debug_pause": debugController.Pause(); return true;
                case "debug_step_over": debugController.StepOver(); return true;
                case "debug_step_into": debugController.StepInto(); return true;
                case "debug_step_out": debugController.StepOut(); return true;
                case "toggle_breakpoint": ToggleBreakpointAtCursor(); return true;
                case "debug_conditional_breakpoint":
                    if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) PromptBreakpointOptions(activeTab.FilePath, activeTab.CursorLine);
                    return true;
                case "debug_remove_all_breakpoints": debugController.Breakpoints.RemoveAll(); return true;
                case "debug_active_file": selectedDebugConfigName = DebugController.ActiveFileConfigName; StartDebugging(); return true;
                case "debug_create_launch_json": CreateLaunchJson(); return true;
                case "debug_open_view": ShowDebugSidebar(); return true;
                case "debug_console": ShowDebugConsole(); return true;
                case "python_select_interpreter": OpenPythonInterpreterPicker(statusPythonItem != null && statusPythonItem.Visible ? (Control)statusPythonItem : (Control)statusBar); return true;
                case "python_run_file": if (activeTab != null) { Task t = ExecuteActiveFileInTerminalAsync(activeTab); } return true;
                case "python_debug_file": DebugPythonFile(); return true;
                case "stop_run":
                    if (debugController.IsActive) { debugController.StopAsync(); return true; }
                    return false;
            }
            return false;
        }

        // ---------------- Notifications ----------------

        public void ShowDebugNotification(string message, string hint, string installCommand)
        {
            if (string.IsNullOrEmpty(message)) return;
            AppendOutputLog("[Debug] " + message + (string.IsNullOrEmpty(hint) ? "" : " " + hint));
            if (statusLeftLabel != null) statusLeftLabel.Text = message;
            string body = message + (string.IsNullOrEmpty(hint) ? "" : "\n" + hint);
            if (!string.IsNullOrEmpty(installCommand))
            {
                string cmd = installCommand;
                ShowToastNotification("Debugger", body, "Install in Terminal", delegate { RunCommandInActiveTerminal(cmd); }, 15000);
            }
            else
            {
                ShowToastNotification("Debugger", body, null, null, 9000);
            }
        }

        /// <summary>Types a command into the active terminal (after a brand-new terminal's pending venv activation).</summary>
        public void RunCommandInActiveTerminal(string cmd)
        {
            bottomDockPanel.Visible = true;
            SwitchBottomDockTab("terminal");
            if (terminalSessions.Count == 0) CreateNewTerminalSession();
            TerminalSessionState st = activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count ? terminalSessions[activeTerminalSessionIndex] : null;
            ConPtyTerminalSession pty = st != null ? st.ConPtySession : null;
            if (pty != null && pty.IsRunning)
            {
                string pending = PythonVenvActivator.TakePendingActivation(pty);
                if (pending != null) pty.SendCommandLine(pending);
                pty.SendCommandLine(cmd);
            }
            else
            {
                Task t = ExecuteTerminalCommandAsync(cmd);
            }
        }

        private static TerminalShellFlavor FlavorOfPty(ConPtyTerminalSession pty)
        {
            if (pty == null) return TerminalShellFlavor.PowerShell;
            string shell = !string.IsNullOrEmpty(pty.ShellExecutable) ? pty.ShellExecutable : pty.CommandLine;
            TerminalShellFlavor f = PythonVenvActivator.DetectShellFlavor(shell ?? "", pty.ShellName ?? "");
            if (f == TerminalShellFlavor.Unknown || f == TerminalShellFlavor.Python) f = TerminalShellFlavor.PowerShell;
            return f;
        }

        /// <summary>runInTerminal: a dedicated, reused terminal; falls back to a plain process when ConPTY is unavailable.</summary>
        private int? RunDebuggeeInTerminal(string title, string cwd, List<string> args, Dictionary<string, string> env)
        {
            if (args == null || args.Count == 0) return null;
            ConPtyTerminalSession pty = debugTerminalState != null && terminalSessions.Contains(debugTerminalState) ? debugTerminalState.ConPtySession : null;
            if (pty == null || !pty.IsRunning)
            {
                debugTerminalState = CreateNewTerminalSession(null, null, !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd) ? cwd : null);
                pty = debugTerminalState != null ? debugTerminalState.ConPtySession : null;
            }
            bottomDockPanel.Visible = true;
            SwitchBottomDockTab("terminal");
            if (pty != null && pty.IsRunning)
            {
                string pending = PythonVenvActivator.TakePendingActivation(pty);
                if (pending != null) pty.SendCommandLine(pending);
                pty.SendCommandLine(DebugTerminalCommands.Build(FlavorOfPty(pty), cwd, args, env));
                return null;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(args[0], DebugAdapterConnection.BuildArgumentString(args.Skip(1).ToList()));
                psi.UseShellExecute = false;
                if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
                if (env != null)
                    foreach (KeyValuePair<string, string> kv in env)
                    {
                        if (kv.Value == null) psi.EnvironmentVariables.Remove(kv.Key);
                        else psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                Process p = Process.Start(psi);
                return p != null ? (int?)p.Id : null;
            }
            catch (Exception ex)
            {
                debugController.AppendConsole("stderr", "Could not start the debuggee: " + ex.Message + "\n");
                return null;
            }
        }

        // ---------------- Zero-setup Python ----------------

        /// <summary>Run Python File command line using the selected interpreter (hook in ExecuteActiveFileInTerminalAsync).</summary>
        private string BuildPythonRunCommandWinForms(string filePath)
        {
            PythonInterpreterInfo sel = PythonInterpreterService.GetSelected(workspaceRoot);
            string exe = sel != null ? sel.Path : "python";
            TerminalSessionState st = activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count ? terminalSessions[activeTerminalSessionIndex] : null;
            return DebugTerminalCommands.BuildRun(FlavorOfPty(st != null ? st.ConPtySession : null), exe, filePath);
        }

        /// <summary>Hook after the editor title ▶ is created: right-click offers Run / Debug Python File.</summary>
        private void AttachPythonRunMenu(Button runBtn)
        {
            if (runBtn == null) return;
            runBtn.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || activeTab == null || !IsPythonBufferWinForms(activeTab)) return;
                ContextMenuStrip menu = CreateAutoDisposingContextMenu();
                menu.Items.Add(CreateMenuItem("Run Python File", "", delegate { Task t = ExecuteActiveFileInTerminalAsync(activeTab); }));
                menu.Items.Add(CreateMenuItem("Debug Python File", "", DebugPythonFile));
                menu.Show(runBtn, e.Location);
            };
        }

        public void OpenPythonInterpreterPicker(Control anchor)
        {
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            List<PythonInterpreterInfo> all = PythonInterpreterService.GetCached(workspaceRoot);
            PythonInterpreterInfo sel = PythonInterpreterService.GetSelected(workspaceRoot);
            menu.Items.Add(CreateMenuItem("Enter interpreter path...", "", PromptInterpreterPathWinForms));
            menu.Items.Add(CreateMenuItem("Refresh interpreter list", "", delegate { pythonDiscoveryRequested = true; PythonInterpreterService.RefreshInBackground(workspaceRoot); }));
            menu.Items.Add(new ToolStripSeparator());
            if (all == null)
            {
                pythonDiscoveryRequested = true;
                PythonInterpreterService.RefreshInBackground(workspaceRoot);
                menu.Items.Add(new ToolStripMenuItem("Discovering Python interpreters...") { Enabled = false });
            }
            else
            {
                foreach (PythonInterpreterInfo i in all)
                {
                    PythonInterpreterInfo info = i;
                    ToolStripMenuItem it = CreateMenuItem("Python " + info.DisplayLabel, info.Kind, delegate { SelectPythonInterpreterWinForms(info); });
                    it.ToolTipText = info.Path;
                    it.Checked = sel != null && sel.Path == info.Path;
                    menu.Items.Add(it);
                }
                if (all.Count == 0) menu.Items.Add(new ToolStripMenuItem("No interpreters found: install Python 3 (python.org / winget)") { Enabled = false });
            }
            Control a = anchor ?? (Control)statusBar;
            menu.Show(a, new Point(0, -menu.PreferredSize.Height));
        }

        private void SelectPythonInterpreterWinForms(PythonInterpreterInfo info)
        {
            if (info == null) return;
            PythonInterpreterService.Select(workspaceRoot, info);
            if (statusLeftLabel != null) statusLeftLabel.Text = "Python interpreter: " + info.DisplayLabel;
            UpdateDebugStatusItems(activeTab);
            LayoutStatusBarItems();
        }

        private void PromptInterpreterPathWinForms()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Python: Select Interpreter";
                dlg.Filter = "Python interpreter (python*.exe)|python*.exe|All files (*.*)|*.*";
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)) dlg.InitialDirectory = workspaceRoot;
                if (dlg.ShowDialog(this) != DialogResult.OK || !File.Exists(dlg.FileName)) return;
                PythonDiscoveryEnvironment env = PythonDiscoveryEnvironment.CreateDefault();
                env.ProbeVersions = false;
                SelectPythonInterpreterWinForms(PythonInterpreterDiscovery.FromPath(dlg.FileName, env));
            }
        }

        // ---------------- Hover while stopped ----------------

        private void OnEditorMouseMoveForDebugHover(object sender, MouseEventArgs e)
        {
            if (debugController == null || !debugController.IsStopped) return;
            if (Math.Abs(e.X - debugHoverPoint.X) < 4 && Math.Abs(e.Y - debugHoverPoint.Y) < 4) return;
            debugHoverPoint = e.Location;
            HideDebugHover();
            debugHoverTimer.Stop();
            debugHoverTimer.Start();
        }

        private void ShowDebugHover()
        {
            if (!debugController.IsStopped || editorControl == null || editorControl.Canvas == null) return;
            EditorTabBuffer buf = editorControl.CurrentBuffer;
            if (buf == null) return;
            int line, col;
            if (!editorControl.Canvas.TryGetDocPositionAt(debugHoverPoint, out line, out col)) return;
            string expr = DebugController.ExpressionAt(buf.Lines[line], col);
            if (string.IsNullOrEmpty(expr)) return;
            int gen = ++debugHoverGeneration;
            Point pt = debugHoverPoint;
            debugController.EvaluateForHoverAsync(expr).ContinueWith(delegate (Task<string> t)
            {
                string value = t.IsFaulted ? null : t.Result;
                if (value == null) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (gen != debugHoverGeneration || !debugController.IsStopped) return;
                        debugHoverTip.Show(expr + " = " + value, editorControl.Canvas, pt.X, pt.Y + 20, 8000);
                    }));
                }
                catch { }
            });
        }

        private void HideDebugHover()
        {
            debugHoverGeneration++;
            try { if (debugHoverTip != null && editorControl != null && editorControl.Canvas != null) debugHoverTip.Hide(editorControl.Canvas); } catch { }
        }
    }
}
