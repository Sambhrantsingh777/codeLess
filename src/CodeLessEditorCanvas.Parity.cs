using System;
using System.Drawing;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Merge-conflict line backgrounds, segmented code lenses (Accept Current | Accept Incoming | Accept Both | Compare)
    /// and gutter git-diff bar clicks for the WinForms editor canvas. Strict C# 5.
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        /// <summary>Click on a gutter git-diff bar: (buffer, 0-based line, bar rectangle in client coordinates).</summary>
        public event Action<EditorTabBuffer, int, Rectangle> GutterDiffClicked;

        public static bool HasGutterDiffMarker(EditorTabBuffer buf, int line)
        {
            if (buf == null || line < 0) return false;
            if (buf.GutterAddedLines != null && buf.GutterAddedLines.Contains(line)) return true;
            if (buf.GutterModifiedLines != null && buf.GutterModifiedLines.Contains(line)) return true;
            if (buf.GutterDeletedAfterLine != null && (buf.GutterDeletedAfterLine.Contains(line) || (line == 0 && buf.GutterDeletedAfterLine.Contains(-1)))) return true;
            return false;
        }

        private bool TryRaiseGutterDiffClick(EditorTabBuffer buf, int line, MouseEventArgs e)
        {
            Action<EditorTabBuffer, int, Rectangle> h = GutterDiffClicked;
            if (h == null || !HasGutterDiffMarker(buf, line)) return false;
            int row = Math.Max(0, (e.Y - 4) / Math.Max(1, LineHeight));
            h(buf, line, new Rectangle(GutterWidth - 6, row * LineHeight + 4, 6, LineHeight));
            return true;
        }

        public static Color ConflictColorFor(ConflictLineKind kind)
        {
            switch (kind)
            {
                case ConflictLineKind.CurrentHeader: return Color.FromArgb(128, 64, 200, 174);
                case ConflictLineKind.Current: return Color.FromArgb(51, 64, 200, 174);
                case ConflictLineKind.IncomingHeader: return Color.FromArgb(128, 64, 166, 255);
                case ConflictLineKind.Incoming: return Color.FromArgb(51, 64, 166, 255);
                case ConflictLineKind.BaseHeader: return Color.FromArgb(102, 96, 96, 96);
                case ConflictLineKind.Base: return Color.FromArgb(41, 96, 96, 96);
                default: return Color.Empty;
            }
        }

        private void DrawConflictLineBackground(Graphics g, EditorTabBuffer buf, int docLine, int y, int codeAreaRight)
        {
            if (buf == null || buf.Lines == null || buf.Lines.Count > 200000) return;
            ConflictLineKind kind = ConflictDecorations.KindAt(buf, docLine);
            if (kind == ConflictLineKind.None || kind == ConflictLineKind.Separator) return;
            using (SolidBrush b = new SolidBrush(ConflictColorFor(kind)))
                g.FillRectangle(b, GutterWidth, y, Math.Max(0, codeAreaRight - GutterWidth), LineHeight);
        }

        /// <summary>Draws "A | B | C" lens parts, each with its own hit rectangle.</summary>
        private void DrawSegmentedAnnotation(Graphics g, EditorLineAnnotation ann, int docLine, int x, int y, int codeAreaRight)
        {
            EditorLineAnnotation[] parts = ann.GetSegmentParts();
            if (parts == null) return;
            StudioTheme th = owner.Theme;
            Color sepColor = StudioTheme.Blend(th.BgEditor, th.FgMuted, 0.7);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            for (int i = 0; i < parts.Length; i++)
            {
                if (x > codeAreaRight - 40) break;
                if (i > 0)
                {
                    string sep = EditorLineAnnotation.SegmentSeparator;
                    Size ss = TextRenderer.MeasureText(g, sep, gutterFont, new Size(400, LineHeight), flags);
                    TextRenderer.DrawText(g, sep, gutterFont, new Rectangle(x, y, ss.Width, LineHeight), sepColor, flags);
                    x += ss.Width;
                }
                EditorLineAnnotation p = parts[i];
                bool hot = ReferenceEquals(p, hoveredLineAnnotation);
                Color c = Color.FromArgb(hot ? 255 : 210, th.AccentPrimary);
                Size sz = TextRenderer.MeasureText(g, p.Text, gutterFont, new Size(Math.Max(40, codeAreaRight - x), LineHeight), flags);
                int w = Math.Min(Math.Max(10, codeAreaRight - x - 6), sz.Width + 2);
                Rectangle r = new Rectangle(x, y, w, LineHeight);
                TextRenderer.DrawText(g, p.Text, gutterFont, r, c, flags | TextFormatFlags.EndEllipsis);
                if (hot)
                {
                    using (Pen pen = new Pen(c))
                        g.DrawLine(pen, x, y + LineHeight - 3, x + w - 2, y + LineHeight - 3);
                }
                AnnotationHit hit = new AnnotationHit();
                hit.Rect = r;
                hit.Line = docLine;
                hit.Annotation = p;
                annotationHits.Add(hit);
                x += w;
            }
        }
    }
}
