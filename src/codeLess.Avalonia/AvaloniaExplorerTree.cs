using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class ExplorerTreeNode
    {
        public string Text { get; set; } = "";
        public string Tag { get; set; } = "";
        public bool IsDirectory { get; set; }
        public bool IsExpanded { get; set; }
        /// <summary>Rendered greyed-out (gitignored / build output / dependency folder), like VS Code.</summary>
        public bool IsDimmed { get; set; }
        public int StateImageIndex { get; set; }
        public ExplorerTreeNode Parent { get; set; }
        public List<ExplorerTreeNode> Nodes { get; } = new List<ExplorerTreeNode>();

        public ExplorerTreeNode() { }

        public ExplorerTreeNode(string text)
        {
            Text = text ?? "";
        }

        public int Level
        {
            get
            {
                int lvl = 0;
                var p = Parent;
                while (p != null) { lvl++; p = p.Parent; }
                return lvl;
            }
        }

        public void Expand()
        {
            IsExpanded = true;
        }

        public void Collapse()
        {
            IsExpanded = false;
        }
    }

    public class AvaloniaExplorerTree : Control
    {
        public const int RowHeight = 22;

        public List<ExplorerTreeNode> Nodes { get; } = new List<ExplorerTreeNode>();
        public List<ExplorerTreeNode> VisibleFlatRows { get; } = new List<ExplorerTreeNode>();
        public ExplorerTreeNode SelectedNode { get; set; }
        public string ActiveEditorFilePath { get; set; } = "";
        public Dictionary<string, string> GitStatusByFullPath { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ModifiedFolderPaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public double ScrollOffsetY { get; set; } = 0;
        private int hoveredRowIndex = -1;

        public Color BgColor { get; set; } = Color.FromRgb(22, 22, 26);
        public Color TextPrimary { get; set; } = Color.FromRgb(228, 228, 234);
        public Color TextSecondary { get; set; } = Color.FromRgb(142, 142, 154);
        public Color HoverFill { get; set; } = Color.FromRgb(34, 34, 42);
        public Color SelectedFill { get; set; } = Color.FromRgb(42, 54, 78);
        public Color AccentColor { get; set; } = Color.FromRgb(59, 130, 246);
        public Color IndentGuideColor { get; set; } = Color.FromRgb(44, 44, 52);
        public Color ActiveFileFill { get; set; } = Color.FromRgb(40, 42, 48);
        public Color ActiveGuideColor { get; set; } = Color.FromRgb(90, 94, 102);

        public event Action<ExplorerTreeNode> BeforeExpand;
        public event Action<ExplorerTreeNode> AfterSelect;
        public event Action<ExplorerTreeNode, bool> NodeActivated; // (node, isDoubleClickOrPin)
        public event Action<ExplorerTreeNode, Point> NodeContextMenuRequested;
        public event Action<ExplorerTreeNode> RenameRequested;
        public event Action<ExplorerTreeNode> DeleteRequested;

        public AvaloniaExplorerTree()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        private StudioTheme scrollTheme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private readonly SlimScrollbar scrollbar = new SlimScrollbar();
        /// <summary>The list scrollbar (tests).</summary>
        public SlimScrollbar Scrollbar => scrollbar;

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            scrollTheme = theme;
            BgColor = AvaloniaColorHelper.ToAvColor(theme.SidebarBg);
            TextPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            TextSecondary = AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
            AccentColor = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);
            bool isLight = AvaloniaColorHelper.IsLightColor(theme.SidebarBg);
            HoverFill = AvaloniaColorHelper.ToAvColor(UiTokens.Hover(theme, theme.SidebarBg));
            SelectedFill = AvaloniaColorHelper.ToAvColor(UiTokens.ListActiveSelection(theme, theme.SidebarBg));
            IndentGuideColor = AvaloniaColorHelper.ToAvColor(UiTokens.IndentGuide(theme, theme.SidebarBg));
            ActiveFileFill = AvaloniaColorHelper.ToAvColor(UiTokens.ListInactiveSelection(theme, theme.SidebarBg));
            ActiveGuideColor = AvaloniaColorHelper.ToAvColor(UiTokens.IndentGuideActive(theme, theme.SidebarBg));
            InvalidateVisual();
        }

        public void RebuildTreeLayout(bool preserveScroll = true)
        {
            VisibleFlatRows.Clear();
            foreach (var root in Nodes)
            {
                AddVisibleRecursive(root);
            }
            if (!preserveScroll)
                ScrollOffsetY = 0;
            ClampScroll();
            InvalidateVisual();
        }

        private void AddVisibleRecursive(ExplorerTreeNode node)
        {
            if (node == null) return;
            VisibleFlatRows.Add(node);
            if (node.IsExpanded && node.Nodes.Count > 0)
            {
                foreach (var child in node.Nodes)
                {
                    if ((child.Tag ?? "") == "__LAZY_DIR__") continue;
                    child.Parent = node;
                    AddVisibleRecursive(child);
                }
            }
        }

        public void CollapseAllFolders()
        {
            foreach (var root in Nodes)
            {
                CollapseRecursive(root, isTopRoot: true);
            }
            RebuildTreeLayout(preserveScroll: false);
        }

        private void CollapseRecursive(ExplorerTreeNode node, bool isTopRoot)
        {
            if (node == null) return;
            if (!isTopRoot)
                node.IsExpanded = false;
            else
                node.IsExpanded = true;
            foreach (var child in node.Nodes)
                CollapseRecursive(child, false);
        }

        public ExplorerTreeNode FindNodeByPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            foreach (var root in Nodes)
            {
                var found = FindNodeRecursive(root, fullPath);
                if (found != null) return found;
            }
            return null;
        }

        private ExplorerTreeNode FindNodeRecursive(ExplorerTreeNode cur, string fullPath)
        {
            if (cur == null) return null;
            if (string.Equals(cur.Tag, fullPath, StringComparison.OrdinalIgnoreCase))
                return cur;
            foreach (var child in cur.Nodes)
            {
                var f = FindNodeRecursive(child, fullPath);
                if (f != null) return f;
            }
            return null;
        }

        private void ClampScroll()
        {
            double maxScroll = Math.Max(0, VisibleFlatRows.Count * RowHeight - Math.Max(RowHeight, Bounds.Height));
            if (ScrollOffsetY > maxScroll) ScrollOffsetY = maxScroll;
            if (ScrollOffsetY < 0) ScrollOffsetY = 0;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            ScrollOffsetY -= e.Delta.Y * RowHeight * 2.5;
            ClampScroll();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pt = e.GetPosition(this);
            if (scrollbar.Drag(pt, out double dragged)) { ScrollOffsetY = dragged; ClampScroll(); InvalidateVisual(); return; }
            if (scrollbar.UpdateHover(pt)) InvalidateVisual();
            if (scrollbar.Contains(pt)) { if (hoveredRowIndex != -1) { hoveredRowIndex = -1; InvalidateVisual(); } return; }
            int idx = (int)((pt.Y + ScrollOffsetY) / RowHeight);
            if (idx < 0 || idx >= VisibleFlatRows.Count) idx = -1;
            if (idx != hoveredRowIndex)
            {
                hoveredRowIndex = idx;
                InvalidateVisual();
            }
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            InvalidateVisual(); // the list scrollbar shows while the pointer is over the tree (VS Code)
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (scrollbar.Release()) { e.Pointer.Capture(null); InvalidateVisual(); }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (scrollbar.Release()) InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            scrollbar.UpdateHover(null);
            InvalidateVisual();
            if (hoveredRowIndex != -1)
            {
                hoveredRowIndex = -1;
                InvalidateVisual();
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetPosition(this);
            if (scrollbar.Contains(pt))
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && scrollbar.Press(pt, out double off))
                {
                    ScrollOffsetY = off;
                    ClampScroll();
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            int idx = (int)((pt.Y + ScrollOffsetY) / RowHeight);
            if (idx < 0 || idx >= VisibleFlatRows.Count) return;

            var node = VisibleFlatRows[idx];
            SelectedNode = node;
            AfterSelect?.Invoke(node);

            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                InvalidateVisual();
                NodeContextMenuRequested?.Invoke(node, pt);
                e.Handled = true;
                return;
            }

            bool isFolder = node.IsDirectory || node.StateImageIndex == 1 || node.Nodes.Count > 0;
            if (isFolder)
            {
                ToggleFolderNode(node);
            }
            else
            {
                bool isDoubleClick = e.ClickCount >= 2;
                NodeActivated?.Invoke(node, isDoubleClick);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        public void ToggleFolderNode(ExplorerTreeNode node)
        {
            if (node == null) return;
            if (!node.IsExpanded)
            {
                BeforeExpand?.Invoke(node);
                node.IsExpanded = true;
            }
            else
            {
                node.IsExpanded = false;
            }
            RebuildTreeLayout(preserveScroll: true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (VisibleFlatRows.Count == 0) return;

            int curIdx = SelectedNode != null ? VisibleFlatRows.IndexOf(SelectedNode) : -1;
            if (e.Key == Key.Down)
            {
                int next = Math.Min(VisibleFlatRows.Count - 1, curIdx + 1);
                SelectIndex(next);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                int prev = Math.Max(0, curIdx <= 0 ? 0 : curIdx - 1);
                SelectIndex(prev);
                e.Handled = true;
            }
            else if (e.Key == Key.Right && SelectedNode != null)
            {
                bool isFolder = SelectedNode.IsDirectory || SelectedNode.StateImageIndex == 1 || SelectedNode.Nodes.Count > 0;
                if (isFolder && !SelectedNode.IsExpanded)
                {
                    ToggleFolderNode(SelectedNode);
                }
                else if (isFolder && SelectedNode.Nodes.Count > 0)
                {
                    SelectIndex(Math.Min(VisibleFlatRows.Count - 1, curIdx + 1));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left && SelectedNode != null)
            {
                bool isFolder = SelectedNode.IsDirectory || SelectedNode.StateImageIndex == 1 || SelectedNode.Nodes.Count > 0;
                if (isFolder && SelectedNode.IsExpanded)
                {
                    ToggleFolderNode(SelectedNode);
                }
                else if (SelectedNode.Parent != null)
                {
                    int pIdx = VisibleFlatRows.IndexOf(SelectedNode.Parent);
                    if (pIdx >= 0) SelectIndex(pIdx);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && SelectedNode != null)
            {
                bool isFolder = SelectedNode.IsDirectory || SelectedNode.StateImageIndex == 1 || SelectedNode.Nodes.Count > 0;
                if (isFolder)
                    ToggleFolderNode(SelectedNode);
                else
                    NodeActivated?.Invoke(SelectedNode, true);
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && SelectedNode != null)
            {
                RenameRequested?.Invoke(SelectedNode);
                e.Handled = true;
            }
            else if (SelectedNode != null && (e.Key == Key.Delete ||
                     (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta)))) // Cmd+Backspace on macOS
            {
                DeleteRequested?.Invoke(SelectedNode);
                e.Handled = true;
            }
        }

        public void ScrollNodeIntoView(ExplorerTreeNode node)
        {
            int idx = node != null ? VisibleFlatRows.IndexOf(node) : -1;
            if (idx < 0) return;
            double rowTop = idx * RowHeight;
            double rowBot = rowTop + RowHeight;
            double viewH = Bounds.Height > RowHeight ? Bounds.Height : RowHeight * 20;
            if (rowTop < ScrollOffsetY) ScrollOffsetY = rowTop;
            else if (rowBot > ScrollOffsetY + viewH) ScrollOffsetY = rowBot - viewH;
            ClampScroll();
            InvalidateVisual();
        }

        private void SelectIndex(int idx)
        {
            if (idx < 0 || idx >= VisibleFlatRows.Count) return;
            SelectedNode = VisibleFlatRows[idx];
            AfterSelect?.Invoke(SelectedNode);
            double rowTop = idx * RowHeight;
            double rowBot = rowTop + RowHeight;
            if (rowTop < ScrollOffsetY)
                ScrollOffsetY = rowTop;
            else if (rowBot > ScrollOffsetY + Bounds.Height && Bounds.Height > RowHeight)
                ScrollOffsetY = rowBot - Bounds.Height;
            ClampScroll();
            InvalidateVisual();
        }

        public static void GetFileTypeBadge(string fileName, out string badgeText, out Color badgeColor)
        {
            string ext = (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".cs":
                    badgeText = "C#";
                    badgeColor = Color.FromRgb(56, 189, 248);
                    break;
                case ".py":
                    badgeText = "PY";
                    badgeColor = Color.FromRgb(250, 204, 21);
                    break;
                case ".js":
                case ".jsx":
                    badgeText = "JS";
                    badgeColor = Color.FromRgb(245, 158, 11);
                    break;
                case ".ts":
                case ".tsx":
                    badgeText = "TS";
                    badgeColor = Color.FromRgb(59, 130, 246);
                    break;
                case ".rs":
                    badgeText = "RS";
                    badgeColor = Color.FromRgb(249, 115, 22);
                    break;
                case ".ps1":
                    badgeText = "PS";
                    badgeColor = Color.FromRgb(96, 165, 250);
                    break;
                case ".sh":
                case ".zsh":
                    badgeText = "SH";
                    badgeColor = Color.FromRgb(74, 222, 128);
                    break;
                case ".json":
                case ".yaml":
                case ".yml":
                case ".toml":
                    badgeText = "{}";
                    badgeColor = Color.FromRgb(168, 85, 247);
                    break;
                case ".iss":
                    badgeText = "IS";
                    badgeColor = Color.FromRgb(45, 212, 191);
                    break;
                case ".md":
                    badgeText = "MD";
                    badgeColor = Color.FromRgb(148, 163, 184);
                    break;
                default:
                    badgeText = "≡";
                    badgeColor = Color.FromRgb(148, 163, 184);
                    break;
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            context.FillRectangle(new SolidColorBrush(BgColor), new Rect(0, 0, w, h));

            if (VisibleFlatRows.Count == 0) return;

            var fontRegular = new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Normal);
            var fontBold = new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
            var fontBadge = new Typeface("Cascadia Code, Consolas, Menlo, monospace", FontStyle.Normal, FontWeight.Bold);

            string selectedDirTag = null;
            if (SelectedNode != null)
            {
                bool selIsDir = SelectedNode.IsDirectory || SelectedNode.StateImageIndex == 1 || SelectedNode.Nodes.Count > 0;
                selectedDirTag = selIsDir ? SelectedNode.Tag : (SelectedNode.Parent != null ? SelectedNode.Parent.Tag : null);
            }

            int firstRow = Math.Max(0, (int)(ScrollOffsetY / RowHeight));
            int lastRow = Math.Min(VisibleFlatRows.Count - 1, (int)((ScrollOffsetY + h) / RowHeight) + 1);

            for (int i = firstRow; i <= lastRow; i++)
            {
                var node = VisibleFlatRows[i];
                double y = i * RowHeight - ScrollOffsetY;
                var rowRect = new Rect(0, y, w, RowHeight);

                bool isSelected = ReferenceEquals(node, SelectedNode);
                bool isHovered = (i == hoveredRowIndex);
                string fullPath = node.Tag ?? "";
                bool isActiveFile = !string.IsNullOrEmpty(ActiveEditorFilePath) &&
                                    !string.IsNullOrEmpty(fullPath) &&
                                    string.Equals(fullPath, ActiveEditorFilePath, StringComparison.OrdinalIgnoreCase);

                if (isSelected)
                {
                    context.FillRectangle(new SolidColorBrush(SelectedFill), rowRect);
                }
                else if (isActiveFile)
                {
                    context.FillRectangle(new SolidColorBrush(ActiveFileFill), rowRect);
                }
                else if (isHovered)
                {
                    context.FillRectangle(new SolidColorBrush(HoverFill), rowRect);
                }

                int depth = node.Level;
                for (int d = 1; d <= depth; d++)
                {
                    // Indent guide sits under the parent folder's twistie (VS Code).
                    double gx = Math.Round(8 + (d - 1) * UiTokens.TreeIndent + 5.5) + 0.5;
                    bool isActiveGuide = !string.IsNullOrEmpty(selectedDirTag) && !string.IsNullOrEmpty(fullPath) &&
                                         (string.Equals(fullPath, selectedDirTag, StringComparison.OrdinalIgnoreCase) ||
                                          fullPath.StartsWith(selectedDirTag.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    var guidePen = new Pen(new SolidColorBrush(isActiveGuide ? ActiveGuideColor : IndentGuideColor), 1);
                    context.DrawLine(guidePen, new Point(gx, y), new Point(gx, y + RowHeight));
                }

                double indentX = 8 + depth * UiTokens.TreeIndent;
                bool isFolder = node.IsDirectory || node.StateImageIndex == 1 || node.Nodes.Count > 0;

                if (isFolder)
                {
                    // Draw crisp vector chevron (▸ or ▾)
                    var chevRect = new Rect(indentX, y + 5, 11, 12);
                    AvaloniaVectorIcons.DrawIcon(context, node.IsExpanded ? "chevron-down" : "chevron-right", chevRect, TextSecondary, 1.5f);

                    // Draw folder icon
                    var folderColor = Color.FromRgb(220, 182, 122);
                    var folderPen = new Pen(new SolidColorBrush(folderColor), 1.3);
                    double fx = indentX + 15;
                    double fy = y + 5;
                    context.DrawRectangle(
                        node.IsExpanded ? new SolidColorBrush(Color.FromArgb(55, 220, 182, 122)) : null,
                        folderPen,
                        new Rect(fx, fy + 2, 12, 9), 1.2, 1.2);
                    context.DrawLine(folderPen, new Point(fx + 1, fy + 1), new Point(fx + 5, fy + 1));
                }
                else
                {
                    string badgeText;
                    Color badgeColor;
                    GetFileTypeBadge(node.Text, out badgeText, out badgeColor);
                    var bft = new FormattedText(
                        badgeText,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        fontBadge,
                        8.5,
                        new SolidColorBrush(badgeColor));
                    context.DrawText(bft, new Point(indentX + 12, y + (RowHeight - bft.Height) / 2.0));
                }

                // Git status color & right badge (VS Code Light+ vs Dark+ colors)
                bool isLightBg = (BgColor.R + BgColor.G + BgColor.B) > 380;
                Color modAmber = isLightBg ? Color.FromRgb(137, 85, 3) : Color.FromRgb(226, 192, 141);
                Color addGreen = isLightBg ? Color.FromRgb(0, 113, 0) : Color.FromRgb(115, 201, 145);
                Color delRed = isLightBg ? Color.FromRgb(173, 7, 7) : Color.FromRgb(241, 76, 76);

                Color labelColor = node.IsDimmed && !isSelected
                    ? Color.FromArgb(150, TextSecondary.R, TextSecondary.G, TextSecondary.B)
                    : TextPrimary;
                string gitBadge = null;
                Color gitBadgeColor = TextSecondary;

                if (!isFolder && !string.IsNullOrEmpty(fullPath) && GitStatusByFullPath != null &&
                    GitStatusByFullPath.TryGetValue(fullPath, out string st) && !string.IsNullOrEmpty(st))
                {
                    string code = st.Trim().ToUpperInvariant();
                    if (code.Contains("M"))
                    {
                        gitBadge = "M";
                        gitBadgeColor = modAmber;
                        labelColor = gitBadgeColor;
                    }
                    else if (code.Contains("A") || code.Contains("?") || code.Contains("U"))
                    {
                        gitBadge = code.Contains("?") ? "U" : "A";
                        gitBadgeColor = addGreen;
                        labelColor = gitBadgeColor;
                    }
                    else if (code.Contains("D"))
                    {
                        gitBadge = "D";
                        gitBadgeColor = delRed;
                        labelColor = gitBadgeColor;
                    }
                }
                else if (isFolder && !string.IsNullOrEmpty(fullPath) && ModifiedFolderPaths != null && ModifiedFolderPaths.Contains(fullPath))
                {
                    gitBadge = "●";
                    gitBadgeColor = modAmber;
                }

                double textX = indentX + 32;
                double maxTextW = Math.Max(20, w - textX - (gitBadge != null ? 24 : 8));
                var ft = new FormattedText(
                    node.Text ?? "",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    (isFolder && depth == 0) ? fontBold : fontRegular,
                    12.0,
                    new SolidColorBrush(labelColor))
                {
                    MaxTextWidth = maxTextW,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                context.DrawText(ft, new Point(textX, y + (RowHeight - ft.Height) / 2.0));

                if (!string.IsNullOrEmpty(gitBadge))
                {
                    var gft = new FormattedText(
                        gitBadge,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        fontBadge,
                        10.0,
                        new SolidColorBrush(gitBadgeColor));
                    context.DrawText(gft, new Point(w - gft.Width - 10, y + (RowHeight - gft.Height) / 2.0));
                }
            }

            // VS Code list scrollbar: 10 px, shown while hovering / dragging, drag + click-to-jump (QA #28 consistency)
            scrollbar.Paint(context, scrollTheme, Bounds.Size, VisibleFlatRows.Count * RowHeight, h, ScrollOffsetY, IsPointerOver);
        }
    }
}
