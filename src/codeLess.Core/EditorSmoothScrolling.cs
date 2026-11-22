using System;

namespace codeLess.Studio
{
    /// <summary>
    /// Runtime switches for smooth scrolling and the caret (mirrors the persisted settings, read by both editors):
    /// VS Code's editor.smoothScrolling, editor.cursorSmoothCaretAnimation, editor.cursorBlinking,
    /// editor.mouseWheelScrollSensitivity and editor.fastScrollSensitivity. STRICT C# 5.
    /// </summary>
    public static class EditorScrollOptions
    {
        /// <summary>Animate scrolling caused by discrete input (mouse-wheel notches, PageUp / PageDown, reveal, scrollbar clicks).</summary>
        public static bool SmoothScrolling = true;
        /// <summary>Animate caret moves made by navigation (keyboard / mouse); typing always snaps.</summary>
        public static bool SmoothCaretAnimation = true;
        /// <summary>blink / smooth / phase / expand / solid.</summary>
        public static string CursorBlinking = CaretBlink.Smooth;
        public static double MouseWheelScrollSensitivity = 1.0;
        /// <summary>Multiplier while Alt / Option is held (VS Code editor.fastScrollSensitivity).</summary>
        public static double FastScrollSensitivity = 5.0;

        /// <summary>VS Code's smooth scroll duration (ms).</summary>
        public const double SmoothScrollDurationMs = 125;
        /// <summary>VS Code's smooth caret transition (ms).</summary>
        public const double CaretAnimationMs = 80;

        public static void ApplyFrom(IdePersistentSettings s)
        {
            if (s == null) return;
            SmoothScrolling = s.EditorSmoothScrolling;
            SmoothCaretAnimation = s.EditorSmoothCaretAnimation;
            CursorBlinking = CaretBlink.Normalize(s.EditorCursorBlinking);
            MouseWheelScrollSensitivity = ClampSensitivity(s.EditorMouseWheelScrollSensitivity, 1.0);
            FastScrollSensitivity = ClampSensitivity(s.EditorFastScrollSensitivity, 5.0);
        }

        public static void CopyTo(IdePersistentSettings s)
        {
            if (s == null) return;
            s.EditorSmoothScrolling = SmoothScrolling;
            s.EditorSmoothCaretAnimation = SmoothCaretAnimation;
            s.EditorCursorBlinking = CaretBlink.Normalize(CursorBlinking);
            s.EditorMouseWheelScrollSensitivity = MouseWheelScrollSensitivity;
            s.EditorFastScrollSensitivity = FastScrollSensitivity;
        }

        /// <summary>Sensitivities outside (0, 20] (or unparsable) fall back to the default.</summary>
        public static double ClampSensitivity(double v, double fallback)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return fallback;
            return Math.Min(20.0, v);
        }

        /// <summary>Pixels to scroll for a wheel delta (Avalonia / WPF units: one notch = 1.0, macOS precise = points / 50).</summary>
        public static double WheelPixels(double delta, bool precise, double lineHeight, bool fast)
        {
            double perUnit = precise ? 50.0 : 3.0 * Math.Max(1.0, lineHeight);
            return delta * perUnit * MouseWheelScrollSensitivity * (fast ? FastScrollSensitivity : 1.0);
        }
    }

    /// <summary>Persisted smooth-scrolling / caret settings (absent keys keep the defaults when loading old files).</summary>
    public partial class IdePersistentSettings
    {
        private bool editorSmoothScrolling = true;
        private bool editorSmoothCaretAnimation = true;
        private string editorCursorBlinking = CaretBlink.Smooth;
        private double editorMouseWheelScrollSensitivity = 1.0;
        private double editorFastScrollSensitivity = 5.0;

        public bool EditorSmoothScrolling { get { return editorSmoothScrolling; } set { editorSmoothScrolling = value; } }
        public bool EditorSmoothCaretAnimation { get { return editorSmoothCaretAnimation; } set { editorSmoothCaretAnimation = value; } }
        public string EditorCursorBlinking { get { return editorCursorBlinking; } set { editorCursorBlinking = CaretBlink.Normalize(value); } }
        public double EditorMouseWheelScrollSensitivity { get { return editorMouseWheelScrollSensitivity; } set { editorMouseWheelScrollSensitivity = value; } }
        public double EditorFastScrollSensitivity { get { return editorFastScrollSensitivity; } set { editorFastScrollSensitivity = value; } }
    }

    /// <summary>Timing curves used by the editor animations (CSS timing functions, VS Code's easeOutCubic).</summary>
    public static class EditorEasing
    {
        public static double EaseOutCubic(double t)
        {
            t = Clamp01(t);
            double u = 1 - t;
            return 1 - u * u * u;
        }

        /// <summary>CSS cubic-bezier(x1, y1, x2, y2) evaluated at progress <paramref name="x"/>.</summary>
        public static double CubicBezier(double x1, double y1, double x2, double y2, double x)
        {
            x = Clamp01(x);
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            // solve bx(t) = x (Newton, then bisection as a fallback)
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double err = Bez(x1, x2, t) - x;
                if (Math.Abs(err) < 1e-6) return Bez(y1, y2, t);
                double d = BezDeriv(x1, x2, t);
                if (Math.Abs(d) < 1e-6) break;
                t -= err / d;
            }
            double lo = 0, hi = 1;
            t = x;
            for (int i = 0; i < 40; i++)
            {
                double v = Bez(x1, x2, t);
                if (Math.Abs(v - x) < 1e-6) break;
                if (v < x) lo = t; else hi = t;
                t = (lo + hi) / 2;
            }
            return Bez(y1, y2, t);
        }

        /// <summary>CSS "ease-in-out".</summary>
        public static double EaseInOut(double t) { return CubicBezier(0.42, 0, 0.58, 1, t); }
        /// <summary>CSS "ease" (the default of a CSS transition, used by VS Code's smooth caret).</summary>
        public static double Ease(double t) { return CubicBezier(0.25, 0.1, 0.25, 1, t); }

        public static double Clamp01(double t) { return t < 0 ? 0 : (t > 1 ? 1 : t); }

        private static double Bez(double p1, double p2, double t)
        {
            double u = 1 - t;
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
        }

        private static double BezDeriv(double p1, double p2, double t)
        {
            double u = 1 - t;
            return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
        }
    }

    /// <summary>
    /// One smooth scroll along an axis, like VS Code's SmoothScrollingOperation: an ease-out-cubic curve over
    /// <see cref="EditorScrollOptions.SmoothScrollDurationMs"/>; a jump of more than 2.5 viewports animates only the last
    /// and first 0.75 viewport (a composed curve) so far reveals stay quick. New input mid-animation retargets from the
    /// current position; precise (trackpad) deltas shift the whole animation so they stay 1:1. Units are the caller's
    /// (visual rows or pixels). STRICT C# 5.
    /// </summary>
    public sealed class SmoothScrollAnimation
    {
        private double from, to, startMs, durationMs, stop1, stop2;
        private bool far, active;

        public bool IsActive { get { return active; } }
        public double From { get { return from; } }
        /// <summary>Final position of the running animation (the current position when idle).</summary>
        public double Target { get { return to; } }

        /// <summary>Starts (or restarts) an animation from <paramref name="current"/> to <paramref name="target"/>.</summary>
        public void Start(double current, double target, double viewport, double nowMs, double duration)
        {
            from = current;
            to = target;
            startMs = nowMs;
            durationMs = Math.Max(1, duration);
            active = Math.Abs(target - current) > 1e-9;
            double vp = Math.Max(1e-6, viewport);
            far = Math.Abs(to - from) > 2.5 * vp;
            if (far)
            {
                if (from < to) { stop1 = from + 0.75 * vp; stop2 = to - 0.75 * vp; }
                else { stop1 = from - 0.75 * vp; stop2 = to + 0.75 * vp; }
            }
        }

        /// <summary>
        /// Moves the target by <paramref name="delta"/>: from the current target when animating (wheel notches add up),
        /// from <paramref name="current"/> otherwise.
        /// </summary>
        public void AddToTarget(double current, double delta, double viewport, double nowMs, double duration)
        {
            double baseTarget = active ? to : current;
            double cur = active ? Sample(nowMs) : current;
            Start(cur, baseTarget + delta, viewport, nowMs, duration);
        }

        /// <summary>Shifts start, stops and target (a precise delta applied 1:1 while an animation runs).</summary>
        public void ShiftBy(double delta)
        {
            from += delta; to += delta; stop1 += delta; stop2 += delta;
        }

        /// <summary>Clamps the target into [min, max] (document end reached while animating).</summary>
        public void ClampTarget(double min, double max)
        {
            if (to < min) to = min;
            if (to > max) to = max;
        }

        /// <summary>Position at <paramref name="nowMs"/>; the animation ends (inactive) once the duration elapsed.</summary>
        public double Sample(double nowMs)
        {
            if (!active) return to;
            double t = (nowMs - startMs) / durationMs;
            if (t >= 1) { active = false; return to; }
            if (t <= 0) return from;
            if (!far) return from + (to - from) * EditorEasing.EaseOutCubic(t);
            const double cut = 0.33;
            if (t < cut) return from + (stop1 - from) * EditorEasing.EaseOutCubic(t / cut);
            return stop2 + (to - stop2) * EditorEasing.EaseOutCubic((t - cut) / (1 - cut));
        }

        /// <summary>Stops at the target.</summary>
        public void Finish() { active = false; }

        /// <summary>Stops where it is (another input took over the position).</summary>
        public void Cancel(double current) { active = false; from = to = current; }
    }

    /// <summary>
    /// Tells precise scrolling (trackpad, momentum, free-spinning wheels) from discrete mouse-wheel notches with only
    /// the delta and the time between events (Avalonia does not expose NSEvent.hasPreciseScrollingDeltas): a stream
    /// of events closer than <see cref="StreamGapMs"/> is applied 1:1; an isolated event or a whole-notch delta
    /// (Windows / X11 report 1.0 per notch) is animated. Animated and precise deltas compose, so a gesture that starts
    /// with an animated first event stays continuous. STRICT C# 5.
    /// </summary>
    public sealed class WheelScrollClassifier
    {
        public const double StreamGapMs = 50;
        private double lastMs = double.NegativeInfinity;
        private bool lastWasNotch;

        /// <summary>True when the event should be applied directly (no animation).</summary>
        public bool IsPrecise(double dx, double dy, double nowMs)
        {
            double gap = nowMs - lastMs;
            lastMs = nowMs;
            double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            bool notch = m >= 0.999 && IsAlmostInt(dx) && IsAlmostInt(dy);
            bool streaming = gap >= 0 && gap < StreamGapMs;
            bool wasNotch = lastWasNotch;
            lastWasNotch = notch;
            if (notch && (!streaming || wasNotch)) return false; // a mouse wheel, even when spun fast
            return streaming;
        }

        public void Reset() { lastMs = double.NegativeInfinity; lastWasNotch = false; }

        private static bool IsAlmostInt(double v) { return Math.Abs(v - Math.Round(v)) < 0.001; }
    }

    /// <summary>
    /// Caret blinking like VS Code's editor.cursorBlinking: the caret stays solid for 500 ms after any input, then
    /// "blink" toggles every 500 ms; "smooth" / "phase" fade (CSS keyframes, 0.5 s ease-in-out, alternate) and
    /// "expand" scales the caret vertically; these stop (solid caret) after 20 iterations like VS Code's CSS animation,
    /// so an idle editor stops repainting. "solid" never blinks. STRICT C# 5.
    /// </summary>
    public static class CaretBlink
    {
        public const string Blink = "blink", Smooth = "smooth", Phase = "phase", Expand = "expand", Solid = "solid";
        public const double IntervalMs = 500;
        public const int AnimatedIterations = 20;
        public static readonly string[] Styles = new string[] { Blink, Smooth, Phase, Expand, Solid };

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return Smooth;
            string v = s.Trim().ToLowerInvariant();
            for (int i = 0; i < Styles.Length; i++) if (Styles[i] == v) return v;
            return Smooth;
        }

        /// <summary>True when the style ever changes the caret's appearance.</summary>
        public static bool Animates(string style) { return Normalize(style) != Solid; }

        /// <summary>Opacity (0..1) and vertical scale (0..1) of the caret <paramref name="msSinceReset"/> after the last input.</summary>
        public static void Evaluate(string style, double msSinceReset, out double opacity, out double scaleY)
        {
            opacity = 1; scaleY = 1;
            style = Normalize(style);
            if (style == Solid || msSinceReset < IntervalMs) return;
            double u = msSinceReset - IntervalMs;
            long iter = (long)Math.Floor(u / IntervalMs);
            if (style == Blink)
            {
                opacity = (iter % 2 == 0) ? 0 : 1; // the first toggle hides it
                return;
            }
            if (iter >= AnimatedIterations) return; // the CSS animation ran out: solid
            double p = (u - iter * IntervalMs) / IntervalMs;
            if (iter % 2 == 1) p = 1 - p; // alternate
            if (style == Smooth) opacity = 1 - Segment(p, 0.2, 0.6);
            else if (style == Phase) opacity = 1 - Segment(p, 0.2, 0.9);
            else scaleY = 1 - Segment(p, 0.2, 0.8);
        }

        /// <summary>Milliseconds until the appearance next changes (0 while it is changing, -1 when it never will again).</summary>
        public static double NextChangeMs(string style, double msSinceReset)
        {
            style = Normalize(style);
            if (style == Solid) return -1;
            if (msSinceReset < IntervalMs) return IntervalMs - msSinceReset;
            double u = msSinceReset - IntervalMs;
            long iter = (long)Math.Floor(u / IntervalMs);
            if (style == Blink) return Math.Max(1, (iter + 1) * IntervalMs - u);
            if (iter >= AnimatedIterations) return -1;
            double o0, s0;
            Evaluate(style, msSinceReset, out o0, out s0);
            const double step = 8;
            for (double d = step; d <= 2 * IntervalMs; d += step)
            {
                double o1, s1;
                Evaluate(style, msSinceReset + d, out o1, out s1);
                if (Math.Abs(o1 - o0) > 0.003 || Math.Abs(s1 - s0) > 0.003) return d <= step ? 0 : d - step;
            }
            return -1;
        }

        /// <summary>Total time after which the animated styles are solid for good.</summary>
        public static double AnimationEndMs { get { return IntervalMs * (AnimatedIterations + 1); } }

        // 0 before a, eased 0 -> 1 between a and b, 1 after b
        private static double Segment(double p, double a, double b)
        {
            if (p <= a) return 0;
            if (p >= b) return 1;
            return EditorEasing.EaseInOut((p - a) / (b - a));
        }
    }

    /// <summary>
    /// Smooth caret transition (VS Code editor.cursorSmoothCaretAnimation): the caret glides from its previous to its
    /// new position over <see cref="EditorScrollOptions.CaretAnimationMs"/> with CSS "ease". Coordinates are document
    /// space (content pixels), so scrolling while it runs does not drag the caret. STRICT C# 5.
    /// </summary>
    public sealed class CaretMoveAnimation
    {
        private double fx, fy, tx, ty, startMs, durationMs;
        private bool active;

        public bool IsActive { get { return active; } }
        public double TargetX { get { return tx; } }
        public double TargetY { get { return ty; } }

        public void Start(double fromX, double fromY, double toX, double toY, double nowMs, double duration)
        {
            fx = fromX; fy = fromY; tx = toX; ty = toY; startMs = nowMs; durationMs = Math.Max(1, duration);
            active = Math.Abs(toX - fromX) > 0.01 || Math.Abs(toY - fromY) > 0.01;
        }

        public void Snap(double x, double y) { fx = tx = x; fy = ty = y; active = false; }

        public void Sample(double nowMs, out double x, out double y)
        {
            if (!active) { x = tx; y = ty; return; }
            double t = (nowMs - startMs) / durationMs;
            if (t >= 1) { active = false; x = tx; y = ty; return; }
            double e = EditorEasing.Ease(Math.Max(0, t));
            x = fx + (tx - fx) * e;
            y = fy + (ty - fy) * e;
        }
    }

    /// <summary>Fractional vertical scroll position (VS Code scrolls by pixels, not lines).</summary>
    public partial class EditorTabBuffer
    {
        private double scrollTopFraction;
        private int scrollFracLine = -1, scrollFracRow;

        /// <summary>
        /// Part of a row (0 &lt;= f &lt; 1) the viewport is scrolled past the top row (<see cref="ScrollTopLine"/>,
        /// <see cref="ScrollTopWrapRow"/>). It belongs to that row: code that assigns ScrollTopLine / ScrollTopWrapRow
        /// directly gets a whole-row position (0) without knowing about it.
        /// </summary>
        public double ScrollTopFraction
        {
            get { return (scrollFracLine == ScrollTopLine && scrollFracRow == ScrollTopWrapRow) ? scrollTopFraction : 0; }
            set
            {
                double v = value;
                if (double.IsNaN(v) || v < 0) v = 0;
                if (v >= 1) v = 0.999999;
                scrollTopFraction = v;
                scrollFracLine = ScrollTopLine;
                scrollFracRow = ScrollTopWrapRow;
            }
        }
    }
}
