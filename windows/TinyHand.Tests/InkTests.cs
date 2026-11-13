using System.Drawing;
using TinyHand.Logic;

namespace TinyHand.Tests;

public class InkPaletteTests
{
    [Fact]
    public void Match_ResolvesToTheStyleColour()
    {
        Assert.Equal(InkPalette.Purple, InkPalette.Resolve(InkChoice.Match, PointerStyle.Lavender));
        Assert.Equal(InkPalette.Rgb(0x1D1D21), InkPalette.Resolve(InkChoice.Match, PointerStyle.Black));
        Assert.Equal(InkPalette.Red, InkPalette.Resolve(InkChoice.Match, PointerStyle.Coral));
    }

    [Fact]
    public void FixedChoices_UseTheMacOSHexValues()
    {
        Assert.Equal(Color.FromArgb(255, 255, 41, 31), InkPalette.Fixed(InkChoice.Red));
        Assert.Equal(InkPalette.Rgb(0xFF8A00), InkPalette.Fixed(InkChoice.Orange));
        Assert.Equal(InkPalette.Rgb(0xFFCC00), InkPalette.Fixed(InkChoice.Yellow));
        Assert.Equal(InkPalette.Rgb(0x30C25A), InkPalette.Fixed(InkChoice.Green));
        Assert.Equal(InkPalette.Rgb(0x0A84FF), InkPalette.Fixed(InkChoice.Blue));
        Assert.Equal(InkPalette.Rgb(0x8754F2), InkPalette.Fixed(InkChoice.Purple));
        Assert.Equal(InkPalette.Rgb(0xFFFFFF), InkPalette.Fixed(InkChoice.White));
        Assert.Equal(InkPalette.Rgb(0x1D1D21), InkPalette.Fixed(InkChoice.Black));
        Assert.Null(InkPalette.Fixed(InkChoice.Match));
    }

    [Fact]
    public void FixedChoice_OverridesTheStyleColour()
    {
        Assert.Equal(InkPalette.Rgb(0x0A84FF), InkPalette.Resolve(InkChoice.Blue, PointerStyle.Black));
    }

    [Fact]
    public void StyleTable_MatchesTheMacOSOrderAndColours()
    {
        Assert.Equal(36, PointerStyle.BuiltIn.Count);
        Assert.Equal(36, PointerStyle.BuiltIn.Select(s => s.Id).Distinct().Count());
        Assert.Equal("lavender", PointerStyle.BuiltIn[0].Id);
        Assert.Equal("cookie", PointerStyle.BuiltIn[^1].Id);
        Assert.Equal(InkPalette.Purple, PointerStyle.Lavender.Ink);
        Assert.Equal(InkPalette.Red, PointerStyle.Coral.Ink);
        Assert.Equal(InkPalette.Rgb(0x1D1D21), PointerStyle.Black.Ink);
        Assert.Equal(InkPalette.Rgb(0x2F86F0), PointerStyle.Plush.Ink);
        Assert.Equal(InkPalette.Rgb(0xE0309A), PointerStyle.Neon.Ink);
        Assert.Equal(InkPalette.Red, PointerStyle.Laser.Ink);
    }

    [Fact]
    public void CurrentStyle_IsThePrototypeMidnightHand()
    {
        // hand_black.png is the macOS "Midnight" asset; M3 replaces this with the selection.
        Assert.Equal("black", PointerStyle.Current.Id);
        Assert.Equal(InkPalette.Rgb(0x1D1D21), InkPalette.Resolve(Preferences.InkChoice, PointerStyle.Current));
    }
}

public class InkMetricsTests
{
    [Fact]
    public void Width_FollowsTheMacOSFormula()
    {
        // 4K @100%: 2160 × 0.0055 = 11.88 px (macOS 1080 pt × 2 backing scale).
        Assert.Equal(11.88f, InkMetrics.Width(2160, 1), 3);
        // 1080p @100%: 1080 × 0.0055 = 5.94 px.
        Assert.Equal(5.94f, InkMetrics.Width(1080, 1), 3);
        // The 3 pt floor scales with DPI: 1080p @200% keeps 6 px, not 5.94.
        Assert.Equal(6f, InkMetrics.Width(1080, 2), 3);
    }

    [Fact]
    public void HeadSize_IsMaxOf14PointsAndWidthTimes32()
    {
        Assert.Equal(38.016f, InkMetrics.HeadSize(11.88f, 1), 3);
        Assert.Equal(14f, InkMetrics.HeadSize(4f, 1), 3);
        Assert.Equal(28f, InkMetrics.HeadSize(4f, 2), 3);
    }

    [Fact]
    public void ShouldAppend_DropsPointsCloserThan15Points()
    {
        var origin = new Point(100, 100);
        Assert.False(InkMetrics.ShouldAppend(origin, new Point(101, 100), 1)); // 1 px
        Assert.False(InkMetrics.ShouldAppend(origin, new Point(101, 101), 1)); // ~1.41 px
        Assert.True(InkMetrics.ShouldAppend(origin, new Point(102, 100), 1));  // 2 px
        // At 200% the threshold is 3 physical px.
        Assert.False(InkMetrics.ShouldAppend(origin, new Point(102, 100), 2));
        Assert.True(InkMetrics.ShouldAppend(origin, new Point(103, 100), 2));
    }
}

public class InkFadeTests
{
    [Fact]
    public void Keep_WhenFadeSecondsIsZeroOrNegative()
    {
        Assert.True(InkFade.Keep(0));
        Assert.True(InkFade.Keep(-1));
        Assert.False(InkFade.Keep(3));
        Assert.Equal(1f, InkFade.Opacity(100, 0));
    }

    [Fact]
    public void Delay_IsAtLeastHalfASecond()
    {
        Assert.Equal(0.5, InkFade.DelaySeconds(0.2), 3);
        Assert.Equal(3, InkFade.DelaySeconds(3), 3);
    }

    [Fact]
    public void Opacity_HoldsThenFadesOver06Seconds()
    {
        Assert.Equal(1f, InkFade.Opacity(0, 3));
        Assert.Equal(1f, InkFade.Opacity(3, 3));
        float mid = InkFade.Opacity(3 + InkFade.DurationSeconds / 2, 3);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.Equal(0f, InkFade.Opacity(3 + InkFade.DurationSeconds, 3));
        Assert.Equal(0f, InkFade.Opacity(10, 3));
    }

    [Fact]
    public void EaseIn_IsMonotonicAndSlowerThanLinear()
    {
        Assert.Equal(0, InkFade.EaseIn(0), 6);
        Assert.Equal(1, InkFade.EaseIn(1), 6);
        double previous = 0;
        for (int i = 1; i <= 10; i++)
        {
            double x = i / 10.0;
            double y = InkFade.EaseIn(x);
            Assert.True(y > previous, $"easeIn({x}) = {y} must exceed {previous}");
            Assert.True(y <= x + 1e-6, $"easeIn({x}) = {y} must not exceed linear");
            previous = y;
        }
        // cubic-bezier(0.42, 0, 1, 1) at the midpoint is well below linear.
        Assert.InRange(InkFade.EaseIn(0.5), 0.2, 0.35);
    }
}

public class InkGeometryTests
{
    [Fact]
    public void Mid_IsTheAverage()
    {
        Assert.Equal(new Point(15, 25), InkGeometry.Mid(new Point(10, 20), new Point(20, 30)));
    }

    [Fact]
    public void Head_PointsBackwardsAlongTheLastStretch()
    {
        var points = new[] { new Point(0, 0), new Point(100, 0) };
        Assert.True(InkGeometry.TryHead(points, lineWidth: 10, scale: 1,
            out var end, out var left, out var right));

        Assert.Equal(new Point(100, 0), end);
        // size = max(14, 32) = 32, head = min(32, 80) = 32, spread = π/7.
        Assert.True(left.X < end.X && right.X < end.X, "head must point backwards");
        Assert.True(left.Y < end.Y && right.Y > end.Y, "head must spread on both sides");
        Assert.Equal(left.X, right.X);
        Assert.Equal(-left.Y, right.Y);
    }

    [Fact]
    public void Head_AimsAlongTheLastStretch_NotTheWholeStroke()
    {
        var points = new[] { new Point(0, 0), new Point(100, 0), new Point(100, 50) };
        Assert.True(InkGeometry.TryHead(points, lineWidth: 10, scale: 1,
            out var end, out var left, out var right));

        Assert.Equal(new Point(100, 50), end);
        // The last stretch is vertical (upwards on screen), so the head points down.
        Assert.True(left.Y < end.Y && right.Y < end.Y, "head must point back down the vertical stretch");
        Assert.True(left.X > end.X && right.X < end.X, "head must spread horizontally");
    }

    [Fact]
    public void Head_RejectsDegenerateStrokes()
    {
        Assert.False(InkGeometry.TryHead([], 10, 1, out _, out _, out _));
        Assert.False(InkGeometry.TryHead([new Point(5, 5)], 10, 1, out _, out _, out _));
        Assert.False(InkGeometry.TryHead([new Point(5, 5), new Point(5, 5)], 10, 1, out _, out _, out _));
    }

    [Fact]
    public void Head_IsCappedByTheStrokeLength()
    {
        // A 20 px stroke with a 32 px head: head = min(32, 20 × 0.8) = 16.
        var points = new[] { new Point(0, 0), new Point(20, 0) };
        Assert.True(InkGeometry.TryHead(points, lineWidth: 10, scale: 1,
            out var end, out var left, out _));
        Assert.Equal(20, end.X);
        // Integer endpoints round the head direction, so allow sub-pixel slack.
        Assert.InRange(InkGeometry.Distance(end, left), 15.0, 16.5);
    }
}

public class InkSmootherTests
{
    private const double Dt = 0.008; // 125 Hz mouse report rate

    [Fact]
    public void FirstPoint_IsReturnedUnchanged()
    {
        var smoother = new InkSmoother();
        Assert.Equal(new Point(123, 456), smoother.Filter(new Point(123, 456), 0));
    }

    [Fact]
    public void StationaryJitter_IsAttenuated()
    {
        var smoother = new InkSmoother();
        smoother.Filter(new Point(100, 100), 0);
        double t = 0;
        int maxDeviation = 0;
        for (int i = 0; i < 200; i++)
        {
            t += Dt;
            // ±1 px hand jitter around a still pointer.
            var raw = new Point(100 + (i % 2 == 0 ? 1 : -1), 100 + (i % 3 == 0 ? 1 : -1));
            var filtered = smoother.Filter(raw, t);
            maxDeviation = Math.Max(maxDeviation, Math.Abs(filtered.X - 100));
            maxDeviation = Math.Max(maxDeviation, Math.Abs(filtered.Y - 100));
        }
        Assert.True(maxDeviation <= 1, $"jitter leaked through: {maxDeviation} px");
    }

    [Fact]
    public void FastMovement_FollowsWithinAFewPixels()
    {
        var smoother = new InkSmoother();
        smoother.Filter(new Point(100, 100), 0);
        double t = 0;
        int rawX = 100;
        int filteredX = 100;
        for (int i = 0; i < 60; i++)
        {
            t += Dt;
            rawX += 20; // 2500 px/s
            filteredX = smoother.Filter(new Point(rawX, 100), t).X;
        }
        Assert.InRange(rawX - filteredX, 0, 6);
    }

    [Fact]
    public void SlowMovement_IsSmoothedButFollows()
    {
        var smoother = new InkSmoother();
        smoother.Filter(new Point(100, 100), 0);
        double t = 0;
        int rawX = 100;
        int filteredX = 100;
        for (int i = 0; i < 100; i++)
        {
            t += Dt;
            rawX += 1; // 125 px/s
            filteredX = smoother.Filter(new Point(rawX, 100), t).X;
            Assert.True(filteredX <= rawX, "the filter must not overshoot");
        }
        Assert.InRange(rawX - filteredX, 0, 5);
    }

    [Fact]
    public void Reset_ForgetsThePreviousStroke()
    {
        var smoother = new InkSmoother();
        smoother.Filter(new Point(0, 0), 0);
        smoother.Filter(new Point(100, 100), Dt);
        smoother.Reset();
        Assert.Equal(new Point(500, 500), smoother.Filter(new Point(500, 500), 1));
    }
}
