using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Smooth scrolling for the Avalonia editor (VS Code editor.smoothScrolling): the vertical position is fractional
    /// (a whole visual row plus <see cref="EditorTabBuffer.ScrollTopFraction"/>), the horizontal one exact pixels.
    /// Trackpad / momentum deltas apply 1:1; mouse-wheel notches, paging, Ctrl+Up/Down, scrollbar track clicks and
    /// reveals (go to line / definition, find next, undo, caret moves past the edge) animate for 125 ms with VS Code's
    /// ease-out curve, driven by the compositor's animation frames (TopLevel.RequestAnimationFrame: the display's
    /// refresh rate, 120 Hz on ProMotion), retargeting when new input arrives. Shared math: codeLess.Core
    /// EditorSmoothScrolling.cs (also used by the WinForms editor).
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        internal struct ScrollPos
        {
            public int Line, Row;
            public double Frac;
            public bool SameAs(ScrollPos o) => Line == o.Line && Row == o.Row && Math.Abs(Frac - o.Frac) < 1e-6;
        }

        private static readonly Stopwatch animationStopwatch = Stopwatch.StartNew();
        private readonly SmoothScrollAnimation vAnim = new SmoothScrollAnimation();
        private readonly SmoothScrollAnimation hAnim = new SmoothScrollAnimation();
        private readonly WheelScrollClassifier wheelClassifier = new WheelScrollClassifier();
        private EditorTabBuffer animBuf;
        private ScrollPos animTargetPos, lastAppliedPos;
        private double lastAppliedLeft;
        private int animIndexBuild;
        private bool animationFramePending;
        private int lastRevealEditVersion = int.MinValue;
        private EditorTabBuffer lastRevealBuf;

        /// <summary>Time source of the animations in ms (tests replace it with a fake clock).</summary>
        public Func<double> AnimationClock { get; set; }
        /// <summary>Forces animations on / off (null: on in the desktop app, off in headless tests unless set).</summary>
        public bool? AnimationsOverride { get; set; }

        internal double NowMs => AnimationClock != null ? AnimationClock() : animationStopwatch.Elapsed.TotalMilliseconds;
        internal bool AnimationsAllowed => AnimationsOverride ?? AvaloniaTerminalEmulatorControl.IsDesktopRuntime;
        private bool SmoothScrollingActive => EditorScrollOptions.SmoothScrolling && AnimationsAllowed;

        public bool IsScrollAnimating => vAnim.IsActive || hAnim.IsActive;
        /// <summary>Fractional top visual row being shown (tests / screenshot suite).</summary>
        public double TopRowExactForTest => Buffer == null ? 0 : TopRowExact(Buffer);
        /// <summary>Visual row the running vertical animation ends at (the current row when idle).</summary>
        public double ScrollTargetRow => Buffer == null ? 0 : (vAnim.IsActive ? vAnim.Target : TopRowExact(Buffer));

        // ---------------------------------------------------------------- positions
        private static ScrollPos GetPos(EditorTabBuffer b) => new ScrollPos { Line = b.ScrollTopLine, Row = b.ScrollTopWrapRow, Frac = b.ScrollTopFraction };

        private static void SetPos(EditorTabBuffer b, ScrollPos p)
        {
            b.ScrollTopLine = p.Line;
            b.ScrollTopWrapRow = p.Row;
            b.ScrollTopFraction = p.Frac;
        }

        /// <summary>Device-pixel snapped offset of the first painted row above the viewport top (0 at whole rows).</summary>
        internal double TopRowOffsetPx
        {
            get
            {
                var b = Buffer;
                if (b == null) return 0;
                double f = b.ScrollTopFraction;
                if (f <= 0) return 0;
                double s = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
                if (s <= 0) s = 1.0;
                return Math.Round(f * LineHeight * s) / s;
            }
        }

        /// <summary>The fractional visual row at the top of the viewport (wrap, folds and deleted zones included).</summary>
        internal double TopRowExact(EditorTabBuffer b)
        {
            NormalizeScroll(b);
            var idx = RowIndexFor(b);
            return idx.RowOfLine(b.ScrollTopLine) + b.ScrollTopWrapRow + b.ScrollTopFraction;
        }

        /// <summary>Last scroll position: the last row at the top (VS Code scrollBeyondLastLine).</summary>
        internal double MaxTopRow(EditorTabBuffer b) => Math.Max(0, RowIndexFor(b).TotalRows - 1);

        internal void SetTopRowExact(EditorTabBuffer b, double row)
        {
            var idx = RowIndexFor(b);
            double max = Math.Max(0, idx.TotalRows - 1);
            if (double.IsNaN(row)) row = 0;
            row = Math.Max(0, Math.Min(max, row));
            int whole = (int)Math.Floor(row + 1e-9);
            double frac = row - whole;
            if (frac < 1e-6 || whole >= max) frac = 0;
            int line = idx.LineOfRow(whole, out int sub);
            b.ScrollTopLine = line;
            b.ScrollTopWrapRow = sub;
            NormalizeScroll(b);
            b.ScrollTopFraction = frac;
        }

        private double ViewportRows => Math.Max(1, Bounds.Height / Math.Max(1, LineHeight));
        private double ViewportWidthPx => Math.Max(1, CodeAreaRight - GutterWidth - 8);

        // ---------------------------------------------------------------- immediate scrolling (trackpad, drag)
        /// <summary>Scrolls by pixels right away (precise deltas); a running animation moves along so both add up.</summary>
        internal void ScrollByPixelsNow(EditorTabBuffer b, double dy)
        {
            if (b == null || Math.Abs(dy) < 1e-9) return;
            double rows = dy / Math.Max(1, LineHeight);
            double max = MaxTopRow(b);
            if (vAnim.IsActive && ReferenceEquals(animBuf, b))
            {
                vAnim.ShiftBy(rows);
                vAnim.ClampTarget(0, max);
            }
            SetTopRowExact(b, TopRowExact(b) + rows);
            if (vAnim.IsActive && ReferenceEquals(animBuf, b)) { lastAppliedPos = GetPos(b); animTargetPos = PosOfRow(b, vAnim.Target); }
        }

        internal void ScrollLeftByPixelsNow(EditorTabBuffer b, double dx)
        {
            if (b == null || b.WordWrap || Math.Abs(dx) < 1e-9) return;
            double max = MaxScrollLeftPx(b);
            if (hAnim.IsActive && ReferenceEquals(animBuf, b)) { hAnim.ShiftBy(dx); hAnim.ClampTarget(0, max); }
            b.ScrollLeftPx = Math.Max(0, Math.Min(max, b.ScrollLeftPx + dx));
            lastAppliedLeft = b.ScrollLeftPx;
        }

        private ScrollPos PosOfRow(EditorTabBuffer b, double row)
        {
            var keep = GetPos(b);
            SetTopRowExact(b, row);
            var p = GetPos(b);
            SetPos(b, keep);
            return p;
        }

        /// <summary>Scrolls by a trackpad-like pixel amount without animation (tests / benchmarks).</summary>
        public void ScrollByPixelsForTest(double dy)
        {
            if (Buffer == null) return;
            ScrollByPixelsNow(Buffer, dy);
            InvalidateVisual();
        }

        // ---------------------------------------------------------------- animated scrolling
        /// <summary>Scrolls by visual rows, animated when smooth scrolling is on (wheel notches, Ctrl+Up/Down, paging).</summary>
        internal void SmoothScrollRowsBy(EditorTabBuffer b, double rows)
        {
            if (b == null || Math.Abs(rows) < 1e-9) return;
            if (!SmoothScrollingActive || TopLevel.GetTopLevel(this) == null && AnimationClock == null)
            {
                FinishScrollAnimations(b);
                SetTopRowExact(b, TopRowExact(b) + rows);
                NoteScrollActivity();
                InvalidateVisual();
                return;
            }
            AdoptAnimationBuffer(b);
            double cur = TopRowExact(b);
            double max = MaxTopRow(b);
            if (!vAnim.IsActive) vAnim.Cancel(cur);
            vAnim.AddToTarget(cur, rows, ViewportRows, NowMs, EditorScrollOptions.SmoothScrollDurationMs);
            vAnim.ClampTarget(0, max);
            BeginAnimation(b);
            animTargetPos = PosOfRow(b, vAnim.Target);
        }

        /// <summary>Scrolls so the given fractional row is at the top, animated when smooth scrolling is on.</summary>
        internal void AnimateTopRowTo(EditorTabBuffer b, double row, bool animate = true)
        {
            if (b == null) return;
            row = Math.Max(0, Math.Min(MaxTopRow(b), row));
            if (!animate || !SmoothScrollingActive || TopLevel.GetTopLevel(this) == null && AnimationClock == null)
            {
                FinishScrollAnimations(b);
                SetTopRowExact(b, row);
                NoteScrollActivity();
                InvalidateVisual();
                return;
            }
            AdoptAnimationBuffer(b);
            double cur = TopRowExact(b);
            vAnim.Start(cur, row, ViewportRows, NowMs, EditorScrollOptions.SmoothScrollDurationMs);
            BeginAnimation(b);
            animTargetPos = PosOfRow(b, row);
        }

        internal void SmoothScrollLeftBy(EditorTabBuffer b, double dx)
        {
            if (b == null || b.WordWrap || Math.Abs(dx) < 1e-9) return;
            double max = MaxScrollLeftPx(b);
            if (!SmoothScrollingActive || TopLevel.GetTopLevel(this) == null && AnimationClock == null)
            {
                hAnim.Finish();
                b.ScrollLeftPx = Math.Max(0, Math.Min(max, b.ScrollLeftPx + dx));
                NoteScrollActivity();
                InvalidateVisual();
                return;
            }
            AdoptAnimationBuffer(b);
            double cur = b.ScrollLeftPx;
            if (!hAnim.IsActive) hAnim.Cancel(cur);
            hAnim.AddToTarget(cur, dx, ViewportWidthPx, NowMs, EditorScrollOptions.SmoothScrollDurationMs);
            hAnim.ClampTarget(0, max);
            BeginAnimation(b);
        }

        /// <summary>Animations belong to one buffer: a running one of another tab lands on its target first.</summary>
        private void AdoptAnimationBuffer(EditorTabBuffer b)
        {
            if (ReferenceEquals(animBuf, b)) return;
            if (animBuf != null) FinishScrollAnimations(animBuf);
            animBuf = b;
        }

        private void BeginAnimation(EditorTabBuffer b)
        {
            if (!ReferenceEquals(animBuf, b))
            {
                if (animBuf != null) FinishScrollAnimations(animBuf);
                animBuf = b;
            }
            animIndexBuild = RowIndexFor(b).BuildCount;
            lastAppliedPos = GetPos(b);
            lastAppliedLeft = b.ScrollLeftPx;
            NoteScrollActivity();
            RequestAnimationTick();
        }

        /// <summary>Jumps to the end of any running scroll animation.</summary>
        internal void FinishScrollAnimations(EditorTabBuffer b)
        {
            if (b != null && ReferenceEquals(animBuf, b))
            {
                if (vAnim.IsActive) SetPos(b, animTargetPos);
                if (hAnim.IsActive) b.ScrollLeftPx = hAnim.Target;
            }
            vAnim.Finish();
            hAnim.Finish();
        }

        private void RequestAnimationTick()
        {
            if (animationFramePending) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return; // tests drive AdvanceAnimationsForTest
            animationFramePending = true;
            top.RequestAnimationFrame(_ =>
            {
                animationFramePending = false;
                TickAnimations();
            });
        }

        /// <summary>Advances the scroll and caret animations to <see cref="NowMs"/> (tests; the app uses animation frames).</summary>
        public void AdvanceAnimationsForTest() => TickAnimations();

        private void TickAnimations()
        {
            TickScrollAnimations();
            if (caretOverlay != null && caretOverlay.NeedsFrames) caretOverlay.InvalidateVisual();
        }

        private void TickScrollAnimations()
        {
            var b = Buffer;
            if (!vAnim.IsActive && !hAnim.IsActive) return;
            if (b == null || !ReferenceEquals(b, animBuf)) { FinishScrollAnimations(animBuf); return; } // tab switched: the old view lands on its target
            double now = NowMs;
            if (vAnim.IsActive)
            {
                if (!GetPos(b).SameAs(lastAppliedPos)) vAnim.Cancel(TopRowExact(b)); // moved by something else (typing, a direct jump)
                else if (RowIndexFor(b).BuildCount != animIndexBuild) { SetPos(b, animTargetPos); vAnim.Finish(); } // edit / fold: rows changed
                else
                {
                    SetTopRowExact(b, vAnim.Sample(now));
                    lastAppliedPos = GetPos(b);
                    animIndexBuild = RowIndexFor(b).BuildCount;
                }
            }
            if (hAnim.IsActive)
            {
                if (Math.Abs(b.ScrollLeftPx - lastAppliedLeft) > 0.01 || b.WordWrap) hAnim.Cancel(b.ScrollLeftPx);
                else
                {
                    b.ScrollLeftPx = Math.Max(0, Math.Min(MaxScrollLeftPx(b), hAnim.Sample(now)));
                    lastAppliedLeft = b.ScrollLeftPx;
                }
            }
            NoteScrollActivity();
            InvalidateVisual();
            if (vAnim.IsActive || hAnim.IsActive) RequestAnimationTick();
        }

        // ---------------------------------------------------------------- reveal (shared by every "scroll into view")
        /// <summary>
        /// Runs a scroll-into-view computation and animates from the shown position to its result. The computation sees
        /// the final position of a running animation (VS Code reveals against the future scroll position).
        /// </summary>
        internal void RunReveal(EditorTabBuffer b, Action compute, bool animate)
        {
            if (b == null) { compute(); return; }
            AdoptAnimationBuffer(b);
            bool same = true;
            var shown = GetPos(b);
            double shownLeft = b.ScrollLeftPx;
            bool wasV = same && vAnim.IsActive, wasH = same && hAnim.IsActive;
            var prevTarget = wasV ? animTargetPos : shown;
            double prevLeft = wasH ? hAnim.Target : shownLeft;
            if (wasV) SetPos(b, animTargetPos);
            if (wasH) b.ScrollLeftPx = hAnim.Target;
            compute();
            var target = GetPos(b);
            double targetLeft = b.ScrollLeftPx;
            bool smooth = animate && SmoothScrollingActive && (TopLevel.GetTopLevel(this) != null || AnimationClock != null);
            if (!smooth)
            {
                vAnim.Finish();
                hAnim.Finish();
                return; // the computed position stays
            }
            bool vNew = !target.SameAs(prevTarget);
            bool hNew = Math.Abs(targetLeft - prevLeft) > 0.01;
            double now = NowMs;
            if (vNew)
            {
                double toRow = TopRowExact(b);
                SetPos(b, shown);
                double fromRow = TopRowExact(b);
                if (Math.Abs(toRow - fromRow) > 1e-6)
                {
                    vAnim.Start(fromRow, toRow, ViewportRows, now, EditorScrollOptions.SmoothScrollDurationMs);
                    animTargetPos = target;
                }
                else { SetPos(b, target); vAnim.Finish(); }
            }
            else if (wasV) SetPos(b, shown); // same destination: the running animation continues
            if (hNew)
            {
                b.ScrollLeftPx = shownLeft;
                if (Math.Abs(targetLeft - shownLeft) > 0.01) hAnim.Start(shownLeft, targetLeft, ViewportWidthPx, now, EditorScrollOptions.SmoothScrollDurationMs);
                else { b.ScrollLeftPx = targetLeft; hAnim.Finish(); }
            }
            else if (wasH) b.ScrollLeftPx = shownLeft;
            if (vAnim.IsActive || hAnim.IsActive)
            {
                if (!ReferenceEquals(animBuf, b)) animBuf = b;
                animIndexBuild = RowIndexFor(b).BuildCount;
                lastAppliedPos = GetPos(b);
                lastAppliedLeft = b.ScrollLeftPx;
                NoteScrollActivity();
                RequestAnimationTick();
            }
        }

        /// <summary>
        /// True when the caret reveal at hand follows a text edit (typing, paste, undo): those snap so typing never lags
        /// behind an animation; navigation reveals animate.
        /// </summary>
        private bool RevealFollowsEdit(EditorTabBuffer b)
        {
            bool edited = !ReferenceEquals(lastRevealBuf, b) || b.EditVersion != lastRevealEditVersion;
            lastRevealBuf = b;
            lastRevealEditVersion = b.EditVersion;
            return edited;
        }

        /// <summary>
        /// Scrolls (line, col) into view without moving the caret: vertically with minimal movement (or centred when
        /// <paramref name="center"/> and it is off screen), horizontally when not wrapping. Animated when smooth
        /// scrolling is on and <paramref name="animate"/>. Stable API (undo / redo reveal, go to location).
        /// </summary>
        public void RevealPosition(int line, int col, bool animate = true, bool center = false)
        {
            var b = Buffer;
            if (b == null || b.Lines.Count == 0) return;
            line = Math.Max(0, Math.Min(b.Lines.Count - 1, line));
            col = Math.Max(0, Math.Min((b.Lines[line] ?? "").Length, col));
            RunReveal(b, () => RevealPositionCore(b, line, col, center), animate);
            InvalidateVisual();
        }

        /// <summary>Scrolls a range into view (its start wins when it is taller than the viewport). See <see cref="RevealPosition"/>.</summary>
        public void RevealRange(int startLine, int startCol, int endLine, int endCol, bool animate = true, bool center = false)
        {
            var b = Buffer;
            if (b == null || b.Lines.Count == 0) return;
            startLine = Math.Max(0, Math.Min(b.Lines.Count - 1, startLine));
            endLine = Math.Max(0, Math.Min(b.Lines.Count - 1, endLine));
            if (endLine < startLine || (endLine == startLine && endCol < startCol)) { int t = startLine; startLine = endLine; endLine = t; t = startCol; startCol = endCol; endCol = t; }
            int sc = Math.Max(0, Math.Min((b.Lines[startLine] ?? "").Length, startCol));
            int ec = Math.Max(0, Math.Min((b.Lines[endLine] ?? "").Length, endCol));
            RunReveal(b, () =>
            {
                RevealPositionCore(b, endLine, ec, center);
                RevealPositionCore(b, startLine, sc, false); // the start stays visible when both do not fit
            }, animate);
            InvalidateVisual();
        }

        private void RevealPositionCore(EditorTabBuffer b, int line, int col, bool center)
        {
            int h;
            int guard = 0;
            while (b.FoldedBlockRanges.Count > 0 && (h = EditorFoldState.HiddenBy(b, line)) >= 0 && guard++ < 64) b.FoldedBlockRanges.Remove(h);
            int row = CaretRowOf(b, line, col, false);
            EnsureRowVisible(b, line, row, center);
            if (b.WordWrap || Bounds.Width <= GutterWidth + 40) return;
            double x = ColX(b, line, col);
            double viewW = CodeAreaRight - GutterWidth - 24;
            if (x < b.ScrollLeftPx) b.ScrollLeftPx = Math.Max(0, x - CharWidth * 4);
            else if (x > b.ScrollLeftPx + viewW) b.ScrollLeftPx = x - viewW + CharWidth * 4;
        }

        /// <summary>Keeps (line, row) on screen in rows (wrap / fold / zone aware); centred when requested and off screen.</summary>
        private void EnsureRowVisible(EditorTabBuffer b, int line, int row, bool center)
        {
            int visRows = Bounds.Height > 0 ? Math.Max(1, (int)(Bounds.Height / LineHeight) - 1) : 30;
            NormalizeScroll(b);
            int dist = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, line, row, visRows + 1);
            if (dist >= 0 && dist < visRows)
            {
                if (dist == 0 && b.ScrollTopFraction > 0) b.ScrollTopFraction = 0; // partly scrolled out at the top
                return;
            }
            int l = line, r = row;
            int back = center ? visRows / 2 : (dist < 0 ? 0 : visRows - 1);
            StepRows(b, ref l, ref r, -back, includeZones: true);
            b.ScrollTopLine = l;
            b.ScrollTopWrapRow = r;
        }

        // ---------------------------------------------------------------- wheel
        private void HandleWheelScroll(EditorTabBuffer buf, PointerWheelEventArgs e)
        {
            double dy = e.Delta.Y;
            double dx = e.Delta.X;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && Math.Abs(dx) < 0.001) { dx = dy; dy = 0; } // Shift+wheel scrolls horizontally
            bool fast = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool precise = wheelClassifier.IsPrecise(dx, dy, NowMs);
            if (Math.Abs(dy) > 1e-6)
            {
                double px = -EditorScrollOptions.WheelPixels(dy, precise, LineHeight, fast);
                if (precise || !SmoothScrollingActive) ScrollByPixelsNow(buf, px);
                else SmoothScrollRowsBy(buf, px / Math.Max(1, LineHeight));
                NoteScrollActivity();
            }
            if (Math.Abs(dx) > 1e-6 && !buf.WordWrap) // no horizontal scrolling while wrapping (VS Code)
            {
                double px = -EditorScrollOptions.WheelPixels(dx, precise, LineHeight, fast);
                if (precise || !SmoothScrollingActive) ScrollLeftByPixelsNow(buf, px);
                else SmoothScrollLeftBy(buf, px);
                NoteScrollActivity();
            }
        }

        /// <summary>Feeds a wheel event through the same path as the pointer (tests).</summary>
        public void SimulateWheelForTest(double dx, double dy, KeyModifiers mods = KeyModifiers.None)
        {
            var b = Buffer;
            if (b == null) return;
            var args = new PointerWheelEventArgs(this, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), this, default, 0,
                new PointerPointProperties(), mods, new Vector(dx, dy)) { RoutedEvent = PointerWheelChangedEvent };
            RaiseEvent(args);
        }
    }
}
