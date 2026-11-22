using System;
using Avalonia;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public partial class AvaloniaEditorControl
    {
        /// <summary>
        /// Glyph-margin click: (buffer, 0-based line, isContextClick). When nobody handles it the canvas toggles
        /// <c>buf.Breakpoints</c> itself so the gutter still works standalone.
        /// </summary>
        public event Action<EditorTabBuffer, int, bool> BreakpointGutterClicked;

        internal bool RaiseBreakpointGutter(EditorTabBuffer buf, int line, bool context)
        {
            if (buf == null || line < 0 || line >= buf.Lines.Count) return false;
            var h = BreakpointGutterClicked;
            if (h != null) { h(buf, line, context); Canvas.InvalidateVisual(); return true; }
            if (context) return false;
            if (!buf.Breakpoints.Remove(line)) buf.Breakpoints.Add(line);
            Canvas.InvalidateVisual();
            return true;
        }
    }

    public partial class AvaloniaVirtualEditorCanvas
    {
        private static readonly IBrush BpRed = new SolidColorBrush(Color.FromRgb(229, 20, 0));
        private static readonly IBrush StoppedYellowBg = new SolidColorBrush(Color.FromArgb(70, 255, 213, 0));
        private static readonly IBrush StoppedYellowBgLight = new SolidColorBrush(Color.FromArgb(90, 255, 230, 80));
        private static readonly IBrush FocusedFrameGreenBg = new SolidColorBrush(Color.FromArgb(55, 122, 189, 122));

        /// <summary>Translates a canvas point to a document position (hover / tooling). False over the gutter.</summary>
        public bool TryGetDocPositionAt(Point pt, out int line, out int col)
        {
            line = -1; col = -1;
            var buf = Buffer;
            if (buf == null || pt.X < GutterWidth || pt.Y < 0 || pt.Y > Bounds.Height) return false;
            line = ScreenYToDocLine(pt.Y);
            if (line < 0 || line >= buf.Lines.Count) return false;
            col = CharIndexUnderPoint(line, pt.X);
            return col >= 0;
        }

        /// <summary>Canvas-relative top-left of a document line (for hover popups); null when scrolled out of view.</summary>
        public double? LineTopY(int docLine)
        {
            var buf = Buffer;
            if (buf == null) return null;
            int first = buf.ScrollTopLine;
            if (docLine < first) return null;
            double y = -TopRowOffsetPx;
            for (int l = first; l < docLine && y <= Bounds.Height; l++)
            {
                if (buf.FoldedBlockRanges != null && buf.FoldedBlockRanges.TryGetValue(l, out int end)) { l = end; }
                y += LineHeight;
            }
            return y <= Bounds.Height ? y : (double?)null;
        }

        private void DrawDebugStoppedLineBackground(DrawingContext context, EditorTabBuffer buf, double y, double codeAreaRight)
        {
            IBrush b = buf.DebugStoppedIsTopFrame ? (Theme.IsLight ? StoppedYellowBgLight : StoppedYellowBg) : FocusedFrameGreenBg;
            context.FillRectangle(b, new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight));
        }

        private void DrawDebugGutterGlyphs(DrawingContext context, EditorTabBuffer buf, int docLine, double y)
        {
            double cx = EditorDebugHooks.GlyphMarginWidth / 2.0 + 1;
            double cy = y + LineHeight / 2.0;
            double r = Math.Max(3.5, Math.Min(5.5, LineHeight * 0.24));
            var glyph = buf.GetBreakpointGlyph(docLine);
            Color gray = Theme.IsLight ? Color.FromRgb(140, 140, 140) : Color.FromRgb(150, 150, 150);
            switch (glyph)
            {
                case DebugBreakpointGlyph.Enabled:
                    context.DrawEllipse(BpRed, null, new Point(cx, cy), r, r);
                    break;
                case DebugBreakpointGlyph.Conditional:
                    context.DrawEllipse(BpRed, null, new Point(cx, cy), r, r);
                    var white = new Pen(Brushes.White, 1.2);
                    context.DrawLine(white, new Point(cx - r * 0.5, cy - r * 0.3), new Point(cx + r * 0.5, cy - r * 0.3));
                    context.DrawLine(white, new Point(cx - r * 0.5, cy + r * 0.3), new Point(cx + r * 0.5, cy + r * 0.3));
                    break;
                case DebugBreakpointGlyph.Logpoint:
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(new Point(cx, cy - r - 0.5), true);
                            c.LineTo(new Point(cx + r + 0.5, cy));
                            c.LineTo(new Point(cx, cy + r + 0.5));
                            c.LineTo(new Point(cx - r - 0.5, cy));
                            c.EndFigure(true);
                        }
                        context.DrawGeometry(BpRed, null, g);
                        break;
                    }
                case DebugBreakpointGlyph.Disabled:
                    context.DrawEllipse(new SolidColorBrush(gray, 0.55), null, new Point(cx, cy), r, r);
                    break;
                case DebugBreakpointGlyph.Unverified:
                    context.DrawEllipse(null, new Pen(new SolidColorBrush(gray), 1.3), new Point(cx, cy), r - 0.5, r - 0.5);
                    break;
            }
            if (buf.DebugStoppedLine == docLine)
            {
                // VS Code's stopped-frame arrow: yellow for the top frame, green for a selected outer frame.
                Color fill = buf.DebugStoppedIsTopFrame ? Color.FromRgb(255, 204, 0) : Color.FromRgb(89, 166, 89);
                double left = 2, right = EditorDebugHooks.GlyphMarginWidth + 1, h = Math.Min(LineHeight - 4, 12);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(left, cy - h * 0.28), true);
                    c.LineTo(new Point(right - h * 0.5, cy - h * 0.28));
                    c.LineTo(new Point(right - h * 0.5, cy - h * 0.5));
                    c.LineTo(new Point(right, cy));
                    c.LineTo(new Point(right - h * 0.5, cy + h * 0.5));
                    c.LineTo(new Point(right - h * 0.5, cy + h * 0.28));
                    c.LineTo(new Point(left, cy + h * 0.28));
                    c.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 0.6), g);
            }
        }
    }
}
