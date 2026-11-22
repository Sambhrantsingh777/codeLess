using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code editor switching (same model as Avalonia): Ctrl+Tab / Ctrl+Shift+Tab open the "recently used editors"
    /// picker of the group in MRU order while Ctrl is held; Tab / Shift+Tab / arrows move, releasing Ctrl opens,
    /// Escape cancels, a quick tap switches to the previous editor. Handled in ProcessCmdKey so it works from every
    /// control (tree views, text boxes, terminal) before WinForms turns Ctrl+Tab into focus navigation.
    /// Ctrl+PageUp/PageDown stay tab-strip order.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private readonly EditorMruList<EditorTabBuffer> editorMruWin = new EditorMruList<EditorTabBuffer>();
        private readonly EditorSwitcherSession<EditorTabBuffer> editorSwitcherWin = new EditorSwitcherSession<EditorTabBuffer>();
        private EditorSwitcherPanelWin editorSwitcherPanel;
        private Timer editorSwitcherCtrlPoll;

        public bool IsEditorSwitcherOpen { get { return editorSwitcherWin.IsOpen; } }
        public List<EditorTabBuffer> EditorMruOrder { get { return editorMruWin.Ordered(openTabs); } }

        private void InitEditorSwitcherWin()
        {
            this.KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (!editorSwitcherWin.IsOpen) return;
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey || !e.Control)
                {
                    CommitEditorSwitcherWin();
                    e.Handled = true;
                }
            };
            this.Deactivate += delegate { if (editorSwitcherWin.IsOpen) CancelEditorSwitcherWin(); };
            // Key-up of Ctrl can be lost (released over another window, focus change): poll the modifier state.
            editorSwitcherCtrlPoll = new Timer { Interval = 60 };
            editorSwitcherCtrlPoll.Tick += delegate
            {
                if (!editorSwitcherWin.IsOpen) { editorSwitcherCtrlPoll.Stop(); return; }
                if ((Control.ModifierKeys & Keys.Control) == 0) CommitEditorSwitcherWin();
            };
        }

        /// <summary>Hook from RefreshTabStrip: the active editor becomes the most recently used one.</summary>
        private void TouchEditorMruWin()
        {
            if (activeTab != null) editorMruWin.Touch(activeTab);
        }

        /// <summary>The editor that takes over when <paramref name="closing"/> (the active one) closes: MRU, like VS Code.</summary>
        private EditorTabBuffer EditorAfterCloseWin(EditorTabBuffer closing)
        {
            EditorTabBuffer mru = editorMruWin.MostRecentOther(openTabs, closing);
            return mru ?? openTabs.LastOrDefault();
        }

        /// <summary>ProcessCmdKey hook. True = consumed.</summary>
        private bool HandleEditorSwitcherCmdKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
            if (!editorSwitcherWin.IsOpen)
            {
                if (key == Keys.Tab && ctrl && !alt)
                {
                    OpenOrAdvanceEditorSwitcherWin(shift);
                    return true;
                }
                return false;
            }
            switch (key)
            {
                case Keys.Tab:
                    editorSwitcherWin.Move(shift ? -1 : 1);
                    RenderEditorSwitcherWin();
                    return true;
                case Keys.Down:
                    editorSwitcherWin.Move(1);
                    RenderEditorSwitcherWin();
                    return true;
                case Keys.Up:
                    editorSwitcherWin.Move(-1);
                    RenderEditorSwitcherWin();
                    return true;
                case Keys.Enter:
                    CommitEditorSwitcherWin();
                    return true;
                case Keys.Escape:
                    CancelEditorSwitcherWin();
                    return true;
                case Keys.ControlKey: case Keys.ShiftKey: case Keys.Menu:
                    return true;
            }
            if (!ctrl)
            {
                CommitEditorSwitcherWin();
                return false;
            }
            return true;
        }

        public void OpenOrAdvanceEditorSwitcherWin(bool backwards)
        {
            if (editorSwitcherWin.IsOpen)
            {
                editorSwitcherWin.Move(backwards ? -1 : 1);
                RenderEditorSwitcherWin();
                return;
            }
            List<EditorTabBuffer> order = editorMruWin.Ordered(openTabs);
            if (activeTab != null && order.Contains(activeTab))
            {
                order.Remove(activeTab);
                order.Insert(0, activeTab);
            }
            if (order.Count == 0) return;
            if (order.Count == 1)
            {
                if (!ReferenceEquals(order[0], activeTab)) ActivateTabWin(order[0]);
                return;
            }
            if (!editorSwitcherWin.Begin(order, backwards)) return;
            if (editorSwitcherPanel == null)
            {
                editorSwitcherPanel = new EditorSwitcherPanelWin();
                editorSwitcherPanel.ItemClicked += delegate (int idx)
                {
                    editorSwitcherWin.Select(idx);
                    CommitEditorSwitcherWin();
                };
                this.Controls.Add(editorSwitcherPanel);
            }
            editorSwitcherPanel.Theme = activeTheme;
            editorSwitcherPanel.WorkspaceRoot = workspaceRoot;
            RenderEditorSwitcherWin();
            editorSwitcherPanel.Visible = true;
            editorSwitcherPanel.BringToFront();
            if (editorSwitcherCtrlPoll != null) editorSwitcherCtrlPoll.Start();
        }

        private void RenderEditorSwitcherWin()
        {
            if (editorSwitcherPanel == null) return;
            editorSwitcherPanel.SetItems(editorSwitcherWin.Items, editorSwitcherWin.SelectedIndex);
            int w = Math.Min(600, Math.Max(320, ClientSize.Width - 80));
            editorSwitcherPanel.Size = new Size(w, editorSwitcherPanel.PreferredPanelHeight);
            editorSwitcherPanel.Location = new Point(Math.Max(0, (ClientSize.Width - w) / 2), 40);
            editorSwitcherPanel.Invalidate();
        }

        public void CommitEditorSwitcherWin()
        {
            if (!editorSwitcherWin.IsOpen) return;
            EditorTabBuffer target = editorSwitcherWin.Commit();
            HideEditorSwitcherWin();
            if (target != null && openTabs.Contains(target))
            {
                if (!ReferenceEquals(target, activeTab)) ActivateTabWin(target);
                if (editorControl != null) editorControl.Focus();
            }
        }

        public void CancelEditorSwitcherWin()
        {
            editorSwitcherWin.Cancel();
            HideEditorSwitcherWin();
        }

        private void HideEditorSwitcherWin()
        {
            if (editorSwitcherCtrlPoll != null) editorSwitcherCtrlPoll.Stop();
            if (editorSwitcherPanel != null) editorSwitcherPanel.Visible = false;
        }

        /// <summary>Activates an open editor (same steps as SwitchTabByOffset / the OPEN EDITORS list).</summary>
        private void ActivateTabWin(EditorTabBuffer tab)
        {
            if (tab == null) return;
            activeTab = tab;
            if (explorerTree != null)
            {
                explorerTree.ActiveEditorFilePath = activeTab.FilePath ?? "";
                explorerTree.Invalidate();
            }
            editorControl.LoadBuffer(activeTab);
            RefreshOutlineAndProblems(activeTab);
            RefreshTabStrip();
            UpdateWindowTitle();
        }
    }

    /// <summary>Owner-drawn Ctrl+Tab picker (widget colors, 22 px rows: file badge, name, folder).</summary>
    internal sealed class EditorSwitcherPanelWin : Panel
    {
        private const int RowH = 22, TitleH = 24, MaxRows = 14, Pad = 6;
        private List<EditorTabBuffer> items = new List<EditorTabBuffer>();
        private int selected;
        private int scrollTop;
        public StudioTheme Theme;
        public string WorkspaceRoot;
        public event Action<int> ItemClicked;

        public EditorSwitcherPanelWin()
        {
            DoubleBuffered = true;
            Visible = false;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        public int PreferredPanelHeight
        {
            get { return Pad * 2 + TitleH + Math.Min(MaxRows, Math.Max(1, items.Count)) * RowH + 2; }
        }

        public void SetItems(List<EditorTabBuffer> list, int sel)
        {
            items = list != null ? new List<EditorTabBuffer>(list) : new List<EditorTabBuffer>();
            selected = sel;
            if (selected < scrollTop) scrollTop = selected;
            if (selected >= scrollTop + MaxRows) scrollTop = selected - MaxRows + 1;
            if (scrollTop > Math.Max(0, items.Count - MaxRows)) scrollTop = Math.Max(0, items.Count - MaxRows);
            if (scrollTop < 0) scrollTop = 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int row = (e.Y - Pad - TitleH) / RowH;
            int idx = scrollTop + row;
            if (e.Y >= Pad + TitleH && idx >= 0 && idx < items.Count && ItemClicked != null) ItemClicked(idx);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            StudioTheme t = Theme;
            Color bg = t != null ? UiTokens.WidgetBackground(t) : SystemColors.Window;
            Color border = t != null ? UiTokens.WidgetBorder(t) : SystemColors.ControlDark;
            Color fg = t != null ? t.FgPrimary : SystemColors.WindowText;
            Color muted = t != null ? t.FgMuted : SystemColors.GrayText;
            Color selBg = t != null ? UiTokens.ListActiveSelection(t, bg) : SystemColors.Highlight;
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (Font small = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontSmall)))
            using (Font ui = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi)))
            using (Font badgeFont = new Font("Consolas", 7f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "Recently used editors", small, new Rectangle(Pad + 8, Pad, Width - 2 * Pad - 16, TitleH), muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                int end = Math.Min(items.Count, scrollTop + MaxRows);
                for (int i = scrollTop; i < end; i++)
                {
                    EditorTabBuffer tab = items[i];
                    Rectangle r = new Rectangle(Pad, Pad + TitleH + (i - scrollTop) * RowH, Width - 2 * Pad, RowH);
                    if (i == selected) using (SolidBrush sb = new SolidBrush(selBg)) g.FillRectangle(sb, r);
                    string badge = BadgeFor(tab.FileName);
                    TextRenderer.DrawText(g, badge, badgeFont, new Rectangle(r.X + 2, r.Y, 26, r.Height), muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    string name = (tab.IsDirty ? "● " : "") + (tab.FileName ?? "");
                    Size nameSize = TextRenderer.MeasureText(g, name, ui, new Size(int.MaxValue, RowH), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, name, ui, new Rectangle(r.X + 32, r.Y, r.Width - 40, r.Height), fg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    string dir = FolderOf(tab);
                    int dx = r.X + 32 + nameSize.Width + 10;
                    if (dir.Length > 0 && dx < r.Right - 20)
                        TextRenderer.DrawText(g, dir, small, new Rectangle(dx, r.Y, r.Right - dx - 6, r.Height), muted,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
                }
            }
        }

        private static string BadgeFor(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToUpperInvariant();
            if (ext.Length == 0) return "•";
            return ext.Length > 3 ? ext.Substring(0, 3) : ext;
        }

        private string FolderOf(EditorTabBuffer tab)
        {
            string rel = tab.RelativePath;
            if (string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(tab.FilePath))
            {
                rel = tab.FilePath;
                if (!string.IsNullOrEmpty(WorkspaceRoot) && rel.StartsWith(WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(WorkspaceRoot.Length).TrimStart('\\', '/');
            }
            if (string.IsNullOrEmpty(rel)) return "";
            string dir;
            try { dir = Path.GetDirectoryName(rel) ?? ""; } catch { dir = ""; }
            return dir.Replace('\\', '/');
        }
    }
}
