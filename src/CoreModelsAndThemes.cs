using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: TypeForwardedTo(typeof(codeLess.Studio.NativeMemoryTrimmer))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.StudioTheme))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.EditorDiffHunk))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.DiagnosticItem))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.AstSymbolItem))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.WorkspaceSymbolLocation))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.DiskCheckpointMetadata))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.TimeTravelCheckpointManager))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.BufferSnapshot))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.EditorCaret))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.EditorTabBuffer))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.LlmHarnessProfile))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.ByokVendor))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.Bm25CodeChunk))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.CodeSnippetItem))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.VsixExtensionPackage))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.GitFileStatus))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.AgentToolCallResult))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.HarnessTraceEntry))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.AgentExecutionTurnResult))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.SavedVendorEntry))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.IdePersistentSettings))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.ConPtyNative))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.UnixPtyNative))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.TermCell))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.Vt100TerminalBuffer))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.TerminalProfileInfo))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.ConPtyTerminalSession))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.WorkspaceSearchMatch))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.WorkspaceSearchOptions))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.WorkspaceSearchEngine))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.LlmHarnessAndAgentEngine))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.VsixAndPluginHostEngine))]
[assembly: TypeForwardedTo(typeof(codeLess.Studio.SyntaxVerifier))]

namespace codeLess.Studio
{
    /// <summary>
    /// Custom-painted borderless dark dropdown control that replaces classic Win32 ComboBox
    /// (eliminating white 3D Win95 borders and classic grey dropdown buttons).
    /// </summary>
    public class CodeLessDropdown : Control
    {
        public class ItemList : List<object>
        {
            private readonly CodeLessDropdown owner;
            public ItemList(CodeLessDropdown owner) { this.owner = owner; }
            public new void Add(object item) { base.Add(item); owner.Invalidate(); }
            public new void AddRange(IEnumerable<object> items) { base.AddRange(items); owner.Invalidate(); }
            public new void Clear() { base.Clear(); owner.Invalidate(); }
        }

        public readonly ItemList Items;
        private int selectedIndex = -1;
        public ComboBoxStyle DropDownStyle { get; set; }
        public Color BorderColor { get; set; }
        public Color HoverBackColor { get; set; }
        public event EventHandler SelectedIndexChanged;
        private bool isHovered = false;

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value >= -1 && value < Items.Count)
                {
                    selectedIndex = value;
                    if (selectedIndex >= 0 && selectedIndex < Items.Count && Items[selectedIndex] != null)
                        base.Text = Items[selectedIndex].ToString();
                    this.Invalidate();
                    if (SelectedIndexChanged != null)
                        SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public override string Text
        {
            get
            {
                if (selectedIndex >= 0 && selectedIndex < Items.Count && Items[selectedIndex] != null)
                    return Items[selectedIndex].ToString();
                return base.Text ?? "";
            }
            set
            {
                base.Text = value ?? "";
                int found = -1;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i] != null && string.Equals(Items[i].ToString(), value, StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }
                selectedIndex = found;
                this.Invalidate();
            }
        }

        public CodeLessDropdown()
        {
            this.Items = new ItemList(this);
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.StandardClick |
                ControlStyles.StandardDoubleClick,
                true
            );
            this.Height = 25;
            this.Width = 170;
            this.BackColor = Color.FromArgb(24, 28, 38);
            this.ForeColor = Color.FromArgb(228, 232, 240);
            this.BorderColor = Color.FromArgb(46, 54, 72);
            this.HoverBackColor = Color.FromArgb(32, 38, 52);
            this.Font = new Font("Segoe UI Semibold", 8.8f);
            this.Cursor = Cursors.Hand;
        }

        private Color accentColor = Color.FromArgb(56, 189, 248);
        private Color arrowColor = Color.FromArgb(148, 163, 184);
        private StudioTheme currentTheme = StudioTheme.GetBuiltInThemes()[0];
        public string CustomDialogTitle { get; set; }

        public decimal Value
        {
            get
            {
                decimal v;
                if (decimal.TryParse((this.Text ?? "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v))
                    return v;
                return 0m;
            }
            set
            {
                string formatted = (value == Math.Floor(value))
                    ? ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                this.Text = formatted;
                if (selectedIndex < 0)
                {
                    string alt = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i] != null && string.Equals(Items[i].ToString(), alt, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }
                this.Invalidate();
            }
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            this.currentTheme = theme;
            this.BackColor = theme.BgElevated;
            this.ForeColor = theme.FgPrimary;
            this.BorderColor = theme.BorderSubtle;
            this.HoverBackColor = theme.BgHover;
            this.accentColor = theme.AccentPrimary;
            this.arrowColor = theme.FgSecondary;
            this.Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); isHovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); isHovered = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = isHovered ? HoverBackColor : this.BackColor;
            using (SolidBrush b = new SolidBrush(bg))
                g.FillRectangle(b, 0, 0, this.Width, this.Height);

            using (Pen p = new Pen(isHovered ? accentColor : BorderColor, 1f))
                g.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);

            string display = this.Text;
            Rectangle textRect = new Rectangle(6, 0, Math.Max(10, this.Width - 21), this.Height);
            TextRenderer.DrawText(g, display, this.Font, textRect, this.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            Rectangle arrowRect = new Rectangle(this.Width - 15, 0, 14, this.Height);
            TextRenderer.DrawText(g, "▾", this.Font, arrowRect, arrowColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Items.Count == 0 && DropDownStyle != ComboBoxStyle.DropDown) return;

            ContextMenuStrip menu = new ContextMenuStrip
            {
                BackColor = this.BackColor,
                ForeColor = this.ForeColor,
                Font = this.Font,
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Renderer = new DarkStudioMenuRenderer(() => currentTheme)
            };
            menu.Closed += (s, ev) =>
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                    this.BeginInvoke(new Action(() => menu.Dispose()));
                else
                    menu.Dispose();
            };

            for (int i = 0; i < Items.Count; i++)
            {
                int capIdx = i;
                string label = Items[i] != null ? Items[i].ToString() : "";
                ToolStripMenuItem mi = new ToolStripMenuItem(label)
                {
                    Checked = (i == selectedIndex),
                    BackColor = this.BackColor,
                    ForeColor = (i == selectedIndex) ? accentColor : this.ForeColor
                };
                mi.Click += (s, ev) => { this.SelectedIndex = capIdx; };
                menu.Items.Add(mi);
            }

            if (DropDownStyle == ComboBoxStyle.DropDown)
            {
                if (Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
                string promptTitle = string.IsNullOrEmpty(CustomDialogTitle) ? "Enter Custom Value" : CustomDialogTitle;
                ToolStripMenuItem customItem = new ToolStripMenuItem(promptTitle + "...")
                {
                    BackColor = this.BackColor,
                    ForeColor = accentColor
                };
                customItem.Click += (s, ev) =>
                {
                    using (Form dlg = new Form())
                    {
                        dlg.AutoScaleMode = AutoScaleMode.None;
                        dlg.Text = promptTitle;
                        dlg.Size = new Size(420, 140);
                        dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.MaximizeBox = false;
                        dlg.MinimizeBox = false;
                        dlg.BackColor = this.BackColor;
                        dlg.ForeColor = this.ForeColor;
                        CodeLessDarkInputBox box = new CodeLessDarkInputBox { Location = new Point(16, 20), Size = new Size(372, 28), Text = this.Text };
                        if (currentTheme != null) box.ApplyTheme(currentTheme);
                        Button ok = new Button { Text = "Apply", Location = new Point(298, 60), Size = new Size(90, 28), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = accentColor, ForeColor = Color.White };
                        ok.FlatAppearance.BorderSize = 0;
                        dlg.Controls.Add(box);
                        dlg.Controls.Add(ok);
                        dlg.AcceptButton = ok;
                        if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text))
                        {
                            this.Text = box.Text.Trim();
                            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                        }
                    }
                };
                menu.Items.Add(customItem);
            }

            menu.Show(this, new Point(0, this.Height));
        }
    }

    /// <summary>
    /// Hardware double-buffered custom-painted dark stream & log view that replaces Win32 RichTextBox
    /// (zero Msftedit.dll memory overhead, zero white Win95 scrollbars, and 100% crisp rendering).
    /// </summary>
    public class CodeLessStreamView : Control
    {
        private List<string> lines = new List<string>();
        private string rawText = "";
        private int scrollTopLine = 0;
        private StudioTheme theme = StudioTheme.GetBuiltInThemes()[0];
        public bool ReadOnly { get; set; }
        public BorderStyle BorderStyle { get; set; }
        public Color SelectionColor { get; set; }
        public int SelectionStart { get; set; }

        public void ApplyTheme(StudioTheme newTheme)
        {
            if (newTheme == null) return;
            this.theme = newTheme;
            this.BackColor = newTheme.BgWorkbench;
            this.ForeColor = newTheme.FgPrimary;
            if (this.ContextMenuStrip != null)
            {
                this.ContextMenuStrip.BackColor = newTheme.BgElevated;
                this.ContextMenuStrip.ForeColor = newTheme.FgPrimary;
            }
            this.Invalidate();
        }

        public override string Text
        {
            get { return rawText; }
            set
            {
                rawText = value ?? "";
                RebuildWrappedLines();
                this.Invalidate();
            }
        }

        private int selectedLineIdx = -1;
        private bool isDraggingScroll = false;
        public event Action<string> LineDoubleClicked;

        public CodeLessStreamView()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.StandardClick |
                ControlStyles.StandardDoubleClick,
                true
            );
            this.BackColor = Color.FromArgb(14, 17, 23);
            this.ForeColor = Color.FromArgb(220, 226, 236);
            this.Font = new Font("Consolas", 9.0f);

            ContextMenuStrip ctx = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(20, 23, 31),
                ForeColor = Color.FromArgb(228, 232, 240),
                ShowImageMargin = false,
                Renderer = new DarkStudioMenuRenderer(() => this.theme)
            };
            ctx.Items.Add("Open File / Jump to Line", null, (s, e) =>
            {
                if (selectedLineIdx >= 0 && selectedLineIdx < lines.Count && LineDoubleClicked != null)
                    LineDoubleClicked(lines[selectedLineIdx]);
            });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Copy Selected Line", null, (s, e) =>
            {
                if (selectedLineIdx >= 0 && selectedLineIdx < lines.Count && !string.IsNullOrEmpty(lines[selectedLineIdx]))
                    Clipboard.SetText(lines[selectedLineIdx]);
            });
            ctx.Items.Add("Copy All Output (Ctrl+C)", null, (s, e) =>
            {
                if (!string.IsNullOrEmpty(rawText))
                    Clipboard.SetText(rawText);
            });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Clear Stream", null, (s, e) => Clear());
            this.ContextMenuStrip = ctx;
        }

        public void Clear()
        {
            rawText = "";
            lines.Clear();
            scrollTopLine = 0;
            selectedLineIdx = -1;
            this.Invalidate();
        }

        public void AppendText(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            rawText += chunk;
            if (rawText.Length > 48000)
                rawText = rawText.Substring(rawText.Length - 40000);
            RebuildWrappedLines();
            ScrollToCaret();
        }

        public void ScrollToCaret()
        {
            int vis = VisibleLineCapacity;
            scrollTopLine = Math.Max(0, lines.Count - vis);
            this.Invalidate();
        }

        private int LineHeightPx
        {
            get { return Math.Max(19, this.Font.Height + 4); }
        }

        private int VisibleLineCapacity
        {
            get { return Math.Max(1, (this.Height - 12) / LineHeightPx); }
        }

        private int lastWrappedWidth = -1;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildWrappedLines();
        }

        private void RebuildWrappedLines()
        {
            lines.Clear();
            lastWrappedWidth = this.Width;
            if (string.IsNullOrEmpty(rawText)) return;
            int tenCharPx = TextRenderer.MeasureText("MMMMMMMMMM", this.Font, new Size(2000, 100), TextFormatFlags.NoPadding).Width;
            double charPx = Math.Max(7.4, tenCharPx / 10.0);
            int availPx = Math.Max(140, (this.Width > 60 ? this.Width : 400) - 38);
            int maxCharsPerLine = Math.Max(22, (int)(availPx / charPx) - 1);
            string[] rawLines = rawText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (string rl in rawLines)
            {
                if (rl.Length <= maxCharsPerLine)
                {
                    lines.Add(rl);
                }
                else
                {
                    string indent = "";
                    for (int k = 0; k < rl.Length && k < 8 && rl[k] == ' '; k++) indent += " ";
                    if (rl.TrimStart().StartsWith("• ") || rl.TrimStart().StartsWith("- "))
                        indent += "  ";

                    string rem = rl;
                    bool firstSeg = true;
                    while (rem.Length > 0)
                    {
                        int cap = firstSeg ? maxCharsPerLine : Math.Max(16, maxCharsPerLine - indent.Length);
                        if (rem.Length <= cap)
                        {
                            lines.Add(firstSeg ? rem : (indent + rem));
                            break;
                        }
                        int splitAt = rem.LastIndexOf(' ', cap);
                        if (splitAt <= 4) splitAt = cap;
                        string part = rem.Substring(0, splitAt).TrimEnd();
                        lines.Add(firstSeg ? part : (indent + part));
                        rem = rem.Substring(splitAt).TrimStart();
                        firstSeg = false;
                    }
                }
            }
        }

        private void ScrollStreamFromMouseY(int mouseY)
        {
            int vis = VisibleLineCapacity;
            if (lines.Count <= vis) return;
            int thumbH = Math.Max(20, (int)((double)vis / lines.Count * (this.Height - 4)));
            int trackRange = Math.Max(1, this.Height - 4 - thumbH);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - 2 - thumbH / 2) / trackRange));
            scrollTopLine = (int)Math.Round(ratio * Math.Max(0, lines.Count - vis));
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = e.Delta > 0 ? -3 : 3;
            scrollTopLine = Math.Max(0, Math.Min(Math.Max(0, lines.Count - VisibleLineCapacity), scrollTopLine + delta));
            this.Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            if (e.Button == MouseButtons.Left && lines.Count > VisibleLineCapacity && e.X >= this.Width - 12)
            {
                isDraggingScroll = true;
                ScrollStreamFromMouseY(e.Y);
                return;
            }
            int clickedRow = (e.Y - 6) / LineHeightPx;
            int idx = scrollTopLine + clickedRow;
            if (idx >= 0 && idx < lines.Count)
            {
                selectedLineIdx = idx;
                this.Invalidate();
                if (e.Button == MouseButtons.Left && LineDoubleClicked != null)
                {
                    LineDoubleClicked(lines[idx]);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (isDraggingScroll && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                ScrollStreamFromMouseY(e.Y);
                return;
            }
            bool overSb = lines.Count > VisibleLineCapacity && e.X >= this.Width - 12;
            this.Cursor = overSb ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            isDraggingScroll = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.C)
            {
                if (selectedLineIdx >= 0 && selectedLineIdx < lines.Count && !string.IsNullOrEmpty(lines[selectedLineIdx]))
                    Clipboard.SetText(lines[selectedLineIdx]);
                else if (!string.IsNullOrEmpty(rawText))
                    Clipboard.SetText(rawText);
            }
            else if (e.KeyCode == Keys.Enter && selectedLineIdx >= 0 && selectedLineIdx < lines.Count && LineDoubleClicked != null)
            {
                LineDoubleClicked(lines[selectedLineIdx]);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (this.Width != lastWrappedWidth)
                RebuildWrappedLines();
            Graphics g = e.Graphics;
            g.Clear(this.BackColor);

            using (Pen topBorder = new Pen(theme != null ? theme.BorderSubtle : Color.FromArgb(32, 38, 52), 1f))
                g.DrawLine(topBorder, 0, 0, this.Width, 0);

            int lh = LineHeightPx;
            int vis = VisibleLineCapacity;
            int y = 6;

            for (int i = 0; i < vis; i++)
            {
                int idx = scrollTopLine + i;
                if (idx >= lines.Count) break;
                string ln = lines[idx];
                string tr = ln.TrimStart();

                if (idx == selectedLineIdx)
                {
                    Color selCol = theme != null ? StudioTheme.Blend(this.BackColor, theme.AccentPrimary, 0.18) : Color.FromArgb(30, 41, 59);
                    using (SolidBrush selB = new SolidBrush(selCol))
                        g.FillRectangle(selB, 0, y - 1, this.Width - 8, lh);
                }

                Color c = this.ForeColor;
                if (tr.StartsWith("[") && tr.Contains("]"))
                    c = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248);
                else if (tr.StartsWith("✓") || tr.StartsWith("+ "))
                    c = theme != null ? theme.AccentSecondary : Color.FromArgb(52, 211, 153);
                else if (tr.StartsWith("⚠") || tr.StartsWith("✕") || tr.StartsWith("Error") || tr.StartsWith("- "))
                    c = theme != null ? theme.TokenControl : Color.FromArgb(251, 113, 133);
                else if (tr.StartsWith("PS ") || tr.StartsWith("$ ") || tr.StartsWith("> ") || tr.StartsWith("===") || tr.StartsWith("───"))
                    c = theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184);
                else if (tr.StartsWith("•") || tr.StartsWith("✦") || tr.StartsWith("⚡") || tr.StartsWith("◈"))
                    c = theme != null ? theme.TokenKeyword : Color.FromArgb(192, 132, 252);

                TextRenderer.DrawText(g, ln, this.Font, new Point(10, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                y += lh;
            }

            // Sleek Vertical Scrollbar
            if (lines.Count > vis && this.Height > 24)
            {
                int sbX = this.Width - 6;
                int thumbH = Math.Max(20, (int)((double)vis / lines.Count * (this.Height - 4)));
                int thumbY = 2 + (int)((double)scrollTopLine / Math.Max(1, lines.Count - vis) * (this.Height - 4 - thumbH));
                Color thumbCol = theme != null ? StudioTheme.Blend(this.BackColor, theme.FgSecondary, 0.42) : Color.FromArgb(95, 148, 163, 184);
                using (SolidBrush sb = new SolidBrush(thumbCol))
                    g.FillRectangle(sb, sbX, thumbY, 4, thumbH);
            }
        }
    }

    /// <summary>
    /// Custom-painted VS Code dark list & tree view control with zero Win32 white scrollbars,
    /// full-row hover/selection highlights, and colored language/symbol badges.
    /// Drop-in replacement for WinForms ListBox.
    /// </summary>
    public class CodeLessListBox : Control
    {
        public class ObjectCollection : List<object>
        {
            private readonly CodeLessListBox owner;
            public ObjectCollection(CodeLessListBox owner) { this.owner = owner; }
            public new void Add(object item) { base.Add(item); owner.ClampScroll(); owner.Invalidate(); }
            public new void AddRange(IEnumerable<object> items) { base.AddRange(items); owner.ClampScroll(); owner.Invalidate(); }
            public new void Clear() { base.Clear(); owner.ResetSelection(); owner.Invalidate(); }
            public new void RemoveAt(int index) { base.RemoveAt(index); owner.ClampScroll(); owner.Invalidate(); }
        }

        public readonly ObjectCollection Items;
        private int selectedIndex = -1;
        private int hoveredIndex = -1;
        private int scrollTopIndex = 0;
        private StudioTheme theme = StudioTheme.GetBuiltInThemes()[0];
        public int ItemHeight { get; set; }
        public BorderStyle BorderStyle { get; set; }
        public DrawMode DrawMode { get; set; }
        public event EventHandler SelectedIndexChanged;
        public event DrawItemEventHandler DrawItem;

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                int clamped = (value >= -1 && value < Items.Count) ? value : -1;
                if (selectedIndex != clamped)
                {
                    selectedIndex = clamped;
                    EnsureVisible(selectedIndex);
                    this.Invalidate();
                    if (SelectedIndexChanged != null)
                        SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public object SelectedItem
        {
            get { return (selectedIndex >= 0 && selectedIndex < Items.Count) ? Items[selectedIndex] : null; }
        }

        public CodeLessListBox()
        {
            this.Items = new ObjectCollection(this);
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.StandardClick |
                ControlStyles.StandardDoubleClick,
                true
            );
            this.ItemHeight = 23;
            this.BackColor = Color.FromArgb(14, 17, 24);
            this.ForeColor = Color.FromArgb(224, 230, 240);
            this.Font = new Font("Segoe UI", 9.0f);
            this.Cursor = Cursors.Hand;
        }

        public void ApplyTheme(StudioTheme newTheme)
        {
            if (newTheme == null) return;
            this.theme = newTheme;
            this.BackColor = newTheme.BgSidebar;
            this.ForeColor = newTheme.FgPrimary;
            this.Invalidate();
        }

        internal void ResetSelection()
        {
            selectedIndex = -1;
            hoveredIndex = -1;
            scrollTopIndex = 0;
        }

        internal void ClampScroll()
        {
            int cap = VisibleCapacity;
            if (scrollTopIndex > Math.Max(0, Items.Count - cap))
                scrollTopIndex = Math.Max(0, Items.Count - cap);
        }

        public int VisibleCapacity
        {
            get { return Math.Max(1, (this.Height - 4) / Math.Max(16, ItemHeight)); }
        }

        public void EnsureVisible(int idx)
        {
            if (idx < 0 || idx >= Items.Count) return;
            int cap = VisibleCapacity;
            if (idx < scrollTopIndex) scrollTopIndex = idx;
            else if (idx >= scrollTopIndex + cap) scrollTopIndex = idx - cap + 1;
        }

        private bool isDraggingListScroll = false;

        private void ScrollListFromMouseY(int mouseY)
        {
            int cap = VisibleCapacity;
            if (Items.Count <= cap) return;
            int thumbH = Math.Max(18, (int)((double)cap / Items.Count * (this.Height - 4)));
            int trackRange = Math.Max(1, this.Height - 4 - thumbH);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - 2 - thumbH / 2) / trackRange));
            scrollTopIndex = (int)Math.Round(ratio * Math.Max(0, Items.Count - cap));
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int delta = e.Delta > 0 ? -3 : 3;
            scrollTopIndex = Math.Max(0, Math.Min(Math.Max(0, Items.Count - VisibleCapacity), scrollTopIndex + delta));
            this.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (isDraggingListScroll && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                ScrollListFromMouseY(e.Y);
                return;
            }
            int idx = scrollTopIndex + (e.Y - 2) / Math.Max(16, ItemHeight);
            if (idx < 0 || idx >= Items.Count || (Items.Count > VisibleCapacity && e.X >= this.Width - 10)) idx = -1;
            if (idx != hoveredIndex)
            {
                hoveredIndex = idx;
                this.Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoveredIndex != -1)
            {
                hoveredIndex = -1;
                this.Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            if (e.Button == MouseButtons.Left && Items.Count > VisibleCapacity && e.X >= this.Width - 10)
            {
                isDraggingListScroll = true;
                ScrollListFromMouseY(e.Y);
                return;
            }
            int idx = scrollTopIndex + (e.Y - 2) / Math.Max(16, ItemHeight);
            if (idx >= 0 && idx < Items.Count)
            {
                this.SelectedIndex = idx;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            isDraggingListScroll = false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down && Items.Count > 0)
            {
                this.SelectedIndex = Math.Min(Items.Count - 1, selectedIndex + 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && Items.Count > 0)
            {
                this.SelectedIndex = Math.Max(0, selectedIndex - 1);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(this.BackColor);

            int ih = Math.Max(16, ItemHeight);
            int cap = VisibleCapacity;

            for (int i = 0; i < cap; i++)
            {
                int idx = scrollTopIndex + i;
                if (idx >= Items.Count) break;

                Rectangle rowRect = new Rectangle(0, 2 + i * ih, this.Width, ih);
                bool isSel = (idx == selectedIndex);
                bool isHov = (idx == hoveredIndex);

                if (DrawItem != null && DrawMode != DrawMode.Normal)
                {
                    DrawItemState st = isSel ? DrawItemState.Selected : DrawItemState.Default;
                    DrawItem(this, new DrawItemEventArgs(g, this.Font, rowRect, idx, st, this.ForeColor, this.BackColor));
                    continue;
                }

                if (isSel)
                {
                    Color selBg = theme != null ? StudioTheme.Blend(this.BackColor, theme.AccentPrimary, 0.18) : Color.FromArgb(26, 46, 74);
                    Color accentBar = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248);
                    using (SolidBrush sb = new SolidBrush(selBg))
                        g.FillRectangle(sb, rowRect);
                    using (SolidBrush bar = new SolidBrush(accentBar))
                        g.FillRectangle(bar, 0, rowRect.Y, 2, rowRect.Height);
                }
                else if (isHov)
                {
                    Color hovBg = theme != null ? theme.BgHover : Color.FromArgb(22, 27, 38);
                    using (SolidBrush hb = new SolidBrush(hovBg))
                        g.FillRectangle(hb, rowRect);
                }

                string raw = Items[idx] != null ? Items[idx].ToString() : "";
                DrawFormattedListRow(g, raw, rowRect, isSel);
            }

            // Sleek 4px Scrollbar (Zero white Win32 scrollbars!)
            if (Items.Count > cap && this.Height > 20)
            {
                int sbX = this.Width - 5;
                int thumbH = Math.Max(18, (int)((double)cap / Items.Count * (this.Height - 4)));
                int thumbY = 2 + (int)((double)scrollTopIndex / Math.Max(1, Items.Count - cap) * (this.Height - 4 - thumbH));
                Color thumbCol = theme != null ? StudioTheme.Blend(this.BackColor, theme.FgSecondary, 0.42) : Color.FromArgb(95, 148, 163, 184);
                using (SolidBrush sb = new SolidBrush(thumbCol))
                    g.FillRectangle(sb, sbX, thumbY, 4, thumbH);
            }
        }

        private void DrawFormattedListRow(Graphics g, string text, Rectangle rowRect, bool isSel)
        {
            int x = 8;
            while (x < 64 && text.StartsWith("  "))
            {
                x += 12;
                text = text.Substring(2);
            }

            string badge = null;
            Color badgeColor = theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184);

            if (text.StartsWith("[class] ")) { badge = "C"; badgeColor = theme != null ? theme.AccentSecondary : Color.FromArgb(74, 222, 128); text = text.Substring(8); }
            else if (text.StartsWith("[struct] ")) { badge = "S"; badgeColor = theme != null ? theme.TokenType : Color.FromArgb(56, 189, 248); text = text.Substring(9); }
            else if (text.StartsWith("[interface] ")) { badge = "I"; badgeColor = theme != null ? theme.TokenKeyword : Color.FromArgb(192, 132, 252); text = text.Substring(12); }
            else if (text.StartsWith("[enum] ")) { badge = "E"; badgeColor = theme != null ? theme.TokenNumber : Color.FromArgb(250, 204, 21); text = text.Substring(7); }
            else if (text.StartsWith("[fn] ")) { badge = "fn"; badgeColor = theme != null ? theme.TokenFunction : Color.FromArgb(96, 165, 250); text = text.Substring(5); }
            else if (text.StartsWith("≡ "))
            {
                text = text.Substring(2);
                string ext = Path.GetExtension(text).ToLowerInvariant();
                if (ext == ".cs") { badge = "C#"; badgeColor = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248); }
                else if (ext == ".py") { badge = "PY"; badgeColor = theme != null ? theme.TokenNumber : Color.FromArgb(250, 204, 21); }
                else if (ext == ".js" || ext == ".mjs") { badge = "JS"; badgeColor = theme != null ? theme.TokenType : Color.FromArgb(245, 158, 11); }
                else if (ext == ".ts" || ext == ".tsx") { badge = "TS"; badgeColor = theme != null ? theme.AccentPrimary : Color.FromArgb(96, 165, 250); }
                else if (ext == ".rs") { badge = "RS"; badgeColor = theme != null ? theme.TokenControl : Color.FromArgb(251, 146, 60); }
                else if (ext == ".ps1") { badge = "PS"; badgeColor = theme != null ? theme.TokenFunction : Color.FromArgb(34, 211, 238); }
                else if (ext == ".sh") { badge = "SH"; badgeColor = theme != null ? theme.AccentSecondary : Color.FromArgb(74, 222, 128); }
                else if (ext == ".json" || ext == ".manifest") { badge = "{}"; badgeColor = theme != null ? theme.TokenKeyword : Color.FromArgb(192, 132, 252); }
                else { badge = "≡"; badgeColor = theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184); }
            }

            if (badge != null)
            {
                using (Font bf = new Font("Consolas", 8.2f, FontStyle.Bold))
                {
                    Rectangle br = new Rectangle(x, rowRect.Y, 22, rowRect.Height);
                    TextRenderer.DrawText(g, badge, bf, br, badgeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                }
                x += 24;
            }

            Rectangle tr = new Rectangle(x, rowRect.Y, Math.Max(10, rowRect.Width - x - 8), rowRect.Height);
            Color fg = isSel ? (theme != null ? theme.FgPrimary : Color.White) : this.ForeColor;
            TextRenderer.DrawText(g, text, this.Font, tr, fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Custom-painted VS Code Explorer File Tree control with collapsible folders,
    /// colored language icons (C#, PY, JS, TS, RS, PS, SH, {}), full-row selection, and 4px dark scrollbar.
    /// </summary>
    public class CodeLessExplorerTree : Control
    {
        public class FlatRow
        {
            public TreeNode Node;
            public int Depth;
            public bool IsFolder;
            public bool IsExpanded;
        }

        private readonly TreeView backingTree = new TreeView();
        private readonly HashSet<TreeNode> expandedSet = new HashSet<TreeNode>();
        private readonly List<FlatRow> flatRows = new List<FlatRow>();
        private int hoveredIdx = -1;
        private int scrollTopIdx = 0;
        private StudioTheme theme = StudioTheme.GetBuiltInThemes()[0];

        public TreeNodeCollection Nodes { get { return backingTree.Nodes; } }
        public TreeNode SelectedNode { get; set; }
        public int ItemHeight { get; set; }
        public BorderStyle BorderStyle { get; set; }
        public bool ShowLines { get; set; }
        public bool FullRowSelect { get; set; }
        public bool HideSelection { get; set; }
        public string ActiveEditorFilePath { get; set; }
        public bool ShowEmptyWorkspaceState { get; set; }
        public bool HideRootFolderRow = true;
        public readonly Dictionary<string, string> GitStatusByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Full paths rendered greyed-out (gitignored / build output / dependency folders), like VS Code.</summary>
        public readonly HashSet<string> DimmedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public event TreeNodeMouseClickEventHandler NodeMouseClick;
        public event Action OpenFolderRequested;
        public event Action OnOpenFolderEmptyStateClicked;
        public event Action OnCloneRepoEmptyStateClicked;
        public event Action<string, string> OnInlineRenameCommitted;
        public event Action<TreeNode> BeforeNodeExpand;
        private Rectangle emptyOpenFolderBtnRect = Rectangle.Empty;

        // Inline Rename & Inline New File/Folder Creation
        private TextBox inlineEditBox;
        private Panel inlineEditHost;
        private TreeNode inlineTargetNode;
        private bool inlineIsCreateMode = false;
        private bool inlineCreateIsFolder = false;
        private string inlineCreateParentDir = "";
        private Action<TreeNode, string> inlineRenameCallback;
        private Action<string, string, bool> inlineCreateCallback;

        public bool IsInlineEditing
        {
            get { return inlineEditHost != null && inlineEditHost.Visible; }
        }

        public TextBox InlineEditBox
        {
            get { return inlineEditBox; }
        }

        public CodeLessExplorerTree()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.StandardClick |
                ControlStyles.StandardDoubleClick,
                true
            );
            this.ItemHeight = 22;
            this.BackColor = Color.FromArgb(14, 17, 24);
            this.ForeColor = Color.FromArgb(224, 230, 240);
            this.Font = new Font("Segoe UI", 9.0f);
            this.Cursor = Cursors.Hand;

            BuildInlineEditorBox();
        }

        private void BuildInlineEditorBox()
        {
            inlineEditHost = new Panel
            {
                Visible = false,
                Height = 20,
                Padding = new Padding(3, 1, 3, 1),
                BackColor = Color.FromArgb(24, 28, 38)
            };
            inlineEditHost.Paint += (s, e) =>
            {
                Color accent = theme != null ? theme.AccentPrimary : Color.FromArgb(0, 122, 204);
                using (Pen p = new Pen(accent, 1f))
                    e.Graphics.DrawRectangle(p, 0, 0, inlineEditHost.Width - 1, inlineEditHost.Height - 1);
            };
            inlineEditBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.8f),
                BackColor = Color.FromArgb(24, 28, 38),
                ForeColor = Color.White
            };
            inlineEditBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    CommitInlineEdit();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    CancelInlineEdit();
                }
            };
            inlineEditBox.LostFocus += (s, e) =>
            {
                if (inlineEditHost.Visible)
                    CancelInlineEdit();
            };
            inlineEditHost.Controls.Add(inlineEditBox);
            this.Controls.Add(inlineEditHost);
        }

        public void StartInlineRename()
        {
            StartInlineRename(SelectedNode, (node, newName) =>
            {
                if (OnInlineRenameCommitted != null && node != null && node.Tag is string)
                    OnInlineRenameCommitted((string)node.Tag, newName);
            });
        }

        public void StartInlineRename(TreeNode node, Action<TreeNode, string> onCommit)
        {
            if (node == null) return;
            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0)
                RebuildTreeLayout(true);

            int rowIdx = flatRows.FindIndex(r => r.Node == node);
            if (rowIdx < 0 && node.Nodes.Count > 0)
                rowIdx = flatRows.FindIndex(r => r.Node == node.Nodes[0]);
            if (rowIdx < 0) return;
            EnsureVisible(rowIdx);

            string rawName = node.Text ?? "";
            if (rawName.StartsWith("⊞ ") || rawName.StartsWith("▸ ") || rawName.StartsWith("≡ "))
                rawName = rawName.Substring(2);

            inlineTargetNode = node;
            inlineIsCreateMode = false;
            inlineRenameCallback = onCommit;

            int ih = Math.Max(18, ItemHeight);
            int visIdx = rowIdx - scrollTopIdx;
            int depth = flatRows[rowIdx].Depth;
            int x = 8 + depth * 14 + (flatRows[rowIdx].IsFolder ? 16 : 23);
            int y = 2 + visIdx * ih + 1;

            inlineEditHost.BackColor = theme != null ? theme.BgEditor : Color.FromArgb(24, 28, 38);
            inlineEditBox.BackColor = inlineEditHost.BackColor;
            inlineEditBox.ForeColor = theme != null ? theme.FgPrimary : Color.White;
            inlineEditHost.Bounds = new Rectangle(x, y, Math.Max(80, this.Width - x - 12), ih - 2);
            inlineEditBox.Text = rawName;
            inlineEditHost.Visible = true;
            inlineEditHost.BringToFront();
            inlineEditBox.Focus();
            int dotIdx = rawName.LastIndexOf('.');
            if (dotIdx > 0 && !flatRows[rowIdx].IsFolder)
                inlineEditBox.Select(0, dotIdx);
            else
                inlineEditBox.SelectAll();
            this.Invalidate();
        }

        public void StartInlineCreate(string parentDirPath, bool isFolder, Action<string, string, bool> onCommit)
        {
            if (string.IsNullOrEmpty(parentDirPath)) return;
            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0)
                RebuildTreeLayout(true);

            inlineIsCreateMode = true;
            inlineCreateIsFolder = isFolder;
            inlineCreateParentDir = parentDirPath;
            inlineCreateCallback = onCommit;

            int rowIdx = 0;
            if (SelectedNode != null)
            {
                int found = flatRows.FindIndex(r => r.Node == SelectedNode);
                if (found >= 0) rowIdx = found;
            }
            EnsureVisible(rowIdx);

            int ih = Math.Max(18, ItemHeight);
            int visIdx = Math.Max(0, rowIdx - scrollTopIdx);
            int depth = (rowIdx < flatRows.Count) ? flatRows[rowIdx].Depth + 1 : 1;
            int x = 8 + depth * 14 + 18;
            int y = 2 + visIdx * ih + 1;

            inlineEditHost.BackColor = theme != null ? theme.BgEditor : Color.FromArgb(24, 28, 38);
            inlineEditBox.BackColor = inlineEditHost.BackColor;
            inlineEditBox.ForeColor = theme != null ? theme.FgPrimary : Color.White;
            inlineEditHost.Bounds = new Rectangle(x, y, Math.Max(80, this.Width - x - 12), ih - 2);
            inlineEditBox.Text = isFolder ? "new-folder" : "NewFile.cs";
            inlineEditHost.Visible = true;
            inlineEditHost.BringToFront();
            inlineEditBox.Focus();
            inlineEditBox.SelectAll();
            this.Invalidate();
        }

        public void CommitInlineEdit()
        {
            if (!inlineEditHost.Visible) return;
            string newVal = (inlineEditBox.Text ?? "").Trim();
            inlineEditHost.Visible = false;
            if (string.IsNullOrEmpty(newVal))
            {
                this.Invalidate();
                return;
            }

            if (inlineIsCreateMode)
            {
                var cb = inlineCreateCallback;
                inlineCreateCallback = null;
                if (cb != null) cb(inlineCreateParentDir, newVal, inlineCreateIsFolder);
            }
            else if (inlineTargetNode != null)
            {
                var cb = inlineRenameCallback;
                var node = inlineTargetNode;
                inlineRenameCallback = null;
                inlineTargetNode = null;
                if (cb != null) cb(node, newVal);
            }
            this.Invalidate();
        }

        public void CancelInlineEdit()
        {
            if (!inlineEditHost.Visible) return;
            inlineEditHost.Visible = false;
            inlineTargetNode = null;
            inlineRenameCallback = null;
            inlineCreateCallback = null;
            this.Invalidate();
        }

        public void CollapseAllFolders()
        {
            expandedSet.Clear();
            foreach (TreeNode root in backingTree.Nodes)
                expandedSet.Add(root);
            RebuildFlatRows();
            this.Invalidate();
        }

        public void ApplyTheme(StudioTheme newTheme)
        {
            if (newTheme == null) return;
            this.theme = newTheme;
            this.BackColor = newTheme.BgSidebar;
            this.ForeColor = newTheme.FgPrimary;
            this.Invalidate();
        }

        public void EnsureVisible(int idx)
        {
            if (idx < 0 || idx >= flatRows.Count) return;
            int cap = VisibleCapacity;
            if (idx < scrollTopIdx) scrollTopIdx = idx;
            else if (idx >= scrollTopIdx + cap) scrollTopIdx = idx - cap + 1;
        }

        public void RebuildTreeLayout(bool expandRootAndFirstLevel = false)
        {
            if (expandRootAndFirstLevel)
            {
                expandedSet.Clear();
                foreach (TreeNode root in backingTree.Nodes)
                {
                    expandedSet.Add(root);
                    foreach (TreeNode child in root.Nodes)
                    {
                        bool hasLazyMarker = (child.Nodes.Count == 1 && (child.Nodes[0].Tag as string) == "__LAZY_DIR__");
                        if (child.Nodes.Count > 0 && !hasLazyMarker)
                            expandedSet.Add(child);
                    }
                }
            }
            RebuildFlatRows();
            this.Invalidate();
        }

        /// <summary>Full paths of every expanded folder (used to rebuild the tree without losing state).</summary>
        public HashSet<string> GetExpandedPaths()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode n in expandedSet)
            {
                string p = n.Tag as string;
                if (!string.IsNullOrEmpty(p) && !p.StartsWith("__")) result.Add(p);
            }
            return result;
        }

        public bool IsRootCollapsedByHeader { get { return rootCollapsedByHeader; } }

        /// <summary>Drops references to detached nodes before a rebuild (else collapses don't stick and old trees leak).</summary>
        public void ClearExpanded()
        {
            expandedSet.Clear();
        }

        public TreeNode FindNodeByPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            foreach (TreeNode root in backingTree.Nodes)
            {
                TreeNode f = FindNodeRecursive(root, fullPath);
                if (f != null) return f;
            }
            return null;
        }

        private static TreeNode FindNodeRecursive(TreeNode cur, string fullPath)
        {
            if (string.Equals(cur.Tag as string, fullPath, StringComparison.OrdinalIgnoreCase)) return cur;
            foreach (TreeNode child in cur.Nodes)
            {
                TreeNode f = FindNodeRecursive(child, fullPath);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Marks a node expanded without raising BeforeNodeExpand (caller already populated it).</summary>
        public void MarkExpanded(TreeNode node)
        {
            if (node != null) expandedSet.Add(node);
        }

        /// <summary>Expands all ancestors, selects the node and scrolls it into view.</summary>
        public void RevealAndSelect(TreeNode node)
        {
            if (node == null) return;
            TreeNode p = node.Parent;
            while (p != null) { expandedSet.Add(p); p = p.Parent; }
            SelectedNode = node;
            RebuildFlatRows();
            for (int i = 0; i < flatRows.Count; i++)
            {
                if (ReferenceEquals(flatRows[i].Node, node)) { EnsureVisible(i); break; }
            }
            this.Invalidate();
        }

        public void ExpandNode(TreeNode node)
        {
            if (node == null) return;
            if (BeforeNodeExpand != null) BeforeNodeExpand(node);
            expandedSet.Add(node);
            RebuildFlatRows();
            this.Invalidate();
        }

        private bool rootCollapsedByHeader = false;

        public bool ToggleRootFolderExpanded()
        {
            if (backingTree.Nodes.Count == 0) return true;
            TreeNode root = backingTree.Nodes[0];
            if (!rootCollapsedByHeader)
            {
                rootCollapsedByHeader = true;
                expandedSet.Remove(root);
            }
            else
            {
                rootCollapsedByHeader = false;
                expandedSet.Add(root);
            }
            RebuildFlatRows();
            this.Invalidate();
            return !rootCollapsedByHeader;
        }

        private void RebuildFlatRows()
        {
            flatRows.Clear();
            foreach (TreeNode root in backingTree.Nodes)
            {
                if (!expandedSet.Contains(root) && !rootCollapsedByHeader)
                    expandedSet.Add(root);
                if (HideRootFolderRow && backingTree.Nodes.Count == 1)
                {
                    if (expandedSet.Contains(root))
                    {
                        foreach (TreeNode child in root.Nodes)
                            AppendNodeRecursive(child, 0);
                    }
                }
                else
                {
                    AppendNodeRecursive(root, 0);
                }
            }
            int cap = VisibleCapacity;
            if (scrollTopIdx > Math.Max(0, flatRows.Count - cap))
                scrollTopIdx = Math.Max(0, flatRows.Count - cap);
        }

        private void AppendNodeRecursive(TreeNode node, int depth)
        {
            string tagPath = node.Tag as string;
            if (tagPath == "__LAZY_DIR__") return;
            bool isFolder;
            if (node.StateImageIndex == 1) isFolder = true;
            else if (node.StateImageIndex == 2 || tagPath == "__LOADING__" || tagPath == "__EMPTY_FOLDER__") isFolder = false;
            else isFolder = node.Nodes.Count > 0 || (!string.IsNullOrEmpty(tagPath) && Directory.Exists(tagPath));
            bool exp = expandedSet.Contains(node);
            flatRows.Add(new FlatRow
            {
                Node = node,
                Depth = depth,
                IsFolder = isFolder,
                IsExpanded = exp
            });

            if (isFolder && exp)
            {
                foreach (TreeNode child in node.Nodes)
                    AppendNodeRecursive(child, depth + 1);
            }
        }

        private int VisibleCapacity
        {
            get { return Math.Max(1, (this.Height - 4) / Math.Max(18, ItemHeight)); }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Enter || k == Keys.F2 || k == Keys.Delete)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0)
                RebuildTreeLayout(true);
            if (flatRows.Count == 0) return;

            int curIdx = -1;
            for (int i = 0; i < flatRows.Count; i++)
            {
                if (flatRows[i].Node == SelectedNode) { curIdx = i; break; }
            }

            if (e.KeyCode == Keys.Down)
            {
                int nextIdx = Math.Min(flatRows.Count - 1, curIdx + 1);
                while (nextIdx < flatRows.Count - 1 && ((flatRows[nextIdx].Node.Tag as string) == "__LOADING__" || (flatRows[nextIdx].Node.Tag as string) == "__EMPTY_FOLDER__"))
                    nextIdx++;
                string nextTag = flatRows[nextIdx].Node.Tag as string;
                if (nextTag != "__LOADING__" && nextTag != "__EMPTY_FOLDER__")
                {
                    SelectedNode = flatRows[nextIdx].Node;
                    EnsureVisible(nextIdx);
                    this.Invalidate();
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                int prevIdx = curIdx <= 0 ? 0 : curIdx - 1;
                while (prevIdx > 0 && ((flatRows[prevIdx].Node.Tag as string) == "__LOADING__" || (flatRows[prevIdx].Node.Tag as string) == "__EMPTY_FOLDER__"))
                    prevIdx--;
                string prevTag = flatRows[prevIdx].Node.Tag as string;
                if (prevTag != "__LOADING__" && prevTag != "__EMPTY_FOLDER__")
                {
                    SelectedNode = flatRows[prevIdx].Node;
                    EnsureVisible(prevIdx);
                    this.Invalidate();
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right && curIdx >= 0)
            {
                FlatRow row = flatRows[curIdx];
                if (row.IsFolder)
                {
                    if (!row.IsExpanded)
                    {
                        if (BeforeNodeExpand != null) BeforeNodeExpand(row.Node);
                        expandedSet.Add(row.Node);
                        RebuildFlatRows();
                        this.Invalidate();
                    }
                    else if (curIdx + 1 < flatRows.Count && flatRows[curIdx + 1].Depth > row.Depth)
                    {
                        string childTag = flatRows[curIdx + 1].Node.Tag as string;
                        if (childTag != "__LOADING__" && childTag != "__EMPTY_FOLDER__")
                        {
                            SelectedNode = flatRows[curIdx + 1].Node;
                            EnsureVisible(curIdx + 1);
                            this.Invalidate();
                        }
                    }
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Left && curIdx >= 0)
            {
                FlatRow row = flatRows[curIdx];
                if (row.IsFolder && row.IsExpanded)
                {
                    expandedSet.Remove(row.Node);
                    RebuildFlatRows();
                    this.Invalidate();
                }
                else if (row.Node.Parent != null)
                {
                    SelectedNode = row.Node.Parent;
                    int pIdx = flatRows.FindIndex(r => r.Node == SelectedNode);
                    if (pIdx >= 0) EnsureVisible(pIdx);
                    this.Invalidate();
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && curIdx >= 0)
            {
                FlatRow row = flatRows[curIdx];
                string rowTag = row.Node.Tag as string;
                if (rowTag == "__LOADING__" || rowTag == "__EMPTY_FOLDER__")
                {
                    e.Handled = true;
                    return;
                }
                if (row.IsFolder)
                {
                    if (expandedSet.Contains(row.Node)) expandedSet.Remove(row.Node);
                    else
                    {
                        if (BeforeNodeExpand != null) BeforeNodeExpand(row.Node);
                        expandedSet.Add(row.Node);
                    }
                    RebuildFlatRows();
                    this.Invalidate();
                }
                else if (NodeMouseClick != null)
                {
                    NodeMouseClick(this, new TreeNodeMouseClickEventArgs(row.Node, MouseButtons.Left, 1, 10, 10));
                }
                e.Handled = true;
            }
        }

        private bool isDraggingTreeScroll = false;

        private void ScrollTreeFromMouseY(int mouseY)
        {
            int cap = VisibleCapacity;
            if (flatRows.Count <= cap) return;
            int thumbH = Math.Max(18, (int)((double)cap / flatRows.Count * (this.Height - 4)));
            int trackRange = Math.Max(1, this.Height - 4 - thumbH);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - 2 - thumbH / 2) / trackRange));
            scrollTopIdx = (int)Math.Round(ratio * Math.Max(0, flatRows.Count - cap));
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0) RebuildTreeLayout(true);
            int delta = e.Delta > 0 ? -3 : 3;
            scrollTopIdx = Math.Max(0, Math.Min(Math.Max(0, flatRows.Count - VisibleCapacity), scrollTopIdx + delta));
            this.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0) RebuildTreeLayout(true);
            if (isDraggingTreeScroll && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                ScrollTreeFromMouseY(e.Y);
                return;
            }
            int idx = scrollTopIdx + (e.Y - 2) / Math.Max(18, ItemHeight);
            if (idx < 0 || idx >= flatRows.Count || (flatRows.Count > VisibleCapacity && e.X >= this.Width - 10)) idx = -1;
            else
            {
                string hTag = flatRows[idx].Node.Tag as string;
                if (hTag == "__LOADING__" || hTag == "__EMPTY_FOLDER__") idx = -1;
            }
            if (idx != hoveredIdx)
            {
                hoveredIdx = idx;
                this.Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoveredIdx != -1) { hoveredIdx = -1; this.Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            if (IsInlineEditing) CancelInlineEdit();

            if (backingTree.Nodes.Count == 0)
            {
                if (e.Button == MouseButtons.Left && emptyOpenFolderBtnRect.Contains(e.Location))
                {
                    if (OpenFolderRequested != null) OpenFolderRequested();
                    if (OnOpenFolderEmptyStateClicked != null) OnOpenFolderEmptyStateClicked();
                    else if (OnCloneRepoEmptyStateClicked != null) OnCloneRepoEmptyStateClicked();
                }
                return;
            }

            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0) RebuildTreeLayout(true);
            if (e.Button == MouseButtons.Left && flatRows.Count > VisibleCapacity && e.X >= this.Width - 10)
            {
                isDraggingTreeScroll = true;
                ScrollTreeFromMouseY(e.Y);
                return;
            }
            int idx = scrollTopIdx + (e.Y - 2) / Math.Max(18, ItemHeight);
            if (idx >= 0 && idx < flatRows.Count)
            {
                FlatRow row = flatRows[idx];
                string rowTag = row.Node.Tag as string;
                if (rowTag == "__LOADING__" || rowTag == "__EMPTY_FOLDER__")
                    return;
                SelectedNode = row.Node;
                if (e.Button == MouseButtons.Left && row.IsFolder)
                {
                    if (expandedSet.Contains(row.Node)) expandedSet.Remove(row.Node);
                    else
                    {
                        if (BeforeNodeExpand != null) BeforeNodeExpand(row.Node);
                        expandedSet.Add(row.Node);
                    }
                    RebuildFlatRows();
                }
                this.Invalidate();
                if (NodeMouseClick != null)
                {
                    NodeMouseClick(this, new TreeNodeMouseClickEventArgs(row.Node, e.Button, e.Clicks, e.X, e.Y));
                }
            }
            else if (e.Button == MouseButtons.Right && backingTree.Nodes.Count > 0)
            {
                SelectedNode = backingTree.Nodes[0];
                this.Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            isDraggingTreeScroll = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(this.BackColor);

            if (backingTree.Nodes.Count == 0)
            {
                // VS Code Empty Explorer State ("You have not yet opened a folder." + [Open Folder] button)
                int padX = 18;
                int topY = 20;
                TextRenderer.DrawText(g, "You have not yet opened a folder.", this.Font,
                    new Rectangle(padX, topY, Math.Max(80, this.Width - padX * 2), 36),
                    theme != null ? theme.FgSecondary : Color.FromArgb(160, 165, 175),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                emptyOpenFolderBtnRect = new Rectangle(padX, topY + 42, Math.Max(100, this.Width - padX * 2), 28);
                Color btnBg = theme != null ? theme.AccentPrimary : Color.FromArgb(0, 122, 204);
                using (SolidBrush bb = new SolidBrush(btnBg))
                    g.FillRectangle(bb, emptyOpenFolderBtnRect);
                using (Font bf = new Font("Segoe UI Semibold", 9.0f))
                {
                    TextRenderer.DrawText(g, "Open Folder", bf, emptyOpenFolderBtnRect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                return;
            }

            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0)
                RebuildTreeLayout(true);

            if (flatRows.Count == 0 && backingTree.Nodes.Count > 0 && !rootCollapsedByHeader)
            {
                using (Font lf = new Font(this.Font, FontStyle.Italic))
                {
                    Color emptyFg = theme != null ? theme.FgMuted : Color.FromArgb(148, 163, 184);
                    TextRenderer.DrawText(g, "No files in folder", lf, new Rectangle(14, 6, Math.Max(40, this.Width - 20), 22),
                        emptyFg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                return;
            }

            int ih = Math.Max(18, ItemHeight);
            int cap = VisibleCapacity;

            for (int i = 0; i < cap; i++)
            {
                int idx = scrollTopIdx + i;
                if (idx >= flatRows.Count) break;

                FlatRow row = flatRows[idx];
                Rectangle r = new Rectangle(0, 2 + i * ih, this.Width, ih);
                string nodePath = row.Node.Tag as string;
                bool isActiveEditor = (!row.IsFolder && !string.IsNullOrEmpty(nodePath) && !string.IsNullOrEmpty(ActiveEditorFilePath) &&
                    string.Equals(nodePath, ActiveEditorFilePath, StringComparison.OrdinalIgnoreCase));
                bool isSel = (row.Node == SelectedNode);
                bool isHov = (idx == hoveredIdx);

                if (isSel || isActiveEditor)
                {
                    double alpha = isSel ? 0.20 : 0.12;
                    Color selBg = theme != null ? StudioTheme.Blend(this.BackColor, theme.AccentPrimary, alpha) : Color.FromArgb(26, 46, 74);
                    Color accentBar = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248);
                    using (SolidBrush sb = new SolidBrush(selBg))
                        g.FillRectangle(sb, r);
                    if (isSel)
                    {
                        using (SolidBrush bar = new SolidBrush(accentBar))
                            g.FillRectangle(bar, 0, r.Y, 2, r.Height);
                    }
                }
                else if (isHov)
                {
                    Color hovBg = theme != null ? theme.BgHover : Color.FromArgb(22, 27, 38);
                    using (SolidBrush hb = new SolidBrush(hovBg))
                        g.FillRectangle(hb, r);
                }

                // Draw subtle VS Code tree indent guides
                Color guideCol = theme != null ? StudioTheme.Blend(this.BackColor, theme.FgMuted, 0.25) : Color.FromArgb(30, 148, 163, 184);
                using (Pen guidePen = new Pen(guideCol, 1f))
                {
                    for (int d = 1; d <= row.Depth; d++)
                    {
                        int gx = 10 + (d - 1) * 14 + 5;
                        g.DrawLine(guidePen, gx, r.Y, gx, r.Bottom);
                    }
                }

                int x = 8 + row.Depth * 14;
                string rawName = row.Node.Text ?? "";
                if (rawName.StartsWith("⊞ ") || rawName.StartsWith("▸ ") || rawName.StartsWith("≡ "))
                    rawName = rawName.Substring(2);

                if (row.IsFolder)
                {
                    string chev = row.IsExpanded ? "▾" : "▸";
                    TextRenderer.DrawText(g, chev, this.Font, new Rectangle(x, r.Y, 16, r.Height),
                        theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    x += 16;
                    bool isRootHeaderRow = (!HideRootFolderRow && row.Depth == 0);
                    using (Font ff = new Font(this.Font, isRootHeaderRow ? FontStyle.Bold : FontStyle.Regular))
                    {
                        Color folderFg = isRootHeaderRow
                            ? (theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184))
                            : (isSel ? (theme != null ? theme.FgPrimary : Color.White)
                                : (nodePath != null && DimmedPaths.Contains(nodePath) ? (theme != null ? theme.FgMuted : Color.FromArgb(120, 128, 140)) : this.ForeColor));
                        string folderDisplay = isRootHeaderRow ? rawName.ToUpperInvariant() : rawName;
                        TextRenderer.DrawText(g, folderDisplay, ff, new Rectangle(x, r.Y, Math.Max(10, r.Width - x - 6), r.Height),
                            folderFg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }
                else if (nodePath == "__LOADING__" || nodePath == "__EMPTY_FOLDER__")
                {
                    using (Font lf = new Font(this.Font, FontStyle.Italic))
                    {
                        Color loadFg = theme != null ? theme.FgMuted : Color.FromArgb(148, 163, 184);
                        string displayTxt = nodePath == "__LOADING__" ? ("Loading " + rawName) : rawName;
                        TextRenderer.DrawText(g, displayTxt, lf, new Rectangle(x, r.Y, Math.Max(10, r.Width - x - 6), r.Height),
                            loadFg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }
                else
                {
                    string ext = Path.GetExtension(rawName).ToLowerInvariant();
                    string badge = "≡";
                    Color badgeCol = theme != null ? theme.FgSecondary : Color.FromArgb(148, 163, 184);
                    if (ext == ".cs") { badge = "C#"; badgeCol = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248); }
                    else if (ext == ".py") { badge = "PY"; badgeCol = theme != null ? theme.TokenNumber : Color.FromArgb(250, 204, 21); }
                    else if (ext == ".js" || ext == ".mjs") { badge = "JS"; badgeCol = theme != null ? theme.TokenType : Color.FromArgb(245, 158, 11); }
                    else if (ext == ".ts" || ext == ".tsx") { badge = "TS"; badgeCol = theme != null ? theme.AccentPrimary : Color.FromArgb(96, 165, 250); }
                    else if (ext == ".rs") { badge = "RS"; badgeCol = theme != null ? theme.TokenControl : Color.FromArgb(251, 146, 60); }
                    else if (ext == ".ps1") { badge = "PS"; badgeCol = theme != null ? theme.TokenFunction : Color.FromArgb(34, 211, 238); }
                    else if (ext == ".sh") { badge = "SH"; badgeCol = theme != null ? theme.AccentSecondary : Color.FromArgb(74, 222, 128); }
                    else if (ext == ".json" || ext == ".manifest") { badge = "{}"; badgeCol = theme != null ? theme.TokenKeyword : Color.FromArgb(192, 132, 252); }
                    else if (ext == ".iss") { badge = "IS"; badgeCol = theme != null ? theme.TokenControl : Color.FromArgb(244, 114, 182); }
                    else if (ext == ".md") { badge = "MD"; badgeCol = theme != null ? theme.TokenKeyword : Color.FromArgb(167, 139, 250); }

                    using (Font bf = new Font("Consolas", 8.0f, FontStyle.Bold))
                    {
                        TextRenderer.DrawText(g, badge, bf, new Rectangle(x, r.Y, 22, r.Height),
                            badgeCol, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    }
                    x += 23;

                    // Check Git decoration status
                    string gitStatus = null;
                    if (!string.IsNullOrEmpty(nodePath) && GitStatusByFile.ContainsKey(nodePath))
                        gitStatus = GitStatusByFile[nodePath];

                    Color fileFg = isSel ? (theme != null ? theme.FgPrimary : Color.White)
                        : (nodePath != null && DimmedPaths.Contains(nodePath) ? (theme != null ? theme.FgMuted : Color.FromArgb(120, 128, 140)) : this.ForeColor);
                    Color gitBadgeFg = fileFg;
                    int rightReserve = 6;

                    if (!string.IsNullOrEmpty(gitStatus))
                    {
                        rightReserve = 24;
                        if (gitStatus == "M")
                        {
                            gitBadgeFg = theme != null ? theme.TokenNumber : Color.FromArgb(226, 192, 141);
                            fileFg = gitBadgeFg;
                        }
                        else if (gitStatus == "U" || gitStatus == "??" || gitStatus == "A")
                        {
                            gitStatus = (gitStatus == "??") ? "U" : gitStatus;
                            gitBadgeFg = theme != null ? theme.AccentSecondary : Color.FromArgb(115, 201, 145);
                            fileFg = gitBadgeFg;
                        }
                        else if (gitStatus == "D")
                        {
                            gitBadgeFg = theme != null ? theme.TokenControl : Color.FromArgb(241, 76, 76);
                            fileFg = gitBadgeFg;
                        }
                    }

                    TextRenderer.DrawText(g, rawName, this.Font, new Rectangle(x, r.Y, Math.Max(10, r.Width - x - rightReserve), r.Height),
                        fileFg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    if (!string.IsNullOrEmpty(gitStatus))
                    {
                        using (Font gf = new Font("Consolas", 8.0f, FontStyle.Bold))
                        {
                            TextRenderer.DrawText(g, gitStatus, gf, new Rectangle(r.Right - 22, r.Y, 16, r.Height),
                                gitBadgeFg, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
                        }
                    }
                }
            }

            if (flatRows.Count > cap && this.Height > 20)
            {
                int sbX = this.Width - 5;
                int thumbH = Math.Max(18, (int)((double)cap / flatRows.Count * (this.Height - 4)));
                int thumbY = 2 + (int)((double)scrollTopIdx / Math.Max(1, flatRows.Count - cap) * (this.Height - 4 - thumbH));
                Color thumbCol = theme != null ? StudioTheme.Blend(this.BackColor, theme.FgSecondary, 0.42) : Color.FromArgb(95, 148, 163, 184);
                using (SolidBrush sb = new SolidBrush(thumbCol))
                    g.FillRectangle(sb, sbX, thumbY, 4, thumbH);
            }
        }
    }

    /// <summary>
    /// Anti-aliased vector GDI+ iconography for VS Code-grade Activity Bar, Titlebar, and Chrome buttons.
    /// Zero external icon font or PNG dependency (0 MB overhead).
    /// </summary>
    public static class CodeLessVectorIcons
    {
        private static Bitmap cachedBoltBitmap;
        private static Bitmap cachedFullLogoBitmap;
        private static bool triedLoadingBrandBitmaps = false;

        private static void EnsureBrandBitmapsLoaded()
        {
            if (triedLoadingBrandBitmaps) return;
            triedLoadingBrandBitmaps = true;
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string asmLoc = typeof(CodeLessVectorIcons).Assembly.Location;
                string asmDir = !string.IsNullOrEmpty(asmLoc) ? Path.GetDirectoryName(asmLoc) : exeDir;
                string[] baseDirs = new string[]
                {
                    exeDir,
                    Path.Combine(exeDir, "assets"),
                    Path.Combine(exeDir, "codeLess", "assets"),
                    asmDir,
                    !string.IsNullOrEmpty(asmDir) ? Path.Combine(asmDir, "assets") : ""
                };
                foreach (string dir in baseDirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    string boltPath = Path.Combine(dir, "savatarr_bolt.png");
                    if (cachedBoltBitmap == null && File.Exists(boltPath))
                    {
                        using (FileStream fs = new FileStream(boltPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (Image img = Image.FromStream(fs))
                        {
                            cachedBoltBitmap = new Bitmap(img);
                        }
                    }
                    string fullPath = Path.Combine(dir, "savatarr_full_logo.png");
                    if (cachedFullLogoBitmap == null && File.Exists(fullPath))
                    {
                        using (FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (Image img = Image.FromStream(fs))
                        {
                            cachedFullLogoBitmap = new Bitmap(img);
                        }
                    }
                }
                if (cachedBoltBitmap == null)
                {
                    Icon assoc = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (assoc != null)
                        cachedBoltBitmap = assoc.ToBitmap();
                }
            }
            catch { }
        }

        public static void DrawBrandLogo(Graphics g, Rectangle rect, bool includeWordmark = false)
        {
            if (g == null || rect.Width <= 0 || rect.Height <= 0) return;
            EnsureBrandBitmapsLoaded();
            Bitmap bmp = includeWordmark && cachedFullLogoBitmap != null ? cachedFullLogoBitmap : cachedBoltBitmap;
            SmoothingMode oldSm = g.SmoothingMode;
            InterpolationMode oldIm = g.InterpolationMode;
            PixelOffsetMode oldPom = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (bmp != null)
            {
                g.DrawImage(bmp, rect);
            }
            else
            {
                float x = rect.X;
                float y = rect.Y;
                float w = rect.Width;
                float h = rect.Height;
                PointF[] leftUpper = new PointF[]
                {
                    new PointF(x + w * 0.62f, y + h * 0.05f),
                    new PointF(x + w * 0.14f, y + h * 0.50f),
                    new PointF(x + w * 0.42f, y + h * 0.50f),
                    new PointF(x + w * 0.30f, y + h * 0.78f),
                    new PointF(x + w * 0.54f, y + h * 0.44f),
                    new PointF(x + w * 0.38f, y + h * 0.44f)
                };
                PointF[] rightLower = new PointF[]
                {
                    new PointF(x + w * 0.70f, y + h * 0.22f),
                    new PointF(x + w * 0.46f, y + h * 0.56f),
                    new PointF(x + w * 0.62f, y + h * 0.56f),
                    new PointF(x + w * 0.38f, y + h * 0.95f),
                    new PointF(x + w * 0.86f, y + h * 0.50f),
                    new PointF(x + w * 0.58f, y + h * 0.50f)
                };
                using (SolidBrush darkBrush = new SolidBrush(Color.FromArgb(100, 108, 120)))
                using (SolidBrush lightBrush = new SolidBrush(Color.FromArgb(182, 190, 202)))
                using (Pen edgePen = new Pen(Color.FromArgb(52, 58, 70), 1f))
                {
                    g.FillPolygon(darkBrush, leftUpper);
                    g.DrawPolygon(edgePen, leftUpper);
                    g.FillPolygon(lightBrush, rightLower);
                    g.DrawPolygon(edgePen, rightLower);
                }
            }
            g.SmoothingMode = oldSm;
            g.InterpolationMode = oldIm;
            g.PixelOffsetMode = oldPom;
        }

        public static void DrawIcon(Graphics g, string kind, Rectangle rect, Color strokeColor, float strokeWidth = 1.6f)
        {
            if (g == null || string.IsNullOrEmpty(kind)) return;
            SmoothingMode oldSm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float minDim = Math.Min(rect.Width, rect.Height);
            float s = minDim >= 26f ? (minDim * 0.56f) : (minDim * 0.88f);
            float r = s * 0.5f;

            using (Pen p = new Pen(strokeColor, strokeWidth))
            using (SolidBrush fill = new SolidBrush(strokeColor))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                if (kind == "logo")
                {
                    int pad = Math.Max(1, (int)(minDim * 0.08f));
                    DrawBrandLogo(g, new Rectangle(rect.X + pad, rect.Y + pad, Math.Max(8, rect.Width - pad * 2), Math.Max(8, rect.Height - pad * 2)), false);
                }
                else if (kind == "explorer")
                {
                    g.DrawRectangle(p, cx - r * 0.85f, cy - r * 0.9f, r * 1.1f, r * 1.45f);
                    g.DrawRectangle(p, cx - r * 0.25f, cy - r * 0.45f, r * 1.1f, r * 1.4f);
                }
                else if (kind == "search")
                {
                    float cr = r * 0.65f;
                    g.DrawEllipse(p, cx - r * 0.82f, cy - r * 0.82f, cr * 2f, cr * 2f);
                    g.DrawLine(p, cx + r * 0.12f, cy + r * 0.12f, cx + r * 0.88f, cy + r * 0.88f);
                }
                else if (kind == "git")
                {
                    g.DrawLine(p, cx - r * 0.45f, cy - r * 0.65f, cx - r * 0.45f, cy + r * 0.65f);
                    g.DrawBezier(p, cx - r * 0.45f, cy + r * 0.25f, cx - r * 0.1f, cy + r * 0.15f, cx + r * 0.45f, cy + r * 0.05f, cx + r * 0.45f, cy - r * 0.45f);
                    g.FillEllipse(fill, cx - r * 0.67f, cy - r * 0.87f, r * 0.44f, r * 0.44f);
                    g.FillEllipse(fill, cx - r * 0.67f, cy + r * 0.43f, r * 0.44f, r * 0.44f);
                    g.FillEllipse(fill, cx + r * 0.23f, cy - r * 0.67f, r * 0.44f, r * 0.44f);
                }
                else if (kind == "extensions")
                {
                    float b = r * 0.72f;
                    g.DrawRectangle(p, cx - r * 0.9f, cy - r * 0.1f, b, b);
                    g.DrawRectangle(p, cx - r * 0.9f, cy - r * 0.92f, b, b);
                    g.DrawRectangle(p, cx - r * 0.08f, cy - r * 0.1f, b, b);
                    g.DrawRectangle(p, cx + r * 0.22f, cy - r * 0.92f, b * 0.95f, b * 0.95f);
                }
                else if (kind == "harness")
                {
                    PointF[] diamond = new PointF[]
                    {
                        new PointF(cx, cy - r * 0.95f),
                        new PointF(cx + r * 0.95f, cy),
                        new PointF(cx, cy + r * 0.95f),
                        new PointF(cx - r * 0.95f, cy)
                    };
                    g.DrawPolygon(p, diamond);
                    g.FillEllipse(fill, cx - r * 0.28f, cy - r * 0.28f, r * 0.56f, r * 0.56f);
                }
                else if (kind == "arena")
                {
                    g.DrawLine(p, cx - r * 0.8f, cy + r * 0.8f, cx + r * 0.8f, cy - r * 0.8f);
                    g.DrawLine(p, cx + r * 0.8f, cy + r * 0.8f, cx - r * 0.8f, cy - r * 0.8f);
                    g.DrawLine(p, cx - r * 0.85f, cy + r * 0.35f, cx - r * 0.35f, cy + r * 0.85f);
                    g.DrawLine(p, cx + r * 0.85f, cy + r * 0.35f, cx + r * 0.35f, cy + r * 0.85f);
                }
                else if (kind == "terminal")
                {
                    g.DrawRectangle(p, cx - r * 0.92f, cy - r * 0.78f, r * 1.84f, r * 1.56f);
                    g.DrawLine(p, cx - r * 0.52f, cy - r * 0.28f, cx - r * 0.12f, cy + r * 0.02f);
                    g.DrawLine(p, cx - r * 0.12f, cy + r * 0.02f, cx - r * 0.52f, cy + r * 0.32f);
                    g.DrawLine(p, cx + r * 0.05f, cy + r * 0.34f, cx + r * 0.55f, cy + r * 0.34f);
                }
                else if (kind == "home")
                {
                    PointF[] roof = new PointF[]
                    {
                        new PointF(cx - r * 0.95f, cy - r * 0.05f),
                        new PointF(cx, cy - r * 0.9f),
                        new PointF(cx + r * 0.95f, cy - r * 0.05f)
                    };
                    g.DrawLines(p, roof);
                    g.DrawLine(p, cx - r * 0.68f, cy - r * 0.15f, cx - r * 0.68f, cy + r * 0.85f);
                    g.DrawLine(p, cx + r * 0.68f, cy - r * 0.15f, cx + r * 0.68f, cy + r * 0.85f);
                    g.DrawLine(p, cx - r * 0.68f, cy + r * 0.85f, cx + r * 0.68f, cy + r * 0.85f);
                }
                else if (kind == "layout-left")
                {
                    g.DrawRectangle(p, cx - r * 0.9f, cy - r * 0.75f, r * 1.8f, r * 1.5f);
                    g.FillRectangle(fill, cx - r * 0.85f, cy - r * 0.7f, r * 0.62f, r * 1.4f);
                }
                else if (kind == "layout-bottom")
                {
                    g.DrawRectangle(p, cx - r * 0.9f, cy - r * 0.75f, r * 1.8f, r * 1.5f);
                    g.FillRectangle(fill, cx - r * 0.85f, cy + r * 0.15f, r * 1.7f, r * 0.58f);
                }
                else if (kind == "layout-right")
                {
                    g.DrawRectangle(p, cx - r * 0.9f, cy - r * 0.75f, r * 1.8f, r * 1.5f);
                    g.FillRectangle(fill, cx + r * 0.24f, cy - r * 0.7f, r * 0.62f, r * 1.4f);
                }
                else if (kind == "min")
                {
                    g.DrawLine(p, cx - 5, cy, cx + 5, cy);
                }
                else if (kind == "max")
                {
                    g.DrawRectangle(p, cx - 4.5f, cy - 4.5f, 9f, 9f);
                }
                else if (kind == "close")
                {
                    g.DrawLine(p, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                    g.DrawLine(p, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
                }
                else if (kind == "account")
                {
                    g.DrawEllipse(p, cx - r * 0.48f, cy - r * 0.85f, r * 0.96f, r * 0.96f);
                    g.DrawArc(p, cx - r * 0.88f, cy + r * 0.15f, r * 1.76f, r * 1.35f, 195f, 150f);
                }
                else if (kind == "gear")
                {
                    g.DrawEllipse(p, cx - r * 0.45f, cy - r * 0.45f, r * 0.9f, r * 0.9f);
                    for (int a = 0; a < 8; a++)
                    {
                        double rad = a * Math.PI / 4.0;
                        float x1 = cx + (float)(Math.Cos(rad) * r * 0.55);
                        float y1 = cy + (float)(Math.Sin(rad) * r * 0.55);
                        float x2 = cx + (float)(Math.Cos(rad) * r * 0.92);
                        float y2 = cy + (float)(Math.Sin(rad) * r * 0.92);
                        g.DrawLine(p, x1, y1, x2, y2);
                    }
                }
                else if (kind == "run" || kind == "debug")
                {
                    PointF[] tri = new PointF[]
                    {
                        new PointF(cx - r * 0.55f, cy - r * 0.78f),
                        new PointF(cx + r * 0.78f, cy),
                        new PointF(cx - r * 0.55f, cy + r * 0.78f)
                    };
                    g.DrawPolygon(p, tri);
                }
                else if (kind == "split")
                {
                    g.DrawRectangle(p, cx - r * 0.88f, cy - r * 0.75f, r * 1.76f, r * 1.5f);
                    g.DrawLine(p, cx, cy - r * 0.75f, cx, cy + r * 0.75f);
                }
                else if (kind == "more")
                {
                    float d = r * 0.28f;
                    g.FillEllipse(fill, cx - r * 0.75f - d / 2, cy - d / 2, d, d);
                    g.FillEllipse(fill, cx - d / 2, cy - d / 2, d, d);
                    g.FillEllipse(fill, cx + r * 0.75f - d / 2, cy - d / 2, d, d);
                }
                else if (kind == "bell")
                {
                    g.DrawArc(p, cx - r * 0.65f, cy - r * 0.75f, r * 1.3f, r * 1.3f, 180f, 180f);
                    g.DrawLine(p, cx - r * 0.65f, cy - r * 0.1f, cx - r * 0.75f, cy + r * 0.5f);
                    g.DrawLine(p, cx + r * 0.65f, cy - r * 0.1f, cx + r * 0.75f, cy + r * 0.5f);
                    g.DrawLine(p, cx - r * 0.82f, cy + r * 0.5f, cx + r * 0.82f, cy + r * 0.5f);
                    g.FillEllipse(fill, cx - r * 0.2f, cy + r * 0.58f, r * 0.4f, r * 0.35f);
                }
                else if (kind == "plus")
                {
                    g.DrawLine(p, cx - r * 0.65f, cy, cx + r * 0.65f, cy);
                    g.DrawLine(p, cx, cy - r * 0.65f, cx, cy + r * 0.65f);
                }
                else if (kind == "plus-dropdown")
                {
                    float px = cx - r * 0.42f;
                    float pr = r * 0.52f;
                    g.DrawLine(p, px - pr, cy, px + pr, cy);
                    g.DrawLine(p, px, cy - pr, px, cy + pr);
                    float dx = cx + r * 0.52f;
                    float dr = r * 0.32f;
                    g.DrawLine(p, dx - dr, cy - dr * 0.5f, dx, cy + dr * 0.6f);
                    g.DrawLine(p, dx, cy + dr * 0.6f, dx + dr, cy - dr * 0.5f);
                }
                else if (kind == "chevron-up")
                {
                    g.DrawLine(p, cx - r * 0.65f, cy + r * 0.28f, cx, cy - r * 0.35f);
                    g.DrawLine(p, cx, cy - r * 0.35f, cx + r * 0.65f, cy + r * 0.28f);
                }
                else if (kind == "arrow-up")
                {
                    g.DrawLine(p, cx, cy + r * 0.68f, cx, cy - r * 0.62f);
                    g.DrawLine(p, cx - r * 0.48f, cy - r * 0.14f, cx, cy - r * 0.62f);
                    g.DrawLine(p, cx + r * 0.48f, cy - r * 0.14f, cx, cy - r * 0.62f);
                }
                else if (kind == "chevron-down")
                {
                    g.DrawLine(p, cx - r * 0.65f, cy - r * 0.28f, cx, cy + r * 0.35f);
                    g.DrawLine(p, cx, cy + r * 0.35f, cx + r * 0.65f, cy - r * 0.28f);
                }
                else if (kind == "wrap")
                {
                    g.DrawLine(p, cx - r * 0.75f, cy - r * 0.5f, cx + r * 0.75f, cy - r * 0.5f);
                    g.DrawLine(p, cx - r * 0.75f, cy, cx + r * 0.45f, cy);
                    g.DrawArc(p, cx + r * 0.15f, cy, r * 0.6f, r * 0.55f, 270f, 180f);
                    g.DrawLine(p, cx + r * 0.45f, cy + r * 0.55f, cx - r * 0.25f, cy + r * 0.55f);
                    g.DrawLine(p, cx - r * 0.25f, cy + r * 0.55f, cx - r * 0.02f, cy + r * 0.32f);
                    g.DrawLine(p, cx - r * 0.25f, cy + r * 0.55f, cx - r * 0.02f, cy + r * 0.78f);
                }
                else if (kind == "trash")
                {
                    g.DrawLine(p, cx - r * 0.78f, cy - r * 0.52f, cx + r * 0.78f, cy - r * 0.52f);
                    g.DrawLine(p, cx - r * 0.32f, cy - r * 0.82f, cx + r * 0.32f, cy - r * 0.82f);
                    g.DrawLine(p, cx - r * 0.32f, cy - r * 0.82f, cx - r * 0.32f, cy - r * 0.52f);
                    g.DrawLine(p, cx + r * 0.32f, cy - r * 0.82f, cx + r * 0.32f, cy - r * 0.52f);
                    g.DrawRectangle(p, cx - r * 0.56f, cy - r * 0.52f, r * 1.12f, r * 1.34f);
                    g.DrawLine(p, cx - r * 0.18f, cy - r * 0.20f, cx - r * 0.18f, cy + r * 0.50f);
                    g.DrawLine(p, cx + r * 0.18f, cy - r * 0.20f, cx + r * 0.18f, cy + r * 0.50f);
                }
                else if (kind == "history")
                {
                    g.DrawEllipse(p, cx - r * 0.8f, cy - r * 0.8f, r * 1.6f, r * 1.6f);
                    g.DrawLine(p, cx, cy - r * 0.45f, cx, cy);
                    g.DrawLine(p, cx, cy, cx + r * 0.38f, cy + r * 0.25f);
                }
                else if (kind == "key")
                {
                    g.DrawEllipse(p, cx - r * 0.85f, cy - r * 0.45f, r * 0.9f, r * 0.9f);
                    g.DrawLine(p, cx + r * 0.05f, cy, cx + r * 0.85f, cy);
                    g.DrawLine(p, cx + r * 0.55f, cy, cx + r * 0.55f, cy + r * 0.35f);
                    g.DrawLine(p, cx + r * 0.82f, cy, cx + r * 0.82f, cy + r * 0.35f);
                }
                else if (kind == "stop")
                {
                    g.FillRectangle(fill, cx - r * 0.55f, cy - r * 0.55f, r * 1.1f, r * 1.1f);
                }
                else if (kind == "new-file")
                {
                    // VS Code codicon new-file: crisp document outline with top-right dog-ear fold
                    // and bottom-right open quadrant containing a clean '+' symbol
                    float x0 = cx - r * 0.82f;
                    float y0 = cy - r * 0.88f;
                    float w0 = r * 1.24f;
                    float h0 = r * 1.72f;
                    float fold = r * 0.44f;
                    g.DrawLine(p, x0, y0, x0 + w0 - fold, y0);
                    g.DrawLine(p, x0 + w0 - fold, y0, x0 + w0, y0 + fold);
                    g.DrawLine(p, x0 + w0 - fold, y0, x0 + w0 - fold, y0 + fold);
                    g.DrawLine(p, x0 + w0 - fold, y0 + fold, x0 + w0, y0 + fold);
                    g.DrawLine(p, x0 + w0, y0 + fold, x0 + w0, cy + r * 0.02f);
                    g.DrawLine(p, x0, y0, x0, y0 + h0);
                    g.DrawLine(p, x0, y0 + h0, cx + r * 0.02f, y0 + h0);
                    float px = cx + r * 0.54f;
                    float py = cy + r * 0.52f;
                    float pr = r * 0.38f;
                    g.DrawLine(p, px - pr, py, px + pr, py);
                    g.DrawLine(p, px, py - pr, px, py + pr);
                }
                else if (kind == "new-folder")
                {
                    // VS Code codicon new-folder: crisp folder tab + body outline
                    // with bottom-right open quadrant containing a clean '+' symbol
                    float x0 = cx - r * 0.90f;
                    float y0 = cy - r * 0.72f;
                    float w0 = r * 1.66f;
                    float h0 = r * 1.42f;
                    float tabH = h0 * 0.24f;
                    g.DrawLine(p, x0, y0, x0 + w0 * 0.38f, y0);
                    g.DrawLine(p, x0 + w0 * 0.38f, y0, x0 + w0 * 0.52f, y0 + tabH);
                    g.DrawLine(p, x0 + w0 * 0.52f, y0 + tabH, x0 + w0, y0 + tabH);
                    g.DrawLine(p, x0 + w0, y0 + tabH, x0 + w0, cy + r * 0.06f);
                    g.DrawLine(p, x0, y0, x0, y0 + h0);
                    g.DrawLine(p, x0, y0 + h0, cx + r * 0.08f, y0 + h0);
                    float px = cx + r * 0.56f;
                    float py = cy + r * 0.54f;
                    float pr = r * 0.38f;
                    g.DrawLine(p, px - pr, py, px + pr, py);
                    g.DrawLine(p, px, py - pr, px, py + pr);
                }
                else if (kind == "refresh")
                {
                    // VS Code codicon refresh: circular arc with crisp clockwise open-chevron arrowhead at top
                    float ar = r * 0.72f;
                    g.DrawArc(p, cx - ar, cy - ar + r * 0.08f, ar * 2f, ar * 2f, -15f, 290f);
                    float tipX = cx + r * 0.22f;
                    float tipY = cy - ar + r * 0.08f;
                    float wing = r * 0.36f;
                    g.DrawLine(p, tipX - wing, tipY - wing, tipX, tipY);
                    g.DrawLine(p, tipX - wing, tipY + wing, tipX, tipY);
                }
                else if (kind == "collapse-all")
                {
                    // VS Code codicon collapse-all: two stacked squares with a crisp '−' inside the front square
                    float backX = cx - r * 0.32f;
                    float backY = cy - r * 0.86f;
                    float boxSz = r * 1.18f;
                    g.DrawLine(p, backX, backY, backX + boxSz, backY);
                    g.DrawLine(p, backX + boxSz, backY, backX + boxSz, backY + boxSz);
                    float frontX = cx - r * 0.86f;
                    float frontY = cy - r * 0.32f;
                    g.DrawRectangle(p, frontX, frontY, boxSz, boxSz);
                    float midY = frontY + boxSz * 0.5f;
                    g.DrawLine(p, frontX + boxSz * 0.24f, midY, frontX + boxSz * 0.76f, midY);
                }
                else if (kind == "agent")
                {
                    PointF[] star = new PointF[]
                    {
                        new PointF(cx - r * 0.12f, cy - r * 0.88f),
                        new PointF(cx + r * 0.10f, cy - r * 0.22f),
                        new PointF(cx + r * 0.76f, cy),
                        new PointF(cx + r * 0.10f, cy + r * 0.22f),
                        new PointF(cx - r * 0.12f, cy + r * 0.88f),
                        new PointF(cx - r * 0.34f, cy + r * 0.22f),
                        new PointF(cx - r * 1.00f, cy),
                        new PointF(cx - r * 0.34f, cy - r * 0.22f)
                    };
                    g.DrawPolygon(p, star);
                    float sx = cx + r * 0.62f;
                    float sy = cy - r * 0.62f;
                    float sr = r * 0.28f;
                    g.DrawLine(p, sx, sy - sr, sx, sy + sr);
                    g.DrawLine(p, sx - sr, sy, sx + sr, sy);
                }
                else if (kind == "error-circle")
                {
                    float er = r * 0.78f;
                    g.DrawEllipse(p, cx - er, cy - er, er * 2f, er * 2f);
                    float xr = er * 0.44f;
                    g.DrawLine(p, cx - xr, cy - xr, cx + xr, cy + xr);
                    g.DrawLine(p, cx + xr, cy - xr, cx - xr, cy + xr);
                }
                else if (kind == "warning-triangle")
                {
                    float tr = r * 0.82f;
                    PointF[] tri = new PointF[]
                    {
                        new PointF(cx, cy - tr * 0.92f),
                        new PointF(cx + tr, cy + tr * 0.82f),
                        new PointF(cx - tr, cy + tr * 0.82f)
                    };
                    g.DrawPolygon(p, tri);
                    g.DrawLine(p, cx, cy - tr * 0.22f, cx, cy + tr * 0.30f);
                    g.FillEllipse(fill, cx - 1.1f, cy + tr * 0.46f, 2.2f, 2.2f);
                }
                else if (kind == "pin")
                {
                    g.DrawLine(p, cx - r * 0.45f, cy - r * 0.75f, cx + r * 0.45f, cy - r * 0.75f);
                    g.DrawLine(p, cx - r * 0.30f, cy - r * 0.75f, cx - r * 0.45f, cy + r * 0.05f);
                    g.DrawLine(p, cx + r * 0.30f, cy - r * 0.75f, cx + r * 0.45f, cy + r * 0.05f);
                    g.DrawLine(p, cx - r * 0.58f, cy + r * 0.05f, cx + r * 0.58f, cy + r * 0.05f);
                    g.DrawLine(p, cx, cy + r * 0.05f, cx, cy + r * 0.82f);
                }
            }
            g.SmoothingMode = oldSm;
        }
    }

    public class CodeLessIndeterminateBar : Control
    {
        private System.Windows.Forms.Timer animTimer;
        private int offsetPct = 0;
        public Color BarColor = Color.FromArgb(56, 189, 248);

        public bool IsRunning
        {
            get { return animTimer != null && animTimer.Enabled; }
        }

        public CodeLessIndeterminateBar()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Height = 2;
            this.Visible = false;
            animTimer = new System.Windows.Forms.Timer { Interval = 30 };
            animTimer.Tick += (s, e) =>
            {
                offsetPct = (offsetPct + 4) % 140;
                if (this.Visible) this.Invalidate();
            };
        }

        public void StartProgress()
        {
            offsetPct = 0;
            this.Visible = true;
            animTimer.Start();
            this.Invalidate();
        }

        public void StopProgress()
        {
            animTimer.Stop();
            this.Visible = false;
            this.Invalidate();
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            this.BackColor = theme.BgSidebar;
            this.BarColor = theme.AccentPrimary;
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(this.BackColor);
            if (!this.Visible) return;
            int w = Math.Max(24, this.Width / 3);
            int x = (int)((offsetPct - 30) / 100.0 * this.Width);
            using (SolidBrush b = new SolidBrush(BarColor))
                e.Graphics.FillRectangle(b, x, 0, w, this.Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && animTimer != null)
            {
                animTimer.Stop();
                animTimer.Dispose();
                animTimer = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Frameless dark VS Code window base supporting native edge resize (WM_NCHITTEST),
    /// crisp 1px outer frame border, drag-to-move dark titlebars, and 100% DrawToBitmap compatibility.
    /// </summary>
    public class CodeLessChromelessForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        public Color OuterFrameColor = Color.FromArgb(40, 48, 66);
        public bool EnableEdgeResize = true;
        private Point dragStartOffset;
        private bool isDraggingWindow = false;

        public CodeLessChromelessForm()
        {
            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Padding = new Padding(1);
            this.DoubleBuffered = true;
            this.StartPosition = FormStartPosition.CenterParent;
        }

        public void AttachWindowDragHandle(Control dragTarget, bool allowDoubleClickMaximize)
        {
            if (dragTarget == null) return;
            dragTarget.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (allowDoubleClickMaximize && e.Clicks == 2)
                    {
                        this.WindowState = (this.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                        return;
                    }
                    isDraggingWindow = true;
                    dragStartOffset = e.Location;
                }
            };
            dragTarget.MouseMove += (s, e) =>
            {
                if (isDraggingWindow && (e.Button & MouseButtons.Left) == MouseButtons.Left)
                {
                    if (this.WindowState == FormWindowState.Maximized)
                    {
                        this.WindowState = FormWindowState.Normal;
                        dragStartOffset = new Point(this.Width / 2, 16);
                    }
                    Point screenPt = dragTarget.PointToScreen(e.Location);
                    this.Location = new Point(screenPt.X - dragStartOffset.X, screenPt.Y - dragStartOffset.Y);
                }
            };
            dragTarget.MouseUp += (s, e) => { isDraggingWindow = false; };
        }

        public Panel BuildDarkDialogTitleBar(string title, StudioTheme theme, bool showMinimize = false, bool showMaximize = false)
        {
            this.OuterFrameColor = theme.BorderSubtle;
            Panel bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = theme.BgActivity,
                Padding = new Padding(8, 0, 0, 0)
            };
            bar.Paint += (s, e) =>
            {
                CodeLessVectorIcons.DrawIcon(e.Graphics, "logo", new Rectangle(10, 8, 18, 18), theme.AccentPrimary, 1.5f);
                using (Font tf = new Font("Segoe UI Semibold", 9.0f))
                {
                    TextRenderer.DrawText(e.Graphics, title ?? this.Text, tf,
                        new Rectangle(34, 0, Math.Max(40, bar.Width - 150), bar.Height),
                        theme.FgPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                using (Pen bp = new Pen(theme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(bp, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };
            AttachWindowDragHandle(bar, showMaximize);

            int btnW = 38;

            if (showMinimize)
            {
                Panel minBtn = CreateCaptionButton("min", theme, false, () =>
                {
                    this.WindowState = FormWindowState.Minimized;
                });
                minBtn.Size = new Size(btnW, 33);
                minBtn.Dock = DockStyle.Right;
                bar.Controls.Add(minBtn);
            }

            if (showMaximize)
            {
                Panel maxBtn = CreateCaptionButton("max", theme, false, () =>
                {
                    this.WindowState = (this.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                });
                maxBtn.Size = new Size(btnW, 33);
                maxBtn.Dock = DockStyle.Right;
                bar.Controls.Add(maxBtn);
            }

            Panel closeBtn = CreateCaptionButton("close", theme, true, () =>
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            });
            closeBtn.Size = new Size(btnW, 33);
            closeBtn.Dock = DockStyle.Right;
            bar.Controls.Add(closeBtn);

            return bar;
        }

        public static Panel CreateCaptionButton(string iconKind, StudioTheme theme, bool isClose, Action onClick)
        {
            Panel p = new Panel
            {
                Size = new Size(42, 35),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            bool hovered = false;
            p.MouseEnter += (s, e) => { hovered = true; p.Invalidate(); };
            p.MouseLeave += (s, e) => { hovered = false; p.Invalidate(); };
            p.Click += (s, e) => { if (onClick != null) onClick(); };
            p.Paint += (s, e) =>
            {
                if (hovered)
                {
                    Color bg = isClose ? Color.FromArgb(225, 29, 72) : theme.BgHover;
                    using (SolidBrush sb = new SolidBrush(bg))
                        e.Graphics.FillRectangle(sb, 0, 0, p.Width, p.Height);
                }
                Color fg = (hovered && isClose) ? Color.White : theme.FgSecondary;
                CodeLessVectorIcons.DrawIcon(e.Graphics, iconKind, new Rectangle((p.Width - 18) / 2, (p.Height - 18) / 2, 18, 18), fg, 1.4f);
            };
            return p;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && this.GetType().Name != "CodeLessMainForm")
            {
                if (this.CancelButton != null)
                {
                    this.CancelButton.PerformClick();
                }
                if (!this.IsDisposed)
                {
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (this.WindowState != FormWindowState.Maximized)
            {
                using (Pen p = new Pen(OuterFrameColor, 1f))
                    e.Graphics.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && EnableEdgeResize && this.WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                int screenX = unchecked((short)(lp & 0xFFFF));
                int screenY = unchecked((short)((lp >> 16) & 0xFFFF));
                Point pt = this.PointToClient(new Point(screenX, screenY));
                int grip = 7;
                bool left = pt.X <= grip;
                bool right = pt.X >= this.ClientSize.Width - grip;
                bool top = pt.Y <= grip;
                bool bottom = pt.Y >= this.ClientSize.Height - grip;

                if (top && left) m.Result = (IntPtr)HTTOPLEFT;
                else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
                else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
                else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)HTLEFT;
                else if (right) m.Result = (IntPtr)HTRIGHT;
                else if (top) m.Result = (IntPtr)HTTOP;
                else if (bottom) m.Result = (IntPtr)HTBOTTOM;
            }
        }
    }

    /// <summary>
    /// Borderless dark input box wrapped in a custom 1px focus-reactive dark frame with placeholder cue text.
    /// Completely eliminates Win32 gray/white FixedSingle borders across the entire IDE.
    /// </summary>
    public class CodeLessDarkInputBox : Panel
    {
        public readonly TextBox InnerBox;
        private string placeholder = "";
        private bool isFocused = false;
        private Color borderNormal = Color.FromArgb(46, 54, 72);
        private Color borderFocused = Color.FromArgb(56, 189, 248);

        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value ?? ""; this.Invalidate(); }
        }

        public Color BorderColor
        {
            get { return borderNormal; }
            set { borderNormal = value; this.Invalidate(); }
        }

        public Color FocusBorderColor
        {
            get { return borderFocused; }
            set { borderFocused = value; this.Invalidate(); }
        }

        public bool Multiline
        {
            get { return InnerBox.Multiline; }
            set
            {
                InnerBox.Multiline = value;
                RepositionInner();
            }
        }

        public char PasswordChar
        {
            get { return InnerBox.PasswordChar; }
            set { InnerBox.PasswordChar = value; }
        }

        public bool UseSystemPasswordChar
        {
            get { return InnerBox.UseSystemPasswordChar; }
            set { InnerBox.UseSystemPasswordChar = value; }
        }

        public override string Text
        {
            get { return InnerBox.Text; }
            set { InnerBox.Text = value ?? ""; SyncInnerVisibility(); this.Invalidate(); }
        }

        public int SelectionStart
        {
            get { return InnerBox.SelectionStart; }
            set { InnerBox.SelectionStart = value; }
        }

        public new event EventHandler TextChanged;
        public new event KeyEventHandler KeyDown;
        public new event KeyPressEventHandler KeyPress;

        private class InnerTextCueWatcher : NativeWindow
        {
            private readonly CodeLessDarkInputBox owner;
            public InnerTextCueWatcher(CodeLessDarkInputBox owner) { this.owner = owner; }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if ((m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318) && owner != null && !owner.IsDisposed && owner.InnerBox != null)
                {
                    if (string.IsNullOrEmpty(owner.InnerBox.Text) && !string.IsNullOrEmpty(owner.placeholder))
                    {
                        Graphics g = (m.Msg == 0x000F || m.WParam == IntPtr.Zero)
                            ? Graphics.FromHwnd(owner.InnerBox.Handle)
                            : Graphics.FromHdc(m.WParam);
                        using (g)
                        {
                            Rectangle r = new Rectangle(1, 0, Math.Max(8, owner.InnerBox.ClientSize.Width - 2), owner.InnerBox.ClientSize.Height);
                            TextFormatFlags tf = owner.InnerBox.Multiline
                                ? (TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix)
                                : (TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                            TextRenderer.DrawText(g, owner.placeholder, owner.InnerBox.Font, r, owner.placeholderColor, tf);
                        }
                    }
                }
            }
        }

        private readonly InnerTextCueWatcher cueWatcher;
        private Color placeholderColor = Color.FromArgb(115, 130, 152);

        public CodeLessDarkInputBox()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            this.TabStop = true;
            this.Cursor = Cursors.IBeam;
            this.BackColor = Color.FromArgb(14, 17, 23);
            this.ForeColor = Color.FromArgb(228, 232, 240);
            this.Padding = new Padding(9, 5, 9, 5);

            InnerBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = this.BackColor,
                ForeColor = this.ForeColor,
                Font = new Font("Segoe UI", 9.3f),
                Dock = DockStyle.Fill,
                Visible = false
            };

            cueWatcher = new InnerTextCueWatcher(this);
            InnerBox.HandleCreated += (s, e) => cueWatcher.AssignHandle(InnerBox.Handle);
            InnerBox.HandleDestroyed += (s, e) => cueWatcher.ReleaseHandle();
            if (InnerBox.IsHandleCreated) cueWatcher.AssignHandle(InnerBox.Handle);

            InnerBox.GotFocus += (s, e) => { isFocused = true; SyncInnerVisibility(); this.Invalidate(); InnerBox.Invalidate(); };
            InnerBox.LostFocus += (s, e) => { isFocused = false; SyncInnerVisibility(); this.Invalidate(); InnerBox.Invalidate(); };
            InnerBox.TextChanged += (s, e) =>
            {
                SyncInnerVisibility();
                this.Invalidate();
                InnerBox.Invalidate();
                if (TextChanged != null) TextChanged(this, e);
            };
            InnerBox.KeyDown += (s, e) => { if (KeyDown != null) KeyDown(this, e); };
            InnerBox.KeyPress += (s, e) => { if (KeyPress != null) KeyPress(this, e); };

            this.Controls.Add(InnerBox);
            RepositionInner();
            SyncInnerVisibility();
            this.MouseDown += (s, e) => FocusInner();
            this.Click += (s, e) => FocusInner();
            this.Enter += (s, e) => FocusInner();
        }

        private void FocusInner()
        {
            InnerBox.Visible = true;
            InnerBox.Focus();
            isFocused = InnerBox.Focused;
            SyncInnerVisibility();
            this.Invalidate();
        }

        private void SyncInnerVisibility()
        {
            if (InnerBox == null) return;
            bool shouldShow = (isFocused && InnerBox.Focused) || !string.IsNullOrEmpty(InnerBox.Text);
            if (InnerBox.Visible != shouldShow)
                InnerBox.Visible = shouldShow;
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            this.BackColor = theme.BgEditor;
            this.ForeColor = theme.FgPrimary;
            this.borderNormal = theme.BorderSubtle;
            this.borderFocused = theme.AccentPrimary;
            this.placeholderColor = theme.FgMuted;
            InnerBox.BackColor = theme.BgEditor;
            InnerBox.ForeColor = theme.FgPrimary;
            this.Invalidate();
            InnerBox.Invalidate();
        }

        public void Clear() { InnerBox.Clear(); SyncInnerVisibility(); this.Invalidate(); }
        public new bool Focus()
        {
            InnerBox.Visible = true;
            bool ok = InnerBox.Focus();
            isFocused = InnerBox.Focused;
            SyncInnerVisibility();
            this.Invalidate();
            return ok;
        }
        public void SelectAll() { InnerBox.SelectAll(); }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (InnerBox != null) InnerBox.Font = this.Font;
            RepositionInner();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (InnerBox != null) InnerBox.BackColor = this.BackColor;
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            if (InnerBox != null) InnerBox.ForeColor = this.ForeColor;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RepositionInner();
        }

        private void RepositionInner()
        {
            if (InnerBox == null) return;
            if (!InnerBox.Multiline)
            {
                int vPad = Math.Max(3, (this.Height - InnerBox.PreferredHeight) / 2);
                this.Padding = new Padding(9, vPad, 9, vPad);
            }
            else
            {
                this.Padding = new Padding(9, 7, 9, 7);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(this.BackColor))
                g.FillRectangle(bg, 0, 0, this.Width, this.Height);

            using (Pen borderP = new Pen(isFocused ? borderFocused : borderNormal, 1f))
                g.DrawRectangle(borderP, 0, 0, this.Width - 1, this.Height - 1);

            if (string.IsNullOrEmpty(InnerBox.Text) && !string.IsNullOrEmpty(placeholder) && !InnerBox.Visible)
            {
                Rectangle tr = new Rectangle(9, 0, Math.Max(10, this.Width - 18), this.Height);
                TextFormatFlags flags = InnerBox.Multiline
                    ? (TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix)
                    : (TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (InnerBox.Multiline) tr = new Rectangle(9, 7, Math.Max(10, this.Width - 18), Math.Max(16, this.Height - 14));
                TextRenderer.DrawText(g, placeholder, InnerBox.Font, tr, placeholderColor, flags);
            }
        }
    }

    /// <summary>
    /// Custom-painted rich conversation feed control for codeLess.
    /// Renders styled User/Assistant cards, collapsible Thinking drawers, Tool Call chips,
    /// Markdown headings/lists/bold, and interactive fenced Code Blocks with [Copy] & [Apply].
    /// </summary>
    public class CodeLessAgentFeedControl : Control
    {
        public class AgentToolChip
        {
            public string Name;
            public string Summary;
            public string Output;
            public bool Success;
            public bool IsExpanded;
        }

        public class AgentContentSegment
        {
            public bool IsCodeBlock;
            public string Language;
            public string Text;
            public Rectangle CopyBtnBounds;
            public Rectangle InsertBtnBounds;
            public Rectangle ApplyBtnBounds;
        }

        public class AgentMessageCard
        {
            public bool IsUser;
            public bool IsErrorCard;
            public bool IsStreaming;
            public string PendingCommandToApprove;
            public bool CommandApprovalResolved;
            public string CheckpointId;
            public string SenderLabel;
            public string MetaBadge;
            public string RawText;
            public string ThinkingText;
            public bool ThinkingExpanded;
            public List<AgentToolChip> Tools = new List<AgentToolChip>();
            public List<AgentContentSegment> Segments = new List<AgentContentSegment>();
            public Rectangle ThinkingToggleBounds;
            public int MeasuredHeight;
        }

        private class InteractiveButtonHit
        {
            public Rectangle Bounds;
            public string ActionType; // "thinking", "tool_toggle", "copy", "insert", "apply", "copy_card", "restore_cp", "open_ai_settings", "run_cmd", "skip_cmd"
            public AgentMessageCard Card;
            public AgentContentSegment Segment;
            public AgentToolChip ToolChip;
        }

        private readonly List<AgentMessageCard> cards = new List<AgentMessageCard>();
        private readonly List<InteractiveButtonHit> hitRegions = new List<InteractiveButtonHit>();
        private StudioTheme theme = StudioTheme.GetBuiltInThemes()[0];
        private int scrollY = 0;
        private int totalContentHeight = 0;
        private int lastLayoutWidth = -1;
        private string copiedFlashSegmentText = null;
        private AgentMessageCard copiedFlashCard = null;
        private AgentMessageCard lastContextCard = null;
        private Point lastMousePos = new Point(-1, -1);
        private System.Windows.Forms.Timer shimmerTimer;
        private int shimmerPhase = 0;

        public event Action<string> ApplyCodeRequested;
        public event Action<string> InsertCodeAtCursorRequested;
        public event Action<string> RestoreCheckpointRequested;
        public event Action OpenAiSettingsRequested;
        public event Action<string> ApproveTerminalCommandRequested;
        public event Action<string> SkipTerminalCommandRequested;

        public CodeLessAgentFeedControl()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable |
                ControlStyles.StandardClick |
                ControlStyles.StandardDoubleClick,
                true
            );
            this.Font = new Font("Segoe UI", 9.0f);
            this.BackColor = Color.FromArgb(14, 16, 23);
            this.ForeColor = Color.FromArgb(228, 232, 240);

            shimmerTimer = new System.Windows.Forms.Timer { Interval = 90 };
            shimmerTimer.Tick += (s, e) =>
            {
                bool anyStreaming = false;
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i].IsStreaming) { anyStreaming = true; break; }
                }
                if (anyStreaming)
                {
                    shimmerPhase = (shimmerPhase + 1) % 24;
                    if (this.Visible) this.Invalidate();
                }
                else
                {
                    shimmerTimer.Stop(); // nothing streaming: stop waking 11x a second (restarted by BeginStreamingMessage)
                }
            };

            ContextMenuStrip ctx = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(20, 23, 31),
                ForeColor = Color.FromArgb(228, 232, 240),
                ShowImageMargin = false,
                Renderer = new DarkStudioMenuRenderer(() => this.theme)
            };
            ctx.Items.Add("Copy Message Text", null, (s, e) =>
            {
                AgentMessageCard target = lastContextCard ?? (cards.Count > 0 ? cards[cards.Count - 1] : null);
                if (target != null && !string.IsNullOrEmpty(target.RawText))
                {
                    try { Clipboard.SetText(target.RawText); copiedFlashCard = target; this.Invalidate(); } catch { }
                }
            });
            ctx.Items.Add("Copy Entire Conversation", null, (s, e) =>
            {
                if (cards.Count == 0) return;
                StringBuilder sb = new StringBuilder();
                foreach (var c in cards)
                {
                    sb.AppendLine("[" + c.SenderLabel + "] " + c.MetaBadge);
                    sb.AppendLine(c.RawText);
                    sb.AppendLine();
                }
                try { Clipboard.SetText(sb.ToString()); } catch { }
            });
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Clear Agent Feed", null, (s, e) => Clear());
            this.ContextMenuStrip = ctx;
        }

        public void ApplyTheme(StudioTheme newTheme)
        {
            if (newTheme == null) return;
            theme = newTheme;
            this.BackColor = theme.BgSidebar;
            this.ForeColor = theme.FgPrimary;
            if (this.ContextMenuStrip != null)
            {
                this.ContextMenuStrip.BackColor = theme.BgElevated;
                this.ContextMenuStrip.ForeColor = theme.FgPrimary;
            }
            lastLayoutWidth = -1;
            this.Invalidate();
        }

        public void Clear()
        {
            cards.Clear();
            hitRegions.Clear();
            scrollY = 0;
            totalContentHeight = 0;
            this.Invalidate();
        }

        public int MessageCount
        {
            get { return cards.Count; }
        }

        public List<AgentMessageCard> GetCardsSnapshot()
        {
            return new List<AgentMessageCard>(cards);
        }

        public void LoadCardsSnapshot(IEnumerable<AgentMessageCard> savedCards)
        {
            cards.Clear();
            if (savedCards != null)
            {
                foreach (var sc in savedCards)
                {
                    if (sc != null && sc.IsUser)
                    {
                        sc.SenderLabel = "You";
                        sc.MetaBadge = "";
                    }
                    if (sc != null) cards.Add(sc);
                }
            }
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
            shimmerTimer.Start(); // stops itself on the first tick if no restored card is streaming
        }

        public AgentMessageCard AddMessage(bool isUser, string senderLabel, string metaBadge, string markdownText, string thinkingText = null, List<AgentToolChip> tools = null, string checkpointId = null)
        {
            AgentMessageCard card = new AgentMessageCard
            {
                IsUser = isUser,
                SenderLabel = isUser ? "You" : (senderLabel ?? "Assistant"),
                MetaBadge = isUser ? "" : (metaBadge ?? ""),
                RawText = markdownText ?? "",
                ThinkingText = thinkingText ?? "",
                ThinkingExpanded = false,
                CheckpointId = checkpointId,
                Tools = tools != null ? new List<AgentToolChip>(tools) : new List<AgentToolChip>()
            };
            PopulateCardSegmentsAndThinking(card);
            cards.Add(card);
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
            return card;
        }

        public AgentMessageCard BeginStreamingMessage(string senderLabel, string metaBadge)
        {
            AgentMessageCard card = new AgentMessageCard
            {
                IsUser = false,
                IsStreaming = true,
                SenderLabel = senderLabel ?? "Assistant",
                MetaBadge = metaBadge ?? "streaming...",
                RawText = "Thinking...",
                ThinkingText = "",
                ThinkingExpanded = false
            };
            PopulateCardSegmentsAndThinking(card);
            cards.Add(card);
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
            shimmerTimer.Start();
            return card;
        }

        public void UpdateStreamingMessage(AgentMessageCard card, string markdownText, string thinkingText = null, List<AgentToolChip> tools = null, string metaBadge = null)
        {
            if (card == null) return;
            card.RawText = markdownText ?? "";
            if (thinkingText != null) card.ThinkingText = thinkingText;
            if (tools != null) card.Tools = new List<AgentToolChip>(tools);
            if (metaBadge != null) card.MetaBadge = metaBadge;
            PopulateCardSegmentsAndThinking(card);
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
        }

        private static void PopulateCardSegmentsAndThinking(AgentMessageCard card)
        {
            if (card == null) return;
            string cleaned = card.RawText ?? "";

            if (!card.IsUser)
            {
                Match tm = Regex.Match(cleaned, @"<thinking>([\s\S]*?)</thinking>", RegexOptions.IgnoreCase);
                if (tm.Success)
                {
                    if (string.IsNullOrEmpty(card.ThinkingText))
                        card.ThinkingText = tm.Groups[1].Value.Trim();
                    cleaned = Regex.Replace(cleaned, @"<thinking>[\s\S]*?</thinking>\s*", "", RegexOptions.IgnoreCase);
                }
                else
                {
                    Match openTm = Regex.Match(cleaned, @"<thinking>([\s\S]*)$", RegexOptions.IgnoreCase);
                    if (openTm.Success)
                    {
                        card.ThinkingText = openTm.Groups[1].Value.Trim();
                        cleaned = cleaned.Substring(0, openTm.Index).Trim();
                    }
                }

                // Hide raw SEARCH/REPLACE blocks and convert them into compact edit summaries
                MatchCollection srMatches = Regex.Matches(cleaned, @"<<<<<<<\s*SEARCH\s*\r?\n([\s\S]*?)\r?\n=======\s*\r?\n([\s\S]*?)\r?\n>>>>>>>\s*REPLACE", RegexOptions.IgnoreCase);
                if (srMatches.Count > 0)
                {
                    int added = 0;
                    int removed = 0;
                    foreach (Match m in srMatches)
                    {
                        removed += m.Groups[1].Value.Replace("\r\n", "\n").Split('\n').Length;
                        added += m.Groups[2].Value.Replace("\r\n", "\n").Split('\n').Length;
                    }
                    cleaned = Regex.Replace(cleaned, @"<<<<<<<\s*SEARCH[\s\S]*?>>>>>>>\s*REPLACE", "", RegexOptions.IgnoreCase).Trim();
                    if (card.Tools == null) card.Tools = new List<AgentToolChip>();
                    string targetFileLabel = "stream_target.cs";
                    for (int ti = card.Tools.Count - 1; ti >= 0; ti--)
                    {
                        string tn = card.Tools[ti].Name ?? "";
                        if (tn == "stage_diff_hunk" || tn.StartsWith("Edited ") || tn.StartsWith("Staged ") || tn.EndsWith(".cs") || tn.Contains("(+"))
                        {
                            if (tn.Contains(".") && !tn.StartsWith("stage_"))
                            {
                                int sp = tn.IndexOf(' ');
                                targetFileLabel = sp > 0 ? tn.Substring(0, sp) : tn;
                            }
                            card.Tools.RemoveAt(ti);
                        }
                    }
                    card.Tools.Add(new AgentToolChip
                    {
                        Name = string.Format("{0} +{1} −{2}", targetFileLabel, added, removed),
                        Summary = "View Diff · Accept · Reject",
                        Success = true
                    });
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        cleaned = string.Format("Applied inline edit (+{0} −{1} lines). Review changes in the editor diff view.", added, removed);
                    }
                }
                else if (cleaned.Contains("<<<<<<< SEARCH"))
                {
                    int idx = cleaned.IndexOf("<<<<<<< SEARCH", StringComparison.OrdinalIgnoreCase);
                    cleaned = cleaned.Substring(0, idx).Trim();
                    if (string.IsNullOrWhiteSpace(cleaned))
                        cleaned = "Streaming inline diff edit...";
                }

                if (card.Tools != null)
                {
                    foreach (var t in card.Tools)
                    {
                        if (t.Name == "stage_diff_hunk")
                        {
                            t.Name = "Edited file +1 −1";
                            t.Summary = "View Diff · Accept · Reject";
                        }
                    }
                }
            }

            card.Segments = ParseMarkdownSegments(cleaned);
        }

        public void EndStreamingMessage(AgentMessageCard card, string finalMarkdown, string thinkingText = null, List<AgentToolChip> tools = null, string metaBadge = null)
        {
            if (card == null) return;
            card.IsStreaming = false;
            UpdateStreamingMessage(card, finalMarkdown, thinkingText, tools, metaBadge);
        }

        public void AddErrorCard(string errorTitle, string errorDetail)
        {
            AgentMessageCard card = new AgentMessageCard
            {
                IsUser = false,
                IsErrorCard = true,
                SenderLabel = string.IsNullOrEmpty(errorTitle) ? "Provider Connection Error" : errorTitle,
                MetaBadge = "Action Required",
                RawText = errorDetail ?? "Invalid API key or unreachable model endpoint."
            };
            card.Segments = ParseMarkdownSegments(card.RawText);
            cards.Add(card);
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
        }

        public void AddCommandApprovalCard(string commandLine)
        {
            AgentMessageCard card = new AgentMessageCard
            {
                IsUser = false,
                PendingCommandToApprove = commandLine ?? "",
                SenderLabel = "Terminal Command Approval",
                MetaBadge = "ConPTY Shell",
                RawText = "Proposed terminal command:\n```powershell\n" + (commandLine ?? "") + "\n```"
            };
            card.Segments = ParseMarkdownSegments(card.RawText);
            cards.Add(card);
            lastLayoutWidth = -1;
            RecalculateLayout();
            scrollY = Math.Max(0, totalContentHeight - Math.Max(40, this.Height - 16));
            this.Invalidate();
        }

        private static List<AgentContentSegment> ParseMarkdownSegments(string raw)
        {
            var result = new List<AgentContentSegment>();
            if (string.IsNullOrEmpty(raw)) return result;

            string[] allLines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            StringBuilder proseBuf = new StringBuilder();
            StringBuilder codeBuf = null;
            string codeLang = "";

            foreach (string ln in allLines)
            {
                string tr = ln.Trim();
                if (tr.StartsWith("```"))
                {
                    if (codeBuf == null)
                    {
                        if (proseBuf.Length > 0)
                        {
                            result.Add(new AgentContentSegment { IsCodeBlock = false, Text = proseBuf.ToString().TrimEnd('\n') });
                            proseBuf.Length = 0;
                        }
                        codeLang = tr.Length > 3 ? tr.Substring(3).Trim() : "code";
                        codeBuf = new StringBuilder();
                    }
                    else
                    {
                        result.Add(new AgentContentSegment { IsCodeBlock = true, Language = string.IsNullOrEmpty(codeLang) ? "code" : codeLang, Text = codeBuf.ToString().TrimEnd('\n') });
                        codeBuf = null;
                        codeLang = "";
                    }
                }
                else
                {
                    if (codeBuf != null)
                        codeBuf.AppendLine(ln);
                    else
                        proseBuf.AppendLine(ln);
                }
            }

            if (codeBuf != null)
            {
                result.Add(new AgentContentSegment { IsCodeBlock = true, Language = codeLang, Text = codeBuf.ToString().TrimEnd('\n') });
            }
            if (proseBuf.Length > 0)
            {
                result.Add(new AgentContentSegment { IsCodeBlock = false, Text = proseBuf.ToString().TrimEnd('\n') });
            }
            return result;
        }

        private List<string> WrapProseLines(string text, Font f, int maxPx)
        {
            List<string> outLines = new List<string>();
            if (string.IsNullOrEmpty(text)) return outLines;
            string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
            foreach (string para in paragraphs)
            {
                if (para.Trim().Length == 0)
                {
                    outLines.Add("");
                    continue;
                }
                string clean = para.Replace("**", "").Replace("`", "");
                string indent = "";
                if (clean.TrimStart().StartsWith("• ") || clean.TrimStart().StartsWith("- "))
                    indent = "  ";

                string[] words = clean.Split(' ');
                StringBuilder cur = new StringBuilder();
                for (int w = 0; w < words.Length; w++)
                {
                    string word = words[w];
                    string candidate = cur.Length == 0 ? word : (cur.ToString() + " " + word);
                    int mw = TextRenderer.MeasureText(candidate, f, new Size(2000, 100), TextFormatFlags.NoPadding).Width;
                    if (mw > maxPx && cur.Length > 0)
                    {
                        outLines.Add(cur.ToString());
                        cur.Length = 0;
                        cur.Append(indent + word);
                    }
                    else
                    {
                        if (cur.Length > 0) cur.Append(" ");
                        cur.Append(word);
                    }
                }
                if (cur.Length > 0) outLines.Add(cur.ToString());
            }
            return outLines;
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (radius <= 0 || rect.Width <= d || rect.Height <= d)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void RecalculateLayout()
        {
            int w = Math.Max(180, this.Width);
            lastLayoutWidth = w;
            int contentW = Math.Max(120, w - 34);
            int total = 10;

            using (Font fProse = new Font("Segoe UI", 8.9f))
            using (Font fThink = new Font("Segoe UI", 8.2f))
            using (Font fCode = new Font("Consolas", 8.5f))
            {
                foreach (AgentMessageCard card in cards.ToArray())
                {
                    if (card == null) continue;
                    int ch = 34; // header height + padding
                    if (!string.IsNullOrEmpty(card.ThinkingText))
                    {
                        ch += 26;
                        if (card.ThinkingExpanded)
                        {
                            List<string> tLines = WrapProseLines(card.ThinkingText, fThink, contentW - 18);
                            ch += tLines.Count * 16 + 11;
                        }
                    }

                    if (card.Tools != null && card.Tools.Count > 0)
                    {
                        foreach (AgentToolChip tc in card.Tools.ToArray())
                        {
                            if (tc == null) continue;
                            ch += 24;
                            if (tc.IsExpanded && !string.IsNullOrEmpty(tc.Output))
                            {
                                string[] outLines = tc.Output.Replace("\r\n", "\n").Split('\n');
                                int showOut = Math.Min(12, outLines.Length);
                                ch += showOut * 15 + 10;
                            }
                        }
                        ch += 4;
                    }

                    if (card.Segments != null)
                    {
                        foreach (AgentContentSegment seg in card.Segments.ToArray())
                        {
                            if (seg == null) continue;
                            if (seg.IsCodeBlock)
                            {
                                string[] clines = (seg.Text ?? "").Split('\n');
                                int codeLines = clines.Length;
                                ch += 28 + codeLines * 17 + 14;
                            }
                            else
                            {
                                List<string> plines = WrapProseLines(seg.Text, fProse, contentW - 8);
                                ch += plines.Count * 18 + 6;
                            }
                        }
                    }

                    if (card.IsErrorCard)
                        ch += 34;
                    if (!string.IsNullOrEmpty(card.PendingCommandToApprove) && !card.CommandApprovalResolved)
                        ch += 34;

                    card.MeasuredHeight = Math.Max(48, ch + 8);
                    total += card.MeasuredHeight + 10;
                }
            }
            totalContentHeight = total;
            int maxScroll = Math.Max(0, totalContentHeight - this.Height + 10);
            if (scrollY > maxScroll) scrollY = maxScroll;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateLayout();
        }

        private bool isDraggingFeedScroll = false;

        private void ScrollFeedFromMouseY(int mouseY)
        {
            if (totalContentHeight <= this.Height) return;
            int thumbH = Math.Max(22, (int)((double)this.Height / totalContentHeight * (this.Height - 6)));
            int maxScroll = Math.Max(1, totalContentHeight - this.Height + 10);
            int trackRange = Math.Max(1, this.Height - 6 - thumbH);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - 3 - thumbH / 2) / trackRange));
            scrollY = (int)Math.Round(ratio * maxScroll);
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int step = e.Delta > 0 ? -42 : 42;
            int maxScroll = Math.Max(0, totalContentHeight - this.Height + 10);
            scrollY = Math.Max(0, Math.Min(maxScroll, scrollY + step));
            this.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            lastMousePos = e.Location;
            if (isDraggingFeedScroll && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                ScrollFeedFromMouseY(e.Y);
                return;
            }
            bool overHit = (totalContentHeight > this.Height && e.X >= this.Width - 10);
            if (!overHit)
            {
                foreach (InteractiveButtonHit hit in hitRegions.ToArray())
                {
                    if (hit != null && hit.Bounds.Contains(e.Location)) { overHit = true; break; }
                }
            }
            this.Cursor = overHit ? Cursors.Hand : Cursors.Default;
            this.Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();

            if (e.Button == MouseButtons.Left && totalContentHeight > this.Height && e.X >= this.Width - 10)
            {
                isDraggingFeedScroll = true;
                ScrollFeedFromMouseY(e.Y);
                return;
            }

            int checkY = 10 - scrollY;
            foreach (AgentMessageCard c in cards.ToArray())
            {
                if (c == null) continue;
                if (e.Y >= checkY && e.Y <= checkY + c.MeasuredHeight)
                {
                    lastContextCard = c;
                    break;
                }
                checkY += c.MeasuredHeight + 10;
            }

            foreach (InteractiveButtonHit hit in hitRegions.ToArray())
            {
                if (hit != null && hit.Bounds.Contains(e.Location))
                {
                    if (hit.ActionType == "thinking" && hit.Card != null)
                    {
                        hit.Card.ThinkingExpanded = !hit.Card.ThinkingExpanded;
                        RecalculateLayout();
                        this.Invalidate();
                        return;
                    }
                    else if (hit.ActionType == "tool_toggle" && hit.ToolChip != null)
                    {
                        hit.ToolChip.IsExpanded = !hit.ToolChip.IsExpanded;
                        RecalculateLayout();
                        this.Invalidate();
                        return;
                    }
                    else if (hit.ActionType == "copy_card" && hit.Card != null)
                    {
                        try
                        {
                            Clipboard.SetText(hit.Card.RawText ?? "");
                            copiedFlashCard = hit.Card;
                            this.Invalidate();
                        }
                        catch { }
                        return;
                    }
                    else if (hit.ActionType == "restore_cp" && hit.Card != null)
                    {
                        if (RestoreCheckpointRequested != null)
                            RestoreCheckpointRequested(hit.Card.CheckpointId ?? "");
                        return;
                    }
                    else if (hit.ActionType == "open_ai_settings")
                    {
                        if (OpenAiSettingsRequested != null)
                            OpenAiSettingsRequested();
                        return;
                    }
                    else if (hit.ActionType == "run_cmd" && hit.Card != null)
                    {
                        hit.Card.CommandApprovalResolved = true;
                        RecalculateLayout();
                        this.Invalidate();
                        if (ApproveTerminalCommandRequested != null)
                            ApproveTerminalCommandRequested(hit.Card.PendingCommandToApprove ?? "");
                        return;
                    }
                    else if (hit.ActionType == "skip_cmd" && hit.Card != null)
                    {
                        hit.Card.CommandApprovalResolved = true;
                        RecalculateLayout();
                        this.Invalidate();
                        if (SkipTerminalCommandRequested != null)
                            SkipTerminalCommandRequested(hit.Card.PendingCommandToApprove ?? "");
                        return;
                    }
                    else if (hit.ActionType == "copy" && hit.Segment != null)
                    {
                        try
                        {
                            Clipboard.SetText(hit.Segment.Text ?? "");
                            copiedFlashSegmentText = hit.Segment.Text;
                            this.Invalidate();
                        }
                        catch { }
                        return;
                    }
                    else if (hit.ActionType == "insert" && hit.Segment != null)
                    {
                        if (InsertCodeAtCursorRequested != null)
                            InsertCodeAtCursorRequested(hit.Segment.Text ?? "");
                        else if (ApplyCodeRequested != null)
                            ApplyCodeRequested(hit.Segment.Text ?? "");
                        return;
                    }
                    else if (hit.ActionType == "apply" && hit.Segment != null)
                    {
                        if (ApplyCodeRequested != null)
                            ApplyCodeRequested(hit.Segment.Text ?? "");
                        return;
                    }
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            isDraggingFeedScroll = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (this.Width != lastLayoutWidth) RecalculateLayout();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(theme.BgSidebar);
            hitRegions.Clear();

            using (Pen topLine = new Pen(theme.BorderSubtle, 1f))
                g.DrawLine(topLine, 0, 0, this.Width, 0);

            int curY = 10 - scrollY;
            int cardMarginX = 10;
            int cardW = Math.Max(140, this.Width - 22);

            using (Font fHeader = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold))
            using (Font fMeta = new Font("Consolas", 7.8f))
            using (Font fProse = new Font("Segoe UI", 8.9f))
            using (Font fBold = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold))
            using (Font fThink = new Font("Segoe UI", 8.2f))
            using (Font fCode = new Font("Consolas", 8.5f))
            {
                foreach (AgentMessageCard card in cards.ToArray())
                {
                    if (card == null) continue;
                    int cardTop = curY;
                    int cardH = card.MeasuredHeight;
                    curY += cardH + 10;

                    if (cardTop + cardH < 0 || cardTop > this.Height) continue;

                    Rectangle cardRect = new Rectangle(cardMarginX, cardTop, cardW, cardH);
                    Color cardBg = card.IsErrorCard
                        ? StudioTheme.Blend(theme.BgEditor, Color.FromArgb(220, 38, 38), 0.14)
                        : (card.IsUser ? theme.BgElevated : StudioTheme.Blend(theme.BgSidebar, theme.BgEditor, 0.45));
                    Color cardBorder = card.IsErrorCard
                        ? Color.FromArgb(239, 68, 68)
                        : (card.IsUser ? theme.BorderSubtle : Color.FromArgb(60, theme.BorderSubtle.R, theme.BorderSubtle.G, theme.BorderSubtle.B));

                    using (GraphicsPath rPath = CreateRoundedPath(cardRect, 6))
                    {
                        using (SolidBrush cb = new SolidBrush(cardBg))
                            g.FillPath(cb, rPath);
                        using (Pen bp = new Pen(cardBorder, 1f))
                            g.DrawPath(bp, rPath);
                    }

                    // Header row (Cursor style: clean "You" for user, "✦ model" for assistant, no debug jargon)
                    int iy = cardRect.Y + 8;
                    int ix = cardRect.X + 10;
                    string displaySender = card.SenderLabel ?? (card.IsUser ? "You" : "Assistant");
                    if (card.IsUser)
                    {
                        displaySender = "You";
                    }
                    else if (!card.IsErrorCard && string.IsNullOrEmpty(card.PendingCommandToApprove))
                    {
                        displaySender = displaySender.Replace("✦", "").Trim();
                        if (string.IsNullOrEmpty(displaySender))
                            displaySender = "Assistant";
                    }

                    int badgeW;
                    if (card.IsUser || card.IsErrorCard)
                    {
                        Color badgeBg = card.IsErrorCard ? Color.FromArgb(220, 38, 38) : StudioTheme.Blend(cardBg, theme.AccentPrimary, 0.22);
                        Color badgeFg = card.IsErrorCard ? Color.White : theme.FgPrimary;
                        badgeW = TextRenderer.MeasureText(displaySender, fHeader, new Size(220, 20), TextFormatFlags.NoPadding).Width + 14;
                        Rectangle badgeR = new Rectangle(ix, iy, badgeW, 18);
                        using (GraphicsPath bp = CreateRoundedPath(badgeR, 4))
                        using (SolidBrush bb = new SolidBrush(badgeBg))
                            g.FillPath(bb, bp);
                        TextRenderer.DrawText(g, displaySender, fHeader, badgeR, badgeFg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                    }
                    else
                    {
                        CodeLessVectorIcons.DrawIcon(g, "agent", new Rectangle(ix, iy + 2, 13, 13), theme.AccentPrimary, 1.35f);
                        int textW = TextRenderer.MeasureText(displaySender, fHeader, new Size(220, 20), TextFormatFlags.NoPadding).Width + 4;
                        badgeW = 16 + textW;
                        Rectangle badgeR = new Rectangle(ix + 16, iy, textW, 18);
                        TextRenderer.DrawText(g, displaySender, fHeader, badgeR, theme.FgSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                    }

                    // Copy message prose button on header right (always hit-testable, visually subtle unless hovered)
                    Rectangle copyCardR = new Rectangle(cardRect.Right - 48, iy, 40, 18);
                    bool hovCopyCard = copyCardR.Contains(lastMousePos);
                    bool cardHovered = cardRect.Contains(lastMousePos);
                    bool cardCopied = (copiedFlashCard == card);
                    if (cardHovered || hovCopyCard || cardCopied)
                    {
                        using (SolidBrush ccb = new SolidBrush(hovCopyCard ? theme.BgHover : theme.BgElevated))
                            g.FillRectangle(ccb, copyCardR);
                        using (Pen ccp = new Pen(theme.BorderSubtle, 1f))
                            g.DrawRectangle(ccp, copyCardR);
                        TextRenderer.DrawText(g, cardCopied ? "Copied" : "Copy", fMeta, copyCardR, cardCopied ? theme.AccentSecondary : theme.FgSecondary,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                    }
                    hitRegions.Add(new InteractiveButtonHit { Bounds = copyCardR, ActionType = "copy_card", Card = card });

                    int rightButtonsLeft = copyCardR.Left;

                    // Restore Checkpoint button on User messages (Cursor style)
                    if (card.IsUser)
                    {
                        Rectangle restoreR = new Rectangle(copyCardR.Left - 58, iy, 54, 18);
                        bool hovRestore = restoreR.Contains(lastMousePos);
                        if (cardHovered || hovRestore)
                        {
                            using (SolidBrush rb = new SolidBrush(hovRestore ? theme.BgHover : theme.BgWorkbench))
                                g.FillRectangle(rb, restoreR);
                            using (Pen rp = new Pen(theme.BorderSubtle, 1f))
                                g.DrawRectangle(rp, restoreR);
                            TextRenderer.DrawText(g, "Restore", fMeta, restoreR, hovRestore ? theme.AccentPrimary : theme.FgSecondary,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                        }
                        hitRegions.Add(new InteractiveButtonHit { Bounds = restoreR, ActionType = "restore_cp", Card = card });
                        rightButtonsLeft = restoreR.Left;
                    }

                    string metaText = card.IsUser ? "" : (card.MetaBadge ?? "");
                    metaText = Regex.Replace(metaText, @"^(?:AGENT|ASK|EDIT|AUTO-FIX|PROMPT)\s*(?:#\d+)?\s*·?\s*", "", RegexOptions.IgnoreCase).Trim();
                    if (metaText.IndexOf("Prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        metaText.IndexOf("HARNESS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        metaText.IndexOf("LOCAL ENGINE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        metaText.IndexOf("tok", StringComparison.OrdinalIgnoreCase) >= 0)
                        metaText = "";
                    if (card.IsStreaming)
                    {
                        metaText = "Generating...";
                    }

                    if (!string.IsNullOrEmpty(metaText))
                    {
                        Rectangle metaR = new Rectangle(ix + badgeW + 6, iy, Math.Max(30, rightButtonsLeft - (ix + badgeW + 10)), 18);
                        TextRenderer.DrawText(g, metaText, fMeta, metaR, card.IsStreaming ? theme.AccentPrimary : theme.FgMuted,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                    iy += 24;

                    // Optional collapsible thinking toggle ("Thought for 1s")
                    if (!string.IsNullOrEmpty(card.ThinkingText))
                    {
                        Rectangle thRect = new Rectangle(ix, iy, cardW - 20, 22);
                        using (GraphicsPath tpPath = CreateRoundedPath(thRect, 4))
                        {
                            using (SolidBrush tb = new SolidBrush(theme.BgActivity))
                                g.FillPath(tb, tpPath);
                            using (Pen tp = new Pen(theme.BorderSubtle, 1f))
                                g.DrawPath(tp, tpPath);
                        }
                        string thTitle = (card.ThinkingExpanded ? "▾ " : "▸ ") + "Thought for 1s";
                        TextRenderer.DrawText(g, thTitle, fThink, new Rectangle(thRect.X + 8, thRect.Y, thRect.Width - 12, thRect.Height),
                            theme.FgSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        hitRegions.Add(new InteractiveButtonHit { Bounds = thRect, ActionType = "thinking", Card = card });
                        iy += 25;

                        if (card.ThinkingExpanded)
                        {
                            List<string> tLines = WrapProseLines(card.ThinkingText, fThink, cardW - 32);
                            int showLines = tLines.Count;
                            Rectangle bodyR = new Rectangle(ix, iy, cardW - 20, showLines * 16 + 8);
                            using (SolidBrush tb2 = new SolidBrush(theme.BgWorkbench))
                                g.FillRectangle(tb2, bodyR);
                            int ty = iy + 4;
                            for (int tl = 0; tl < showLines; tl++)
                            {
                                TextRenderer.DrawText(g, tLines[tl], fThink, new Point(ix + 8, ty), theme.FgMuted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                                ty += 16;
                            }
                            iy += showLines * 16 + 11;
                        }
                    }

                    // Collapsible Tool-Call Cards ("Read file X", "Edited Y +12 -3", "Ran command")
                    if (card.Tools != null && card.Tools.Count > 0)
                    {
                        foreach (AgentToolChip tc in card.Tools.ToArray())
                        {
                            if (tc == null) continue;
                            Rectangle toolRowR = new Rectangle(ix, iy, cardW - 20, 21);
                            Color toolTone = tc.Success ? theme.AccentSecondary : theme.TokenControl;
                            using (SolidBrush pb = new SolidBrush(StudioTheme.Blend(cardBg, toolTone, 0.11)))
                                g.FillRectangle(pb, toolRowR);
                            using (Pen pp = new Pen(StudioTheme.Blend(cardBg, toolTone, 0.35), 1f))
                                g.DrawRectangle(pp, toolRowR);

                            string chev = !string.IsNullOrEmpty(tc.Output) ? (tc.IsExpanded ? "▾ " : "▸ ") : "";
                            string summary = !string.IsNullOrEmpty(tc.Summary) ? (" — " + tc.Summary) : "";
                            string lbl = chev + (tc.Success ? "✓ " : "✕ ") + tc.Name + summary;
                            TextRenderer.DrawText(g, lbl, fThink, new Rectangle(toolRowR.X + 6, toolRowR.Y, toolRowR.Width - 12, toolRowR.Height), toolTone,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                            hitRegions.Add(new InteractiveButtonHit { Bounds = toolRowR, ActionType = "tool_toggle", Card = card, ToolChip = tc });
                            iy += 24;

                            if (tc.IsExpanded && !string.IsNullOrEmpty(tc.Output))
                            {
                                string[] outLines = tc.Output.Replace("\r\n", "\n").Split('\n');
                                int showOut = Math.Min(12, outLines.Length);
                                Rectangle outBox = new Rectangle(ix + 6, iy, cardW - 26, showOut * 15 + 6);
                                using (SolidBrush ob = new SolidBrush(theme.BgWorkbench))
                                    g.FillRectangle(ob, outBox);
                                using (Pen op = new Pen(theme.BorderSubtle, 1f))
                                    g.DrawRectangle(op, outBox);
                                for (int ol = 0; ol < showOut; ol++)
                                {
                                    TextRenderer.DrawText(g, outLines[ol], fMeta, new Rectangle(outBox.X + 6, outBox.Y + 3 + ol * 15, outBox.Width - 12, 15),
                                        theme.FgSecondary, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                                }
                                iy += showOut * 15 + 10;
                            }
                        }
                        iy += 4;
                    }

                    // Segments: markdown prose & interactive syntax-highlighted code blocks
                    if (card.Segments != null)
                    foreach (AgentContentSegment seg in card.Segments.ToArray())
                    {
                        if (seg == null) continue;
                        if (seg.IsCodeBlock)
                        {
                            string[] clines = (seg.Text ?? "").Split('\n');
                            int visCode = clines.Length;
                            int blockH = 26 + visCode * 17 + 8;
                            Rectangle codeBox = new Rectangle(ix, iy, cardW - 20, blockH);
                            using (GraphicsPath cbPath = CreateRoundedPath(codeBox, 5))
                            {
                                using (SolidBrush cbg = new SolidBrush(theme.BgWorkbench))
                                    g.FillPath(cbg, cbPath);
                                using (Pen cbp = new Pen(theme.BorderSubtle, 1f))
                                    g.DrawPath(cbp, cbPath);
                            }

                            Rectangle codeHdr = new Rectangle(codeBox.X + 1, codeBox.Y + 1, codeBox.Width - 2, 24);
                            using (SolidBrush chb = new SolidBrush(theme.BgActivity))
                                g.FillRectangle(chb, codeHdr);
                            TextRenderer.DrawText(g, (seg.Language ?? "code").ToLowerInvariant(), fMeta,
                                new Rectangle(codeHdr.X + 8, codeHdr.Y, 90, codeHdr.Height), theme.FgSecondary,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

                            // Per-block Copy, Insert at Cursor, and Apply buttons
                            Rectangle applyR = new Rectangle(codeHdr.Right - 58, codeHdr.Y + 3, 54, 18);
                            Rectangle insertR = new Rectangle(applyR.Left - 54, codeHdr.Y + 3, 50, 18);
                            Rectangle copyR = new Rectangle(insertR.Left - 52, codeHdr.Y + 3, 48, 18);

                            bool hovApply = applyR.Contains(lastMousePos);
                            bool hovInsert = insertR.Contains(lastMousePos);
                            bool hovCopy = copyR.Contains(lastMousePos);
                            bool justCopied = (copiedFlashSegmentText != null && copiedFlashSegmentText == seg.Text);

                            using (SolidBrush cpBg = new SolidBrush(hovCopy ? theme.BgHover : theme.BgElevated))
                                g.FillRectangle(cpBg, copyR);
                            using (Pen cpBd = new Pen(theme.BorderSubtle, 1f))
                                g.DrawRectangle(cpBd, copyR);
                            TextRenderer.DrawText(g, justCopied ? "✓" : "Copy", fMeta, copyR, justCopied ? theme.AccentSecondary : theme.FgPrimary,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                            using (SolidBrush inBg = new SolidBrush(hovInsert ? theme.BgHover : theme.BgElevated))
                                g.FillRectangle(inBg, insertR);
                            using (Pen inBd = new Pen(theme.BorderSubtle, 1f))
                                g.DrawRectangle(inBd, insertR);
                            TextRenderer.DrawText(g, "Insert", fMeta, insertR, theme.FgPrimary,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                            using (SolidBrush apBg = new SolidBrush(hovApply ? theme.AccentSecondary : theme.AccentPrimary))
                                g.FillRectangle(apBg, applyR);
                            TextRenderer.DrawText(g, "Apply", fMeta, applyR, Color.White,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                            hitRegions.Add(new InteractiveButtonHit { Bounds = copyR, ActionType = "copy", Card = card, Segment = seg });
                            hitRegions.Add(new InteractiveButtonHit { Bounds = insertR, ActionType = "insert", Card = card, Segment = seg });
                            hitRegions.Add(new InteractiveButtonHit { Bounds = applyR, ActionType = "apply", Card = card, Segment = seg });

                            int cy = codeHdr.Bottom + 4;
                            for (int cl = 0; cl < visCode; cl++)
                            {
                                string codeLn = clines[cl];
                                Color cc = ClassifyCodeBlockLineColor(codeLn, theme);
                                TextRenderer.DrawText(g, codeLn, fCode, new Rectangle(codeBox.X + 8, cy, codeBox.Width - 16, 17), cc,
                                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                                cy += 17;
                            }
                            iy += blockH + 8;
                        }
                        else
                        {
                            List<string> plines = WrapProseLines(seg.Text, fProse, cardW - 28);
                            foreach (string pl in plines)
                            {
                                string ptr = pl.TrimStart();
                                Color pc = card.IsErrorCard ? Color.FromArgb(252, 165, 165) : theme.FgPrimary;
                                Font pf = fProse;
                                if (ptr.StartsWith("### ") || ptr.StartsWith("## "))
                                {
                                    pc = theme.AccentPrimary;
                                    pf = fBold;
                                }
                                else if (ptr.StartsWith("✓"))
                                {
                                    pc = theme.AccentSecondary;
                                }
                                TextRenderer.DrawText(g, pl, pf, new Point(ix, iy), pc, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                                iy += 18;
                            }
                            iy += 4;
                        }
                    }

                    // Error Card action button: "Open AI Settings"
                    if (card.IsErrorCard)
                    {
                        Rectangle openAiBtn = new Rectangle(ix, iy + 2, 146, 26);
                        bool hovBtn = openAiBtn.Contains(lastMousePos);
                        using (SolidBrush bb = new SolidBrush(hovBtn ? Color.FromArgb(239, 68, 68) : Color.FromArgb(185, 28, 28)))
                            g.FillRectangle(bb, openAiBtn);
                        TextRenderer.DrawText(g, "Open AI Settings", fHeader, openAiBtn, Color.White,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                        hitRegions.Add(new InteractiveButtonHit { Bounds = openAiBtn, ActionType = "open_ai_settings", Card = card });
                        iy += 32;
                    }

                    // Terminal Command Approval buttons: "Run" / "Skip"
                    if (!string.IsNullOrEmpty(card.PendingCommandToApprove) && !card.CommandApprovalResolved)
                    {
                        Rectangle runCmdBtn = new Rectangle(ix, iy + 2, 128, 26);
                        Rectangle skipCmdBtn = new Rectangle(ix + 136, iy + 2, 74, 26);
                        using (SolidBrush rb = new SolidBrush(theme.AccentSecondary))
                            g.FillRectangle(rb, runCmdBtn);
                        TextRenderer.DrawText(g, "Run in Terminal", fHeader, runCmdBtn, Color.White,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                        using (SolidBrush sb = new SolidBrush(theme.BgElevated))
                            g.FillRectangle(sb, skipCmdBtn);
                        using (Pen sp = new Pen(theme.BorderSubtle, 1f))
                            g.DrawRectangle(sp, skipCmdBtn);
                        TextRenderer.DrawText(g, "Skip", fHeader, skipCmdBtn, theme.FgSecondary,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                        hitRegions.Add(new InteractiveButtonHit { Bounds = runCmdBtn, ActionType = "run_cmd", Card = card });
                        hitRegions.Add(new InteractiveButtonHit { Bounds = skipCmdBtn, ActionType = "skip_cmd", Card = card });
                        iy += 32;
                    }
                }
            }

            if (totalContentHeight > this.Height && this.Height > 24)
            {
                int sbX = this.Width - 5;
                int thumbH = Math.Max(22, (int)((double)this.Height / totalContentHeight * (this.Height - 6)));
                int maxScroll = Math.Max(1, totalContentHeight - this.Height + 10);
                int thumbY = 3 + (int)((double)scrollY / maxScroll * (this.Height - 6 - thumbH));
                Color thumbCol = StudioTheme.Blend(theme.BgSidebar, theme.FgSecondary, 0.42);
                using (SolidBrush sb = new SolidBrush(thumbCol))
                    g.FillRectangle(sb, sbX, thumbY, 4, thumbH);
            }
        }

        private static Color ClassifyCodeBlockLineColor(string codeLn, StudioTheme t)
        {
            string ctr = (codeLn ?? "").TrimStart();
            if (ctr.StartsWith("//") || ctr.StartsWith("#")) return t.TokenComment;
            if (ctr.StartsWith("+")) return t.AccentSecondary;
            if (ctr.StartsWith("-")) return t.TokenControl;
            if (ctr.StartsWith("public ") || ctr.StartsWith("private ") || ctr.StartsWith("protected ") ||
                ctr.StartsWith("class ") || ctr.StartsWith("def ") || ctr.StartsWith("function ") ||
                ctr.StartsWith("import ") || ctr.StartsWith("from ") || ctr.StartsWith("using ") ||
                ctr.StartsWith("return ") || ctr.StartsWith("if ") || ctr.StartsWith("for ") || ctr.StartsWith("async "))
                return t.TokenKeyword;
            if (ctr.Contains("\"") || ctr.Contains("'")) return t.TokenString;
            return t.FgPrimary;
        }
    }

    // =========================================================================
    // VS CODE BOTTOM-RIGHT TOAST NOTIFICATION OVERLAY
    // =========================================================================
    public class CodeLessToastNotification : Panel
    {
        private System.Windows.Forms.Timer dismissTimer;
        private StudioTheme theme;
        private string severity;
        private string titleText;
        private string messageText;
        private string actionLabel;
        private Action actionCallback;
        private Rectangle actionBtnRect = Rectangle.Empty;
        private Rectangle closeBtnRect = Rectangle.Empty;

        public CodeLessToastNotification()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.Size = new Size(340, 74);
            this.Visible = false;
            this.Cursor = Cursors.Hand;

            dismissTimer = new System.Windows.Forms.Timer { Interval = 4500 };
            dismissTimer.Tick += (s, e) =>
            {
                dismissTimer.Stop();
                this.Visible = false;
            };
        }

        public void ShowToast(Control parentForm, StudioTheme activeTheme, string title, string message, string sev = "info", string btnLabel = null, Action onBtnClick = null, int durationMs = 4500)
        {
            this.theme = activeTheme ?? StudioTheme.GetBuiltInThemes()[0];
            this.titleText = title ?? "codeLess";
            this.messageText = message ?? "";
            this.severity = (sev ?? "info").ToLowerInvariant();
            this.actionLabel = btnLabel;
            this.actionCallback = onBtnClick;

            int h = string.IsNullOrEmpty(btnLabel) ? 68 : 94;
            this.Size = new Size(348, h);
            if (parentForm != null)
            {
                if (!parentForm.Controls.Contains(this))
                    parentForm.Controls.Add(this);
                this.Location = new Point(Math.Max(10, parentForm.ClientSize.Width - this.Width - 16), Math.Max(10, parentForm.ClientSize.Height - this.Height - 32));
                this.BringToFront();
            }
            this.Visible = true;
            this.Invalidate();

            dismissTimer.Stop();
            if (durationMs > 0)
            {
                dismissTimer.Interval = durationMs;
                dismissTimer.Start();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (closeBtnRect.Contains(e.Location))
            {
                dismissTimer.Stop();
                this.Visible = false;
                return;
            }
            if (!string.IsNullOrEmpty(actionLabel) && actionBtnRect.Contains(e.Location))
            {
                dismissTimer.Stop();
                this.Visible = false;
                if (actionCallback != null) actionCallback();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            StudioTheme t = theme ?? StudioTheme.GetBuiltInThemes()[0];

            using (SolidBrush bg = new SolidBrush(t.BgElevated))
                g.FillRectangle(bg, 0, 0, this.Width, this.Height);

            Color accent = t.AccentPrimary;
            string icon = "ℹ";
            if (severity == "error") { accent = Color.FromArgb(239, 68, 68); icon = "⊗"; }
            else if (severity == "warning") { accent = Color.FromArgb(245, 158, 11); icon = "⚠"; }
            else if (severity == "success") { accent = Color.FromArgb(16, 185, 129); icon = "✓"; }

            using (SolidBrush stripe = new SolidBrush(accent))
                g.FillRectangle(stripe, 0, 0, 4, this.Height);
            using (Pen bp = new Pen(t.BorderSubtle, 1f))
                g.DrawRectangle(bp, 0, 0, this.Width - 1, this.Height - 1);

            using (Font fIcon = new Font("Segoe UI Semibold", 11f, FontStyle.Bold))
            using (Font fTitle = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold))
            using (Font fBody = new Font("Segoe UI", 8.5f))
            {
                TextRenderer.DrawText(g, icon, fIcon, new Rectangle(12, 10, 22, 22), accent, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, titleText, fTitle, new Rectangle(34, 10, this.Width - 64, 20), t.FgPrimary, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, messageText, fBody, new Rectangle(34, 30, this.Width - 46, 32), t.FgSecondary, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                closeBtnRect = new Rectangle(this.Width - 26, 8, 20, 20);
                TextRenderer.DrawText(g, "×", fTitle, closeBtnRect, t.FgSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                if (!string.IsNullOrEmpty(actionLabel))
                {
                    int btnW = Math.Max(80, TextRenderer.MeasureText(actionLabel, fBody).Width + 20);
                    actionBtnRect = new Rectangle(this.Width - btnW - 12, this.Height - 30, btnW, 22);
                    using (SolidBrush ab = new SolidBrush(t.AccentPrimary))
                        g.FillRectangle(ab, actionBtnRect);
                    TextRenderer.DrawText(g, actionLabel, fBody, actionBtnRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }
    }
}