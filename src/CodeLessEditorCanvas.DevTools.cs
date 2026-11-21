using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>End-of-line annotations (inline git blame, "Send Request" code lens) for the WinForms editor canvas.</summary>
    public partial class CodeLessVirtualCanvas
    {
        private class AnnotationHit
        {
            public Rectangle Rect;
            public int Line;
            public EditorLineAnnotation Annotation;
        }

        private readonly List<AnnotationHit> annotationHits = new List<AnnotationHit>();
        private EditorLineAnnotation hoveredLineAnnotation;

        /// <summary>Supplies the annotation for a document line (called per visible line while painting; keep it cheap).</summary>
        public Func<EditorTabBuffer, int, EditorLineAnnotation> LineAnnotationProvider;
        /// <summary>(buffer, annotation, 0-based line, rectangle in canvas client coordinates).</summary>
        public event Action<EditorTabBuffer, EditorLineAnnotation, int, Rectangle> LineAnnotationClicked;
        /// <summary>Raised when the pointer enters an annotation, or with a null annotation when it leaves.</summary>
        public event Action<EditorTabBuffer, EditorLineAnnotation, int, Rectangle> LineAnnotationHovered;

        public bool IsPointerOverAnnotation { get { return hoveredLineAnnotation != null; } }

        private void ClearAnnotationHits()
        {
            annotationHits.Clear();
        }

        private void DrawLineAnnotation(Graphics g, EditorTabBuffer buf, int docLine, int sliceVisLen, int effScrollLeft, int y, int codeAreaRight)
        {
            Func<EditorTabBuffer, int, EditorLineAnnotation> provider = LineAnnotationProvider;
            if (provider == null || buf == null) return;
            if (docLine == buf.CursorLine && !string.IsNullOrEmpty(buf.GhostText) && buf.GhostLine == docLine) return;
            if (buf.Diagnostics != null && buf.Diagnostics.Any(d => d.Line == docLine + 1 && d.Severity != "Info")) return;
            EditorLineAnnotation ann;
            try { ann = provider(buf, docLine); } catch { ann = null; }
            if (ann == null || string.IsNullOrEmpty(ann.Text)) return;
            StudioTheme th = owner.Theme;
            int x = GutterWidth + 8 + (sliceVisLen - effScrollLeft + 3) * CharWidth;
            if (sliceVisLen == 0) x = GutterWidth + 8 + (ann.IsAction ? 0 : CharWidth);
            if (x < GutterWidth + 4) x = GutterWidth + 4;
            int avail = codeAreaRight - x - 10;
            if (avail < 60) return;
            bool hot = ReferenceEquals(ann, hoveredLineAnnotation) || (hoveredLineAnnotation != null && hoveredLineAnnotation.Tag != null && ReferenceEquals(hoveredLineAnnotation.Tag, ann.Tag));
            Color c = ann.IsAction
                ? Color.FromArgb(hot ? 255 : 210, th.AccentPrimary)
                : StudioTheme.Blend(th.BgEditor, th.FgMuted, hot ? 0.95 : 0.7);
            Font f = ann.IsAction ? gutterFont : italicFont;
            Size sz = TextRenderer.MeasureText(g, ann.Text, f, new Size(avail, LineHeight), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int w = Math.Min(avail, sz.Width + 2);
            Rectangle r = new Rectangle(x, y, w, LineHeight);
            TextRenderer.DrawText(g, ann.Text, f, r, c,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (hot && ann.IsAction)
            {
                using (Pen p = new Pen(c))
                    g.DrawLine(p, x, y + LineHeight - 3, x + w - 2, y + LineHeight - 3);
            }
            AnnotationHit hit = new AnnotationHit();
            hit.Rect = r;
            hit.Line = docLine;
            hit.Annotation = ann;
            annotationHits.Add(hit);
        }

        /// <summary>Handles a left click on an annotation; returns true when consumed.</summary>
        private bool TryClickLineAnnotation(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || annotationHits.Count == 0) return false;
            foreach (AnnotationHit h in annotationHits)
            {
                if (h.Rect.Contains(e.Location))
                {
                    Action<EditorTabBuffer, EditorLineAnnotation, int, Rectangle> handler = LineAnnotationClicked;
                    if (handler != null) handler(owner.CurrentBuffer, h.Annotation, h.Line, h.Rect);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Updates hover state; returns true when the pointer is over an annotation (hand cursor).</summary>
        private bool UpdateLineAnnotationHover(Point pt)
        {
            AnnotationHit found = null;
            foreach (AnnotationHit h in annotationHits)
            {
                if (h.Rect.Contains(pt)) { found = h; break; }
            }
            EditorLineAnnotation ann = found != null ? found.Annotation : null;
            if (!ReferenceEquals(ann, hoveredLineAnnotation))
            {
                hoveredLineAnnotation = ann;
                Action<EditorTabBuffer, EditorLineAnnotation, int, Rectangle> handler = LineAnnotationHovered;
                if (handler != null) handler(owner.CurrentBuffer, ann, found != null ? found.Line : -1, found != null ? found.Rect : Rectangle.Empty);
                Invalidate();
            }
            return found != null;
        }

        private void ClearLineAnnotationHover()
        {
            if (hoveredLineAnnotation == null) return;
            hoveredLineAnnotation = null;
            Action<EditorTabBuffer, EditorLineAnnotation, int, Rectangle> handler = LineAnnotationHovered;
            if (handler != null) handler(owner.CurrentBuffer, null, -1, Rectangle.Empty);
            Invalidate();
        }
    }
}
