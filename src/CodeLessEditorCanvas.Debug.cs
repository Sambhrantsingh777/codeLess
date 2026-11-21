using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>Debugger decorations for the WinForms editor canvas (breakpoint glyphs, stopped-frame arrow / line).</summary>
    public partial class CodeLessVirtualCanvas
    {
        /// <summary>Document position under a client point (hover tooling). False over the gutter / outside text.</summary>
        public bool TryGetDocPositionAt(Point pt, out int line, out int col)
        {
            line = -1;
            col = -1;
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || pt.X < GutterWidth || pt.Y < 0 || pt.Y > Height) return false;
            ScreenPointToLineCol(pt, out line, out col);
            return line >= 0 && line < buf.Lines.Count;
        }

        private void DrawDebugStoppedLineBackground(Graphics g, EditorTabBuffer buf, int y, int codeAreaRight)
        {
            StudioTheme th = owner.Theme;
            bool light = th != null && th.IsLight;
            Color c = buf.DebugStoppedIsTopFrame
                ? (light ? Color.FromArgb(90, 255, 230, 80) : Color.FromArgb(70, 255, 213, 0))
                : Color.FromArgb(55, 122, 189, 122);
            using (SolidBrush b = new SolidBrush(c))
                g.FillRectangle(b, GutterWidth, y, Math.Max(0, codeAreaRight - GutterWidth), LineHeight);
        }

        private void DrawDebugGutterGlyphs(Graphics g, EditorTabBuffer buf, int docLine, int y)
        {
            DebugBreakpointGlyph glyph = buf.GetBreakpointGlyph(docLine);
            bool stopped = buf.DebugStoppedLine == docLine;
            if (glyph == DebugBreakpointGlyph.None && !stopped) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = 10;
            int x = 5;
            int top = y + (LineHeight - d) / 2;
            Color red = Color.FromArgb(229, 20, 0);
            Color gray = Color.FromArgb(150, 150, 150);
            switch (glyph)
            {
                case DebugBreakpointGlyph.Enabled:
                    using (SolidBrush b = new SolidBrush(red)) g.FillEllipse(b, x, top, d, d);
                    break;
                case DebugBreakpointGlyph.Conditional:
                    using (SolidBrush b = new SolidBrush(red)) g.FillEllipse(b, x, top, d, d);
                    using (Pen p = new Pen(Color.White, 1.2f))
                    {
                        g.DrawLine(p, x + 2.5f, top + 3.5f, x + d - 2.5f, top + 3.5f);
                        g.DrawLine(p, x + 2.5f, top + d - 3.5f, x + d - 2.5f, top + d - 3.5f);
                    }
                    break;
                case DebugBreakpointGlyph.Logpoint:
                    using (SolidBrush b = new SolidBrush(red))
                    {
                        PointF[] pts = new PointF[]
                        {
                            new PointF(x + d / 2f, top - 0.5f), new PointF(x + d + 0.5f, top + d / 2f),
                            new PointF(x + d / 2f, top + d + 0.5f), new PointF(x - 0.5f, top + d / 2f)
                        };
                        g.FillPolygon(b, pts);
                    }
                    break;
                case DebugBreakpointGlyph.Disabled:
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(140, gray))) g.FillEllipse(b, x, top, d, d);
                    break;
                case DebugBreakpointGlyph.Unverified:
                    using (Pen p = new Pen(gray, 1.3f)) g.DrawEllipse(p, x + 0.5f, top + 0.5f, d - 1, d - 1);
                    break;
            }
            if (stopped)
            {
                // VS Code's stopped-frame arrow: yellow for the top frame, green for a selected outer frame.
                Color fill = buf.DebugStoppedIsTopFrame ? Color.FromArgb(255, 204, 0) : Color.FromArgb(89, 166, 89);
                float cy = y + LineHeight / 2f;
                float h = Math.Min(LineHeight - 4, 12);
                float left = 2, right = EditorDebugHooks.GlyphMarginWidth + 1;
                PointF[] arrow = new PointF[]
                {
                    new PointF(left, cy - h * 0.28f), new PointF(right - h * 0.5f, cy - h * 0.28f), new PointF(right - h * 0.5f, cy - h * 0.5f),
                    new PointF(right, cy), new PointF(right - h * 0.5f, cy + h * 0.5f), new PointF(right - h * 0.5f, cy + h * 0.28f),
                    new PointF(left, cy + h * 0.28f)
                };
                using (SolidBrush b = new SolidBrush(fill)) g.FillPolygon(b, arrow);
                using (Pen p = new Pen(Color.FromArgb(140, 0, 0, 0), 0.6f)) g.DrawPolygon(p, arrow);
            }
            g.SmoothingMode = old;
        }
    }
}
