using System.Drawing;
using TinyHand.Logic;

namespace TinyHand.Tests;

public class InkStabilizerTests
{
    [Fact]
    public void Off_ReturnsThePointUnchanged()
    {
        var stabilizer = new InkStabilizer();
        Assert.Equal(new PointF(10, 20), stabilizer.Push(new PointF(10, 20), 0));
        Assert.Equal(0, stabilizer.Count);
    }

    [Fact]
    public void StraightLine_StaysStraight()
    {
        // The weighted average of collinear points lies on the line: the streamline must not
        // introduce the low-frequency wave a time-based low-pass produces on a staircase.
        var stabilizer = new InkStabilizer();
        for (int i = 0; i < 200; i++)
        {
            var point = new PointF(100 + i * 0.37f, 100 + i * 0.13f);
            var output = stabilizer.Push(point, 16);
            double deviation = Math.Abs((output.X - 100) * 0.13 - (output.Y - 100) * 0.37)
                / Math.Sqrt(0.37 * 0.37 + 0.13 * 0.13);
            Assert.True(deviation < 0.01, $"point {i} left the line by {deviation} px");
        }
    }

    [Fact]
    public void Jitter_IsAttenuated()
    {
        var stabilizer = new InkStabilizer();
        float maxDeviation = 0;
        for (int i = 0; i < 400; i++)
        {
            // 40 px/s along x with a ±1 px hand tremor on y.
            var point = new PointF(100 + i * 0.32f, 100 + (i % 2 == 0 ? 1f : -1f));
            var output = stabilizer.Push(point, 16);
            if (i > 100) maxDeviation = Math.Max(maxDeviation, Math.Abs(output.Y - 100));
        }
        Assert.True(maxDeviation < 0.35f, $"jitter leaked through: {maxDeviation} px");
    }

    [Fact]
    public void Lag_IsBoundedByTheWindow()
    {
        var stabilizer = new InkStabilizer();
        PointF output = default;
        for (int i = 0; i < 400; i++)
            output = stabilizer.Push(new PointF(100 + i, 100), 16);
        Assert.InRange(499 - output.X, 0, 16);
    }

    [Fact]
    public void Corner_IsRoundedButFollowed()
    {
        var stabilizer = new InkStabilizer();
        PointF output = default;
        for (int i = 0; i < 200; i++) output = stabilizer.Push(new PointF(100 + i * 0.5f, 100), 16);
        for (int i = 0; i < 200; i++) output = stabilizer.Push(new PointF(200, 100 + i * 0.5f), 16);
        // The corner is cut by at most a third of the window, and the stroke still reaches it.
        Assert.InRange(Math.Abs(output.Y - 200), 0, 16);
        Assert.InRange(output.X, 196, 200);
    }

    [Fact]
    public void StationaryPointer_SettlesUnderTheCursor()
    {
        // Without the capped duplicate weight the average stays ~window/4 behind a stopped
        // cursor; the stroke must settle under the hand instead.
        var stabilizer = new InkStabilizer();
        for (int i = 0; i < 200; i++) stabilizer.Push(new PointF(100 + i * 0.32f, 100), 16);
        PointF output = default;
        for (int i = 0; i < 40; i++) output = stabilizer.Push(new PointF(164, 100), 16);
        Assert.InRange(164 - output.X, 0, 2);
    }

    [Fact]
    public void Reset_ClearsTheBuffer()
    {
        var stabilizer = new InkStabilizer();
        for (int i = 0; i < 20; i++) stabilizer.Push(new PointF(i, 0), 16);
        Assert.True(stabilizer.Count > 1);
        stabilizer.Reset();
        Assert.Equal(0, stabilizer.Count);
        Assert.Equal(new PointF(500, 500), stabilizer.Push(new PointF(500, 500), 16));
    }

    [Fact]
    public void Drain_ConvergesToTheTarget()
    {
        var stabilizer = new InkStabilizer();
        for (int i = 0; i < 200; i++) stabilizer.Push(new PointF(100 + i * 0.32f, 100), 16);
        var target = new PointF(200, 100);
        var outputs = stabilizer.Drain(target, 16).ToList();
        Assert.True(outputs.Count > 1);
        Assert.Equal(target, outputs[^1]);
        // The catch-up must be monotonic, not a jump back and forth.
        for (int i = 1; i < outputs.Count; i++)
            Assert.True(outputs[i].X >= outputs[i - 1].X - 0.01f, $"drain went backwards at {i}");
    }
}

public class InkPipelineTests
{
    private const double Dt = 0.008; // 125 Hz mouse report rate

    /// <summary>
    /// The user-visible contract: a slow draw with a hand tremor must produce a smooth edge.
    /// Runs the real hook pipeline (One Euro → streamline → 1.5 pt dedup) and measures how far
    /// the appended centreline strays from the intended straight line. The old pipeline
    /// (filter only, rounded to whole pixels) measured ~0.9 px here; the two-stage pipeline
    /// stays under half a pixel, which is invisible at the stroke width.
    /// </summary>
    [Fact]
    public void JitterySlowStroke_StaysOnTheLine()
    {
        var points = RunPipeline(tremor: 1.5, quantize: true);
        Assert.True(points.Count > 50, $"stroke too short: {points.Count} points");
        Assert.True(MaxDeviation(points) < 0.45,
            $"edge wobble {MaxDeviation(points):0.00} px exceeds the 0.45 px budget");
    }

    [Fact]
    public void SlowDiagonal_DoesNotStaircase()
    {
        // A mouse reports whole pixels, so a slow diagonal arrives as a staircase. The
        // streamline must average it back onto the line instead of drawing the steps.
        var points = RunPipeline(tremor: 0, quantize: true);
        Assert.True(MaxDeviation(points) < 0.2, $"staircase wobble {MaxDeviation(points):0.00} px");
    }

    /// <summary>20° line at 40 px/s through the real hook pipeline, starting after the 4 pt
    /// dead zone like <c>OverlayController.AppendStroke</c>.</summary>
    private static List<PointF> RunPipeline(double tremor, bool quantize)
    {
        var smoother = new InkSmoother();
        var stabilizer = new InkStabilizer();
        var level = SmoothingLevel.Max;
        double window = SmoothingPresets.Window(level);

        double angle = 20 * Math.PI / 180;
        float ux = (float)Math.Cos(angle), uy = (float)Math.Sin(angle);
        var points = new List<PointF>();
        PointF last = default;
        bool started = false;
        double t = 0;

        for (int i = 0; i < 800; i++)
        {
            t += Dt;
            // 40 px/s along the line, 6 Hz tremor, 1 px mouse quantization.
            float x = (float)(100 + 40 * ux * t + tremor * Math.Sin(2 * Math.PI * 6 * t));
            float y = (float)(100 + 40 * uy * t + tremor * Math.Sin(2 * Math.PI * 6 * t + 1));
            if (quantize)
            {
                x = MathF.Round(x);
                y = MathF.Round(y);
            }

            var stabilized = stabilizer.Push(smoother.Filter(new PointF(x, y), t, level), window);
            if (!started)
            {
                if (InkGeometry.Distance(new PointF(100, 100), stabilized) < 4) continue;
                started = true;
                last = stabilized;
                points.Add(stabilized);
                continue;
            }
            if (!InkMetrics.ShouldAppend(last, stabilized, 1)) continue;
            points.Add(stabilized);
            last = stabilized;
        }
        return points;
    }

    private static double MaxDeviation(List<PointF> points)
    {
        double angle = 20 * Math.PI / 180;
        double ux = Math.Cos(angle), uy = Math.Sin(angle);
        double max = 0;
        // Skip the first 100 points: the filter and the stabilizer are still filling up.
        for (int i = 100; i < points.Count; i++)
        {
            double deviation = Math.Abs((points[i].X - 100) * uy - (points[i].Y - 100) * ux);
            max = Math.Max(max, deviation);
        }
        return max;
    }
}
