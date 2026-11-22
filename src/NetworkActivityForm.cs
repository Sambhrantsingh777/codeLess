using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Windows (WinForms) privacy surface: Offline Mode state, the status-bar shield and the Network Activity window.
    /// CodeLessMainForm only creates one controller and wires a few hooks. C# 5 only.
    /// </summary>
    public class WinFormsNetworkPrivacyController
    {
        private readonly Form owner;
        private readonly IdePersistentSettings settings;
        private readonly Func<StudioTheme> themeProvider;
        private NetworkActivityForm panel;
        private Label statusItem;
        private ToolTip statusToolTip;

        /// <summary>Called after the status item's text changes width (re-run the status bar layout).</summary>
        public Action StatusItemResized;

        public WinFormsNetworkPrivacyController(Form owner, IdePersistentSettings settings, Func<StudioTheme> themeProvider)
        {
            this.owner = owner;
            this.settings = settings;
            this.themeProvider = themeProvider;
            NetworkLedger.SetOfflineMode(settings != null && settings.OfflineMode);
            NetworkLedger.OfflineModeChanged += OnOfflineModeChanged;
            NetworkLedger.EntryAdded += OnEntryAdded;
            NetworkLedger.Cleared += OnCleared;
            owner.FormClosed += (s, e) =>
            {
                NetworkLedger.OfflineModeChanged -= OnOfflineModeChanged;
                NetworkLedger.EntryAdded -= OnEntryAdded;
                NetworkLedger.Cleared -= OnCleared;
            };
        }

        public Label StatusItem { get { return statusItem; } }

        /// <summary>Status bar shield: click opens Network Activity, right-click offers the Offline Mode toggle.</summary>
        public Label CreateStatusItem(Font font)
        {
            statusToolTip = new ToolTip();
            statusItem = new Label
            {
                AutoSize = true,
                Font = font,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Padding = new Padding(20, 3, 6, 3),
                Margin = new Padding(0),
                BackColor = Color.Transparent,
                ForeColor = Color.White
            };
            statusItem.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DrawShield(e.Graphics, new Rectangle(5, (statusItem.Height - 13) / 2, 12, 13), statusItem.ForeColor, NetworkLedger.OfflineMode);
            };
            statusItem.MouseEnter += (s, e) => { statusItem.BackColor = Color.FromArgb(45, 255, 255, 255); };
            statusItem.MouseLeave += (s, e) => { statusItem.BackColor = NetworkLedger.OfflineMode ? Color.FromArgb(70, 245, 158, 11) : Color.Transparent; };
            statusItem.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowStatusMenu();
                else if (e.Button == MouseButtons.Left) ShowPanel();
            };
            RefreshStatusItem();
            return statusItem;
        }

        private void ShowStatusMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("Show Network Activity");
            show.Click += (s, e) => ShowPanel();
            ToolStripMenuItem offline = new ToolStripMenuItem("Offline Mode") { Checked = NetworkLedger.OfflineMode };
            offline.Click += (s, e) => ToggleOfflineMode();
            menu.Items.Add(show);
            menu.Items.Add(offline);
            menu.Closed += (s, e) => { menu.BeginInvoke(new Action(() => menu.Dispose())); };
            menu.Show(statusItem, new Point(0, -menu.PreferredSize.Height));
        }

        public void ToggleOfflineMode()
        {
            NetworkLedger.SetOfflineMode(!NetworkLedger.OfflineMode);
        }

        public void ShowPanel()
        {
            if (panel == null || panel.IsDisposed)
            {
                panel = new NetworkActivityForm(themeProvider != null ? themeProvider() : null);
                panel.FormClosed += (s, e) => { panel = null; };
                panel.StartPosition = FormStartPosition.Manual;
                Rectangle ob = owner.Bounds;
                panel.Location = new Point(ob.Left + Math.Max(0, (ob.Width - panel.Width) / 2), ob.Top + Math.Max(0, (ob.Height - panel.Height) / 2));
                panel.Show(owner);
            }
            else
            {
                if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
                panel.Activate();
            }
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (panel != null && !panel.IsDisposed) panel.ApplyTheme(theme);
        }

        private void OnOfflineModeChanged(bool offline)
        {
            RunOnUi(() =>
            {
                if (settings != null && settings.OfflineMode != offline)
                {
                    settings.OfflineMode = offline;
                    try { settings.Save(); } catch { }
                }
                RefreshStatusItem();
            });
        }

        private void OnEntryAdded(NetworkLedgerEntry e) { RunOnUi(RefreshStatusItem); }
        private void OnCleared() { RunOnUi(RefreshStatusItem); }

        private void RunOnUi(Action a)
        {
            try
            {
                if (owner.IsDisposed || !owner.IsHandleCreated) return;
                if (owner.InvokeRequired) owner.BeginInvoke(a);
                else a();
            }
            catch { }
        }

        public void RefreshStatusItem()
        {
            if (statusItem == null) return;
            bool offline = NetworkLedger.OfflineMode;
            int sent = NetworkLedger.SessionRequestCount - NetworkLedger.SessionBlockedCount;
            string text = offline ? "Offline" : sent.ToString(CultureInfo.InvariantCulture);
            if (statusItem.Text != text)
            {
                statusItem.Text = text;
                if (StatusItemResized != null) StatusItemResized();
            }
            statusItem.BackColor = offline ? Color.FromArgb(70, 245, 158, 11) : Color.Transparent;
            if (statusToolTip != null)
                statusToolTip.SetToolTip(statusItem, NetworkLedger.GetSummary() + "\nClick: Network Activity   Right-click: Offline Mode");
            statusItem.Invalidate();
        }

        /// <summary>Shield glyph (filled with a slash-free check when Offline Mode is on).</summary>
        public static void DrawShield(Graphics g, Rectangle r, Color color, bool filled)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                p.AddLine(x + w / 2f, y, x + w, y + h * 0.18f);
                p.AddLine(x + w, y + h * 0.18f, x + w, y + h * 0.48f);
                p.AddBezier(x + w, y + h * 0.48f, x + w, y + h * 0.78f, x + w * 0.72f, y + h * 0.92f, x + w / 2f, y + h);
                p.AddBezier(x + w / 2f, y + h, x + w * 0.28f, y + h * 0.92f, x, y + h * 0.78f, x, y + h * 0.48f);
                p.AddLine(x, y + h * 0.48f, x, y + h * 0.18f);
                p.CloseFigure();
                if (filled)
                {
                    using (SolidBrush b = new SolidBrush(color)) g.FillPath(b, p);
                }
                else
                {
                    using (Pen pen = new Pen(color, 1.3f)) g.DrawPath(pen, p);
                }
            }
        }
    }

    /// <summary>Non-modal "Network Activity" window: live ledger table, per-feature totals, filter, clear and export.</summary>
    public class NetworkActivityForm : CodeLessChromelessForm
    {
        private StudioTheme theme;
        private Panel titleBar;
        private readonly Panel body;
        private readonly Label summaryLabel;
        private readonly Label noteLabel;
        private readonly CheckBox offlineBox;
        private readonly CodeLessDarkInputBox filterBox;
        private readonly Button clearBtn;
        private readonly Button exportJsonBtn;
        private readonly Button exportCsvBtn;
        private readonly ListView table;
        private readonly ListView totalsView;
        private List<NetworkLedgerEntry> visible = new List<NetworkLedgerEntry>();
        private bool refreshQueued;

        public NetworkActivityForm(StudioTheme theme)
        {
            this.theme = theme ?? StudioTheme.GetBuiltInThemes()[0];
            Text = "Network Activity — codeLess";
            Size = new Size(980, 600);
            MinimumSize = new Size(640, 380);
            ShowInTaskbar = false;
            KeyPreview = true;

            body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 10) };

            summaryLabel = new Label { Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10f, FontStyle.Bold), UseMnemonic = false, AutoEllipsis = true };
            noteLabel = new Label { Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 8.4f), UseMnemonic = false, Text = NetworkLedger.UntrackedNote };

            Panel toolbar = new Panel { Dock = DockStyle.Top, Height = 34 };
            offlineBox = new CheckBox { Text = "Offline Mode (block all non-local requests)", AutoSize = true, Location = new Point(0, 7), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.8f), Cursor = Cursors.Hand, UseMnemonic = false, Checked = NetworkLedger.OfflineMode };
            offlineBox.CheckedChanged += (s, e) => NetworkLedger.SetOfflineMode(offlineBox.Checked);
            filterBox = new CodeLessDarkInputBox { Size = new Size(220, 26), Location = new Point(300, 4), Placeholder = "Filter (feature, host, path, status, blocked)" };
            filterBox.TextChanged += (s, e) => RefreshAll();
            clearBtn = MakeButton("Clear");
            exportJsonBtn = MakeButton("Export JSON...");
            exportCsvBtn = MakeButton("Export CSV...");
            clearBtn.Click += (s, e) => NetworkLedger.Clear();
            exportJsonBtn.Click += (s, e) => Export(true);
            exportCsvBtn.Click += (s, e) => Export(false);
            toolbar.Controls.AddRange(new Control[] { offlineBox, filterBox, clearBtn, exportJsonBtn, exportCsvBtn });
            toolbar.Resize += (s, e) =>
            {
                int x = toolbar.ClientSize.Width;
                foreach (Button b in new Button[] { exportCsvBtn, exportJsonBtn, clearBtn })
                {
                    x -= b.Width;
                    b.Location = new Point(x, 3);
                    x -= 6;
                }
                filterBox.Location = new Point(Math.Max(offlineBox.Right + 12, x - filterBox.Width), 4);
            };

            table = MakeListView();
            table.VirtualMode = true;
            table.ShowItemToolTips = true;
            table.RetrieveVirtualItem += OnRetrieveVirtualItem;
            AddColumns(table, new string[] { "Time", "Feature", "Method", "Host", "Path", "Up", "Down", "Status", "Duration", "Blocked" },
                new int[] { 70, 210, 72, 150, 170, 62, 70, 64, 76, 76 }); // every UPPERCASE header fits (METHOD, BLOCKED)

            totalsView = MakeListView();
            totalsView.Dock = DockStyle.Bottom;
            totalsView.Height = 120;
            AddColumns(totalsView, new string[] { "Feature (session totals)", "Requests", "Blocked", "Up", "Down", "Total time", "Hosts" },
                new int[] { 240, 70, 64, 74, 80, 80, 300 });
            Label totalsHeader = new Label { Dock = DockStyle.Bottom, Height = 22, Text = "Totals per feature", Font = new Font("Segoe UI", 8.8f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false };

            body.Controls.Add(table);
            body.Controls.Add(totalsHeader);
            body.Controls.Add(totalsView);
            body.Controls.Add(toolbar);
            body.Controls.Add(noteLabel);
            body.Controls.Add(summaryLabel);
            Controls.Add(body);

            NetworkLedger.EntryAdded += OnLedgerChanged;
            NetworkLedger.Cleared += OnLedgerCleared;
            NetworkLedger.OfflineModeChanged += OnOfflineChanged;
            NetworkLedger.InFlightChanged += OnInFlightChanged;
            FormClosed += (s, e) =>
            {
                NetworkLedger.EntryAdded -= OnLedgerChanged;
                NetworkLedger.Cleared -= OnLedgerCleared;
                NetworkLedger.OfflineModeChanged -= OnOfflineChanged;
                NetworkLedger.InFlightChanged -= OnInFlightChanged;
            };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            ApplyTheme(this.theme);
            RefreshAll();
        }

        private Button MakeButton(string text)
        {
            Button b = new Button { Text = text, FlatStyle = FlatStyle.Flat, AutoSize = false, Height = 28, Font = new Font("Segoe UI", 8.8f), Cursor = Cursors.Hand, UseMnemonic = false };
            b.Width = TextRenderer.MeasureText(text, b.Font).Width + 22;
            return b;
        }

        private ListView MakeListView()
        {
            ListView lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                Font = new Font("Segoe UI", 8.8f)
            };
            lv.DrawColumnHeader += (s, e) =>
            {
                using (SolidBrush bg = new SolidBrush(theme.BgSidebar)) e.Graphics.FillRectangle(bg, e.Bounds);
                using (Pen p = new Pen(theme.BorderSubtle)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), theme.FgSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            lv.DrawItem += (s, e) => { e.DrawDefault = true; };
            lv.DrawSubItem += (s, e) => { e.DrawDefault = true; };
            return lv;
        }

        private static void AddColumns(ListView lv, string[] names, int[] widths)
        {
            for (int i = 0; i < names.Length; i++)
                lv.Columns.Add(names[i], widths[i]);
        }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            BackColor = t.BorderSubtle;
            if (titleBar != null)
            {
                Controls.Remove(titleBar);
                titleBar.Dispose();
            }
            titleBar = BuildDarkDialogTitleBar("Network Activity — what codeLess sends, and where", t, true, true);
            Controls.Add(titleBar);
            body.BackColor = t.BgEditor;
            summaryLabel.ForeColor = t.FgPrimary;
            noteLabel.ForeColor = t.FgSecondary;
            offlineBox.ForeColor = t.FgPrimary;
            filterBox.ApplyTheme(t);
            foreach (Button b in new Button[] { clearBtn, exportJsonBtn, exportCsvBtn })
            {
                b.BackColor = t.BgHover;
                b.ForeColor = t.FgPrimary;
                b.FlatAppearance.BorderColor = t.BorderSubtle;
            }
            foreach (ListView lv in new ListView[] { table, totalsView })
            {
                lv.BackColor = t.BgSidebar;
                lv.ForeColor = t.FgPrimary;
                lv.Invalidate();
            }
            foreach (Control c in body.Controls)
            {
                Label l = c as Label;
                if (l != null && l != summaryLabel && l != noteLabel) l.ForeColor = t.FgSecondary;
            }
            RefreshAll();
        }

        private void OnLedgerChanged(NetworkLedgerEntry e) { QueueRefresh(); }
        private void OnLedgerCleared() { QueueRefresh(); }
        private void OnInFlightChanged(int n) { QueueRefresh(); }

        private void OnOfflineChanged(bool offline)
        {
            Invoke0(() =>
            {
                if (offlineBox.Checked != offline) offlineBox.Checked = offline;
                RefreshAll();
            });
        }

        private void QueueRefresh()
        {
            if (refreshQueued) return;
            refreshQueued = true;
            Invoke0(() => { refreshQueued = false; RefreshAll(); });
        }

        private void Invoke0(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) { refreshQueued = false; return; }
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { refreshQueued = false; }
        }

        public void RefreshAll()
        {
            string filter = filterBox != null ? filterBox.Text : "";
            List<NetworkLedgerEntry> all = NetworkLedger.Snapshot();
            List<NetworkLedgerEntry> list = new List<NetworkLedgerEntry>();
            for (int i = all.Count - 1; i >= 0; i--)
                if (NetworkLedger.MatchesFilter(all[i], filter)) list.Add(all[i]);
            visible = list;
            table.VirtualListSize = visible.Count;
            table.Invalidate();

            int inFlight = NetworkLedger.InFlightCount;
            summaryLabel.Text = NetworkLedger.GetSummary() + (inFlight > 0 ? " · " + inFlight + " in flight" : "");

            totalsView.BeginUpdate();
            totalsView.Items.Clear();
            foreach (NetworkFeatureTotal t in NetworkLedger.GetFeatureTotals())
            {
                ListViewItem it = new ListViewItem(t.Feature);
                it.SubItems.Add(t.Requests.ToString(CultureInfo.InvariantCulture));
                it.SubItems.Add(t.Blocked.ToString(CultureInfo.InvariantCulture));
                it.SubItems.Add(NetworkLedger.FormatBytes(t.BytesSent));
                it.SubItems.Add(NetworkLedger.FormatBytes(t.BytesReceived));
                it.SubItems.Add(t.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms");
                it.SubItems.Add(string.Join(", ", t.Hosts.ToArray()));
                totalsView.Items.Add(it);
            }
            ListViewItem total = new ListViewItem("Session total");
            total.SubItems.Add(NetworkLedger.SessionRequestCount.ToString(CultureInfo.InvariantCulture));
            total.SubItems.Add(NetworkLedger.SessionBlockedCount.ToString(CultureInfo.InvariantCulture));
            total.SubItems.Add(NetworkLedger.FormatBytes(NetworkLedger.SessionBytesSent));
            total.SubItems.Add(NetworkLedger.FormatBytes(NetworkLedger.SessionBytesReceived));
            total.SubItems.Add("");
            total.SubItems.Add(string.Join(", ", NetworkLedger.GetContactedHosts().ToArray()));
            total.Font = new Font(totalsView.Font, FontStyle.Bold);
            totalsView.Items.Add(total);
            totalsView.EndUpdate();
        }

        private void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            NetworkLedgerEntry en = e.ItemIndex >= 0 && e.ItemIndex < visible.Count ? visible[e.ItemIndex] : null;
            if (en == null)
            {
                e.Item = new ListViewItem(new string[] { "", "", "", "", "", "", "", "", "", "" });
                return;
            }
            ListViewItem it = new ListViewItem(new string[]
            {
                en.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                en.Feature,
                en.Method,
                en.Host,
                en.Path,
                NetworkLedger.FormatBytes(en.BytesSent),
                NetworkLedger.FormatBytes(en.BytesReceived),
                NetworkLedger.FormatStatus(en),
                en.Blocked ? "" : en.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms",
                en.Blocked ? "yes" : ""
            });
            if (en.Blocked) it.ForeColor = theme.WarnAmber;
            else if (en.Status <= 0 || en.Status >= 400) it.ForeColor = theme.DangerCoral;
            it.ToolTipText = en.Url + (string.IsNullOrEmpty(en.Note) ? "" : "\n" + en.Note);
            e.Item = it;
        }

        private void Export(bool json)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Export Network Activity";
                dlg.FileName = "codeless-network-activity-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (json ? ".json" : ".csv");
                dlg.Filter = json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    List<NetworkLedgerEntry> rows = new List<NetworkLedgerEntry>(visible);
                    rows.Reverse();
                    File.WriteAllText(dlg.FileName, json ? NetworkLedger.ExportJson(rows) : NetworkLedger.ExportCsv(rows), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not export: " + ex.Message, "Network Activity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }
}
