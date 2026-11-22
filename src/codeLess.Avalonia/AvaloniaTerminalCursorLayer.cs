using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// The terminal cursor on its own layer above the grid (xterm.js also keeps the cursor on a separate render layer):
    /// blinking and focus changes repaint only this layer's few pixels instead of re-recording the whole grid. Whenever
    /// the grid repaints, this layer repaints in the same frame from the grid's snapshot, so the cursor never lags the
    /// text. Styles follow terminal.integrated.cursorStyle (block / line / underline), cursorStyleInactive (outline by
    /// default) and the application's DECSCUSR; the block cursor draws the character under it in the background colour
    /// (VS Code: terminalCursor.foreground = terminal foreground, terminalCursor.background = terminal background).
    /// </summary>
    internal sealed class AvaloniaTerminalCursorLayer : Control
    {
        private readonly AvaloniaTerminalGridCanvas canvas;

        public AvaloniaTerminalCursorLayer(AvaloniaTerminalGridCanvas canvas)
        {
            this.canvas = canvas;
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
        }

        /// <summary>Times this layer was painted (tests).</summary>
        internal long RenderCount { get; private set; }
        /// <summary>Cursor rectangle painted in the last frame (empty when hidden).</summary>
        internal Rect LastRect { get; private set; }
        /// <summary>Style painted in the last frame (block / line / underline / outline), null when hidden.</summary>
        internal string LastStyle { get; private set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            RenderCount++;
            LastRect = default;
            LastStyle = null;
            var owner = canvas.Owner;
            if (owner == null || !canvas.TryGetCursor(out Rect cell, out string style, out bool blink, out TermCell under)) return;
            if (blink && !owner.CursorBlinkOn) return;
            Color color = owner.TermFgColor;
            TerminalCursorStyle.Geometry(style, cell.X, cell.Y, cell.Width, cell.Height, TerminalOptions.CursorWidth,
                out double rx, out double ry, out double rw, out double rh);
            var rect = new Rect(rx, ry, rw, rh);
            LastRect = rect;
            LastStyle = style;
            var brush = canvas.GetBrush(color);
            if (style == TerminalCursorStyle.Outline)
            {
                context.DrawRectangle(null, canvas.GetPen(brush, 1), rect.Deflate(0.5));
                return;
            }
            context.FillRectangle(brush, rect);
            if (style != TerminalCursorStyle.Block) return;
            char ch = under.Char;
            if (ch <= ' ' || char.IsLowSurrogate(ch) || char.IsHighSurrogate(ch) || TerminalBoxDrawing.IsCustomGlyph(ch)) return;
            bool bold = (under.Flags & TermCell.FLAG_BOLD) != 0, italic = (under.Flags & TermCell.FLAG_ITALIC) != 0;
            var text = canvas.GetLayout(ch.ToString(), bold, italic, owner.TermBgColor);
            using (context.PushClip(rect))
                text.Draw(context, new Point(cell.X, cell.Y + canvas.TextTopOffset));
        }
    }
}
