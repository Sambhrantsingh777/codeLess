using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Language Server Protocol integration for the Windows (WinForms) workbench: document sync, diagnostics,
    /// completion, hover, signature help, definition / references, rename, formatting, code actions, outline,
    /// status bar item, install hint, log and configuration dialogs. Protocol work lives in codeLess.Core
    /// (LanguageServerManager), shared with the Avalonia UI. STRICT C# 5.
    /// </summary>
    public sealed partial class WinFormsLanguageServerController : IDisposable
    {
        private readonly Form form;
        private readonly IdePersistentSettings settings;
        private LanguageServerManager mgr;
        private readonly Dictionary<string, EditorTabBuffer> openDocs = new Dictionary<string, EditorTabBuffer>(StringComparer.Ordinal);
        private readonly List<WinFormsLspEditorBinding> bindings = new List<WinFormsLspEditorBinding>();
        private Panel toast;

        // Host callbacks (set by CodeLessMainForm).
        public Func<List<EditorTabBuffer>> GetOpenTabs;
        public Func<EditorTabBuffer> GetActiveTab;
        public Func<string> GetWorkspaceRoot;
        public Func<StudioTheme> GetTheme;
        public Action<string> SetStatus;
        public Func<string, EditorTabBuffer> OpenFile;
        public Action<int, int> JumpTo;
        public Action<EditorTabBuffer> RefreshProblems;
        public Action<string> TypeInTerminal;
        public Action RefreshTabs;
        public Action LayoutStatusBar;
        /// <summary>Appends a line to the Output panel.</summary>
        public Action<string> AppendOutput;
        /// <summary>Status bar item (created by the form with its status item factory).</summary>
        public Label StatusItem;

        public WinFormsLanguageServerController(Form form, IdePersistentSettings settings)
        {
            this.form = form;
            this.settings = settings;
            mgr = new LanguageServerManager(PostToUi);
            mgr.UserSettingsProvider = delegate { return settings.GetLanguageServerUserSettings(); };
            mgr.IsHintSuppressed = delegate (string lang) { return settings.IsLanguageServerHintDismissed(lang); };
            mgr.ApplyWorkspaceEditHandler = delegate (LspWorkspaceEdit edit) { return ApplyWorkspaceEdit(edit, "Applied", LspEditGuard.ForServer(mgr)).Success; };
            mgr.DiagnosticsChanged += OnDiagnosticsChanged;
            mgr.StatusChanged += UpdateStatusItem;
            mgr.ServerMissing += ShowHint;
            mgr.ServerMessage += delegate (string sev, string msg) { Status(msg); };
            InitInstalls(); // LanguageServerWinForms.Install.cs
        }

        public LanguageServerManager Manager { get { return mgr; } }

        private void PostToUi(Action a)
        {
            try
            {
                if (form == null || form.IsDisposed || !form.IsHandleCreated) return;
                form.BeginInvoke(a);
            }
            catch { }
        }

        private void Status(string msg)
        {
            if (SetStatus != null) SetStatus(msg ?? "");
        }

        private StudioTheme Theme { get { return GetTheme != null ? GetTheme() : null; } }
        private EditorTabBuffer Active { get { return GetActiveTab != null ? GetActiveTab() : null; } }
        private string Root { get { string r = GetWorkspaceRoot != null ? GetWorkspaceRoot() : null; return string.IsNullOrEmpty(r) ? null : r; } }

        private static string Key(string path) { return LspText.PathKey(path ?? ""); }

        public void Attach(CodeLessEditorControl ctrl)
        {
            if (ctrl == null) return;
            bindings.Add(new WinFormsLspEditorBinding(this, ctrl));
        }

        public void Dispose()
        {
            foreach (WinFormsLspEditorBinding b in bindings) b.Detach();
            bindings.Clear();
            LanguageServerManager m = mgr;
            mgr = null;
            if (m != null) { try { m.Dispose(); } catch { } }
        }

        // ------------------------------------------------------------------ lifecycle hooks

        private static bool Eligible(EditorTabBuffer tab)
        {
            return tab != null && !string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath) && (tab.Content == null || tab.Content.Length <= 4 * 1024 * 1024);
        }

        /// <summary>Hook: called whenever the tab strip is rebuilt. Opens the active document, closes documents whose tab is gone.</summary>
        public void SyncTabs()
        {
            foreach (WinFormsLspEditorBinding b in bindings) b.HidePopupsIfEditorChanged(); // stale suggest list of the previous file
            if (mgr == null) return;
            mgr.SetWorkspaceRoot(Root);
            List<EditorTabBuffer> tabs = GetOpenTabs != null ? GetOpenTabs() : new List<EditorTabBuffer>();
            foreach (KeyValuePair<string, EditorTabBuffer> kv in openDocs.ToList())
            {
                if (tabs.Contains(kv.Value) && Key(kv.Value.FilePath) == kv.Key) continue;
                openDocs.Remove(kv.Key);
                mgr.CloseDocument(kv.Key);
            }
            EditorTabBuffer active = Active;
            if (Eligible(active) && !openDocs.ContainsKey(Key(active.FilePath)))
            {
                openDocs[Key(active.FilePath)] = active;
                EditorTabBuffer captured = active;
                mgr.OpenDocument(active.FilePath, delegate { return LspEditing.BufferText(captured); });
            }
            UpdateStatusItem();
        }

        public void OnBufferModified(EditorTabBuffer buf)
        {
            if (mgr == null || buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            mgr.ChangeDocument(buf.FilePath);
            foreach (WinFormsLspEditorBinding b in bindings) b.OnBufferModified(buf);
        }

        public void OnSaved(EditorTabBuffer buf)
        {
            if (mgr == null || buf == null) return;
            if (!openDocs.ContainsKey(Key(buf.FilePath))) { SyncTabs(); return; }
            mgr.SaveDocument(buf.FilePath);
        }

        public void SetWorkspaceRoot(string root)
        {
            if (mgr != null) mgr.SetWorkspaceRoot(string.IsNullOrEmpty(root) ? null : root);
        }

        private void OnDiagnosticsChanged(string path)
        {
            EditorTabBuffer tab = FindOpenTab(path);
            if (tab == null) return;
            LspDiagnosticsStore.MergeInto(tab);
            if (ReferenceEquals(tab, Active) && RefreshProblems != null) RefreshProblems(tab);
            foreach (WinFormsLspEditorBinding b in bindings)
                if (ReferenceEquals(b.Editor.CurrentBuffer, tab)) b.Editor.Canvas.Invalidate();
        }

        public EditorTabBuffer FindOpenTab(string path)
        {
            if (string.IsNullOrEmpty(path) || GetOpenTabs == null) return null;
            string k = Key(path);
            foreach (EditorTabBuffer t in GetOpenTabs())
                if (!string.IsNullOrEmpty(t.FilePath) && Key(t.FilePath) == k) return t;
            return null;
        }

        /// <summary>The path to use for LSP requests, or null when no server document is open for the buffer.</summary>
        public string PathFor(EditorTabBuffer buf)
        {
            return mgr != null && buf != null && !string.IsNullOrEmpty(buf.FilePath) && mgr.IsDocumentOpen(buf.FilePath) ? buf.FilePath : null;
        }

        public bool IsActiveFor(EditorTabBuffer buf)
        {
            string p = PathFor(buf);
            return p != null && mgr.IsActive(p);
        }

        // ------------------------------------------------------------------ status bar / hint / dialogs

        public void UpdateStatusItem()
        {
            if (StatusItem == null) return;
            EditorTabBuffer a = Active;
            LanguageServerStatus st = mgr != null && a != null && !string.IsNullOrEmpty(a.FilePath) ? mgr.GetStatus(a.FilePath) : null;
            bool visible = st != null && !string.IsNullOrEmpty(st.StatusBarText);
            string text = visible ? st.StatusBarText : "";
            if (StatusItem.Visible != visible || StatusItem.Text != text)
            {
                StatusItem.Visible = visible;
                StatusItem.Text = text;
                if (LayoutStatusBar != null) LayoutStatusBar();
            }
            StatusItem.Tag = visible ? st.Tooltip : null;
        }

        public void ShowStatusMenu(Control anchor)
        {
            ContextMenuStrip menu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(delegate { return Theme; }) };
            menu.Closed += delegate { menu.BeginInvoke(new Action(delegate { menu.Dispose(); })); };
            EditorTabBuffer a = Active;
            LanguageServerStatus st = mgr != null && a != null ? mgr.GetStatus(a.FilePath) : null;
            if (st != null && !string.IsNullOrEmpty(st.Message))
            {
                ToolStripMenuItem info = new ToolStripMenuItem(st.Message.Length > 90 ? st.Message.Substring(0, 90) + "…" : st.Message) { Enabled = false };
                menu.Items.Add(info);
                menu.Items.Add(new ToolStripSeparator());
            }
            AddInstallMenuItems(menu, st);
            if (st != null && st.State == LanguageServerState.NotInstalled && !string.IsNullOrEmpty(st.InstallCommand))
            {
                string cmd = st.InstallCommand;
                ToolStripMenuItem install = new ToolStripMenuItem("Install in Terminal: " + cmd);
                install.Click += delegate { TypeInstallCommand(cmd); };
                menu.Items.Add(install);
            }
            ToolStripMenuItem log = new ToolStripMenuItem("Show Language Server Log");
            log.Click += delegate { ShowLog(); };
            ToolStripMenuItem restart = new ToolStripMenuItem("Restart Language Server");
            restart.Click += delegate { RestartActive(); };
            ToolStripMenuItem config = new ToolStripMenuItem("Configure Language Servers...");
            config.Click += delegate { ShowSettingsDialog(); };
            menu.Items.Add(log);
            menu.Items.Add(restart);
            menu.Items.Add(config);
            menu.Show(anchor, new Point(0, -menu.PreferredSize.Height));
        }

        public void RestartActive()
        {
            EditorTabBuffer a = Active;
            if (mgr == null || PathFor(a) == null) { Status("No language server for the active file."); return; }
            mgr.RestartServer(a.FilePath);
            Status("Restarting language server…");
        }

        private void TypeInstallCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd) || TypeInTerminal == null) return;
            TypeInTerminal(cmd); // typed, not executed: the user presses Enter
            Status("Press Enter in the terminal to install, then use Restart Language Server.");
        }

        private void ShowHint(LanguageServerHint hint)
        {
            if (hint == null || form == null || form.IsDisposed) return;
            if (hint.InstallFailed) { ShowInstallFailure(hint); return; }
            Status(hint.Message);
            StudioTheme t = Theme;
            if (toast != null) { form.Controls.Remove(toast); toast.Dispose(); toast = null; }
            Panel p = new Panel { Width = 440, Height = 92, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(10) };
            if (t != null) { p.BackColor = t.BgElevated; p.ForeColor = t.FgPrimary; }
            Label text = new Label { Text = hint.Message, AutoSize = false, Dock = DockStyle.Top, Height = 44, UseMnemonic = false };
            FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, FlowDirection = FlowDirection.RightToLeft };
            Panel captured = p;
            Action close = delegate
            {
                if (captured.IsDisposed) return;
                form.Controls.Remove(captured);
                captured.Dispose();
                if (ReferenceEquals(toast, captured)) toast = null;
            };
            Button closeBtn = MakeButton("Close", t);
            closeBtn.Click += delegate { close(); };
            Button never = MakeButton("Don't Show Again", t);
            never.Click += delegate
            {
                settings.DismissLanguageServerHint(hint.LanguageId);
                try { settings.Save(); } catch { }
                close();
            };
            row.Controls.Add(closeBtn);
            row.Controls.Add(never);
            if (!string.IsNullOrEmpty(hint.InstallCommand))
            {
                Button install = MakeButton("Install in Terminal", t);
                install.Click += delegate { close(); TypeInstallCommand(hint.InstallCommand); };
                row.Controls.Add(install);
            }
            p.Controls.Add(row);
            p.Controls.Add(text);
            p.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            p.Location = new Point(Math.Max(0, form.ClientSize.Width - p.Width - 18), Math.Max(0, form.ClientSize.Height - p.Height - 40));
            toast = p;
            form.Controls.Add(p);
            p.BringToFront();
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += delegate { timer.Stop(); timer.Dispose(); close(); };
            timer.Start();
        }

        internal static Button MakeButton(string text, StudioTheme t)
        {
            Button b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 0, 0, 0) };
            if (t != null) { b.BackColor = t.BgHover; b.ForeColor = t.FgPrimary; b.FlatAppearance.BorderColor = t.BorderSubtle; }
            return b;
        }

        private Form NewDialog(string title, int w, int h)
        {
            StudioTheme t = Theme;
            Form d = new Form
            {
                Text = title,
                Width = w,
                Height = h,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                KeyPreview = true
            };
            if (t != null) { d.BackColor = t.BgElevated; d.ForeColor = t.FgPrimary; }
            d.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) d.Close(); };
            return d;
        }

        private TextBox NewMonoBox(bool readOnly)
        {
            StudioTheme t = Theme;
            TextBox box = new TextBox
            {
                Multiline = true,
                ReadOnly = readOnly,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle
            };
            if (t != null) { box.BackColor = t.BgEditor; box.ForeColor = t.FgPrimary; }
            return box;
        }

        public void ShowLog()
        {
            if (mgr == null) return;
            EditorTabBuffer a = Active;
            string path = a != null ? a.FilePath : null;
            using (Form d = NewDialog("Language Server Log", 960, 620))
            {
                TextBox box = NewMonoBox(true);
                box.Text = mgr.GetLog(path).Replace("\n", "\r\n");
                FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                Button close = MakeButton("Close", Theme);
                close.Click += delegate { d.Close(); };
                Button restart = MakeButton("Restart Server", Theme);
                restart.Click += delegate { RestartActive(); };
                Button copy = MakeButton("Copy", Theme);
                copy.Click += delegate { try { Clipboard.SetText(box.Text ?? ""); } catch { } };
                Button refresh = MakeButton("Refresh", Theme);
                refresh.Click += delegate { if (mgr != null) { box.Text = mgr.GetLog(path).Replace("\n", "\r\n"); box.SelectionStart = box.TextLength; box.ScrollToCaret(); } };
                row.Controls.Add(close);
                row.Controls.Add(restart);
                row.Controls.Add(copy);
                row.Controls.Add(refresh);
                d.Controls.Add(box);
                d.Controls.Add(row);
                d.Shown += delegate { box.SelectionStart = box.TextLength; box.ScrollToCaret(); };
                d.ShowDialog(form);
            }
        }

        public void ShowSettingsDialog()
        {
            using (Form d = NewDialog("Language Servers", 820, 600))
            {
                StudioTheme t = Theme;
                TextBox overrides = NewMonoBox(false);
                overrides.Dock = DockStyle.Top;
                overrides.Height = 170;
                overrides.Text = (string.IsNullOrWhiteSpace(settings.LanguageServerOverrides) ? LanguageServerUserSettings.OverridesHelp : settings.LanguageServerOverrides).Replace("\r\n", "\n").Replace("\n", "\r\n");
                Label head = new Label { Text = "Overrides (one per line: language = command args, or language = off):", Dock = DockStyle.Top, Height = 22, UseMnemonic = false };
                CheckBox disable = new CheckBox { Text = "Disable all language servers", Checked = settings.DisableLanguageServers, Dock = DockStyle.Top, Height = 28 };
                CheckBox autoInstall = new CheckBox { Text = "Install missing language servers automatically (into " + LanguageServerInstaller.DefaultManagedRoot() + ")", Checked = settings.AutoInstallLanguageServers, Dock = DockStyle.Top, Height = 28, UseMnemonic = false };
                Label detHead = new Label { Text = "Detected servers:", Dock = DockStyle.Top, Height = 22, Font = new Font(d.Font, FontStyle.Bold) };
                TextBox detected = NewMonoBox(true);
                detected.Text = "Detecting installed language servers…";
                FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                Button cancel = MakeButton("Cancel", t);
                cancel.Click += delegate { d.Close(); };
                Button save = MakeButton("Save", t);
                if (t != null) { save.BackColor = t.AccentPrimary; save.ForeColor = Color.White; }
                save.Click += delegate
                {
                    string text = (overrides.Text ?? "").Replace("\r\n", "\n");
                    settings.LanguageServerOverrides = text.Trim() == LanguageServerUserSettings.OverridesHelp.Trim() ? "" : text;
                    settings.DisableLanguageServers = disable.Checked;
                    settings.AutoInstallLanguageServers = autoInstall.Checked;
                    try { settings.Save(); } catch { }
                    if (mgr != null)
                    {
                        LanguageServerRegistry.ClearCache();
                        foreach (LanguageServerInstance inst in mgr.GetInstances()) mgr.RestartInstance(inst);
                    }
                    Status("Language server settings saved.");
                    d.Close();
                };
                row.Controls.Add(cancel);
                row.Controls.Add(save);
                d.Controls.Add(detected);
                d.Controls.Add(detHead);
                d.Controls.Add(autoInstall);
                d.Controls.Add(disable);
                d.Controls.Add(overrides);
                d.Controls.Add(head);
                d.Controls.Add(row);
                string root = Root;
                string managedRoot = mgr != null ? mgr.ManagedServersRoot : LanguageServerInstaller.DefaultManagedRoot();
                LanguageServerUserSettings user = settings.GetLanguageServerUserSettings();
                Task.Factory.StartNew(delegate
                {
                    List<string> lines = new List<string>();
                    foreach (LanguageServerDefinition def in LanguageServerRegistry.Definitions)
                    {
                        try
                        {
                            LanguageServerResolution r = LanguageServerRegistry.Resolve(def.LanguageIds[0], root, user, managedRoot);
                            if (r.CanStart) lines.Add("OK   " + def.DisplayName + ": " + r.DisplayName + "  (" + r.Executable + ")" + (r.FromManagedFolder ? "  [installed by codeLess]" : ""));
                            else if (r.Disabled) lines.Add("off  " + def.DisplayName);
                            else lines.Add("--   " + def.DisplayName + ": not found" + (r.InstallCommand != null ? "  - install: " + r.InstallCommand : ""));
                        }
                        catch (Exception ex) { lines.Add("??   " + def.DisplayName + ": " + ex.Message); }
                    }
                    string all = string.Join("\r\n", lines.ToArray());
                    try { if (!d.IsDisposed && d.IsHandleCreated) d.BeginInvoke(new Action(delegate { if (!detected.IsDisposed) detected.Text = all; })); } catch { }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                d.ShowDialog(form);
            }
        }

        /// <summary>Simple themed list picker (references, multiple definitions, code actions). Returns the chosen index or -1.</summary>
        public int Pick(string title, List<string> items, List<bool> enabled)
        {
            if (items == null || items.Count == 0) return -1;
            using (Form d = NewDialog(title, 900, Math.Min(560, 120 + items.Count * 20)))
            {
                StudioTheme t = Theme;
                ListBox list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None, IntegralHeight = false };
                if (t != null) { list.BackColor = t.BgEditor; list.ForeColor = t.FgPrimary; }
                foreach (string s in items) list.Items.Add(s);
                list.SelectedIndex = 0;
                int chosen = -1;
                Action accept = delegate
                {
                    int i = list.SelectedIndex;
                    if (i < 0 || (enabled != null && i < enabled.Count && !enabled[i])) return;
                    chosen = i;
                    d.Close();
                };
                list.DoubleClick += delegate { accept(); };
                list.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; accept(); } };
                d.Controls.Add(list);
                d.ShowDialog(form);
                return chosen;
            }
        }

        // ------------------------------------------------------------------ navigation / refactoring

        public void NavigateTo(LspLocation loc)
        {
            if (loc == null || string.IsNullOrEmpty(loc.FilePath) || OpenFile == null) return;
            if (!File.Exists(loc.FilePath)) { Status("File not found: " + loc.FilePath); return; }
            EditorTabBuffer tab = OpenFile(loc.FilePath);
            if (tab == null) return;
            int line = loc.Range != null ? loc.Range.StartLine + 1 : 1;
            int col = loc.Range != null ? loc.Range.StartCharacter + 1 : 1;
            if (JumpTo != null) JumpTo(line, col);
        }

        private string Preview(LspLocation loc)
        {
            if (loc == null || loc.Range == null) return "";
            try
            {
                EditorTabBuffer tab = FindOpenTab(loc.FilePath);
                if (tab != null) return loc.Range.StartLine < tab.Lines.Count ? tab.Lines[loc.Range.StartLine].Trim() : "";
                if (File.Exists(loc.FilePath) && new FileInfo(loc.FilePath).Length < 8 * 1024 * 1024)
                    return (File.ReadLines(loc.FilePath).Skip(loc.Range.StartLine).FirstOrDefault() ?? "").Trim();
            }
            catch { }
            return "";
        }

        private void ShowLocations(List<LspLocation> locs, string what)
        {
            if (locs.Count == 1) { NavigateTo(locs[0]); return; }
            List<LspLocation> shown = locs.Take(500).ToList();
            List<string> rows = new List<string>();
            string root = Root;
            foreach (LspLocation l in shown)
            {
                string rel = root != null ? LlmHarnessAndAgentEngine.GetRelativePath(root, l.FilePath) : Path.GetFileName(l.FilePath);
                rows.Add(rel + ":" + (l.Range.StartLine + 1) + "   " + Preview(l));
            }
            Status(locs.Count + " " + what);
            int i = Pick(locs.Count + " " + what + (locs.Count > 500 ? " (first 500)" : ""), rows, null);
            if (i >= 0) NavigateTo(shown[i]);
        }

        private static async Task<T> WithCap<T>(Task<T> t, int ms) where T : class
        {
            Task done = await Task.WhenAny(t, Task.Delay(ms));
            return done == t && !t.IsFaulted && !t.IsCanceled ? t.Result : null;
        }

        /// <summary>Definition-like navigation through the server. <paramref name="fallback"/> runs when the server has no answer.</summary>
        public async void NavigateAsync(EditorTabBuffer buf, int line, int col, string method, string what, Action fallback)
        {
            string path = PathFor(buf);
            List<LspLocation> locs = null;
            if (path != null && mgr.IsActive(path))
            {
                try { locs = await WithCap(mgr.RequestLocationsAsync(path, line, col, method, CancellationToken.None), 4000); }
                catch { locs = null; }
            }
            if (locs != null && locs.Count > 0) { ShowLocations(locs, what); return; }
            if (fallback != null) fallback();
            else Status("No " + what + " found.");
        }

        public async void FindReferences(EditorTabBuffer buf, Action fallback)
        {
            string path = PathFor(buf);
            if (path == null || !mgr.IsActive(path))
            {
                List<LspLocation> fallbackRefs = LspEditing.FindWordReferencesFallback(buf, GetOpenTabs != null ? GetOpenTabs() : null, Root, 500, 500);
                if (fallbackRefs.Count > 0) { ShowLocations(fallbackRefs, "text matches (no language server)"); return; }
                Status("No references found.");
                return;
            }
            Status("Finding references…");
            List<LspLocation> refs = null;
            try { refs = await mgr.RequestReferencesAsync(path, buf.CursorLine, buf.CursorCol, true, CancellationToken.None); }
            catch { refs = null; }
            if (refs == null)
            {
                List<LspLocation> fallbackRefs = LspEditing.FindWordReferencesFallback(buf, GetOpenTabs != null ? GetOpenTabs() : null, Root, 500, 500);
                if (fallbackRefs.Count > 0) { ShowLocations(fallbackRefs, "text matches (no language server)"); return; }
                Status("No references found.");
                return;
            }
            if (refs.Count == 0) { Status("No references found."); return; }
            ShowLocations(refs, "references");
        }

        public async void Rename(EditorTabBuffer buf, Action fallback)
        {
            string path = PathFor(buf);
            if (path == null || !mgr.Supports(path, "renameProvider")) { if (fallback != null) fallback(); return; }
            int line = buf.CursorLine, col = buf.CursorCol;
            string placeholder;
            try { placeholder = await mgr.PrepareRenameAsync(path, line, col, CancellationToken.None); }
            catch (Exception ex)
            {
                Exception inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                Status("Rename: " + inner.Message);
                return;
            }
            int ws;
            if (placeholder == null) placeholder = LspEditing.WordAt(buf.Lines[line], col, out ws);
            if (string.IsNullOrEmpty(placeholder)) { Status("Nothing to rename at the cursor."); return; }
            string newName = null;
            using (PromptInputDialog dlg = new PromptInputDialog("Rename Symbol (F2)", "Rename '" + placeholder + "' to:", placeholder, Theme))
            {
                if (dlg.ShowDialog(form) == DialogResult.OK) newName = (dlg.InputValue ?? "").Trim();
            }
            if (string.IsNullOrEmpty(newName) || newName == placeholder) return;
            LspWorkspaceEdit edit;
            // Snapshot before the request: typing while the server computes the rename must not shift its offsets.
            LspEditGuard guard = LspEditGuard.Capture(GetOpenTabs != null ? GetOpenTabs() : null, mgr);
            try { edit = await mgr.RequestRenameAsync(path, line, col, newName, CancellationToken.None); }
            catch (Exception ex)
            {
                Exception inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                Status("Rename failed: " + inner.Message);
                return;
            }
            if (edit == null || edit.IsEmpty) { Status("Rename: the language server returned no changes."); return; }
            ApplyWorkspaceEdit(edit, "Renamed to '" + newName + "':", guard);
        }

        public bool SupportsFormattingFor(EditorTabBuffer buf)
        {
            string p = PathFor(buf);
            return p != null && mgr.Supports(p, "documentFormattingProvider");
        }

        public LspApplyResult ApplyWorkspaceEdit(LspWorkspaceEdit edit, string verb)
        {
            return ApplyWorkspaceEdit(edit, verb, null);
        }

        public LspApplyResult ApplyWorkspaceEdit(LspWorkspaceEdit edit, string verb, LspEditGuard guard)
        {
            LspApplyResult result = LspEditing.ApplyWorkspaceEdit(edit, FindOpenTab, delegate (string oldPath, string newPath)
            {
                EditorTabBuffer tab = FindOpenTab(oldPath);
                if (tab == null) return;
                tab.FilePath = newPath;
                tab.FileName = Path.GetFileName(newPath);
                string root = Root;
                tab.RelativePath = root != null ? LlmHarnessAndAgentEngine.GetRelativePath(root, newPath) : tab.FileName;
            }, guard, delegate (IList<EditorTabBuffer> touchedBuffers, IList<string> touchedDisk)
            {
                foreach (EditorTabBuffer b in touchedBuffers)
                {
                    bool shown = false;
                    foreach (WinFormsLspEditorBinding eb in bindings)
                    {
                        if (!ReferenceEquals(eb.Editor.CurrentBuffer, b)) continue;
                        eb.Editor.NotifyBufferContentChanged();
                        eb.Editor.Canvas.Invalidate();
                        shown = true;
                    }
                    if (!shown)
                    {
                        LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(b);
                        if (mgr != null && !string.IsNullOrEmpty(b.FilePath)) mgr.ChangeDocument(b.FilePath);
                    }
                }
                if (RefreshTabs != null) RefreshTabs();
            });
            if (result.Rejected)
            {
                Status(verb + " not applied: " + result.Errors[0]);
                return result;
            }
            foreach (EditorTabBuffer b in result.ChangedBuffers)
            {
                bool shown = false;
                foreach (WinFormsLspEditorBinding eb in bindings)
                {
                    if (!ReferenceEquals(eb.Editor.CurrentBuffer, b)) continue;
                    eb.Editor.NotifyBufferContentChanged();
                    eb.Editor.Canvas.Invalidate();
                    shown = true;
                }
                if (!shown)
                {
                    LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(b);
                    if (mgr != null && !string.IsNullOrEmpty(b.FilePath)) mgr.ChangeDocument(b.FilePath);
                }
            }
            SyncTabs();
            if (RefreshTabs != null) RefreshTabs();
            Status(verb + " " + result.Summary);
            return result;
        }

        public async void Format(CodeLessEditorControl ctrl, bool selectionOnly)
        {
            EditorTabBuffer buf = ctrl != null ? ctrl.CurrentBuffer : null;
            if (buf == null) return;
            string path = PathFor(buf);
            string lang = LanguageServerRegistry.LanguageIdForPath(path ?? buf.FileName ?? "") ?? (string.IsNullOrEmpty(buf.Language) ? "plaintext" : buf.Language);
            if (path == null || !mgr.IsActive(path))
            {
                Status("There is no formatter for '" + lang + "' files installed.");
                return;
            }
            LspRange range = null;
            if (selectionOnly && buf.HasSelection())
            {
                int sl, sc, el, ec;
                buf.GetNormalizedSelection(out sl, out sc, out el, out ec);
                range = new LspRange(sl, sc, el, ec);
            }
            int tab = buf.CustomTabSize.HasValue && buf.CustomTabSize.Value > 0 ? buf.CustomTabSize.Value : (settings.TabSize > 0 ? settings.TabSize : 4);
            bool spaces = buf.CustomInsertSpaces.HasValue ? buf.CustomInsertSpaces.Value : settings.InsertSpaces;
            string before = LspEditing.BufferText(buf);
            List<LspTextEdit> edits = await mgr.RequestFormattingAsync(path, range, tab, spaces, CancellationToken.None);
            if (edits == null)
            {
                Status("There is no formatter for '" + lang + "' files installed.");
                return;
            }
            if (!ReferenceEquals(ctrl.CurrentBuffer, buf) || LspEditing.BufferText(buf) != before) { Status("Format skipped: the document changed."); return; }
            if (edits.Count == 0) { Status("Already formatted."); return; }
            if (!LspEditing.ApplyEditsToBuffer(buf, edits, true)) { Status("Format failed: overlapping edits."); return; }
            ctrl.NotifyBufferContentChanged();
            ctrl.Canvas.Invalidate();
            Status("Formatted (" + edits.Count + " edit" + (edits.Count == 1 ? "" : "s") + ").");
        }

        public async void QuickFix(EditorTabBuffer buf)
        {
            string path = PathFor(buf);
            if (path == null || !mgr.Supports(path, "codeActionProvider")) { Status("No code actions available (no language server)."); return; }
            LspRange range;
            if (buf.HasSelection())
            {
                int sl, sc, el, ec;
                buf.GetNormalizedSelection(out sl, out sc, out el, out ec);
                range = new LspRange(sl, sc, el, ec);
            }
            else range = new LspRange(buf.CursorLine, buf.CursorCol, buf.CursorLine, buf.CursorCol);
            LspEditGuard listGuard = LspEditGuard.Capture(GetOpenTabs != null ? GetOpenTabs() : null, mgr);
            List<LspCodeAction> actions = await mgr.RequestCodeActionsAsync(path, range, CancellationToken.None);
            if (actions == null || actions.Count == 0) { Status("No code actions available."); return; }
            List<LspCodeAction> ordered = actions.OrderByDescending(a => a.IsPreferred).ThenBy(a => a.DisabledReason != null).ToList();
            List<string> rows = new List<string>();
            List<bool> enabled = new List<bool>();
            foreach (LspCodeAction a in ordered)
            {
                rows.Add((a.IsPreferred ? "★ " : "   ") + a.Title + (a.DisabledReason != null ? "   (" + a.DisabledReason + ")" : ""));
                enabled.Add(a.DisabledReason == null);
            }
            int i = Pick("Code Actions", rows, enabled);
            if (i < 0) return;
            LspCodeAction chosen = ordered[i];
            // An edit that came with the list was computed for the text at list time; a lazily resolved one for the text at resolve time.
            LspEditGuard guard = chosen.Edit != null ? listGuard : LspEditGuard.Capture(GetOpenTabs != null ? GetOpenTabs() : null, mgr);
            LspCodeAction resolved = await mgr.ResolveCodeActionAsync(path, chosen, CancellationToken.None) ?? chosen;
            if (resolved.Edit != null) ApplyWorkspaceEdit(resolved.Edit, resolved.Title + ":", guard);
            if (resolved.Command != null && mgr.ClassifyCommand(path, resolved.Command) == LanguageServerManager.LspCommandTarget.Server)
                await mgr.ExecuteCommandAsync(path, resolved.Command, CancellationToken.None);
        }

        /// <summary>Palette / menu commands ("lsp_*").</summary>
        public void ExecuteCommand(string id, CodeLessEditorControl ctrl)
        {
            EditorTabBuffer buf = ctrl != null ? ctrl.CurrentBuffer : Active;
            switch (id)
            {
                case "lsp_restart": RestartActive(); break;
                case "lsp_log": ShowLog(); break;
                case "lsp_config": ShowSettingsDialog(); break;
                case "lsp_install": ShowInstallPicker(); break;
                case "lsp_install_log": ShowInstallLog(); break;
                case "lsp_typedef": if (buf != null) NavigateAsync(buf, buf.CursorLine, buf.CursorCol, "textDocument/typeDefinition", "type definitions", null); break;
                case "lsp_impl": if (buf != null) NavigateAsync(buf, buf.CursorLine, buf.CursorCol, "textDocument/implementation", "implementations", null); break;
                case "lsp_refs": if (buf != null) FindReferences(buf, delegate { Status("Find All References needs a language server for this file."); }); break;
                case "lsp_rename": if (buf != null) Rename(buf, delegate { Status("Rename Symbol needs a language server for this file."); }); break;
                case "lsp_format": Format(ctrl, false); break;
                case "lsp_quickfix": if (buf != null) QuickFix(buf); break;
            }
        }

        /// <summary>Editor shortcuts handled through the server (only when one serves the buffer; else the editor's own handling runs).</summary>
        internal bool HandleEditorKey(CodeLessEditorControl ctrl, KeyEventArgs e)
        {
            EditorTabBuffer buf = ctrl.CurrentBuffer;
            if (buf == null || PathFor(buf) == null) return false;
            bool active = IsActiveFor(buf);
            if (!active) return false;
            if (e.KeyCode == Keys.F12 && !e.Shift && !e.Alt && !e.Control)
            {
                int line = buf.CursorLine, col = buf.CursorCol;
                NavigateAsync(buf, line, col, "textDocument/definition", "definitions", delegate
                {
                    if (ReferenceEquals(ctrl.CurrentBuffer, buf) && !ctrl.TriggerFollowLinkAt(line, col))
                        ctrl.TriggerGoToDefinition(LspEditing.WordAtOrEmpty(buf.Lines[line], col));
                });
                return true;
            }
            if (e.KeyCode == Keys.F12 && e.Control && !e.Shift && !e.Alt) { ExecuteCommand("lsp_impl", ctrl); return true; }
            if (e.KeyCode == Keys.F12 && e.Shift && !e.Control && !e.Alt)
            {
                FindReferences(buf, delegate { ctrl.TriggerGoToReferences(LspEditing.WordAtOrEmpty(buf.Lines[buf.CursorLine], buf.CursorCol)); });
                return true;
            }
            if (e.KeyCode == Keys.F2 && !e.Control && !e.Shift && !e.Alt && mgr.Supports(buf.FilePath, "renameProvider"))
            {
                Rename(buf, null);
                return true;
            }
            if (e.KeyCode == Keys.F && e.Shift && e.Alt && !e.Control) { Format(ctrl, buf.HasSelection()); return true; }
            if (e.KeyCode == Keys.OemPeriod && e.Control && !e.Shift && !e.Alt) { QuickFix(buf); return true; }
            return false;
        }

        /// <summary>Ctrl+Click on an identifier: server first, then the built-in link handling.</summary>
        public bool TryFollowSymbolLink(EditorTabBuffer source, EditorLinkTarget target, Action fallback)
        {
            if (target == null || target.Kind != EditorLinkTarget.KindSymbol || !IsActiveFor(source)) return false;
            int line = -1, col = -1;
            foreach (WinFormsLspEditorBinding b in bindings)
                if (ReferenceEquals(b.Editor.CurrentBuffer, source) && b.LastMouseLineCol(out line, out col)) break;
            if (line < 0) { line = source.CursorLine; col = source.CursorCol; }
            NavigateAsync(source, line, col, "textDocument/definition", "definitions", fallback);
            return true;
        }

        public void AddMenuItems(ToolStripMenuItem goMenu, Func<string, string, Action, ToolStripMenuItem> create)
        {
            if (goMenu == null || create == null) return;
            goMenu.DropDownItems.Add(new ToolStripSeparator());
            goMenu.DropDownItems.Add(create("Go to Type Definition", "", delegate { ExecuteCommand("lsp_typedef", null); }));
            goMenu.DropDownItems.Add(create("Go to Implementation", "Ctrl+F12", delegate { ExecuteCommand("lsp_impl", null); }));
            goMenu.DropDownItems.Add(create("Find All References (Language Server)", "Shift+F12", delegate { ExecuteCommand("lsp_refs", null); }));
            goMenu.DropDownItems.Add(create("Rename Symbol (Language Server)", "F2", delegate { ExecuteCommand("lsp_rename", null); }));
            goMenu.DropDownItems.Add(create("Quick Fix...", "Ctrl+.", delegate { ExecuteCommand("lsp_quickfix", null); }));
            goMenu.DropDownItems.Add(new ToolStripSeparator());
            goMenu.DropDownItems.Add(create("Restart Language Server", "", delegate { ExecuteCommand("lsp_restart", null); }));
            goMenu.DropDownItems.Add(create("Show Language Server Log", "", delegate { ExecuteCommand("lsp_log", null); }));
            goMenu.DropDownItems.Add(create("Configure Language Servers...", "", delegate { ExecuteCommand("lsp_config", null); }));
            goMenu.DropDownItems.Add(create("Language Servers: Install/Update...", "", delegate { ExecuteCommand("lsp_install", null); }));
        }
    }

    /// <summary>Per-editor LSP UI state (completion popup, hover / signature tips) for the WinForms canvas.</summary>
    internal sealed partial class WinFormsLspEditorBinding
    {
        private readonly WinFormsLanguageServerController owner;
        public readonly CodeLessEditorControl Editor;
        private readonly Panel completionFrame;
        private readonly ListBox completionList;
        private readonly Label completionDoc;
        private readonly Label hoverTip;
        private readonly Label signatureTip;
        private readonly System.Windows.Forms.Timer hoverTimer;
        private readonly System.Windows.Forms.Timer symbolsTimer;
        private LspCompletionSession session;
        private CancellationTokenSource completionCts, hoverCts, signatureCts, resolveCts;
        private Point hoverPt, lastMousePt;
        private bool hasMousePt;
        private int completionGeneration;
        private int signatureLine = -1;

        public WinFormsLspEditorBinding(WinFormsLanguageServerController owner, CodeLessEditorControl editor)
        {
            this.owner = owner;
            Editor = editor;
            completionFrame = new Panel { Visible = false, Size = new Size(420, 12 * 20 + 4), Padding = new Padding(1) };
            completionList = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 418,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 20,
                IntegralHeight = false,
                Font = new Font("Consolas", 9.5f),
                TabStop = false
            };
            completionList.DrawItem += DrawItem;
            completionList.MouseDoubleClick += delegate { AcceptSelected(); };
            completionList.SelectedIndexChanged += delegate
            {
                if (session != null && completionList.SelectedIndex >= 0 && completionList.SelectedIndex != session.SelectedIndex)
                {
                    session.SelectedIndex = completionList.SelectedIndex;
                    ScheduleResolve();
                }
            };
            completionDoc = new Label { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8, 6, 8, 6), UseMnemonic = false, Font = new Font("Segoe UI", 9f) };
            completionFrame.Controls.Add(completionDoc);
            completionFrame.Controls.Add(completionList);
            hoverTip = NewTip();
            signatureTip = NewTip();
            Panel body = editor.EditorBodyHost;
            body.Controls.Add(completionFrame);
            body.Controls.Add(hoverTip);
            body.Controls.Add(signatureTip);
            hoverTimer = new System.Windows.Forms.Timer { Interval = 500 };
            hoverTimer.Tick += delegate { hoverTimer.Stop(); RequestHover(hoverPt); };
            symbolsTimer = new System.Windows.Forms.Timer { Interval = 700 };
            symbolsTimer.Tick += delegate { symbolsTimer.Stop(); RefreshSymbols(); };
            editor.Canvas.PreviewKeyHandler = OnPreviewKey;
            editor.Canvas.PreviewCharHandler = OnPreviewChar;
            editor.Canvas.CharTyped += OnCharTyped;
            editor.Canvas.MouseMovedOverCanvas += OnMouseMoved;
            editor.Canvas.MouseLeftCanvas += OnMouseLeft;
            editor.OnCursorOrSelectionChanged += OnCaretMoved;
            editor.SuppressBuiltInAutocomplete = delegate (EditorTabBuffer b) { return owner.IsActiveFor(b) || IsImportContextAtCaret(b); };
            editor.ExplicitSuggestHandler = delegate { return TryStartImportCompletionAtCaret(null); };
            ApplyTheme();
        }

        private static Label NewTip()
        {
            return new Label
            {
                Visible = false,
                AutoSize = true,
                MaximumSize = new Size(620, 320),
                Padding = new Padding(8, 5, 8, 5),
                BorderStyle = BorderStyle.FixedSingle,
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9f)
            };
        }

        public void Detach()
        {
            HideAll();
            hoverTimer.Stop();
            symbolsTimer.Stop();
            Editor.Canvas.PreviewKeyHandler = null;
            Editor.Canvas.PreviewCharHandler = null;
            Editor.Canvas.CharTyped -= OnCharTyped;
            Editor.Canvas.MouseMovedOverCanvas -= OnMouseMoved;
            Editor.Canvas.MouseLeftCanvas -= OnMouseLeft;
            Editor.OnCursorOrSelectionChanged -= OnCaretMoved;
            Editor.SuppressBuiltInAutocomplete = null;
            Editor.ExplicitSuggestHandler = null;
        }

        private StudioTheme Theme { get { return owner.GetTheme != null ? owner.GetTheme() : null; } }
        private LanguageServerManager Lsp { get { return owner.Manager; } }
        private EditorTabBuffer Buf { get { return Editor.CurrentBuffer; } }
        private string PathOrNull { get { return owner.PathFor(Buf); } }

        public void ApplyTheme()
        {
            StudioTheme t = Theme;
            if (t == null) return;
            completionFrame.BackColor = t.BorderSubtle;
            completionList.BackColor = t.BgElevated;
            completionList.ForeColor = t.FgPrimary;
            completionDoc.BackColor = t.BgElevated;
            completionDoc.ForeColor = t.FgPrimary;
            foreach (Label l in new[] { hoverTip, signatureTip }) { l.BackColor = t.BgElevated; l.ForeColor = t.FgPrimary; }
        }

        public bool LastMouseLineCol(out int line, out int col)
        {
            line = -1; col = -1;
            if (!hasMousePt) return false;
            Editor.Canvas.HitTestLineCol(lastMousePt, out line, out col);
            return line >= 0;
        }

        public void HideAll()
        {
            CloseCompletion();
            HideHover();
            HideSignature();
        }

        private void HideHover()
        {
            hoverTimer.Stop();
            if (hoverCts != null) hoverCts.Cancel();
            hoverTip.Visible = false;
        }

        private void HideSignature()
        {
            if (signatureCts != null) signatureCts.Cancel();
            signatureTip.Visible = false;
        }

        private void CloseCompletion()
        {
            if (completionCts != null) completionCts.Cancel();
            if (resolveCts != null) resolveCts.Cancel();
            session = null;
            completionFrame.Visible = false;
        }

        // ---------------------------------------------------------------- keys

        private bool OnPreviewKey(KeyEventArgs e)
        {
            if (Buf == null || Lsp == null) return false;
            if (hoverTip.Visible) HideHover();
            if (completionFrame.Visible && session != null)
            {
                bool plain = !e.Control && !e.Alt;
                if (plain && !e.Shift && e.KeyCode == Keys.Down) { Move(1); return true; }
                if (plain && !e.Shift && e.KeyCode == Keys.Up) { Move(-1); return true; }
                if (plain && e.KeyCode == Keys.PageDown) { Move(12); return true; }
                if (plain && e.KeyCode == Keys.PageUp) { Move(-12); return true; }
                if (plain && !e.Shift && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)) { AcceptSelected(); return true; }
                if (e.KeyCode == Keys.Escape) { CloseCompletion(); return true; }
            }
            if (e.KeyCode == Keys.Escape && signatureTip.Visible) { HideSignature(); return true; }
            if (e.KeyCode == Keys.Space && e.Control && !e.Alt && !e.Shift && TryStartImportCompletionAtCaret(null)) return true;
            if (e.KeyCode == Keys.Space && e.Control && !e.Alt && owner.IsActiveFor(Buf))
            {
                if (e.Shift) TriggerSignatureHelp(null, false);
                else TriggerCompletion(null, false);
                return true;
            }
            return owner.HandleEditorKey(Editor, e);
        }

        private void Move(int delta)
        {
            session.MoveSelection(delta);
            completionList.SelectedIndex = session.SelectedIndex;
            ScheduleResolve();
        }

        // ---------------------------------------------------------------- typing / caret

        private bool OnPreviewChar(char ch)
        {
            if (Buf == null) return false;
            if (session != null && completionFrame.Visible && !LspEditing.IsWordChar(ch))
            {
                LanguageServerManager lsp = Lsp;
                string path = PathOrNull;
                IList<string> serverCommits = lsp != null && path != null ? lsp.GetCompletionCommitCharacters(path) : null;
                if (session.IsCommitCharacter(ch, serverCommits))
                {
                    LspCompletionSession s = session;
                    LspCompletionItem item = s.Selected;
                    EditorTabBuffer buf = Buf;
                    CloseCompletion();
                    if (item != null && buf != null)
                    {
                        PrepareImportAccept(s, item, buf);
                        if (LspEditing.ApplyCompletion(buf, item))
                        {
                            Editor.NotifyBufferContentChanged();
                            Editor.Canvas.Invalidate();
                        }
                    }
                }
            }
            return false;
        }

        private void OnCharTyped(char ch)
        {
            HideHover();
            if (TryImportCompletionOnTyped(ch)) return; // import paths / Python modules (merged with the server's items)
            LanguageServerManager lsp = Lsp;
            string path = PathOrNull;
            if (lsp == null || path == null || !lsp.IsActive(path)) return;
            EditorTabBuffer buf = Buf;
            string text = ch.ToString();
            if (ch == ')') HideSignature();
            if (lsp.GetSignatureTriggerCharacters(path, false).Contains(text)) TriggerSignatureHelp(text, false);
            else if (signatureTip.Visible) TriggerSignatureHelp(null, true);
            if (session != null && completionFrame.Visible)
            {
                if (LspEditing.IsWordChar(ch))
                {
                    if (session.IsIncomplete) TriggerCompletion(null, true);
                    return;
                }
                CloseCompletion();
            }
            string trig;
            if (LspCompletionSession.ShouldTrigger(buf.Lines[buf.CursorLine], buf.CursorCol, text, lsp.GetCompletionTriggerCharacters(path), 1, out trig))
                TriggerCompletion(trig, false);
        }

        public void OnBufferModified(EditorTabBuffer buf)
        {
            if (!ReferenceEquals(buf, Buf)) return;
            HideHover();
            if (session != null && completionFrame.Visible) Refilter();
            symbolsTimer.Stop();
            symbolsTimer.Start();
        }

        private void OnCaretMoved(EditorTabBuffer buf)
        {
            if (session != null && completionFrame.Visible) Refilter();
            if (signatureTip.Visible && buf != null && buf.CursorLine != signatureLine) HideSignature();
        }

        private void Refilter()
        {
            EditorTabBuffer buf = Buf;
            if (buf == null || session == null) return;
            buf.ClampCursor();
            if (!session.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol)) { CloseCompletion(); return; }
            FillList();
            ScheduleResolve();
        }

        // ---------------------------------------------------------------- completion

        public async void TriggerCompletion(string triggerCharacter, bool incompleteRetrigger)
        {
            LanguageServerManager lsp = Lsp;
            EditorTabBuffer buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null) return;
            if (!incompleteRetrigger && TryStartImportCompletionAtCaret(triggerCharacter)) return;
            if (completionCts != null) completionCts.Cancel();
            CancellationTokenSource cts = new CancellationTokenSource();
            completionCts = cts;
            int gen = ++completionGeneration;
            buf.ClampCursor();
            int line = buf.CursorLine, col = buf.CursorCol;
            string lineText = buf.Lines[line];
            int startCol = LspEditing.WordStart(lineText, col);
            LspCompletionList list;
            try { list = await lsp.RequestCompletionAsync(path, line, col, triggerCharacter, incompleteRetrigger, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || gen != completionGeneration || !ReferenceEquals(buf, Buf)) return;
            if (list == null || list.Items.Count == 0) { if (!incompleteRetrigger) CloseCompletion(); return; }
            LspCompletionSession s = new LspCompletionSession { FilePath = path, Line = line, StartCol = startCol };
            LspCompletionItem first = list.Items.FirstOrDefault(i => i.TextEdit != null && i.TextEdit.Range != null && i.TextEdit.Range.StartLine == line);
            if (first != null && first.TextEdit.Range.StartCharacter < startCol && IsWordRun(lineText, first.TextEdit.Range.StartCharacter, startCol))
                s.StartCol = first.TextEdit.Range.StartCharacter;
            s.SetItems(list);
            buf.ClampCursor();
            if (!s.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol)) { CloseCompletion(); return; }
            session = s;
            FillList();
            PositionCompletion();
            completionFrame.Visible = true;
            completionFrame.BringToFront();
            ScheduleResolve();
        }

        private static bool IsWordRun(string line, int from, int to)
        {
            for (int i = from; i < to && i < line.Length; i++) if (!LspEditing.IsWordChar(line[i])) return false;
            return true;
        }

        private void FillList()
        {
            completionList.BeginUpdate();
            completionList.Items.Clear();
            foreach (LspCompletionItem it in session.Visible) completionList.Items.Add(it);
            completionList.SelectedIndex = Math.Max(0, Math.Min(session.Visible.Count - 1, session.SelectedIndex));
            completionList.EndUpdate();
            completionFrame.Height = Math.Min(12, Math.Max(1, session.Visible.Count)) * completionList.ItemHeight + 4;
        }

        private void PositionCompletion()
        {
            Panel body = Editor.EditorBodyHost;
            Point caret = Editor.Canvas.GetCursorPixelPoint();
            int lh = Editor.Canvas.LineHeight;
            int y = caret.Y + lh + 2;
            if (y + completionFrame.Height > body.Height && caret.Y - completionFrame.Height - 2 > 0) y = caret.Y - completionFrame.Height - 2;
            int x = Math.Max(0, Math.Min(caret.X - 24, body.Width - completionFrame.Width - 20));
            completionFrame.Location = new Point(x, y);
        }

        private void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= completionList.Items.Count) return;
            StudioTheme t = Theme;
            LspCompletionItem it = completionList.Items[e.Index] as LspCompletionItem;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = t != null ? (sel ? t.BgSelection : t.BgElevated) : (sel ? SystemColors.Highlight : SystemColors.Window);
            Color fg = t != null ? t.FgPrimary : SystemColors.WindowText;
            Color fg2 = t != null ? t.FgSecondary : SystemColors.GrayText;
            Color accent = t != null ? t.AccentPrimary : SystemColors.HotTrack;
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            if (it == null) return;
            Rectangle glyph = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, 20, e.Bounds.Height);
            if (!DrawPathKindGlyph(e.Graphics, it, glyph, fg2))
                TextRenderer.DrawText(e.Graphics, KindGlyph(it.Kind), e.Font, glyph, accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            string right = LspCompletionSession.RightText(it);
            int rightW = Math.Min(170, TextRenderer.MeasureText(right, e.Font).Width);
            Rectangle labelRect = new Rectangle(e.Bounds.X + 24, e.Bounds.Y, e.Bounds.Width - 30 - rightW, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, LspCompletionSession.LeftText(it), e.Font, labelRect, fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            Rectangle rightRect = new Rectangle(e.Bounds.Right - rightW - 6, e.Bounds.Y, rightW, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, right, e.Font, rightRect, fg2, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        private static string KindGlyph(int kind)
        {
            switch (kind)
            {
                case 2: case 3: case 4: return "ƒ";
                case 5: case 10: return "◇";
                case 6: return "x";
                case 7: case 8: case 22: return "◆";
                case 9: return "▣";
                case 13: case 20: return "≡";
                case 14: return "k";
                case 15: return "⧉";
                case 21: return "π";
                default: return "·";
            }
        }

        private void ScheduleResolve()
        {
            LspCompletionItem item = session != null ? session.Selected : null;
            if (resolveCts != null) resolveCts.Cancel();
            if (item == null) return;
            SetDoc(DocFor(item));
            if (item.Resolved || Lsp == null) return;
            CancellationTokenSource cts = new CancellationTokenSource();
            resolveCts = cts;
            string path = session.FilePath;
            ResolveLater(path, item, cts);
        }

        private async void ResolveLater(string path, LspCompletionItem item, CancellationTokenSource cts)
        {
            await Task.Delay(80);
            if (cts.IsCancellationRequested || Lsp == null) return;
            LspCompletionItem r = await Lsp.ResolveCompletionAsync(path, item, cts.Token);
            if (cts.IsCancellationRequested || session == null || !ReferenceEquals(session.Selected, item)) return;
            SetDoc(DocFor(r ?? item));
        }

        private void SetDoc(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length > 1200) text = text.Substring(0, 1200) + "…";
            completionDoc.Text = text;
            bool show = text.Length > 0;
            completionDoc.Visible = show;
            completionFrame.Width = show ? 418 + 2 + 320 : 420;
        }

        private static string DocFor(LspCompletionItem it)
        {
            string detail = it.Detail ?? "";
            string doc = it.Documentation ?? "";
            if (detail.Length > 0 && doc.Length > 0) return detail + "\n\n" + doc;
            return detail + doc;
        }

        private async void AcceptSelected()
        {
            LspCompletionSession s = session;
            LspCompletionItem item = s != null ? s.Selected : null;
            EditorTabBuffer buf = Buf;
            if (item == null || buf == null) { CloseCompletion(); return; }
            string before = LspEditing.BufferText(buf);
            CloseCompletion();
            LanguageServerManager lsp = Lsp;
            if (lsp != null && !item.Resolved)
            {
                Task<LspCompletionItem> t = lsp.ResolveCompletionAsync(s.FilePath, item, CancellationToken.None);
                await Task.WhenAny(t, Task.Delay(400));
                if (!ReferenceEquals(buf, Buf) || LspEditing.BufferText(buf) != before) return;
            }
            PrepareImportAccept(s, item, buf);
            if (!LspEditing.ApplyCompletion(buf, item)) return;
            Editor.NotifyBufferContentChanged();
            Editor.Canvas.Invalidate();
            Editor.Canvas.Focus();
            if (RetriggerAfterImportAccept(item)) return; // accepted a folder: list its contents
            if (item.Command != null && lsp != null)
            {
                LanguageServerManager.LspCommandTarget target = lsp.ClassifyCommand(s.FilePath, item.Command);
                if (target == LanguageServerManager.LspCommandTarget.TriggerSuggest) { TriggerCompletion(null, false); return; }
                if (target == LanguageServerManager.LspCommandTarget.TriggerParameterHints) { TriggerSignatureHelp(null, false); return; }
                if (target == LanguageServerManager.LspCommandTarget.Server) { Task<object> ignored = lsp.ExecuteCommandAsync(s.FilePath, item.Command, CancellationToken.None); }
            }
            string inserted = item.InsertText ?? (item.TextEdit != null ? item.TextEdit.NewText : "") ?? "";
            if (item.IsSnippet && inserted.Contains("(")) TriggerSignatureHelp(null, false);
        }

        // ---------------------------------------------------------------- signature help

        public async void TriggerSignatureHelp(string triggerCharacter, bool retrigger)
        {
            LanguageServerManager lsp = Lsp;
            EditorTabBuffer buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null) return;
            if (signatureCts != null) signatureCts.Cancel();
            CancellationTokenSource cts = new CancellationTokenSource();
            signatureCts = cts;
            int line = buf.CursorLine, col = buf.CursorCol;
            LspSignatureHelp help;
            try { help = await lsp.RequestSignatureHelpAsync(path, line, col, triggerCharacter, retrigger, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || !ReferenceEquals(buf, Buf)) return;
            if (help == null || help.Active == null) { signatureTip.Visible = false; return; }
            LspSignatureHelp.Signature sig = help.Active;
            string label = sig.Label ?? "";
            int[] span = help.ActiveParameterSpan;
            string text = label;
            if (span != null && span[0] >= 0 && span[1] <= label.Length && span[1] > span[0])
                text = label.Substring(0, span[0]) + "【" + label.Substring(span[0], span[1] - span[0]) + "】" + label.Substring(span[1]);
            if (help.Signatures.Count > 1) text += "   (" + (help.ActiveSignature + 1) + "/" + help.Signatures.Count + ")";
            int ap = sig.ActiveParameter >= 0 ? sig.ActiveParameter : help.ActiveParameter;
            if (ap >= 0 && ap < sig.ParameterDocs.Count && !string.IsNullOrEmpty(sig.ParameterDocs[ap])) text += "\n" + sig.ParameterDocs[ap];
            if (!string.IsNullOrEmpty(sig.Documentation)) text += "\n" + sig.Documentation;
            signatureLine = line;
            signatureTip.Text = text;
            Point caret = Editor.Canvas.GetCursorPixelPoint();
            Size pref = signatureTip.GetPreferredSize(new Size(620, 320));
            int y = caret.Y - pref.Height - 2;
            if (y < 0) y = caret.Y + Editor.Canvas.LineHeight + 2;
            signatureTip.Location = new Point(Math.Max(0, caret.X - 24), y);
            signatureTip.Visible = true;
            signatureTip.BringToFront();
        }

        // ---------------------------------------------------------------- hover

        private void OnMouseMoved(Point pt)
        {
            if (hasMousePt && Math.Abs(pt.X - lastMousePt.X) < 2 && Math.Abs(pt.Y - lastMousePt.Y) < 2) return;
            lastMousePt = pt;
            hasMousePt = true;
            if (Lsp == null || PathOrNull == null) return;
            if (hoverTip.Visible) HideHover();
            hoverPt = pt;
            hoverTimer.Stop();
            hoverTimer.Start();
        }

        private void OnMouseLeft()
        {
            HideHover();
        }

        private async void RequestHover(Point pt)
        {
            LanguageServerManager lsp = Lsp;
            EditorTabBuffer buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null || completionFrame.Visible || !lsp.Supports(path, "hoverProvider")) return;
            if (pt.X < CodeLessVirtualCanvasGutter()) return;
            int line, col;
            Editor.Canvas.HitTestLineCol(pt, out line, out col);
            if (line < 0 || line >= buf.Lines.Count || col >= buf.Lines[line].Length) return;
            if (hoverCts != null) hoverCts.Cancel();
            CancellationTokenSource cts = new CancellationTokenSource();
            hoverCts = cts;
            LspHover hover;
            try { hover = await lsp.RequestHoverAsync(path, line, col, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || !ReferenceEquals(buf, Buf) || hover == null || string.IsNullOrWhiteSpace(hover.Text)) return;
            string text = hover.Text.Length > 3000 ? hover.Text.Substring(0, 3000) + "…" : hover.Text;
            hoverTip.Text = text;
            Panel body = Editor.EditorBodyHost;
            Size pref = hoverTip.GetPreferredSize(new Size(620, 320));
            int lh = Editor.Canvas.LineHeight;
            int y = pt.Y + lh;
            if (y + pref.Height > body.Height && pt.Y - pref.Height - 4 > 0) y = pt.Y - pref.Height - 4;
            hoverTip.Location = new Point(Math.Max(0, Math.Min(pt.X - 8, body.Width - pref.Width - 8)), y);
            hoverTip.Visible = true;
            hoverTip.BringToFront();
        }

        private int CodeLessVirtualCanvasGutter()
        {
            return Editor.Canvas.GutterPixelWidth;
        }

        // ---------------------------------------------------------------- outline

        private async void RefreshSymbols()
        {
            LanguageServerManager lsp = Lsp;
            EditorTabBuffer buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null || !lsp.Supports(path, "documentSymbolProvider")) return;
            List<LspDocumentSymbol> syms = await lsp.RequestDocumentSymbolsAsync(path, CancellationToken.None);
            if (syms == null || !ReferenceEquals(buf, Buf)) return;
            LspSymbolsStore.Set(path, syms);
            LspSymbolsStore.ApplyTo(buf);
            if (owner.RefreshProblems != null) owner.RefreshProblems(buf);
        }
    }
}
