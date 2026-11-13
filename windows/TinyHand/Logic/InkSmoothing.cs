using System.Drawing;

namespace TinyHand.Logic;

/// <summary>Smoothing strength, cycled at runtime with Ctrl+Alt+S.</summary>
public enum SmoothingLevel
{
    Off,
    Light,
    Medium,
    Strong,
    Max,
}

/// <summary>
/// Two-stage smoothing presets per level. <see cref="Of"/> is the One Euro input filter
/// (minCutoff Hz, beta Hz per px/s); <see cref="Window"/> is the streamline window in points,
/// the path length the distance-weighted average spans.
/// </summary>
public static class SmoothingPresets
{
    public static (double MinCutoff, double Beta) Of(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Light => (1.0, 0.05),
        SmoothingLevel.Medium => (0.5, 0.03),
        SmoothingLevel.Strong => (0.3, 0.02),
        _ => (0.15, 0.015), // Max; Off bypasses the filter entirely
    };

    /// <summary>
    /// Streamline window in points. The One Euro filter alone leaves a residual wobble: it is
    /// a time-based low-pass, so on a quantized mouse path it trades jitter for a slow
    /// low-frequency wave. The streamline averages over a path length instead, which removes
    /// that wave and keeps straight lines straight. Longer windows flatten the edge further
    /// but trail the cursor more; 16 pt at Max is ~5 px of lag, under the hand sprite.
    /// </summary>
    public static double Window(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Off => 0,
        SmoothingLevel.Light => 6,
        SmoothingLevel.Medium => 10,
        SmoothingLevel.Strong => 13,
        _ => 16,
    };

    public static SmoothingLevel Next(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Off => SmoothingLevel.Light,
        SmoothingLevel.Light => SmoothingLevel.Medium,
        SmoothingLevel.Medium => SmoothingLevel.Strong,
        SmoothingLevel.Strong => SmoothingLevel.Max,
        _ => SmoothingLevel.Off,
    };

    public static string Title(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Off => "off",
        SmoothingLevel.Light => "light",
        SmoothingLevel.Medium => "medium",
        SmoothingLevel.Strong => "strong",
        _ => "max",
    };
}

/// <summary>
/// One Euro filter (Casiez, Roussel &amp; Vogel 2012) for pointer input. A first-order
/// low-pass whose cutoff rises with speed: at rest the cutoff is low, so the hand's jitter
/// is attenuated to a few percent and tiny mouse movements no longer wobble the stroke; at
/// speed the cutoff rises, keeping the lag bounded instead of trailing the cursor. Applied
/// per axis with the same timestamps, so it is independent of the mouse report rate
/// (125 Hz .. 1000 Hz). The strength comes from <see cref="SmoothingPresets"/>.
///
/// The output is sub-pixel on purpose: rounding to whole pixels re-quantizes the smoothed
/// path into a 1 px staircase, which is exactly the edge wobble this stage is meant to
/// remove. <see cref="InkStabilizer"/> then averages the residual wave out.
/// </summary>
public sealed class InkSmoother
{
    /// <summary>Cutoff of the derivative low-pass (Hz): smooths the speed estimate.</summary>
    public const double DerivativeCutoff = 1.0;

    private double _x, _y;
    private double _dx, _dy;
    private double _lastTime;
    private bool _started;

    /// <summary>Forget the previous stroke; the next sample is returned unchanged.</summary>
    public void Reset() => _started = false;

    /// <summary>Filter one raw pointer sample taken at <paramref name="nowSeconds"/>.</summary>
    public PointF Filter(PointF raw, double nowSeconds, SmoothingLevel level)
    {
        if (level == SmoothingLevel.Off)
        {
            _started = false; // switching back on starts fresh at the current point
            return raw;
        }

        var (minCutoff, beta) = SmoothingPresets.Of(level);
        if (!_started)
        {
            _x = raw.X;
            _y = raw.Y;
            _dx = 0;
            _dy = 0;
            _lastTime = nowSeconds;
            _started = true;
            return raw;
        }

        double dt = Math.Max(1e-3, nowSeconds - _lastTime);
        _lastTime = nowSeconds;

        // Speed estimate (filtered so alternating jitter does not raise the cutoff).
        _dx = LowPass((raw.X - _x) / dt, Alpha(DerivativeCutoff, dt), _dx);
        _dy = LowPass((raw.Y - _y) / dt, Alpha(DerivativeCutoff, dt), _dy);

        // One cutoff from the speed magnitude, not one per axis: per-axis cutoffs lag x and y
        // by different amounts, which rotates the stroke off a diagonal (a visible bow).
        double cutoff = minCutoff + beta * Math.Sqrt(_dx * _dx + _dy * _dy);
        double alpha = Alpha(cutoff, dt);
        _x = LowPass(raw.X, alpha, _x);
        _y = LowPass(raw.Y, alpha, _y);
        return new PointF((float)_x, (float)_y);
    }

    private static double Alpha(double cutoffHz, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoffHz);
        return 1.0 / (1.0 + tau / dt);
    }

    private static double LowPass(double value, double alpha, double previous) =>
        alpha * value + (1 - alpha) * previous;
}

/// <summary>
/// Distance-weighted streamline: the stroke point is the weighted average of the recent
/// path, with weights falling off as (1 − d/window)². Unlike a time-based low-pass this
/// preserves straight lines exactly (the average of collinear points lies on the line), so a
/// slow diagonal no longer turns into a staircase, and the residual wave the One Euro filter
/// leaves behind is averaged out. The window is a path length, so the lag is bounded by it
/// regardless of the mouse report rate; the tip carries the most weight, so the stroke still
/// follows the cursor, and a stopped pointer accumulates weight until it settles under it.
/// </summary>
public sealed class InkStabilizer
{
    /// <summary>Samples closer than this are treated as the same point (a stationary
    /// pointer must not grow the buffer without bound).</summary>
    public const float DuplicateEpsilon = 0.05f;

    private readonly List<(PointF Point, double Weight)> _points = [];

    /// <summary>Samples currently inside the window (diagnostics/tests).</summary>
    public int Count => _points.Count;

    /// <summary>Forget the previous stroke.</summary>
    public void Reset() => _points.Clear();

    /// <summary>
    /// Feed one filtered sample and return the stabilized stroke point.
    /// <paramref name="window"/> is the averaging length in pixels; ≤ 0 bypasses the stage.
    /// </summary>
    public PointF Push(PointF point, double window)
    {
        if (window <= 0)
        {
            _points.Clear();
            return point;
        }

        if (_points.Count > 0 && InkGeometry.Distance(_points[^1].Point, point) < DuplicateEpsilon)
        {
            // A stopped pointer must still pull the average to the cursor. The cap is the
            // sample count, so the tip's weight matches the path's total weight regardless of
            // the mouse report rate: the stroke settles ~1 px behind the cursor, not
            // window/4, and resuming is not stuck behind a long pause.
            var last = _points[^1];
            _points[^1] = (point, Math.Min(last.Weight + 1, Math.Max(1, _points.Count)));
        }
        else
        {
            _points.Add((point, 1));
        }

        Trim(window);
        return Average(window);
    }

    /// <summary>
    /// Release catch-up: push <paramref name="target"/> while the window shrinks to zero, so
    /// the weighted average converges to it along a smooth curve instead of jumping there
    /// (a jump would leave a straight tail at the end of the stroke).
    /// </summary>
    public IEnumerable<PointF> Drain(PointF target, double window, int steps = 8)
    {
        if (window <= 0)
        {
            yield return Push(target, 0);
            yield break;
        }

        for (int i = 1; i <= steps; i++)
        {
            double shrinking = window * (1 - i / (double)(steps + 1));
            yield return Push(target, shrinking);
        }
        yield return Push(target, 0);
    }

    /// <summary>Drop samples farther than the window from the tip: their weight is zero.</summary>
    private void Trim(double window)
    {
        double distance = 0;
        int keepFrom = 0;
        for (int i = _points.Count - 1; i > 0; i--)
        {
            distance += InkGeometry.Distance(_points[i].Point, _points[i - 1].Point);
            if (distance > window)
            {
                keepFrom = i;
                break;
            }
        }
        if (keepFrom > 0) _points.RemoveRange(0, keepFrom);
    }

    private PointF Average(double window)
    {
        double wx = 0, wy = 0, wsum = 0, distance = 0;
        for (int i = _points.Count - 1; i >= 0; i--)
        {
            if (i < _points.Count - 1)
                distance += InkGeometry.Distance(_points[i + 1].Point, _points[i].Point);
            double t = Math.Max(0, 1 - distance / window);
            double w = _points[i].Weight * t * t;
            wx += _points[i].Point.X * w;
            wy += _points[i].Point.Y * w;
            wsum += w;
        }
        return wsum > 0
            ? new PointF((float)(wx / wsum), (float)(wy / wsum))
            : _points[^1].Point;
    }
}
