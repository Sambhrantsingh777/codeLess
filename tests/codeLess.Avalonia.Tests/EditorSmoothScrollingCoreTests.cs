using System;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared smooth-scrolling / caret logic (codeLess.Core EditorSmoothScrolling.cs, used by both editors): easing,
    /// the VS Code-style scroll animation (retargeting, far jumps, precise deltas riding along), the wheel classifier,
    /// blink styles, the caret transition, the fractional scroll position and the persisted settings.
    /// </summary>
    public class EditorSmoothScrollingCoreTests
    {
        [Fact]
        public void EaseOutCubic_AndCssCurves_HaveFixedEndsAndAreMonotonic()
        {
            Assert.Equal(0, EditorEasing.EaseOutCubic(0), 6);
            Assert.Equal(1, EditorEasing.EaseOutCubic(1), 6);
            Assert.True(EditorEasing.EaseOutCubic(0.5) > 0.5); // ease-out: most of the way early
            double prevE = -1, prevIo = -1, prevO = -1;
            for (double t = 0; t <= 1.0001; t += 0.05)
            {
                double e = EditorEasing.Ease(t), io = EditorEasing.EaseInOut(t), o = EditorEasing.EaseOutCubic(t);
                Assert.True(e >= prevE - 1e-9 && io >= prevIo - 1e-9 && o >= prevO - 1e-9);
                prevE = e; prevIo = io; prevO = o;
            }
            Assert.Equal(0.5, EditorEasing.EaseInOut(0.5), 3); // symmetric
            Assert.Equal(1, EditorEasing.EaseInOut(1), 6);
        }

        [Fact]
        public void ScrollAnimation_ReachesTargetAfterDuration_AndRetargetsFromTheCurrentPosition()
        {
            var a = new SmoothScrollAnimation();
            a.Start(10, 16, 40, 1000, 125);
            Assert.True(a.IsActive);
            Assert.Equal(10, a.Sample(1000), 6);
            double mid = a.Sample(1060);
            Assert.InRange(mid, 10.01, 15.99);
            // a second wheel notch mid-flight: from where it is now, the target adds up
            a.AddToTarget(mid, 6, 40, 1060, 125);
            Assert.Equal(22, a.Target, 6);
            Assert.Equal(mid, a.Sample(1060), 6);
            double later = a.Sample(1120);
            Assert.InRange(later, mid, 22);
            Assert.Equal(22, a.Sample(1185), 6);
            Assert.False(a.IsActive);
        }

        [Fact]
        public void ScrollAnimation_FarJump_OnlyAnimatesTheEnds_AndPreciseDeltasShiftEverything()
        {
            var a = new SmoothScrollAnimation();
            a.Start(0, 1000, 40, 0, 125); // 25 viewports away
            double justAfterCut = a.Sample(125 * 0.34);
            Assert.True(justAfterCut > 1000 - 0.75 * 40 - 1, "the composed curve skips the middle: " + justAfterCut);
            double early = a.Sample(125 * 0.2);
            Assert.True(early <= 0.75 * 40 + 1e-9, "the first part stays within 0.75 viewport: " + early);
            var b = new SmoothScrollAnimation();
            b.Start(0, 10, 40, 0, 125);
            double before = b.Sample(50);
            b.ShiftBy(3);
            Assert.Equal(before + 3, b.Sample(50), 6);
            Assert.Equal(13, b.Target, 6);
            b.ClampTarget(0, 11);
            Assert.Equal(11, b.Target, 6);
        }

        [Fact]
        public void WheelClassifier_StreamsArePrecise_NotchesAndIsolatedEventsAnimate()
        {
            var c = new WheelScrollClassifier();
            Assert.False(c.IsPrecise(0, -0.06, 0));     // first event of a gesture: animated (composes with what follows)
            Assert.True(c.IsPrecise(0, -0.08, 8));      // trackpad stream at 120 Hz
            Assert.True(c.IsPrecise(0, -0.5, 16));
            Assert.True(c.IsPrecise(0, -0.02, 24));     // momentum tail
            Assert.False(c.IsPrecise(0, -1, 500));      // isolated notch
            Assert.False(c.IsPrecise(0, -1, 515));      // fast-spun wheel: still notches (VS Code animates them)
            Assert.False(c.IsPrecise(0, -2, 530));
            var d = new WheelScrollClassifier();
            Assert.False(d.IsPrecise(0, 0.1, 0));
            Assert.True(d.IsPrecise(0, 0.12, 10));
            Assert.True(d.IsPrecise(0, 1.0, 20));       // a big precise delta inside a stream stays precise
        }

        [Fact]
        public void WheelPixels_PreciseIsOneToOne_NotchesAreThreeLines()
        {
            double sens = EditorScrollOptions.MouseWheelScrollSensitivity;
            double fastSens = EditorScrollOptions.FastScrollSensitivity;
            Assert.Equal(0.06 * 50 * sens, EditorScrollOptions.WheelPixels(0.06, true, 20, false), 6);
            Assert.Equal(3 * 20 * sens, EditorScrollOptions.WheelPixels(1, false, 20, false), 6);
            Assert.Equal(3 * 20 * sens * fastSens, EditorScrollOptions.WheelPixels(1, false, 20, true), 6);
            Assert.Equal(1.0, EditorScrollOptions.ClampSensitivity(-3, 1.0), 6);
            Assert.Equal(20.0, EditorScrollOptions.ClampSensitivity(500, 1.0), 6);
        }

        [Fact]
        public void CaretBlink_StylesFollowVsCode()
        {
            double o, s;
            CaretBlink.Evaluate(CaretBlink.Solid, 5000, out o, out s);
            Assert.Equal(1, o); Assert.Equal(1, s);
            Assert.Equal(-1, CaretBlink.NextChangeMs(CaretBlink.Solid, 0));

            // solid for 500 ms after input
            CaretBlink.Evaluate(CaretBlink.Smooth, 499, out o, out s);
            Assert.Equal(1, o);
            Assert.Equal(1, CaretBlink.NextChangeMs(CaretBlink.Smooth, 499), 1);
            // smooth: hold 20 %, fade until 60 %, hidden, then back (alternate)
            CaretBlink.Evaluate(CaretBlink.Smooth, 500 + 50, out o, out s);
            Assert.Equal(1, o, 3);
            CaretBlink.Evaluate(CaretBlink.Smooth, 500 + 200, out o, out s);
            Assert.InRange(o, 0.05, 0.95);
            Assert.Equal(0, CaretBlink.NextChangeMs(CaretBlink.Smooth, 500 + 200));
            CaretBlink.Evaluate(CaretBlink.Smooth, 500 + 450, out o, out s);
            Assert.Equal(0, o, 3);
            CaretBlink.Evaluate(CaretBlink.Smooth, 500 + 950, out o, out s);
            Assert.Equal(1, o, 3); // second (reversed) iteration ends visible
            // VS Code's CSS animation runs 20 iterations, then the caret stays solid (no more repaints)
            CaretBlink.Evaluate(CaretBlink.Smooth, CaretBlink.AnimationEndMs + 10, out o, out s);
            Assert.Equal(1, o);
            Assert.Equal(-1, CaretBlink.NextChangeMs(CaretBlink.Smooth, CaretBlink.AnimationEndMs + 10));
            // expand scales instead of fading
            CaretBlink.Evaluate(CaretBlink.Expand, 500 + 250, out o, out s);
            Assert.Equal(1, o);
            Assert.InRange(s, 0.05, 0.95);
            // blink: hard toggles every 500 ms, forever
            CaretBlink.Evaluate(CaretBlink.Blink, 600, out o, out s);
            Assert.Equal(0, o);
            CaretBlink.Evaluate(CaretBlink.Blink, 1100, out o, out s);
            Assert.Equal(1, o);
            Assert.True(CaretBlink.NextChangeMs(CaretBlink.Blink, 60000) > 0);
            Assert.Equal("smooth", CaretBlink.Normalize("bogus"));
            Assert.Equal("phase", CaretBlink.Normalize(" Phase "));
        }

        [Fact]
        public void CaretMoveAnimation_GlidesWithEase_AndSnaps()
        {
            var m = new CaretMoveAnimation();
            m.Start(0, 0, 100, 40, 0, 80);
            double x, y;
            m.Sample(40, out x, out y);
            Assert.InRange(x, 1, 99);
            Assert.InRange(y, 0.4, 39.6);
            m.Sample(80, out x, out y);
            Assert.Equal(100, x, 6);
            Assert.Equal(40, y, 6);
            Assert.False(m.IsActive);
            m.Start(0, 0, 50, 0, 100, 80);
            m.Snap(7, 9);
            m.Sample(120, out x, out y);
            Assert.Equal(7, x); Assert.Equal(9, y);
            Assert.False(m.IsActive);
        }

        [Fact]
        public void ScrollTopFraction_BelongsToItsRow()
        {
            var b = new EditorTabBuffer();
            b.ScrollTopLine = 10;
            b.ScrollTopFraction = 0.4;
            Assert.Equal(0.4, b.ScrollTopFraction, 6);
            b.ScrollTopLine = 11; // code that only knows whole lines gets a whole-row position
            Assert.Equal(0, b.ScrollTopFraction);
            b.ScrollTopLine = 10;
            Assert.Equal(0.4, b.ScrollTopFraction, 6);
            b.ScrollTopWrapRow = 1;
            Assert.Equal(0, b.ScrollTopFraction);
            b.ScrollTopFraction = 1.5;
            Assert.True(b.ScrollTopFraction < 1);
        }

        [Fact]
        public void Settings_DefaultOn_RoundTrip_AndOldFilesKeepDefaults()
        {
            var s = new IdePersistentSettings();
            Assert.True(s.EditorSmoothScrolling);
            Assert.True(s.EditorSmoothCaretAnimation);
            Assert.Equal("smooth", s.EditorCursorBlinking);
            Assert.Equal(1.0, s.EditorMouseWheelScrollSensitivity);
            Assert.Equal(5.0, s.EditorFastScrollSensitivity);

            s.EditorSmoothScrolling = false;
            s.EditorSmoothCaretAnimation = false;
            s.EditorCursorBlinking = "phase";
            s.EditorMouseWheelScrollSensitivity = 2.5;
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            string json = ser.Serialize(s);
            var back = ser.Deserialize<IdePersistentSettings>(json);
            Assert.False(back.EditorSmoothScrolling);
            Assert.False(back.EditorSmoothCaretAnimation);
            Assert.Equal("phase", back.EditorCursorBlinking);
            Assert.Equal(2.5, back.EditorMouseWheelScrollSensitivity, 6);

            var old = ser.Deserialize<IdePersistentSettings>("{\"TabSize\":4}");
            Assert.True(old.EditorSmoothScrolling);
            Assert.Equal("smooth", old.EditorCursorBlinking);
            var bad = ser.Deserialize<IdePersistentSettings>("{\"EditorCursorBlinking\":\"wobble\"}");
            Assert.Equal("smooth", bad.EditorCursorBlinking);
        }
    }
}
