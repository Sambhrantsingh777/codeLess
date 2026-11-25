using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code-style Workspace Trust UI (WinForms): first-open prompt, "Restricted Mode" status-bar item, Restricted Mode
    /// banner, Manage Workspace Trust dialog (+ a block in Settings listing trusted folders), palette / menu commands and
    /// live re-evaluation on trust changes (language servers restart, git refreshes). Rules: <see cref="WorkspaceTrust"/>.
    /// Strict C# 5.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private Label statusRestrictedItem;
        private Panel restrictedBannerWin;
        private string restrictedBannerDismissedForWin;
        private bool workspaceTrustInitialized;

        /// <summary>Palette entries: category, title, shortcut, command id.</summary>
        public static readonly string[][] WorkspaceTrustPaletteCommands = new string[][]
        {
            new string[] { "Workspaces", "Workspaces: Manage Workspace Trust", "", "trust_manage" },
            new string[] { "Workspaces", "Workspaces: Trust This Folder", "", "trust_this" },
            new string[] { "Workspaces", "Workspaces: Restricted Mode (Don't Trust This Folder)", "", "trust_restrict" }
        };

        public bool IsWorkspaceRestrictedWin
        {
            get { return !string.IsNullOrEmpty(workspaceRoot) && WorkspaceTrust.IsRestricted(workspaceRoot); }
        }

        /// <summary>Hook: called once the status bar exists.</summary>
        private void InitializeWorkspaceTrustWin()
        {
            if (workspaceTrustInitialized) return;
            workspaceTrustInitialized = true;
            statusRestrictedItem = new Label
            {
                AutoSize = false,
                Height = 20,
                Text = "⛨ Restricted Mode",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = StatusItemFont,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(0x8A, 0x5A, 0x00),
                Cursor = Cursors.Hand,
                Visible = false,
                UseMnemonic = false
            };
            statusRestrictedItem.Width = TextRenderer.MeasureText(statusRestrictedItem.Text, StatusItemFont).Width + 16;
            new ToolTip().SetToolTip(statusRestrictedItem, "Restricted Mode: tasks, debugging, git, workspace language servers and verification are disabled. Click to manage Workspace Trust.");
            statusRestrictedItem.Click += delegate { ShowManageWorkspaceTrustWin(); };
            if (statusBar != null) statusBar.Controls.Add(statusRestrictedItem);

            restrictedBannerWin = BuildRestrictedBannerWin();
            try
            {
                Control host = editorControl != null ? editorControl.Parent : null;
                if (host != null)
                {
                    host.Controls.Add(restrictedBannerWin);
                    restrictedBannerWin.SendToBack(); // docked first: sits above everything else in the editor column
                }
            }
            catch { }

            WorkspaceTrust.TrustChanged += OnWorkspaceTrustChangedAnyThreadWin;
            FormClosed += delegate { WorkspaceTrust.TrustChanged -= OnWorkspaceTrustChangedAnyThreadWin; };
            ApplyWorkspaceTrustStateWin();
        }

        private Panel BuildRestrictedBannerWin()
        {
            Panel p = new Panel { Dock = DockStyle.Top, Height = 30, Visible = false, Padding = new Padding(8, 4, 8, 4) };
            Label text = new Label { Dock = DockStyle.Fill, Text = "⛨  " + WorkspaceTrust.BannerText, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Font = new Font("Segoe UI", 9f) };
            Button close = new Button { Dock = DockStyle.Right, Width = 28, Text = "✕", FlatStyle = FlatStyle.Flat };
            close.FlatAppearance.BorderSize = 0;
            Button learn = new Button { Dock = DockStyle.Right, Width = 90, Text = "Learn More", FlatStyle = FlatStyle.Flat };
            Button manage = new Button { Dock = DockStyle.Right, Width = 80, Text = "Manage", FlatStyle = FlatStyle.Flat };
            manage.Click += delegate { ShowManageWorkspaceTrustWin(); };
            learn.Click += delegate { EditorLinkResolver.OpenExternalUrl(WorkspaceTrust.LearnMoreUrl); };
            close.Click += delegate { restrictedBannerDismissedForWin = workspaceRoot; ApplyWorkspaceTrustStateWin(); };
            p.Controls.Add(text);
            p.Controls.Add(manage);
            p.Controls.Add(learn);
            p.Controls.Add(close);
            return p;
        }

        private void OnWorkspaceTrustChangedAnyThreadWin()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(OnWorkspaceTrustChangedWin));
            }
            catch { }
        }

        /// <summary>Re-enables / disables trust-gated features at once (UI thread, no restart).</summary>
        public void OnWorkspaceTrustChangedWin()
        {
            if (IsDisposed) return;
            ApplyWorkspaceTrustStateWin();
            try { if (lspController != null && lspController.Manager != null) lspController.Manager.OnWorkspaceTrustChanged(); } catch { }
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                try { RefreshGitSidebarStatus(); } catch { }
                try { PythonInterpreterService.RefreshInBackground(workspaceRoot); } catch { }
            }
        }

        /// <summary>Hook from LoadWorkspace: indicators + the first-open prompt.</summary>
        private void OnWorkspaceTrustFolderOpenedWin()
        {
            ApplyWorkspaceTrustStateWin();
            if (string.IsNullOrEmpty(workspaceRoot) || !WorkspaceTrust.NeedsPrompt(workspaceRoot)) return;
            if (SuppressUnsavedPrompts) return; // automated / smoke runs: stay in Restricted Mode, never block on a dialog
            string folder = workspaceRoot;
            // After the folder has loaded (the prompt must not block LoadWorkspace's own UI work).
            try { if (IsHandleCreated) BeginInvoke(new Action(delegate { if (folder == workspaceRoot && WorkspaceTrust.NeedsPrompt(folder)) ShowWorkspaceTrustPromptWin(); })); else ShowWorkspaceTrustPromptWin(); }
            catch { }
        }

        private void ApplyWorkspaceTrustStateWin()
        {
            bool restricted = IsWorkspaceRestrictedWin;
            if (statusRestrictedItem != null) statusRestrictedItem.Visible = restricted;
            if (restrictedBannerWin != null)
            {
                restrictedBannerWin.Visible = restricted && !string.Equals(restrictedBannerDismissedForWin, workspaceRoot, StringComparison.Ordinal);
                if (activeTheme != null)
                {
                    bool isLight = activeTheme.IsLight;
                    restrictedBannerWin.BackColor = isLight ? Color.FromArgb(0xFF, 0xF4, 0xCE) : Color.FromArgb(0x35, 0x2A, 0x05);
                    foreach (Control c in restrictedBannerWin.Controls)
                    {
                        Button b = c as Button;
                        if (b != null && b.Text == "Manage")
                        {
                            b.BackColor = activeTheme.AccentPrimary;
                            b.ForeColor = Color.White;
                            b.FlatAppearance.BorderSize = 1;
                            b.FlatAppearance.BorderColor = activeTheme.AccentPrimary;
                        }
                        else if (b != null && b.Text == "Learn More")
                        {
                            b.BackColor = isLight ? Color.FromArgb(0xE5, 0xE5, 0xE5) : Color.FromArgb(0x31, 0x31, 0x31);
                            b.ForeColor = activeTheme.FgPrimary;
                            b.FlatAppearance.BorderSize = 1;
                            b.FlatAppearance.BorderColor = isLight ? Color.FromArgb(0xCE, 0xCE, 0xCE) : Color.FromArgb(0x3C, 0x3C, 0x3C);
                        }
                        else
                        {
                            c.ForeColor = activeTheme.FgPrimary;
                        }
                    }
                }
            }
            LayoutStatusBarItems();
        }

        /// <summary>Hook from LayoutStatusBarItems: the Restricted Mode item sits at the far left.</summary>
        private int LayoutRestrictedModeItem(int leftX)
        {
            if (statusRestrictedItem == null || !statusRestrictedItem.Visible) return leftX;
            statusRestrictedItem.Location = new Point(leftX, 1);
            return statusRestrictedItem.Right + 6;
        }

        private bool ExecuteWorkspaceTrustCommandWin(string id)
        {
            switch (id)
            {
                case "trust_manage": ShowManageWorkspaceTrustWin(); return true;
                case "trust_this": if (!string.IsNullOrEmpty(workspaceRoot)) WorkspaceTrust.Trust(workspaceRoot); return true;
                case "trust_restrict": if (!string.IsNullOrEmpty(workspaceRoot)) WorkspaceTrust.Restrict(workspaceRoot); return true;
            }
            return false;
        }

        public void ShowWorkspaceTrustPromptWin()
        {
            string folder = workspaceRoot;
            if (string.IsNullOrEmpty(folder)) return;
            using (WorkspaceTrustPromptDialog dlg = new WorkspaceTrustPromptDialog(folder, activeTheme))
            {
                dlg.ShowDialog(this);
                if (dlg.Answer == 0) return; // closed without answering: stays in Restricted Mode, asked again next time
                WorkspaceTrust.ApplyPromptAnswer(folder, dlg.Answer == 1, dlg.TrustParent);
                statusLeftLabel.Text = dlg.Answer == 1 ? "Trusted " + folder + ": all features are enabled."
                    : "Restricted Mode: codeLess won't run anything from this folder. Use Manage Workspace Trust to change it.";
            }
        }

        public void ShowManageWorkspaceTrustWin()
        {
            using (ManageWorkspaceTrustDialog dlg = new ManageWorkspaceTrustDialog(workspaceRoot, activeTheme))
                dlg.ShowDialog(this);
        }
    }

    /// <summary>"Do you trust the authors of the files in this folder?"</summary>
    public sealed class WorkspaceTrustPromptDialog : Form
    {
        /// <summary>0 = no answer, 1 = trust, 2 = Restricted Mode.</summary>
        public int Answer;
        public bool TrustParent;

        public WorkspaceTrustPromptDialog(string folder, StudioTheme theme)
        {
            Text = "Workspace Trust";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 250);
            Font = new Font("Segoe UI", 9f);
            if (theme != null) { BackColor = theme.BgElevated; ForeColor = theme.FgPrimary; }
            Label title = new Label { Location = new Point(16, 14), Size = new Size(568, 24), Text = WorkspaceTrust.PromptTitle, Font = new Font("Segoe UI Semibold", 11f), UseMnemonic = false };
            Label body = new Label { Location = new Point(16, 44), Size = new Size(568, 78), Text = WorkspaceTrust.PromptBody, UseMnemonic = false };
            Label path = new Label { Location = new Point(16, 124), Size = new Size(568, 20), Text = folder, UseMnemonic = false, AutoEllipsis = true };
            if (theme != null) path.ForeColor = theme.FgSecondary;
            Controls.Add(title);
            Controls.Add(body);
            Controls.Add(path);
            string parent = WorkspaceTrust.ParentFolderForPrompt(folder);
            CheckBox parentChk = null;
            if (parent != null)
            {
                parentChk = new CheckBox { Location = new Point(16, 150), Size = new Size(568, 22), Text = "Trust the authors of all files in the parent folder '" + Path.GetFileName(parent) + "'", UseMnemonic = false };
                Controls.Add(parentChk);
            }
            Button trust = new Button { Location = new Point(16, 196), Size = new Size(200, 32), Text = "Yes, I trust the authors", FlatStyle = FlatStyle.Flat };
            Button restrict = new Button { Location = new Point(226, 196), Size = new Size(358, 32), Text = "No, I don't trust the authors (Restricted Mode)", FlatStyle = FlatStyle.Flat };
            if (theme != null) { trust.BackColor = theme.AccentPrimary; trust.ForeColor = Color.White; }
            trust.Click += delegate { Answer = 1; TrustParent = parentChk != null && parentChk.Checked; Close(); };
            restrict.Click += delegate { Answer = 2; Close(); };
            Controls.Add(trust);
            Controls.Add(restrict);
            AcceptButton = restrict;
            CancelButton = restrict;
        }
    }

    /// <summary>Manage Workspace Trust: the open folder's state and the list of trusted folders (changes apply at once).</summary>
    public sealed class ManageWorkspaceTrustDialog : Form
    {
        private readonly string folder;
        private readonly StudioTheme theme;

        public ManageWorkspaceTrustDialog(string folder, StudioTheme theme)
        {
            this.folder = folder;
            this.theme = theme;
            Text = "Workspace Trust";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 400);
            Font = new Font("Segoe UI", 9f);
            if (theme != null) { BackColor = theme.BgElevated; ForeColor = theme.FgPrimary; }
            Rebuild();
        }

        private void Rebuild()
        {
            SuspendLayout();
            Controls.Clear();
            int y = 14;
            if (!string.IsNullOrEmpty(folder))
            {
                bool trusted = WorkspaceTrust.IsTrusted(folder);
                Label state = new Label
                {
                    Location = new Point(16, y), Size = new Size(608, 40), UseMnemonic = false, AutoEllipsis = true,
                    Text = (trusted ? "This folder is trusted: all features are enabled." : "This folder is in Restricted Mode: codeLess won't run tasks, debugging, git, workspace language servers or verification here.") + "\n" + folder
                };
                Controls.Add(state);
                y += 46;
                if (!trusted)
                {
                    Button t = new Button { Location = new Point(16, y), Size = new Size(130, 28), Text = "Trust Folder", FlatStyle = FlatStyle.Flat };
                    t.Click += delegate { WorkspaceTrust.Trust(folder); Rebuild(); };
                    Controls.Add(t);
                    string parent = WorkspaceTrust.ParentFolderForPrompt(folder);
                    if (parent != null)
                    {
                        Button tp = new Button { Location = new Point(156, y), Size = new Size(160, 28), Text = "Trust Parent Folder", FlatStyle = FlatStyle.Flat };
                        tp.Click += delegate { WorkspaceTrust.Trust(parent); Rebuild(); };
                        Controls.Add(tp);
                    }
                }
                else
                {
                    Button r = new Button { Location = new Point(16, y), Size = new Size(220, 28), Text = "Don't Trust (Restricted Mode)", FlatStyle = FlatStyle.Flat };
                    string covering = WorkspaceTrust.TrustedRootFor(folder);
                    if (covering != null && !string.Equals(LspText.PathKey(covering), LspText.PathKey(folder), StringComparison.Ordinal)) r.Enabled = false;
                    r.Click += delegate { WorkspaceTrust.Restrict(folder); Rebuild(); };
                    Controls.Add(r);
                }
                y += 38;
            }
            WorkspaceTrustSettingsBlockWin.AddTrustedFolderList(this, theme, 16, ref y, 608, Rebuild);
            Button close = new Button { Location = new Point(524, 360), Size = new Size(100, 28), Text = "Close", FlatStyle = FlatStyle.Flat };
            close.Click += delegate { Close(); };
            Controls.Add(close);
            CancelButton = close;
            ResumeLayout();
        }
    }

    /// <summary>Trusted-folder list with Remove (Settings and the Manage dialog). Changes apply immediately.</summary>
    public static class WorkspaceTrustSettingsBlockWin
    {
        public static void Build(Panel body, StudioTheme theme, ref int y)
        {
            Label hdr = new Label { Location = new Point(22, y), Size = new Size(680, 22), Text = "WORKSPACE TRUST", Font = new Font("Segoe UI Semibold", 9.2f), UseMnemonic = false };
            if (theme != null) hdr.ForeColor = theme.AccentPrimary;
            body.Controls.Add(hdr);
            y += 26;
            Panel host = new Panel { Location = new Point(24, y), Size = new Size(640, 150) };
            body.Controls.Add(host);
            Action rebuild = null;
            rebuild = delegate
            {
                host.Controls.Clear();
                int yy = 0;
                AddTrustedFolderList(host, theme, 0, ref yy, 640, rebuild);
            };
            rebuild();
            y += 156;
        }

        public static void AddTrustedFolderList(Control parent, StudioTheme theme, int left, ref int y, int width, Action rebuild)
        {
            Label lbl = new Label { Location = new Point(left, y), Size = new Size(width, 20), Text = "Trusted folders (codeLess runs workspace tools, tasks, debugging and git here):", UseMnemonic = false };
            if (theme != null) lbl.ForeColor = theme.FgSecondary;
            parent.Controls.Add(lbl);
            y += 22;
            ListBox list = new ListBox { Location = new Point(left, y), Size = new Size(width - 100, 84), IntegralHeight = false };
            if (theme != null) { list.BackColor = theme.BgSidebar; list.ForeColor = theme.FgPrimary; }
            foreach (string f in WorkspaceTrust.GetTrustedFolders()) list.Items.Add(f);
            parent.Controls.Add(list);
            Button remove = new Button { Location = new Point(left + width - 92, y), Size = new Size(92, 28), Text = "Remove", FlatStyle = FlatStyle.Flat };
            remove.Click += delegate
            {
                string sel = list.SelectedItem as string;
                if (sel == null) return;
                WorkspaceTrust.Revoke(sel);
                if (rebuild != null) rebuild();
            };
            parent.Controls.Add(remove);
            y += 90;
            CheckBox enabled = new CheckBox { Location = new Point(left, y), Size = new Size(width, 22), Text = "Enable Workspace Trust (ask before running code from new folders)", Checked = WorkspaceTrust.Enabled, UseMnemonic = false };
            enabled.CheckedChanged += delegate { WorkspaceTrust.Enabled = enabled.Checked; };
            parent.Controls.Add(enabled);
            y += 26;
        }
    }
}
