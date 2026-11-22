using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// WinForms side of automatic language server installs (same engine as Avalonia: LanguageServerInstaller in
    /// codeLess.Core): progress in the status bar item and the Output panel, the failure notification (Show Log / Copy
    /// Command / Don't Ask Again), the install log and "Language Servers: Install/Update...". STRICT C# 5.
    /// </summary>
    public sealed partial class WinFormsLanguageServerController
    {
        private void InitInstalls()
        {
            mgr.Installer = LanguageServerInstaller.Shared;
            mgr.AutoInstallEnabled = delegate { return settings == null || settings.AutoInstallLanguageServers; };
            mgr.InstallOutput += delegate (string line) { if (AppendOutput != null) AppendOutput("[Language Servers] " + line); };
            mgr.InstallFinished += delegate (LanguageServerInstallResult r)
            {
                if (r.Success || r.Cancelled) Status(r.Message);
            };
        }

        private void AddInstallMenuItems(ContextMenuStrip menu, LanguageServerStatus st)
        {
            if (mgr == null || st == null) return;
            LanguageServerDefinition def = LanguageServerRegistry.DefinitionForLanguage(st.LanguageId ?? "");
            if (def == null) return;
            string id = def.Id;
            if (st.State == LanguageServerState.Installing)
            {
                ToolStripMenuItem cancel = new ToolStripMenuItem("Cancel Installation");
                cancel.Click += delegate { if (mgr != null) mgr.CancelInstall(id); };
                ToolStripMenuItem log = new ToolStripMenuItem("Show Installation Log");
                log.Click += delegate { ShowInstallLog(); };
                menu.Items.Add(cancel);
                menu.Items.Add(log);
                menu.Items.Add(new ToolStripSeparator());
            }
            else if (st.State == LanguageServerState.NotInstalled && st.CanInstall)
            {
                ToolStripMenuItem install = new ToolStripMenuItem("Install " + def.DisplayName + " Language Server");
                install.Click += delegate { InstallServer(id); };
                menu.Items.Add(install);
            }
        }

        /// <summary>Installs (or updates) one server family now (ignores the failure backoff).</summary>
        public void InstallServer(string serverId)
        {
            if (mgr == null) return;
            LanguageServerInstallJob job = mgr.InstallServer(serverId);
            if (job != null) Status(job.StatusText);
        }

        /// <summary>"Language Servers: Install/Update...": pick a server family to install or update.</summary>
        public void ShowInstallPicker()
        {
            if (mgr == null) return;
            string root = Root;
            string managed = mgr.ManagedServersRoot;
            LanguageServerUserSettings user = settings.GetLanguageServerUserSettings();
            Status("Detecting language servers...");
            Task.Factory.StartNew(delegate
            {
                List<string> ids = new List<string>();
                List<string> labels = new List<string>();
                foreach (LanguageServerDefinition d in LanguageServerRegistry.Definitions)
                {
                    if (LanguageServerInstaller.RecipesFor(d.Id).Count == 0) continue;
                    string state;
                    try
                    {
                        LanguageServerResolution r = LanguageServerRegistry.Resolve(d.LanguageIds[0], root, user, managed);
                        state = r.FromManagedFolder ? "installed by codeLess - update" : r.Executable != null ? "found " + r.DisplayName + " (" + r.Executable + ") - install a codeLess copy" : "not installed";
                    }
                    catch (Exception ex) { state = ex.Message; }
                    ids.Add(d.Id);
                    labels.Add(d.DisplayName + "  -  " + state);
                }
                PostToUi(delegate
                {
                    Status("");
                    int i = Pick("Language Servers: Install/Update", labels, null);
                    if (i >= 0 && i < ids.Count) InstallServer(ids[i]);
                });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void ShowInstallLog()
        {
            LanguageServerInstaller installer = mgr != null ? mgr.Installer : null;
            string text = installer != null ? installer.GetLog() : "";
            if (string.IsNullOrEmpty(text)) text = "No language server was installed in this session.";
            using (Form d = NewDialog("Language Server Installs", 960, 620))
            {
                TextBox box = NewMonoBox(true);
                box.Text = ("Servers are installed into " + (mgr != null ? mgr.ManagedServersRoot : LanguageServerInstaller.DefaultManagedRoot()) + "\n\n" + text).Replace("\r\n", "\n").Replace("\n", "\r\n");
                FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
                Button close = MakeButton("Close", Theme);
                close.Click += delegate { d.Close(); };
                Button copy = MakeButton("Copy", Theme);
                copy.Click += delegate { try { Clipboard.SetText(box.Text ?? ""); } catch { } };
                row.Controls.Add(close);
                row.Controls.Add(copy);
                d.Controls.Add(box);
                d.Controls.Add(row);
                d.Shown += delegate { box.SelectionStart = box.TextLength; box.ScrollToCaret(); };
                d.ShowDialog(form);
            }
        }

        /// <summary>Failure notification of an automatic install: Show Log / Copy Command / Don't Ask Again for &lt;lang&gt;.</summary>
        private void ShowInstallFailure(LanguageServerHint hint)
        {
            Status(hint.Message);
            if (AppendOutput != null) AppendOutput("[Language Servers] " + hint.Message);
            StudioTheme t = Theme;
            if (toast != null) { form.Controls.Remove(toast); toast.Dispose(); toast = null; }
            Panel p = new Panel { Width = 500, Height = 108, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(10) };
            if (t != null) { p.BackColor = t.BgElevated; p.ForeColor = t.FgPrimary; }
            Label text = new Label { Text = hint.Message, AutoSize = false, Dock = DockStyle.Top, Height = 58, UseMnemonic = false };
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
            Button never = MakeButton("Don't Ask Again for " + hint.LanguageName, t);
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
                string cmd = hint.InstallCommand;
                Button copy = MakeButton("Copy Command", t);
                copy.Click += delegate
                {
                    try { Clipboard.SetText(cmd); Status("Install command copied to the clipboard."); } catch { }
                    close();
                };
                row.Controls.Add(copy);
            }
            Button log = MakeButton("Show Log", t);
            log.Click += delegate { close(); ShowInstallLog(); };
            row.Controls.Add(log);
            p.Controls.Add(row);
            p.Controls.Add(text);
            p.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            p.Location = new Point(Math.Max(0, form.ClientSize.Width - p.Width - 18), Math.Max(0, form.ClientSize.Height - p.Height - 40));
            toast = p;
            form.Controls.Add(p);
            p.BringToFront();
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 30000 };
            timer.Tick += delegate { timer.Stop(); timer.Dispose(); close(); };
            timer.Start();
        }
    }
}
