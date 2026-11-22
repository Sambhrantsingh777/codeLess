using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Smooth scrolling and caret for the WinForms editor, same model as the Avalonia editor (codeLess.Core
    /// EditorSmoothScrolling.cs): a fractional top row (pixel-precise precision-touchpad scrolling), 125 ms ease-out
    /// animations for wheel notches, paging, Ctrl+Up/Down, scrollbar track clicks and reveals, VS Code's blink styles and
    /// the smooth caret transition. The painted scene is cached in a bitmap: blinking and the caret glide only blit the
    /// caret's few pixels from it instead of repainting the editor. STRICT C# 5.
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        private static readonly Stopwatch wfAnimClock = Stopwatch.StartNew();
        private readonly SmoothScrollAnimation wfVAnim = new SmoothScrollAnimation();
        private readonly SmoothScrollAnimation wfHAnim = new SmoothScrollAnimation();
        private readonly WheelScrollClassifier wfWheel = new WheelScrollClassifier();
        private readonly CaretMoveAnimation wfCaretMove = new CaretMoveAnimation();
        private System.Windows.Forms.Timer wfAnimTimer;
        private EditorTabBuffer wfAnimBuf;
        private int wfAnimTopLine, wfAnimTopRow, wfAnimIndexBuild;
        private double wfAnimTopFrac, wfAnimTargetRow;
        private int wfAppliedLine = -1, wfAppliedRow;
        private double wfAppliedFrac;
        private int wfSubRowLine = -1;
        private int wfRevealEditVersion = int.MinValue;
        private EditorTabBuffer wfRevealBuf;

        // scene cache (everything but the carets)
        private Bitmap wfScene;
        private bool wfSceneDirty = true;
        private bool wfSceneValid;
        private bool wfCaretOnlyInvalidate;
        private Rectangle wfLastCaretBounds = Rectangle.Empty;

        // caret state
        private double wfBlinkResetMs;
        private bool wfBlinkFinished;
        private EditorTabBuffer wfCaretBuf;
        private int wfCaretLine = -1, wfCaretCol = -1, wfCaretEditVersion = int.MinValue;
        private double wfCaretContentX, wfCaretContentY;
        private bool wfCaretHasLast;

        /// <summary>Forces animations on / off (tests); null = on.</summary>
        public bool? AnimationsOverrideWF;

        private double NowMsWF { get { return wfAnimClock.Elapsed.TotalMilliseconds; } }
        private bool SmoothScrollingActiveWF { get { return EditorScrollOptions.SmoothScrolling && (!AnimationsOverrideWF.HasValue || AnimationsOverrideWF.Value); } }
        private bool CaretAnimationsActiveWF { get { return EditorScrollOptions.SmoothCaretAnimation && (!AnimationsOverrideWF.HasValue || AnimationsOverrideWF.Value); } }

        // ------------------------------------------------------------------ fractional position
        /// <summary>Wrap row of ScrollTopLine shown at the top (only when set by the smooth-scrolling code for that line).</summary>
        private int TopSubRowWF(EditorTabBuffer buf)
        {
            if (buf == null || !buf.WordWrap || wfSubRowLine != buf.ScrollTopLine || buf.ScrollTopWrapRow <= 0) return 0;
            if (buf.FoldedLines != null && buf.FoldedLines.Contains(buf.ScrollTopLine)) return 0;
            return Math.Min(buf.ScrollTopWrapRow, WrapOfWF(buf, buf.ScrollTopLine).RowCount - 1);
        }

        /// <summary>Pixels the first painted row starts above the top edge (fractional scroll position).</summary>
        private int TopSubPixelsWF(EditorTabBuffer buf)
        {
            if (buf == null) return 0;
            if (buf.WordWrap && TopSubRowWF(buf) != buf.ScrollTopWrapRow && buf.ScrollTopWrapRow != 0) return 0;
            return (int)Math.Round(buf.ScrollTopFraction * LineHeight);
        }

        private double TopRowExactWF(EditorTabBuffer buf)
        {
            EditorVisualRowIndex idx = RowIndexWF(buf);
            int sub = TopSubRowWF(buf);
            double frac = (sub == buf.ScrollTopWrapRow || buf.ScrollTopWrapRow == 0) ? buf.ScrollTopFraction : 0;
            return idx.RowOfLine(buf.ScrollTopLine) + sub + frac;
        }

        private double MaxTopRowWF(EditorTabBuffer buf) { return Math.Max(0, RowIndexWF(buf).TotalRows - 1); }

        private void SetTopRowExactWF(EditorTabBuffer buf, double row)
        {
            EditorVisualRowIndex idx = RowIndexWF(buf);
            double max = Math.Max(0, idx.TotalRows - 1);
            if (double.IsNaN(row)) row = 0;
            row = Math.Max(0, Math.Min(max, row));
            int whole = (int)Math.Floor(row + 1e-9);
            double frac = row - whole;
            if (frac < 1e-6 || whole >= max) frac = 0;
            int sub;
            int line = idx.LineOfRow(whole, out sub);
            buf.ScrollTopLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, line));
            buf.ScrollTopWrapRow = buf.WordWrap ? Math.Max(0, sub) : 0;
            wfSubRowLine = buf.ScrollTopLine;
            buf.ScrollTopFraction = frac;
        }

        private double ViewportRowsWF { get { return Math.Max(1.0, (double)this.Height / Math.Max(1, LineHeight)); } }

        // ------------------------------------------------------------------ scrolling
        /// <summary>Precise (touchpad) scrolling: applied 1:1; a running animation moves along.</summary>
        private void ScrollByPixelsNowWF(EditorTabBuffer buf, double dy)
        {
            if (buf == null || Math.Abs(dy) < 1e-9) return;
            double rows = dy / Math.Max(1, LineHeight);
            bool anim = wfVAnim.IsActive && ReferenceEquals(wfAnimBuf, buf);
            if (anim) { wfVAnim.ShiftBy(rows); wfVAnim.ClampTarget(0, MaxTopRowWF(buf)); }
            SetTopRowExactWF(buf, TopRowExactWF(buf) + rows);
            if (anim) RememberAppliedWF(buf);
            NoteScrollActivityWF();
        }

        /// <summary>Animated scroll by visual rows (wheel notches, Ctrl+Up/Down, Alt+PageUp/Down, Alt+track click).</summary>
        private void SmoothScrollRowsWF(EditorTabBuffer buf, double rows)
        {
            if (buf == null || Math.Abs(rows) < 1e-9) return;
            if (!SmoothScrollingActiveWF)
            {
                FinishScrollAnimationsWF(buf);
                SetTopRowExactWF(buf, TopRowExactWF(buf) + rows);
                NoteScrollActivityWF();
                Invalidate();
                return;
            }
            AdoptAnimationBufferWF(buf);
            double cur = TopRowExactWF(buf);
            if (!wfVAnim.IsActive) wfVAnim.Cancel(cur);
            wfVAnim.AddToTarget(cur, rows, ViewportRowsWF, NowMsWF, EditorScrollOptions.SmoothScrollDurationMs);
            wfVAnim.ClampTarget(0, MaxTopRowWF(buf));
            BeginScrollAnimationWF(buf, wfVAnim.Target);
        }

        private void AnimateTopRowToWF(EditorTabBuffer buf, double row)
        {
            if (buf == null) return;
            row = Math.Max(0, Math.Min(MaxTopRowWF(buf), row));
            if (!SmoothScrollingActiveWF)
            {
                FinishScrollAnimationsWF(buf);
                SetTopRowExactWF(buf, row);
                NoteScrollActivityWF();
                Invalidate();
                return;
            }
            AdoptAnimationBufferWF(buf);
            wfVAnim.Start(TopRowExactWF(buf), row, ViewportRowsWF, NowMsWF, EditorScrollOptions.SmoothScrollDurationMs);
            BeginScrollAnimationWF(buf, row);
        }

        /// <summary>Animated horizontal scroll in columns (the WinForms painter offsets whole columns).</summary>
        private void SmoothScrollColsWF(EditorTabBuffer buf, int cols)
        {
            if (buf == null || buf.WordWrap || cols == 0) return;
            int max = MaxScrollLeftColWF(buf);
            if (!SmoothScrollingActiveWF)
            {
                wfHAnim.Finish();
                buf.ScrollLeftCol = Math.Max(0, Math.Min(max, buf.ScrollLeftCol + cols));
                NoteScrollActivityWF();
                Invalidate();
                return;
            }
            AdoptAnimationBufferWF(buf);
            double cur = buf.ScrollLeftCol;
            if (!wfHAnim.IsActive) wfHAnim.Cancel(cur);
            wfHAnim.AddToTarget(cur, cols, Math.Max(10, (CodeAreaRightWF - GutterWidth) / Math.Max(6, CharWidth)), NowMsWF, EditorScrollOptions.SmoothScrollDurationMs);
            wfHAnim.ClampTarget(0, max);
            EnsureAnimTimerWF();
        }

        /// <summary>Animations belong to one buffer: a running one of another tab lands on its target first.</summary>
        private void AdoptAnimationBufferWF(EditorTabBuffer buf)
        {
            if (ReferenceEquals(wfAnimBuf, buf)) return;
            if (wfAnimBuf != null) FinishScrollAnimationsWF(wfAnimBuf);
            wfAnimBuf = buf;
        }

        private void BeginScrollAnimationWF(EditorTabBuffer buf, double targetRow)
        {
            AdoptAnimationBufferWF(buf);
            EditorTabBuffer b = buf;
            int keepLine = b.ScrollTopLine, keepRow = b.ScrollTopWrapRow, keepSubLine = wfSubRowLine;
            double keepFrac = b.ScrollTopFraction;
            SetTopRowExactWF(b, targetRow);
            wfAnimTopLine = b.ScrollTopLine; wfAnimTopRow = b.ScrollTopWrapRow; wfAnimTopFrac = b.ScrollTopFraction; wfAnimTargetRow = targetRow;
            b.ScrollTopLine = keepLine; b.ScrollTopWrapRow = keepRow; wfSubRowLine = keepSubLine; b.ScrollTopFraction = keepFrac;
            wfAnimIndexBuild = RowIndexWF(b).BuildCount;
            RememberAppliedWF(b);
            NoteScrollActivityWF();
            EnsureAnimTimerWF();
        }

        private void RememberAppliedWF(EditorTabBuffer buf)
        {
            wfAppliedLine = buf.ScrollTopLine; wfAppliedRow = buf.ScrollTopWrapRow; wfAppliedFrac = buf.ScrollTopFraction;
        }

        private bool MovedElsewhereWF(EditorTabBuffer buf)
        {
            return buf.ScrollTopLine != wfAppliedLine || buf.ScrollTopWrapRow != wfAppliedRow || Math.Abs(buf.ScrollTopFraction - wfAppliedFrac) > 1e-6;
        }

        private void FinishScrollAnimationsWF(EditorTabBuffer buf)
        {
            if (buf != null && ReferenceEquals(wfAnimBuf, buf))
            {
                if (wfVAnim.IsActive) { buf.ScrollTopLine = wfAnimTopLine; buf.ScrollTopWrapRow = wfAnimTopRow; wfSubRowLine = wfAnimTopLine; buf.ScrollTopFraction = wfAnimTopFrac; }
                if (wfHAnim.IsActive) buf.ScrollLeftCol = (int)Math.Round(wfHAnim.Target);
            }
            wfVAnim.Finish();
            wfHAnim.Finish();
        }

        private void EnsureAnimTimerWF()
        {
            if (wfAnimTimer == null)
            {
                wfAnimTimer = new System.Windows.Forms.Timer();
                wfAnimTimer.Interval = 15; // ~64 Hz (the WinForms timer resolution)
                wfAnimTimer.Tick += delegate { TickAnimationsWF(); };
            }
            if (!wfAnimTimer.Enabled) wfAnimTimer.Start();
        }

        private void TickAnimationsWF()
        {
            if (IsDisposed) { if (wfAnimTimer != null) wfAnimTimer.Stop(); return; }
            EditorTabBuffer buf = owner.CurrentBuffer;
            double now = NowMsWF;
            bool scrolled = false;
            if ((wfVAnim.IsActive || wfHAnim.IsActive) && (buf == null || !ReferenceEquals(buf, wfAnimBuf)))
                FinishScrollAnimationsWF(wfAnimBuf); // tab switched: the old view lands on its target
            if (buf != null && wfVAnim.IsActive)
            {
                if (MovedElsewhereWF(buf)) wfVAnim.Cancel(TopRowExactWF(buf));
                else if (RowIndexWF(buf).BuildCount != wfAnimIndexBuild) FinishScrollAnimationsWF(buf);
                else
                {
                    SetTopRowExactWF(buf, wfVAnim.Sample(now));
                    RememberAppliedWF(buf);
                    wfAnimIndexBuild = RowIndexWF(buf).BuildCount;
                }
                scrolled = true;
            }
            if (buf != null && wfHAnim.IsActive)
            {
                buf.ScrollLeftCol = Math.Max(0, Math.Min(MaxScrollLeftColWF(buf), (int)Math.Round(wfHAnim.Sample(now))));
                scrolled = true;
            }
            if (scrolled)
            {
                NoteScrollActivityWF();
                owner.DismissAutocomplete();
                Invalidate();
            }
            else if (wfCaretMove.IsActive) InvalidateCaretWF();
            if (!wfVAnim.IsActive && !wfHAnim.IsActive && !wfCaretMove.IsActive && wfAnimTimer != null) wfAnimTimer.Stop();
        }

        /// <summary>Mouse wheel / precision touchpad (WM_MOUSEWHEEL; 120 = one notch).</summary>
        private void HandleWheelWF(EditorTabBuffer buf, MouseEventArgs e, bool horizontal)
        {
            double units = e.Delta / 120.0;
            bool fast = (Control.ModifierKeys & Keys.Alt) == Keys.Alt;
            bool precise = wfWheel.IsPrecise(horizontal ? units : 0, horizontal ? 0 : units, NowMsWF);
            if (horizontal)
            {
                if (buf.WordWrap) return;
                double px = -EditorScrollOptions.WheelPixels(units, precise, LineHeight, fast);
                int cols = (int)Math.Round(px / Math.Max(1, CharWidth));
                if (cols == 0) cols = px < 0 ? -1 : 1;
                if (precise || !SmoothScrollingActiveWF) { wfHAnim.Finish(); buf.ScrollLeftCol = Math.Max(0, Math.Min(MaxScrollLeftColWF(buf), buf.ScrollLeftCol + cols)); NoteScrollActivityWF(); Invalidate(); }
                else SmoothScrollColsWF(buf, cols);
                return;
            }
            double dy = -EditorScrollOptions.WheelPixels(units, precise, LineHeight, fast);
            if (precise || !SmoothScrollingActiveWF) { ScrollByPixelsNowWF(buf, dy); Invalidate(); }
            else SmoothScrollRowsWF(buf, dy / Math.Max(1, LineHeight));
        }

        /// <summary>Horizontal touchpad swipes / tilt wheels (WM_MOUSEHWHEEL; WinForms has no event for it).</summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEHWHEEL = 0x020E;
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                EditorTabBuffer buf = owner.CurrentBuffer;
                if (buf != null)
                {
                    long wp = m.WParam.ToInt64();
                    int delta = (short)((wp >> 16) & 0xFFFF);
                    // positive = to the right, the opposite sign of a vertical wheel's "up"
                    HandleWheelWF(buf, new MouseEventArgs(MouseButtons.None, 0, 0, 0, -delta), true);
                    owner.DismissAutocomplete();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ reveal
        /// <summary>
        /// Runs a scroll-into-view computation (against the final position of a running animation) and animates from the
        /// shown position to its result when smooth scrolling is on and <paramref name="animate"/>.
        /// </summary>
        private void RunRevealWF(EditorTabBuffer buf, Action compute, bool animate)
        {
            if (buf == null) { compute(); return; }
            AdoptAnimationBufferWF(buf);
            bool same = true;
            int shownLine = buf.ScrollTopLine, shownRow = buf.ScrollTopWrapRow, shownSub = wfSubRowLine;
            double shownFrac = buf.ScrollTopFraction;
            int shownLeft = buf.ScrollLeftCol;
            bool wasV = same && wfVAnim.IsActive;
            if (wasV) { buf.ScrollTopLine = wfAnimTopLine; buf.ScrollTopWrapRow = wfAnimTopRow; wfSubRowLine = wfAnimTopLine; buf.ScrollTopFraction = wfAnimTopFrac; }
            int prevLine = buf.ScrollTopLine, prevSub = TopSubRowWF(buf);
            double prevFrac = buf.ScrollTopFraction;
            compute();
            bool vNew = buf.ScrollTopLine != prevLine || TopSubRowWF(buf) != prevSub || Math.Abs(buf.ScrollTopFraction - prevFrac) > 1e-6;
            if (!animate || !SmoothScrollingActiveWF)
            {
                wfVAnim.Finish(); wfHAnim.Finish();
                return;
            }
            if (vNew)
            {
                double toRow = TopRowExactWF(buf);
                buf.ScrollTopLine = shownLine; buf.ScrollTopWrapRow = shownRow; wfSubRowLine = shownSub; buf.ScrollTopFraction = shownFrac;
                double fromRow = TopRowExactWF(buf);
                if (Math.Abs(toRow - fromRow) > 1e-6)
                {
                    wfVAnim.Start(fromRow, toRow, ViewportRowsWF, NowMsWF, EditorScrollOptions.SmoothScrollDurationMs);
                    BeginScrollAnimationWF(buf, toRow);
                }
                else SetTopRowExactWF(buf, toRow);
            }
            else if (wasV) { buf.ScrollTopLine = shownLine; buf.ScrollTopWrapRow = shownRow; wfSubRowLine = shownSub; buf.ScrollTopFraction = shownFrac; }
            if (buf.ScrollLeftCol != shownLeft && !buf.WordWrap)
            {
                int target = buf.ScrollLeftCol;
                buf.ScrollLeftCol = shownLeft;
                wfHAnim.Start(shownLeft, target, Math.Max(10, (CodeAreaRightWF - GutterWidth) / Math.Max(6, CharWidth)), NowMsWF, EditorScrollOptions.SmoothScrollDurationMs);
                EnsureAnimTimerWF();
            }
        }

        /// <summary>True right after a text edit (typing / paste / undo): those reveals snap so typing never lags.</summary>
        private bool RevealFollowsEditWF(EditorTabBuffer buf)
        {
            bool edited = !ReferenceEquals(wfRevealBuf, buf) || buf.EditVersion != wfRevealEditVersion;
            wfRevealBuf = buf;
            wfRevealEditVersion = buf.EditVersion;
            return edited;
        }

        /// <summary>
        /// Scrolls (line, col) into view without moving the caret (centred when <paramref name="center"/> and off
        /// screen); animated when smooth scrolling is on. Stable API (undo / redo reveal, go to location).
        /// </summary>
        public void RevealPosition(int line, int col, bool animate, bool center)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines.Count == 0) return;
            line = Math.Max(0, Math.Min(buf.Lines.Count - 1, line));
            col = Math.Max(0, Math.Min((buf.Lines[line] ?? "").Length, col));
            EditorTabBuffer b = buf;
            int l = line, c = col;
            bool ctr = center;
            RunRevealWF(b, delegate { RevealPositionCoreWF(b, l, c, ctr); }, animate);
            Invalidate();
        }

        public void RevealPosition(int line, int col) { RevealPosition(line, col, true, false); }

        /// <summary>Scrolls a range into view (its start wins when it does not fit). See <see cref="RevealPosition(int,int,bool,bool)"/>.</summary>
        public void RevealRange(int startLine, int startCol, int endLine, int endCol, bool animate, bool center)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines.Count == 0) return;
            int sl = Math.Max(0, Math.Min(buf.Lines.Count - 1, startLine)), el = Math.Max(0, Math.Min(buf.Lines.Count - 1, endLine));
            int sc = startCol, ec = endCol;
            if (el < sl || (el == sl && ec < sc)) { int t = sl; sl = el; el = t; t = sc; sc = ec; ec = t; }
            sc = Math.Max(0, Math.Min((buf.Lines[sl] ?? "").Length, sc));
            ec = Math.Max(0, Math.Min((buf.Lines[el] ?? "").Length, ec));
            EditorTabBuffer b = buf;
            bool ctr = center;
            RunRevealWF(b, delegate { RevealPositionCoreWF(b, el, ec, ctr); RevealPositionCoreWF(b, sl, sc, false); }, animate);
            Invalidate();
        }

        public void RevealRange(int startLine, int startCol, int endLine, int endCol) { RevealRange(startLine, startCol, endLine, endCol, true, false); }

        private void RevealPositionCoreWF(EditorTabBuffer buf, int line, int col, bool center)
        {
            int guard = 0, hiddenBy;
            while ((hiddenBy = HiddenByWF(buf, line)) >= 0 && guard++ < 64) buf.FoldedLines.Remove(hiddenBy);
            int visLines = Math.Max(2, VisibleLinesCount);
            EditorVisualRowIndex idx = RowIndexWF(buf);
            int targetRow = idx.RowOfLine(line);
            if (buf.WordWrap) targetRow += WrapOfWF(buf, line).RowOf(col, false);
            double top = TopRowExactWF(buf);
            if (targetRow >= Math.Ceiling(top) && targetRow < Math.Floor(top) + visLines - 1)
            {
                if (targetRow == (int)Math.Floor(top) && top % 1 > 0) SetTopRowExactWF(buf, Math.Floor(top));
            }
            else
            {
                double newTop = center ? targetRow - visLines / 2 : (targetRow < top ? targetRow : targetRow - (visLines - 2));
                SetTopRowExactWF(buf, Math.Max(0, newTop));
            }
            if (buf.WordWrap) return;
            int codeWidthPx = Math.Max(100, this.Width - GutterWidth - (EffectiveShowMinimap ? MinimapWidth : 0) - 20);
            int visCols = Math.Max(10, codeWidthPx / CharWidth);
            int vis = EditorTextLayout.VisualColumn(buf.Lines[line] ?? "", col, TabWF(buf));
            if (vis < buf.ScrollLeftCol) buf.ScrollLeftCol = Math.Max(0, vis - 4);
            else if (vis >= buf.ScrollLeftCol + visCols - 2) buf.ScrollLeftCol = Math.Max(0, vis - visCols + 6);
        }

        // ------------------------------------------------------------------ painting: scene cache + carets
        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            if (!wfCaretOnlyInvalidate) wfSceneDirty = true;
            base.OnInvalidated(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (wfSceneDirty)
            {
                // content changed (scrolling, typing, ...): paint straight to the screen buffer as before; the bitmap
                // copy is only made once a caret-only repaint needs it, so scrolling pays nothing extra
                PaintSceneWF(g);
                wfSceneDirty = false;
                wfSceneValid = false;
            }
            else
            {
                bool blitted = false;
                try
                {
                    int w = Math.Max(1, this.Width), h = Math.Max(1, this.Height);
                    if (wfScene == null || wfScene.Width != w || wfScene.Height != h)
                    {
                        if (wfScene != null) wfScene.Dispose();
                        wfScene = new Bitmap(w, h, PixelFormat.Format32bppRgb); // no alpha: GDI text keeps its pixels opaque
                        wfSceneValid = false;
                    }
                    if (!wfSceneValid)
                    {
                        using (Graphics sg = Graphics.FromImage(wfScene))
                            PaintSceneWF(sg);
                        wfSceneValid = true;
                    }
                    g.DrawImage(wfScene, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
                    blitted = true;
                }
                catch (Exception)
                {
                    wfSceneValid = false; // no memory for the bitmap etc.: paint directly
                }
                if (!blitted) PaintSceneWF(g);
            }
            PaintCaretsWF(g);
        }

        /// <summary>Repaints only the carets (blink / glide): the scene is blitted from the cache.</summary>
        private void InvalidateCaretWF()
        {
            if (IsDisposed) return;
            Rectangle r = CaretBoundsWF();
            Rectangle u = wfLastCaretBounds.IsEmpty ? r : (r.IsEmpty ? wfLastCaretBounds : Rectangle.Union(r, wfLastCaretBounds));
            if (u.IsEmpty) return;
            u.Inflate(3, 3);
            wfCaretOnlyInvalidate = true;
            try { Invalidate(u); }
            finally { wfCaretOnlyInvalidate = false; }
        }

        private Rectangle CaretBoundsWF()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return Rectangle.Empty;
            Point p = GetCursorPixelPoint();
            Rectangle r = new Rectangle(p.X - 1, p.Y - 1, 4, LineHeight);
            double ax, ay;
            if (wfCaretMove.IsActive)
            {
                wfCaretMove.Sample(NowMsWF, out ax, out ay);
                int dx = (int)Math.Round(ax - wfCaretContentX), dy = (int)Math.Round(ay - wfCaretContentY);
                r = Rectangle.Union(r, new Rectangle(p.X + dx - 1, p.Y + dy - 1, 4, LineHeight));
            }
            if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0) r = new Rectangle(GutterWidth, 0, Math.Max(1, CodeAreaRightWF - GutterWidth), this.Height);
            return r;
        }

        /// <summary>Primary + extra carets with the blink opacity / scale and the smooth caret transition.</summary>
        private void PaintCaretsWF(Graphics g)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            wfLastCaretBounds = Rectangle.Empty;
            if (buf == null || buf.Lines == null || !this.Focused) { wfCaretHasLast = false; return; }
            StudioTheme th = owner.Theme;
            int codeAreaRight = CodeAreaRightWF;
            int stickyBottom = (EditorNicetyOptions.StickyScroll && wfLastSticky != null && wfLastSticky.Count > 0) ? wfLastSticky.Count * LineHeight + 4 : 0;
            Rectangle clip = new Rectangle(GutterWidth, stickyBottom, Math.Max(1, codeAreaRight - GutterWidth), Math.Max(1, this.Height - stickyBottom));

            Point p = GetCursorPixelPoint();
            double now = NowMsWF;
            int effLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;
            double contentX = p.X + effLeft * (double)CharWidth;
            double contentY = p.Y + TopRowExactWF(buf) * LineHeight;
            bool moved = buf.CursorLine != wfCaretLine || buf.CursorCol != wfCaretCol;
            bool edited = buf.EditVersion != wfCaretEditVersion || !ReferenceEquals(buf, wfCaretBuf);
            if (wfCaretHasLast && moved && !edited && CaretAnimationsActiveWF && Math.Abs(contentY - wfCaretContentY) <= Math.Max(LineHeight, this.Height))
            {
                double fx, fy;
                wfCaretMove.Sample(now, out fx, out fy);
                if (!wfCaretMove.IsActive) { fx = wfCaretContentX; fy = wfCaretContentY; }
                wfCaretMove.Start(fx, fy, contentX, contentY, now, EditorScrollOptions.CaretAnimationMs);
                EnsureAnimTimerWF();
            }
            else if (moved || edited || !wfCaretMove.IsActive || Math.Abs(wfCaretMove.TargetX - contentX) > 0.5 || Math.Abs(wfCaretMove.TargetY - contentY) > 0.5)
                wfCaretMove.Snap(contentX, contentY);
            wfCaretBuf = buf; wfCaretLine = buf.CursorLine; wfCaretCol = buf.CursorCol; wfCaretEditVersion = buf.EditVersion;
            wfCaretContentX = contentX; wfCaretContentY = contentY; wfCaretHasLast = true;

            double ax, ay;
            wfCaretMove.Sample(now, out ax, out ay);
            bool gliding = wfCaretMove.IsActive;
            int cx = (int)Math.Round(ax - effLeft * (double)CharWidth);
            int cy = (int)Math.Round(ay - TopRowExactWF(buf) * LineHeight);

            double opacity = 1, scaleY = 1;
            if (!gliding && caretTimer != null && caretTimer.Enabled)
                CaretBlink.Evaluate(EditorScrollOptions.CursorBlinking, now - wfBlinkResetMs, out opacity, out scaleY);
            if (opacity <= 0.003 || scaleY <= 0.003) { wfLastCaretBounds = new Rectangle(cx - 1, cy - 1, 4, LineHeight); return; }
            int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(255 * opacity)));
            int hgt = Math.Max(1, (int)Math.Round((LineHeight - 2) * scaleY));
            int yOff = (LineHeight - 2 - hgt) / 2;
            Region prev = g.Clip;
            g.SetClip(clip);
            try
            {
                if (cx >= GutterWidth && cx < codeAreaRight)
                    using (SolidBrush cb = new SolidBrush(Color.FromArgb(alpha, th.AccentPrimary)))
                        g.FillRectangle(cb, cx, cy + yOff, 2, hgt);
                wfLastCaretBounds = new Rectangle(cx - 1, cy - 1, 4, LineHeight);
                if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0)
                    PaintExtraCaretsWF(g, buf, Color.FromArgb(alpha, th.AccentSecondary), hgt, yOff, codeAreaRight);
            }
            finally { g.Clip = prev; }
        }

        private void PaintExtraCaretsWF(Graphics g, EditorTabBuffer buf, Color color, int hgt, int yOff, int codeAreaRight)
        {
            int sub = TopSubPixelsWF(buf);
            int effLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;
            using (SolidBrush cb = new SolidBrush(color))
            {
                for (int r = 0; r < lastPaintedRows.Count; r++)
                {
                    VisualRowEntry seg = lastPaintedRows[r];
                    string text = (seg.DocLine >= 0 && seg.DocLine < buf.Lines.Count) ? (buf.Lines[seg.DocLine] ?? "") : "";
                    foreach (EditorCaret ec in buf.ExtraCarets)
                    {
                        // a caret on a soft break belongs to the next row only
                        if (ec.CursorLine != seg.DocLine || ec.CursorCol < seg.ColStart || !(ec.CursorCol < seg.ColEnd || seg.ColEnd >= text.Length)) continue;
                        int x = GutterWidth + 8 + (SegVcol(text, seg, ec.CursorCol) - effLeft) * CharWidth;
                        if (x < GutterWidth || x >= codeAreaRight) continue;
                        g.FillRectangle(cb, x, r * LineHeight + 4 - sub + yOff, 2, hgt);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ blink
        /// <summary>Any input shows the caret solid and restarts the blink cycle (VS Code).</summary>
        private void RestartCaretBlinkWF()
        {
            wfBlinkResetMs = NowMsWF;
            wfBlinkFinished = false;
            if (caretTimer == null) return;
            if (this.Focused && CaretBlink.Animates(EditorScrollOptions.CursorBlinking))
            {
                caretTimer.Stop();
                caretTimer.Interval = (int)CaretBlink.IntervalMs;
                caretTimer.Start();
            }
            else caretTimer.Stop();
            InvalidateCaretWF();
        }

        private void OnCaretTimerTickWF()
        {
            if (!this.Focused || IsDisposed) { caretTimer.Stop(); return; }
            InvalidateCaretWF();
            double next = CaretBlink.NextChangeMs(EditorScrollOptions.CursorBlinking, NowMsWF - wfBlinkResetMs);
            if (next < 0) { wfBlinkFinished = true; caretTimer.Stop(); return; } // solid until the next input
            int ms = next <= 0 ? 16 : (int)Math.Max(16, Math.Min(1000, next));
            if (caretTimer.Interval != ms) caretTimer.Interval = ms;
        }

        private void DisposeSmoothWF()
        {
            if (wfAnimTimer != null) { try { wfAnimTimer.Stop(); wfAnimTimer.Dispose(); } catch { } wfAnimTimer = null; }
            if (wfScene != null) { try { wfScene.Dispose(); } catch { } wfScene = null; }
        }

        /// <summary>Re-reads the caret / scrolling settings after they changed.</summary>
        public void ApplyCaretSettings()
        {
            RestartCaretBlinkWF();
            Invalidate();
        }
    }
}
