using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>A document in the Windows side pane (Markdown preview, HTTP response, git graph, diff).</summary>
    public class WinAuxDocument
    {
        public string Id = "";
        public string Title = "";
        public string Tooltip = "";
        public Control Content;
        public Action<StudioTheme> ApplyTheme;
        public Action Closed;
    }

    /// <summary>Second editor group docked right of the editor with its own tabs; can be maximized over the editor.</summary>
    public class WinAuxPane : Panel
    {
        private readonly Panel header;
        private readonly FlowLayoutPanel tabs;
        private readonly Panel body;
        private readonly Button maxBtn;
        private readonly Button closeBtn;
        private readonly ToolTip tips = new ToolTip();
        private readonly List<WinAuxDocument> docs = new List<WinAuxDocument>();
        private WinAuxDocument active;
        private StudioTheme theme;
        private bool maximized;

        public event Action AllClosed;
        public event Action<bool> MaximizeToggled;

        public List<WinAuxDocument> Documents { get { return docs; } }
        public WinAuxDocument ActiveDocument { get { return active; } }
        public bool IsMaximized { get { return maximized; } }

        public WinAuxPane(StudioTheme theme)
        {
            this.theme = theme;
            header = new Panel { Dock = DockStyle.Top, Height = 30 };
            tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, Padding = new Padding(0), Margin = new Padding(0) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 64, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 3, 4, 0) };
            closeBtn = SmallButton("✕", "Close Pane");
            maxBtn = SmallButton("□", "Maximize / Restore Pane");
            closeBtn.Click += delegate { CloseAll(); };
            maxBtn.Click += delegate { SetMaximized(!maximized); };
            actions.Controls.Add(closeBtn);
            actions.Controls.Add(maxBtn);
            header.Controls.Add(tabs);
            header.Controls.Add(actions);
            body = new Panel { Dock = DockStyle.Fill };
            Controls.Add(body);
            Controls.Add(header);
            ApplyTheme(theme);
        }

        private Button SmallButton(string text, string tip)
        {
            Button b = new Button { Text = text, Width = 26, Height = 24, FlatStyle = FlatStyle.Flat, Margin = new Padding(1, 0, 1, 0), Font = new Font("Segoe UI", 8.5f) };
            b.FlatAppearance.BorderSize = 0;
            tips.SetToolTip(b, tip);
            return b;
        }

        public WinAuxDocument Find(string id)
        {
            foreach (WinAuxDocument d in docs) if (d.Id == id) return d;
            return null;
        }

        public void Open(WinAuxDocument doc)
        {
            WinAuxDocument existing = Find(doc.Id);
            if (existing != null)
            {
                if (!ReferenceEquals(existing.Content, doc.Content))
                {
                    if (existing.Closed != null) { try { existing.Closed(); } catch { } }
                    if (existing.Content != null) { body.Controls.Remove(existing.Content); existing.Content.Dispose(); }
                    existing.Content = doc.Content;
                    existing.ApplyTheme = doc.ApplyTheme;
                    existing.Closed = doc.Closed;
                    if (existing.ApplyTheme != null) existing.ApplyTheme(theme);
                }
                existing.Title = doc.Title;
                existing.Tooltip = doc.Tooltip;
                Activate(existing);
                return;
            }
            docs.Add(doc);
            if (doc.ApplyTheme != null) doc.ApplyTheme(theme);
            Activate(doc);
        }

        public void Activate(WinAuxDocument doc)
        {
            active = doc;
            foreach (WinAuxDocument d in docs)
            {
                if (d.Content == null) continue;
                if (ReferenceEquals(d, doc))
                {
                    if (!body.Controls.Contains(d.Content)) { d.Content.Dock = DockStyle.Fill; body.Controls.Add(d.Content); }
                    d.Content.Visible = true;
                    d.Content.BringToFront();
                }
                else d.Content.Visible = false;
            }
            RebuildTabs();
        }

        public void Close(WinAuxDocument doc)
        {
            int idx = docs.IndexOf(doc);
            if (idx < 0) return;
            docs.RemoveAt(idx);
            if (doc.Closed != null) { try { doc.Closed(); } catch { } }
            if (doc.Content != null) { body.Controls.Remove(doc.Content); doc.Content.Dispose(); }
            if (ReferenceEquals(active, doc))
            {
                active = null;
                if (docs.Count > 0) Activate(docs[Math.Min(idx, docs.Count - 1)]);
            }
            RebuildTabs();
            if (docs.Count == 0)
            {
                if (maximized) SetMaximized(false);
                Action h = AllClosed;
                if (h != null) h();
            }
        }

        public void CloseAll()
        {
            foreach (WinAuxDocument d in docs.ToList()) Close(d);
        }

        public void SetMaximized(bool max)
        {
            if (maximized == max) return;
            maximized = max;
            maxBtn.Text = max ? "❐" : "□";
            Action<bool> h = MaximizeToggled;
            if (h != null) h(max);
        }

        public void RefreshTitles() { RebuildTabs(); }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            BackColor = t.BgEditor;
            header.BackColor = t.BgSidebar;
            tabs.BackColor = t.BgSidebar;
            body.BackColor = t.BgEditor;
            foreach (Button b in new Button[] { maxBtn, closeBtn })
            {
                b.BackColor = t.BgSidebar;
                b.ForeColor = t.FgSecondary;
                b.FlatAppearance.MouseOverBackColor = t.BgHover;
            }
            foreach (WinAuxDocument d in docs) { if (d.ApplyTheme != null) { try { d.ApplyTheme(t); } catch { } } }
            RebuildTabs();
        }

        private void RebuildTabs()
        {
            tabs.SuspendLayout();
            foreach (Control c in tabs.Controls.Cast<Control>().ToList()) { tabs.Controls.Remove(c); c.Dispose(); }
            foreach (WinAuxDocument d in docs)
            {
                WinAuxDocument captured = d;
                bool on = ReferenceEquals(d, active);
                Panel tab = new Panel { Height = 30, Margin = new Padding(0), BackColor = on ? theme.BgEditor : theme.BgSidebar, Cursor = Cursors.Hand };
                Label title = new Label { Text = d.Title, AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = on ? theme.FgPrimary : theme.FgMuted, Location = new Point(10, 7), BackColor = Color.Transparent };
                Label x = new Label { Text = "✕", AutoSize = true, Font = new Font("Segoe UI", 7.5f), ForeColor = theme.FgMuted, BackColor = Color.Transparent, Cursor = Cursors.Hand };
                tab.Controls.Add(title);
                tab.Controls.Add(x);
                int tw = Math.Min(220, TextRenderer.MeasureText(d.Title, title.Font).Width);
                title.AutoSize = false;
                title.AutoEllipsis = true;
                title.Width = tw;
                title.Height = 18;
                x.Location = new Point(14 + tw, 8);
                tab.Width = tw + 36;
                if (on)
                {
                    tab.Paint += delegate (object s, PaintEventArgs e)
                    {
                        using (SolidBrush b = new SolidBrush(theme.AccentPrimary)) e.Graphics.FillRectangle(b, 0, 0, tab.Width, 2);
                    };
                }
                if (!string.IsNullOrEmpty(d.Tooltip)) tips.SetToolTip(title, d.Tooltip);
                EventHandler activate = delegate { Activate(captured); };
                tab.Click += activate;
                title.Click += activate;
                x.Click += delegate { Close(captured); };
                tabs.Controls.Add(tab);
            }
            tabs.ResumeLayout();
        }
    }

    /// <summary>Double-buffered virtual list: only visible rows are painted; own scrollbar, wheel and keyboard navigation.</summary>
    public abstract class WinVirtualRows : Control
    {
        private readonly VScrollBar vbar = new VScrollBar { Dock = DockStyle.Right, Width = 14 };
        public StudioTheme Theme;
        public int RowHeight = 22;
        public int SelectedIndex = -1;
        public event Action<int> SelectionChanged;
        public event Action NearEnd;

        protected WinVirtualRows(StudioTheme theme)
        {
            Theme = theme;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Controls.Add(vbar);
            vbar.ValueChanged += delegate { Invalidate(); CheckNearEnd(); };
            TabStop = true;
        }

        public abstract int RowCount { get; }
        protected abstract void PaintRow(Graphics g, int index, Rectangle r, bool selected);
        protected virtual string EmptyText { get { return ""; } }

        public int TopOffset { get { return vbar.Value; } }

        public void UpdateScroll()
        {
            int total = RowCount * RowHeight;
            int page = Math.Max(1, ClientSize.Height);
            vbar.Minimum = 0;
            vbar.LargeChange = page;
            vbar.SmallChange = RowHeight;
            vbar.Maximum = Math.Max(0, total - 1);
            vbar.Enabled = total > page;
            if (vbar.Value > Math.Max(0, total - page)) vbar.Value = Math.Max(0, total - page);
            Invalidate();
        }

        public void ScrollTo(int offset)
        {
            int max = Math.Max(0, RowCount * RowHeight - ClientSize.Height);
            vbar.Value = Math.Max(0, Math.Min(max, offset));
        }

        public void Select(int idx)
        {
            if (RowCount == 0) idx = -1; else idx = Math.Max(0, Math.Min(RowCount - 1, idx));
            if (idx == SelectedIndex) return;
            SelectedIndex = idx;
            if (idx >= 0)
            {
                int top = idx * RowHeight;
                if (top < vbar.Value) ScrollTo(top);
                else if (top + RowHeight > vbar.Value + ClientSize.Height) ScrollTo(top + RowHeight - ClientSize.Height);
            }
            Invalidate();
            Action<int> h = SelectionChanged;
            if (h != null) h(idx);
        }

        protected void ResetRows()
        {
            SelectedIndex = -1;
            vbar.Value = 0;
            UpdateScroll();
        }

        private void CheckNearEnd()
        {
            Action h = NearEnd;
            if (h != null && RowCount > 0 && vbar.Value + ClientSize.Height >= RowCount * RowHeight - RowHeight * 40) h();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); CheckNearEnd(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(vbar.Value - Math.Sign(e.Delta) * RowHeight * 3);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int idx = (e.Y + vbar.Value) / RowHeight;
            if (idx >= 0 && idx < RowCount) Select(idx);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, ClientSize.Height / RowHeight - 1);
            switch (e.KeyCode)
            {
                case Keys.Down: Select(SelectedIndex + 1); break;
                case Keys.Up: Select(SelectedIndex - 1); break;
                case Keys.PageDown: Select(SelectedIndex + page); break;
                case Keys.PageUp: Select(SelectedIndex - page); break;
                case Keys.Home: Select(0); break;
                case Keys.End: Select(RowCount - 1); break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.BgEditor);
            int count = RowCount;
            if (count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Font, new Point(14, 12), Theme.FgMuted, TextFormatFlags.NoPrefix);
                return;
            }
            int w = ClientSize.Width - (vbar.Visible ? vbar.Width : 0);
            int first = Math.Max(0, vbar.Value / RowHeight);
            int last = Math.Min(count - 1, (vbar.Value + ClientSize.Height) / RowHeight + 1);
            for (int i = first; i <= last; i++)
            {
                Rectangle r = new Rectangle(0, i * RowHeight - vbar.Value, w, RowHeight);
                g.SetClip(r);
                PaintRow(g, i, r, i == SelectedIndex);
                g.ResetClip();
            }
        }

        protected static void DrawText(Graphics g, string s, Font f, Color c, int x, Rectangle row, int maxWidth)
        {
            if (maxWidth <= 4 || string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, new Rectangle(x, row.Y, maxWidth, row.Height), c,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Side-by-side / inline diff rows (colors follow the theme; syntax via the shared tokenizer).</summary>
    public class WinDiffRows : WinVirtualRows
    {
        private List<LineDiffRow> rows = new List<LineDiffRow>();
        private string oldText = "", newText = "";
        public bool SideBySide = true;
        public string Note = "";
        private readonly Font mono = new Font("Consolas", 9.5f);

        public WinDiffRows(StudioTheme theme) : base(theme) { RowHeight = 18; }

        public override int RowCount { get { return rows.Count; } }
        public List<LineDiffRow> Rows { get { return rows; } }
        protected override string EmptyText { get { return string.IsNullOrEmpty(Note) ? "No differences." : Note; } }

        public void SetTexts(string o, string n, bool sideBySide)
        {
            oldText = o ?? "";
            newText = n ?? "";
            SideBySide = sideBySide;
            rows = sideBySide ? LineDiff.SideBySide(oldText, newText) : LineDiff.Inline(oldText, newText);
            ResetRows();
        }

        public void JumpChange(int dir)
        {
            int start = TopOffset / RowHeight + (dir > 0 ? 2 : -1);
            for (int i = start; i >= 0 && i < rows.Count; i += dir)
            {
                if (rows[i].Kind != LineDiffRow.Equal && (i == 0 || rows[i - 1].Kind == LineDiffRow.Equal)) { ScrollTo(Math.Max(0, (i - 3) * RowHeight)); return; }
            }
        }

        protected override void PaintRow(Graphics g, int index, Rectangle r, bool selected)
        {
            LineDiffRow row = rows[index];
            bool light = Theme.IsLight;
            Color add = light ? Color.FromArgb(205, 240, 210) : StudioTheme.Blend(Theme.BgEditor, Color.FromArgb(46, 160, 67), 0.22);
            Color del = light ? Color.FromArgb(250, 215, 215) : StudioTheme.Blend(Theme.BgEditor, Color.FromArgb(241, 76, 76), 0.22);
            int numW = 44;
            if (SideBySide)
            {
                int half = r.Width / 2;
                Rectangle left = new Rectangle(r.X, r.Y, half - 1, r.Height);
                Rectangle right = new Rectangle(r.X + half + 1, r.Y, r.Width - half - 1, r.Height);
                using (SolidBrush b = new SolidBrush(row.LeftLine < 0 ? Theme.BgSidebar : (row.Kind != LineDiffRow.Equal ? del : Theme.BgEditor))) g.FillRectangle(b, left);
                using (SolidBrush b = new SolidBrush(row.RightLine < 0 ? Theme.BgSidebar : (row.Kind != LineDiffRow.Equal ? add : Theme.BgEditor))) g.FillRectangle(b, right);
                using (Pen p = new Pen(Theme.BorderSubtle)) g.DrawLine(p, r.X + half, r.Y, r.X + half, r.Bottom);
                if (row.LeftLine >= 0) { Num(g, row.LeftLine + 1, left.X, r, numW); Code(g, row.LeftText, left.X + numW + 6, r, left.Right); }
                if (row.RightLine >= 0) { Num(g, row.RightLine + 1, right.X, r, numW); Code(g, row.RightText, right.X + numW + 6, r, right.Right); }
            }
            else
            {
                Color bg = row.Kind == LineDiffRow.Removed ? del : (row.Kind == LineDiffRow.Added ? add : Theme.BgEditor);
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, r);
                if (row.LeftLine >= 0) Num(g, row.LeftLine + 1, r.X, r, numW);
                if (row.RightLine >= 0) Num(g, row.RightLine + 1, r.X + numW, r, numW);
                string sign = row.Kind == LineDiffRow.Removed ? "-" : (row.Kind == LineDiffRow.Added ? "+" : " ");
                TextRenderer.DrawText(g, sign, mono, new Point(r.X + numW * 2 + 4, r.Y + 2), Theme.FgMuted, TextFormatFlags.NoPadding);
                Code(g, row.Kind == LineDiffRow.Removed ? row.LeftText : row.RightText, r.X + numW * 2 + 18, r, r.Right);
            }
            if (selected) using (Pen p = new Pen(Color.FromArgb(120, Theme.AccentPrimary))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        private void Num(Graphics g, int n, int x, Rectangle r, int w)
        {
            TextRenderer.DrawText(g, n.ToString(CultureInfo.InvariantCulture), mono, new Rectangle(x, r.Y, w - 4, r.Height), Theme.FgMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void Code(Graphics g, string line, int x, Rectangle r, int right)
        {
            if (string.IsNullOrEmpty(line)) return;
            string t = line.Replace("\t", "    ");
            if (t.Length > 400) t = t.Substring(0, 400);
            int cw = Math.Max(5, TextRenderer.MeasureText(g, "MMMMMMMMMM", mono, new Size(1000, 50), TextFormatFlags.NoPadding).Width / 10);
            int pos = 0;
            foreach (CodeToken tk in SimpleCodeTokenizer.Tokenize(t, false))
            {
                if (tk.Start > pos) TextRenderer.DrawText(g, t.Substring(pos, tk.Start - pos), mono, new Point(x + pos * cw, r.Y + 2), Theme.FgPrimary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int px = x + tk.Start * cw;
                if (px > right) break;
                TextRenderer.DrawText(g, t.Substring(tk.Start, tk.Length), mono, new Point(px, r.Y + 2), WinDevToolsColors.ForCode(Theme, tk.Kind), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                pos = tk.Start + tk.Length;
            }
        }
    }

    public static class WinDevToolsColors
    {
        public static Color ForCode(StudioTheme t, CodeTokenKind k)
        {
            switch (k)
            {
                case CodeTokenKind.Keyword: return t.TokenKeyword;
                case CodeTokenKind.Control: return t.TokenControl;
                case CodeTokenKind.Type: return t.TokenType;
                case CodeTokenKind.Function: return t.TokenFunction;
                case CodeTokenKind.String: return t.TokenString;
                case CodeTokenKind.Comment: return t.TokenComment;
                case CodeTokenKind.Number: return t.TokenNumber;
                case CodeTokenKind.Operator: return t.TokenOperator;
                default: return t.FgPrimary;
            }
        }

        public static Color ForBody(StudioTheme t, BodyTokenKind k)
        {
            switch (k)
            {
                case BodyTokenKind.Key: return t.TokenFunction;
                case BodyTokenKind.String: return t.TokenString;
                case BodyTokenKind.Number: return t.TokenNumber;
                case BodyTokenKind.Literal: return t.TokenKeyword;
                case BodyTokenKind.Tag: return t.TokenKeyword;
                case BodyTokenKind.AttributeName: return t.TokenType;
                case BodyTokenKind.Comment: return t.TokenComment;
                case BodyTokenKind.Punctuation: return t.FgSecondary;
                default: return t.FgPrimary;
            }
        }

        public static Button FlatButton(string text, StudioTheme t)
        {
            Button b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f), Height = 24, Margin = new Padding(4, 3, 0, 0) };
            b.FlatAppearance.BorderColor = t.BorderSubtle;
            b.BackColor = t.BgElevated;
            b.ForeColor = t.FgPrimary;
            return b;
        }
    }

    public class WinDiffView : Panel
    {
        public readonly WinDiffRows Rows;
        private readonly Panel header;
        private readonly Label title, stats;
        private readonly Button modeBtn, prevBtn, nextBtn;

        public WinDiffView(string titleText, string oldText, string newText, string note, StudioTheme theme)
        {
            Rows = new WinDiffRows(theme) { Dock = DockStyle.Fill, Note = note ?? "" };
            header = new Panel { Dock = DockStyle.Top, Height = 30 };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 6, 0) };
            stats = new Label { AutoSize = true, Margin = new Padding(6, 8, 6, 0), Font = new Font("Segoe UI", 8.5f) };
            modeBtn = WinDevToolsColors.FlatButton("Inline View", theme);
            prevBtn = WinDevToolsColors.FlatButton("↑", theme);
            nextBtn = WinDevToolsColors.FlatButton("↓", theme);
            modeBtn.Click += delegate { Rows.SetTexts(oldText, newText, !Rows.SideBySide); UpdateHeader(); };
            prevBtn.Click += delegate { Rows.JumpChange(-1); };
            nextBtn.Click += delegate { Rows.JumpChange(1); };
            right.Controls.AddRange(new Control[] { stats, modeBtn, prevBtn, nextBtn });
            title = new Label { Dock = DockStyle.Fill, Text = titleText, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(8, 0, 0, 0), Font = new Font("Segoe UI", 9f) };
            header.Controls.Add(title);
            header.Controls.Add(right);
            Controls.Add(Rows);
            Controls.Add(header);
            Rows.SetTexts(oldText, newText, true);
            UpdateHeader();
            ApplyTheme(theme);
        }

        private void UpdateHeader()
        {
            int a, r;
            LineDiff.CountChanges(Rows.Rows, out a, out r);
            stats.Text = "+" + a + "  -" + r;
            modeBtn.Text = Rows.SideBySide ? "Inline View" : "Side by Side";
        }

        public void ApplyTheme(StudioTheme t)
        {
            BackColor = t.BgEditor;
            header.BackColor = t.BgSidebar;
            title.ForeColor = t.FgPrimary;
            stats.ForeColor = t.FgSecondary;
            Rows.Theme = t;
            Rows.Invalidate();
        }
    }

    /// <summary>Commit rows with natively painted graph lanes, ref badges, author, relative date and hash.</summary>
    public class WinGitGraphRows : WinVirtualRows
    {
        public readonly List<GitCommitInfo> Commits = new List<GitCommitInfo>();
        public readonly List<GitGraphRow> Graph = new List<GitGraphRow>();
        public bool ShowGraph = true;
        public string Empty = "Loading history…";
        private const int LaneW = 14;
        private readonly Font ui = new Font("Segoe UI", 9f);
        private readonly Font uiBold = new Font("Segoe UI", 8f, FontStyle.Bold);
        private readonly Font small = new Font("Segoe UI", 8f);
        private readonly Font mono = new Font("Consolas", 8.5f);
        private static readonly Color[] LaneColors =
        {
            Color.FromArgb(25, 120, 229), Color.FromArgb(234, 88, 12), Color.FromArgb(22, 163, 74), Color.FromArgb(192, 38, 211),
            Color.FromArgb(220, 38, 38), Color.FromArgb(13, 148, 136), Color.FromArgb(202, 138, 4), Color.FromArgb(99, 102, 241)
        };

        public WinGitGraphRows(StudioTheme theme) : base(theme) { RowHeight = 24; }

        public override int RowCount { get { return Commits.Count; } }
        protected override string EmptyText { get { return Empty; } }

        public void Clear() { Commits.Clear(); Graph.Clear(); ResetRows(); }

        public void Append(List<GitCommitInfo> c, List<GitGraphRow> g)
        {
            Commits.AddRange(c);
            Graph.AddRange(g);
            UpdateScroll();
        }

        private static Color Lane(int i) { return LaneColors[((i % LaneColors.Length) + LaneColors.Length) % LaneColors.Length]; }

        protected override void PaintRow(Graphics g, int index, Rectangle r, bool selected)
        {
            GitCommitInfo c = Commits[index];
            if (selected) using (SolidBrush b = new SolidBrush(StudioTheme.Blend(Theme.BgEditor, Theme.AccentPrimary, 0.25))) g.FillRectangle(b, r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = r.X + 8;
            int cy = r.Y + r.Height / 2;
            if (ShowGraph && index < Graph.Count)
            {
                GitGraphRow gr = Graph[index];
                foreach (GitGraphSegment s in gr.Segments)
                {
                    int fx = x + s.FromLane * LaneW + LaneW / 2, tx = x + s.ToLane * LaneW + LaneW / 2;
                    using (Pen p = new Pen(Lane(s.Color), 1.8f))
                    {
                        if (s.Kind == GitGraphSegment.TopToBottom) g.DrawLine(p, fx, r.Y, tx, r.Bottom);
                        else if (s.Kind == GitGraphSegment.TopToCenter) Curve(g, p, fx, r.Y, tx, cy);
                        else Curve(g, p, fx, cy, tx, r.Bottom);
                    }
                }
                int nx = x + gr.NodeLane * LaneW + LaneW / 2;
                if (c.IsMerge)
                {
                    using (SolidBrush b = new SolidBrush(Theme.BgEditor)) g.FillEllipse(b, nx - 4, cy - 4, 8, 8);
                    using (Pen p = new Pen(Lane(gr.NodeColor), 2f)) g.DrawEllipse(p, nx - 4, cy - 4, 8, 8);
                }
                else using (SolidBrush b = new SolidBrush(Lane(gr.NodeColor))) g.FillEllipse(b, nx - 4.5f, cy - 4.5f, 9, 9);
                x += Math.Min(gr.LaneCount, 24) * LaneW + 8;
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Theme.AccentPrimary)) g.FillEllipse(b, x + 2, cy - 4, 8, 8);
                x += 20;
            }
            g.SmoothingMode = SmoothingMode.Default;
            bool compact = r.Width < 560;
            int dateW = 100, authorW = compact ? 0 : Math.Min(150, Math.Max(80, r.Width / 6)), hashW = compact ? 0 : 66;
            int msgRight = r.Right - dateW - authorW - hashW - 8;
            foreach (GitRefBadge rf in c.Refs)
            {
                if (x > msgRight - 40) break;
                Color bc = rf.Kind == GitRefBadge.KindTag ? Color.FromArgb(202, 138, 4) : (rf.Kind == GitRefBadge.KindRemote ? Color.FromArgb(100, 116, 139) : Theme.AccentPrimary);
                Font f = rf.IsCurrent ? uiBold : small;
                int bw = Math.Min(170, TextRenderer.MeasureText(rf.Name, f).Width + 8);
                Rectangle br = new Rectangle(x, r.Y + 4, bw, r.Height - 8);
                using (SolidBrush b = new SolidBrush(bc)) g.FillRectangle(b, br);
                TextRenderer.DrawText(g, rf.Name, f, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                x += bw + 4;
            }
            DrawText(g, c.Subject, ui, Theme.FgPrimary, x + 2, r, msgRight - x);
            int ax = msgRight + 8;
            if (authorW > 0) DrawText(g, c.AuthorName, ui, Theme.FgSecondary, ax, r, authorW - 6);
            DrawText(g, GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow), ui, Theme.FgMuted, ax + authorW, r, dateW - 6);
            if (hashW > 0) DrawText(g, c.ShortHash, mono, Theme.FgMuted, ax + authorW + dateW, r, hashW);
        }

        private static void Curve(Graphics g, Pen p, int x1, int y1, int x2, int y2)
        {
            if (x1 == x2) { g.DrawLine(p, x1, y1, x2, y2); return; }
            float my = (y1 + y2) / 2f;
            g.DrawBezier(p, x1, y1, x1, my, x2, my, x2, y2);
        }
    }

    /// <summary>Git Graph / File History panel: filter, all-branches toggle, paged commit list, details and changed files.</summary>
    public class WinGitGraphView : Panel
    {
        public readonly string RepoRoot;
        public readonly string FilePathFilter;
        public readonly WinGitGraphRows List;
        private readonly Panel toolbar;
        private readonly TextBox filter = new TextBox { Width = 220, Font = new Font("Segoe UI", 9f) };
        private readonly CheckBox allBranches = new CheckBox { Text = "All branches", Checked = true, AutoSize = true, Font = new Font("Segoe UI", 9f) };
        private readonly Button refresh;
        private readonly Label status = new Label { AutoSize = true, Font = new Font("Segoe UI", 8.5f) };
        private readonly RichTextBox details = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Top, Height = 190, Font = new Font("Segoe UI", 9f), DetectUrls = false };
        private readonly ListBox files = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9f), IntegralHeight = false };
        private readonly Panel detailsPanel = new Panel { Dock = DockStyle.Right, Width = 330 };
        private readonly Button copyHash;
        private readonly System.Windows.Forms.Timer filterTimer = new System.Windows.Forms.Timer { Interval = 350 };
        private StudioTheme theme;
        private GitGraphLayout layout = new GitGraphLayout();
        private List<string> remotes;
        private int generation;
        private bool loading, exhausted;
        private string currentFilter = "";
        private string pendingHash;
        private int pendingPages;
        private GitCommitInfo selected;
        private List<GitChangedFile> selectedFiles = new List<GitChangedFile>();

        public event Action<GitCommitInfo, GitChangedFile> FileDiffRequested;
        public Action<string> CopyToClipboard;
        public GitCommitInfo SelectedCommit { get { return selected; } }

        public WinGitGraphView(string repoRoot, string repoRelativeFile, StudioTheme theme)
        {
            RepoRoot = repoRoot;
            FilePathFilter = repoRelativeFile;
            this.theme = theme;
            List = new WinGitGraphRows(theme) { Dock = DockStyle.Fill };
            toolbar = new Panel { Dock = DockStyle.Top, Height = 34 };
            refresh = WinDevToolsColors.FlatButton("Refresh", theme);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(6, 4, 0, 0) };
            filter.Margin = new Padding(0, 3, 6, 0);
            allBranches.Margin = new Padding(4, 6, 6, 0);
            status.Margin = new Padding(8, 8, 0, 0);
            flow.Controls.AddRange(new Control[] { filter, allBranches, refresh, status });
            toolbar.Controls.Add(flow);
            allBranches.Visible = string.IsNullOrEmpty(repoRelativeFile);
            copyHash = WinDevToolsColors.FlatButton("Copy Hash", theme);
            copyHash.Dock = DockStyle.Top;
            copyHash.Click += delegate { if (selected != null && CopyToClipboard != null) CopyToClipboard(selected.Hash); };
            detailsPanel.Controls.Add(files);
            detailsPanel.Controls.Add(copyHash);
            detailsPanel.Controls.Add(details);
            var splitter = new Splitter { Dock = DockStyle.Right, Width = 3 };
            Controls.Add(List);
            Controls.Add(splitter);
            Controls.Add(detailsPanel);
            Controls.Add(toolbar);
            SetWatermark();

            List.SelectionChanged += delegate (int idx) { ShowDetails(idx >= 0 && idx < List.Commits.Count ? List.Commits[idx] : null); };
            List.NearEnd += LoadMore;
            files.SelectedIndexChanged += delegate
            {
                int i = files.SelectedIndex;
                if (i >= 0 && i < selectedFiles.Count && selected != null && FileDiffRequested != null) FileDiffRequested(selected, selectedFiles[i]);
            };
            filterTimer.Tick += delegate { filterTimer.Stop(); currentFilter = filter.Text.Trim(); Reload(); };
            filter.TextChanged += delegate { filterTimer.Stop(); filterTimer.Start(); };
            allBranches.CheckedChanged += delegate { Reload(); };
            refresh.Click += delegate { Reload(); };
            ApplyTheme(theme);
            Reload();
        }

        private void SetWatermark()
        {
            // EM_SETCUEBANNER: native placeholder text
            try { SendMessage(filter.Handle, 0x1501, (IntPtr)1, "Filter by message or author"); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private void Post(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        public void Reload()
        {
            generation++;
            layout = new GitGraphLayout();
            List.Clear();
            List.ShowGraph = string.IsNullOrEmpty(FilePathFilter) && string.IsNullOrEmpty(currentFilter);
            List.Empty = "Loading history…";
            exhausted = false;
            loading = false;
            LoadMore();
        }

        public void LoadMore()
        {
            if (loading || exhausted) return;
            loading = true;
            int gen = generation;
            int skip = List.Commits.Count;
            string f = currentFilter;
            bool all = allBranches.Checked;
            GitGraphLayout lay = layout;
            status.Text = skip == 0 ? "Loading…" : "Loading more…";
            Task.Factory.StartNew(delegate
            {
                if (remotes == null) remotes = GitHistoryService.GetRemoteNames(RepoRoot);
                string err;
                List<GitCommitInfo> page = GitHistoryService.LoadPage(RepoRoot, skip, GitHistoryService.PageSize, f, FilePathFilter, all, remotes, out err);
                List<GitGraphRow> rows = new List<GitGraphRow>();
                lock (lay) foreach (GitCommitInfo c in page) rows.Add(lay.AddCommit(c.Hash, c.Parents));
                Post(delegate
                {
                    if (gen != generation) return;
                    loading = false;
                    if (page.Count < GitHistoryService.PageSize) exhausted = true;
                    List.Append(page, rows);
                    if (List.Commits.Count == 0) List.Empty = string.IsNullOrEmpty(err) ? (string.IsNullOrEmpty(f) ? "No commits." : "No commits match \"" + f + "\".") : err;
                    status.Text = List.Commits.Count + (exhausted ? "" : "+") + " commits" + (string.IsNullOrEmpty(FilePathFilter) ? "" : " touching " + FilePathFilter);
                    if (List.SelectedIndex < 0 && List.Commits.Count > 0 && pendingHash == null) List.Select(0);
                    ResolvePending();
                    List.Invalidate();
                });
            });
        }

        public void SelectCommit(string hash)
        {
            pendingHash = hash;
            pendingPages = 0;
            ResolvePending();
        }

        private void ResolvePending()
        {
            if (pendingHash == null) return;
            int idx = List.Commits.FindIndex(delegate (GitCommitInfo c) { return c.Hash.StartsWith(pendingHash, StringComparison.OrdinalIgnoreCase); });
            if (idx >= 0) { pendingHash = null; List.Select(idx); List.ScrollTo(Math.Max(0, (idx - 5) * List.RowHeight)); return; }
            if (!exhausted && pendingPages++ < 25) { if (!loading) LoadMore(); return; }
            pendingHash = null;
        }

        private void ShowDetails(GitCommitInfo c)
        {
            selected = c;
            selectedFiles = new List<GitChangedFile>();
            files.Items.Clear();
            if (c == null) { details.Text = "Select a commit to see its details and changed files."; return; }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(c.Subject).AppendLine();
            sb.AppendLine(c.AuthorName + " <" + c.AuthorEmail + ">");
            sb.AppendLine(c.AuthorTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  (" + GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow) + ")");
            sb.AppendLine(c.Hash);
            if (c.Refs.Count > 0) sb.AppendLine(string.Join(", ", c.Refs.Select(r => r.Name).ToArray()));
            details.Text = sb.ToString();
            GitCommitInfo captured = c;
            Task.Factory.StartNew(delegate
            {
                string err;
                List<GitChangedFile> list = GitHistoryService.GetChangedFiles(RepoRoot, captured, out err);
                string msg = GitHistoryService.GetFullMessage(RepoRoot, captured.Hash);
                Post(delegate
                {
                    if (!ReferenceEquals(selected, captured)) return;
                    int nl = msg.IndexOf('\n');
                    string body = nl >= 0 ? msg.Substring(nl + 1).Trim() : "";
                    if (body.Length > 0) details.AppendText("\n" + body + "\n");
                    selectedFiles = list;
                    files.Items.Clear();
                    foreach (GitChangedFile f in list) files.Items.Add(f.Status + "   " + f.DisplayName);
                    if (err.Length > 0) files.Items.Add(err);
                });
            });
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            BackColor = t.BgEditor;
            toolbar.BackColor = t.BgSidebar;
            foreach (Control c in toolbar.Controls) c.BackColor = t.BgSidebar;
            filter.BackColor = t.BgElevated;
            filter.ForeColor = t.FgPrimary;
            allBranches.ForeColor = t.FgSecondary;
            status.ForeColor = t.FgMuted;
            detailsPanel.BackColor = t.BgSidebar;
            details.BackColor = t.BgSidebar;
            details.ForeColor = t.FgPrimary;
            files.BackColor = t.BgSidebar;
            files.ForeColor = t.FgPrimary;
            List.Theme = t;
            List.Invalidate();
        }
    }

    /// <summary>.http response panel: status badge, time, size, Body/Headers/Request tabs, colored pretty body, copy as cURL.</summary>
    public class WinHttpResponseView : Panel
    {
        private readonly Panel header;
        private readonly Label statusLabel = new Label { AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.White, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(8, 5, 4, 0) };
        private readonly Label meta = new Label { AutoSize = true, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(6, 9, 6, 0) };
        private readonly Button cancelBtn, curlBtn, copyBtn, resendBtn, bodyTab, headersTab, requestTab;
        private readonly RichTextBox body = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, WordWrap = false, Font = new Font("Consolas", 9.5f), DetectUrls = false };
        private StudioTheme theme;
        private string tab = "body";
        private CancellationTokenSource cts;

        public HttpResponseResult Result;
        public HttpResolvedRequest Request;
        public event Action ResendRequested;
        public Action<string> CopyToClipboard;
        public bool IsRunning { get { return cts != null; } }

        public WinHttpResponseView(StudioTheme theme)
        {
            this.theme = theme;
            header = new Panel { Dock = DockStyle.Top, Height = 34 };
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            left.Controls.Add(statusLabel);
            left.Controls.Add(meta);
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 2, 6, 0) };
            cancelBtn = WinDevToolsColors.FlatButton("Cancel", theme);
            curlBtn = WinDevToolsColors.FlatButton("Copy as cURL", theme);
            copyBtn = WinDevToolsColors.FlatButton("Copy Body", theme);
            resendBtn = WinDevToolsColors.FlatButton("Send Again", theme);
            cancelBtn.Visible = false;
            cancelBtn.Click += delegate { try { if (cts != null) cts.Cancel(); } catch { } };
            curlBtn.Click += delegate { if (Request != null && CopyToClipboard != null) CopyToClipboard(HttpClientRunner.ToCurl(Request)); };
            copyBtn.Click += delegate { if (Result != null && CopyToClipboard != null) CopyToClipboard(Result.Body ?? ""); };
            resendBtn.Click += delegate { Action h = ResendRequested; if (h != null) h(); };
            right.Controls.AddRange(new Control[] { cancelBtn, curlBtn, copyBtn, resendBtn });
            header.Controls.Add(left);
            header.Controls.Add(right);
            var tabsBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
            bodyTab = TabButton("BODY", "body");
            headersTab = TabButton("HEADERS", "headers");
            requestTab = TabButton("REQUEST", "request");
            tabsBar.Controls.AddRange(new Control[] { bodyTab, headersTab, requestTab });
            Controls.Add(body);
            Controls.Add(tabsBar);
            Controls.Add(header);
            ApplyTheme(theme);
        }

        private Button TabButton(string text, string id)
        {
            Button b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Height = 24 };
            b.FlatAppearance.BorderSize = 0;
            b.Click += delegate { tab = id; Render(); };
            return b;
        }

        public CancellationToken BeginRequest(HttpResolvedRequest req)
        {
            try { if (cts != null) cts.Cancel(); } catch { }
            cts = new CancellationTokenSource();
            Request = req;
            Result = null;
            cancelBtn.Visible = true;
            statusLabel.Text = "Sending…";
            meta.Text = req.Method + " " + req.Url;
            tab = "body";
            Render();
            return cts.Token;
        }

        public void ShowResult(HttpResponseResult r)
        {
            cts = null;
            cancelBtn.Visible = false;
            Result = r;
            if (r.Request != null) Request = r.Request;
            if (r.HasResponse)
            {
                statusLabel.Text = r.StatusCode + " " + r.ReasonPhrase;
                meta.Text = r.ElapsedMs + " ms  ·  " + HttpClientRunner.FormatSize(r.BodyBytes) + "  ·  " + (Request != null ? Request.Method + " " + Request.Url : "");
            }
            else
            {
                statusLabel.Text = r.Blocked ? "Blocked" : (r.Cancelled ? "Cancelled" : "Error");
                meta.Text = r.Error;
            }
            Render();
        }

        private void Render()
        {
            Color badge = Result == null ? theme.AccentPrimary : (!Result.HasResponse ? Color.FromArgb(185, 28, 28) : (Result.StatusCode < 300 ? Color.FromArgb(22, 140, 64) : (Result.StatusCode < 400 ? Color.FromArgb(37, 99, 235) : (Result.StatusCode < 500 ? Color.FromArgb(202, 138, 4) : Color.FromArgb(185, 28, 28)))));
            statusLabel.BackColor = badge;
            foreach (Button b in new Button[] { bodyTab, headersTab, requestTab })
            {
                bool on = (b == bodyTab && tab == "body") || (b == headersTab && tab == "headers") || (b == requestTab && tab == "request");
                b.ForeColor = on ? theme.FgPrimary : theme.FgMuted;
                b.BackColor = on ? theme.BgHover : theme.BgEditor;
            }
            body.Clear();
            if (tab == "headers") { body.Text = Result != null && Result.HasResponse ? HttpClientRunner.FormatHeaders(Result) : (Result != null ? Result.Error : ""); return; }
            if (tab == "request")
            {
                if (Request == null) return;
                StringBuilder sb = new StringBuilder();
                sb.Append(Request.Method).Append(' ').Append(Request.Url).Append('\n');
                foreach (HttpHeader h in Request.Headers) sb.Append(h.Name).Append(": ").Append(h.Value).Append('\n');
                if (!string.IsNullOrEmpty(Request.Body)) sb.Append('\n').Append(Request.Body);
                if (Request.UnresolvedVariables.Count > 0) sb.Append("\n\n# Unresolved variables: ").Append(string.Join(", ", Request.UnresolvedVariables.ToArray()));
                body.Text = sb.ToString();
                return;
            }
            if (Result == null) { body.Text = Request != null ? "Waiting for response…" : ""; return; }
            if (!Result.HasResponse) { body.Text = Result.Error + (Result.Blocked ? "\n\nTurn off Offline Mode (or target localhost) to send this request." : ""); return; }
            List<BodyToken> tokens;
            string pretty = BodyFormatter.FormatForDisplay(Result.ContentType, Result.Body ?? "", out tokens);
            if (tokens.Count == 0 || pretty.Length > 400000) { body.Text = pretty; return; }
            body.Rtf = BuildRtf(pretty, tokens);
        }

        private string BuildRtf(string text, List<BodyToken> tokens)
        {
            Color[] palette = new Color[] { theme.FgPrimary, theme.TokenFunction, theme.TokenString, theme.TokenNumber, theme.TokenKeyword, theme.FgSecondary, theme.TokenKeyword, theme.TokenType, theme.TokenComment };
            StringBuilder sb = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}{\colortbl;");
            foreach (Color c in palette) sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            sb.Append(@"}\f0\fs19\cf1 ");
            int pos = 0;
            foreach (BodyToken tk in tokens)
            {
                if (tk.Start > pos) MarkdownRtfWriter.AppendText(sb, text.Substring(pos, tk.Start - pos));
                sb.Append(@"{\cf").Append((int)tk.Kind + 1).Append(' ');
                MarkdownRtfWriter.AppendText(sb, text.Substring(tk.Start, tk.Length));
                sb.Append('}');
                pos = tk.Start + tk.Length;
            }
            if (pos < text.Length) MarkdownRtfWriter.AppendText(sb, text.Substring(pos));
            sb.Append('}');
            return sb.ToString();
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            BackColor = t.BgEditor;
            header.BackColor = t.BgSidebar;
            foreach (Control c in header.Controls) c.BackColor = t.BgSidebar;
            meta.ForeColor = t.FgSecondary;
            body.BackColor = t.BgEditor;
            body.ForeColor = t.FgPrimary;
            Render();
        }
    }

    /// <summary>
    /// Native Markdown preview for Windows: RichEdit with RTF from <see cref="MarkdownRtfWriter"/>. Links are located with
    /// Find after loading so clicks map back to URLs; scroll sync uses block anchors.
    /// </summary>
    public class WinMarkdownPreview : Panel
    {
        public const string NetworkFeature = "Markdown Preview";
        private readonly RichTextBox box = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
        private readonly List<KeyValuePair<int, MdRtfLink>> linkRanges = new List<KeyValuePair<int, MdRtfLink>>();
        private readonly List<KeyValuePair<int, int>> anchorPositions = new List<KeyValuePair<int, int>>(); // sourceLine -> char index
        private readonly Dictionary<string, int> headingPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> remoteImages = new Dictionary<string, byte[]>();
        private static readonly HashSet<string> remotePending = new HashSet<string>();
        private StudioTheme theme;
        private string lastText;
        private int lastSyncedLine = -1;

        public string FilePath;
        public MdDocument Document;
        public int RenderCount;
        public event Action<string, MdLinkTarget> LinkClicked;

        public WinMarkdownPreview(string filePath, StudioTheme theme)
        {
            FilePath = filePath;
            this.theme = theme;
            Padding = new Padding(18, 10, 6, 0);
            Controls.Add(box);
            box.MouseUp += OnBoxMouseUp;
            box.MouseMove += delegate (object s, MouseEventArgs e) { box.Cursor = LinkAt(e.Location) != null ? Cursors.Hand : Cursors.IBeam; };
            ApplyTheme(theme);
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            BackColor = t.BgEditor;
            box.BackColor = t.BgEditor;
            box.ForeColor = t.FgPrimary;
            if (lastText != null) { string s = lastText; lastText = null; Render(s); }
        }

        public void Render(string markdown)
        {
            markdown = markdown ?? "";
            if (markdown == lastText) return;
            lastText = markdown;
            Document = MarkdownParser.Parse(markdown);
            MarkdownRtfResult res = MarkdownRtfWriter.Build(Document, MarkdownRtfColors.FromTheme(theme), LoadImage);
            int firstVisible = box.GetCharIndexFromPosition(new Point(2, 2));
            box.SuspendLayout();
            box.Rtf = res.Rtf;
            // Map links and anchors to character positions (sequential search keeps document order).
            linkRanges.Clear();
            anchorPositions.Clear();
            headingPositions.Clear();
            string plain = box.Text;
            int from = 0;
            foreach (MdRtfLink l in res.Links)
            {
                int idx = plain.IndexOf(l.Text, from, StringComparison.Ordinal);
                if (idx < 0) continue;
                linkRanges.Add(new KeyValuePair<int, MdRtfLink>(idx, l));
                from = idx + l.Text.Length;
            }
            from = 0;
            foreach (MdRtfAnchor a in res.Anchors)
            {
                int idx = plain.IndexOf(a.Text, from, StringComparison.Ordinal);
                if (idx < 0) continue;
                anchorPositions.Add(new KeyValuePair<int, int>(a.SourceLine, idx));
                if (!string.IsNullOrEmpty(a.HeadingAnchor) && !headingPositions.ContainsKey(a.HeadingAnchor)) headingPositions[a.HeadingAnchor] = idx;
                from = idx + 1;
            }
            box.ResumeLayout();
            if (firstVisible > 0) ScrollToChar(Math.Min(firstVisible, Math.Max(0, box.TextLength - 1)));
            RenderCount++;
        }

        private byte[] LoadImage(string src)
        {
            string local = MarkdownLinkResolver.ResolveLocalImage(src, FilePath);
            if (local != null)
            {
                try { if (File.Exists(local) && new FileInfo(local).Length < 8 * 1024 * 1024) return File.ReadAllBytes(local); } catch { }
                return null;
            }
            if (!MarkdownLinkResolver.IsRemoteImage(src)) return null;
            lock (remoteImages)
            {
                byte[] cached;
                if (remoteImages.TryGetValue(src, out cached)) return cached;
                if (remotePending.Contains(src)) return null;
            }
            if (NetworkLedger.OfflineMode && !NetworkLedger.IsLoopback(src)) return null; // placeholder text is shown
            lock (remoteImages) remotePending.Add(src);
            string url = src;
            Task.Factory.StartNew(delegate
            {
                byte[] data = null;
                NetworkRequestScope scope;
                string reason;
                if (NetworkLedger.TryBegin(NetworkFeature, "GET", url, out scope, out reason))
                {
                    try
                    {
                        using (System.Net.Http.HttpClient hc = new System.Net.Http.HttpClient())
                        {
                            hc.Timeout = TimeSpan.FromSeconds(15);
                            System.Net.Http.HttpResponseMessage resp = hc.GetAsync(url).Result;
                            scope.Status = (int)resp.StatusCode;
                            if (resp.IsSuccessStatusCode) data = resp.Content.ReadAsByteArrayAsync().Result;
                            if (data != null) scope.AddReceived(data.Length);
                        }
                        scope.Complete();
                    }
                    catch (Exception ex) { scope.Fail(ex); }
                }
                lock (remoteImages) { remotePending.Remove(url); if (data != null && data.Length < 8 * 1024 * 1024) remoteImages[url] = data; }
                if (data == null) return;
                try
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { string s = lastText; lastText = null; Render(s); }));
                }
                catch { }
            });
            return null;
        }

        private MdRtfLink LinkAt(Point p)
        {
            if (linkRanges.Count == 0) return null;
            int idx = box.GetCharIndexFromPosition(p);
            Point cp = box.GetPositionFromCharIndex(idx);
            if (Math.Abs(cp.Y - p.Y) > box.Font.Height * 2) return null;
            foreach (KeyValuePair<int, MdRtfLink> kv in linkRanges)
            {
                if (idx >= kv.Key && idx < kv.Key + kv.Value.Text.Length) return kv.Value;
            }
            return null;
        }

        private void OnBoxMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || box.SelectionLength > 0) return;
            MdRtfLink l = LinkAt(e.Location);
            if (l != null) ActivateLink(l.Url);
        }

        public void ActivateLink(string url)
        {
            MdLinkTarget t = MarkdownLinkResolver.Resolve(url, FilePath);
            if (t.Kind == MdLinkTarget.KindAnchor) ScrollToAnchor(t.Anchor);
            Action<string, MdLinkTarget> h = LinkClicked;
            if (h != null) h(url, t);
        }

        public bool ScrollToAnchor(string anchor)
        {
            int pos;
            if (string.IsNullOrEmpty(anchor) || !headingPositions.TryGetValue(anchor, out pos)) return false;
            ScrollToChar(pos);
            return true;
        }

        public void SyncToSourceLine(int topLine)
        {
            if (topLine == lastSyncedLine || anchorPositions.Count == 0) return;
            lastSyncedLine = topLine;
            int pos = 0;
            foreach (KeyValuePair<int, int> kv in anchorPositions) { if (kv.Key <= topLine) pos = kv.Value; else break; }
            ScrollToChar(pos);
        }

        private const int EM_SETSCROLLPOS = 0x04DE;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

        private void ScrollToChar(int charIndex)
        {
            try
            {
                Point cur = box.GetPositionFromCharIndex(charIndex);
                Point firstPos = box.GetPositionFromCharIndex(0); // negative when scrolled: doc Y = client Y - first Y
                POINT p = new POINT();
                p.X = 0;
                p.Y = Math.Max(0, cur.Y - firstPos.Y - 6);
                if (box.IsHandleCreated) SendMessage(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
            }
            catch { }
        }
    }
}
