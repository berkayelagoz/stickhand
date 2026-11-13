using System.Drawing;

namespace TinyHand.Logic;

/// <summary>
/// One Euro filter (Casiez, Roussel &amp; Vogel 2012) for pointer input. A first-order
/// low-pass whose cutoff rises with speed: at rest the cutoff is ~1 Hz, so the hand's
/// jitter is attenuated to a few percent and tiny mouse movements no longer wobble the
/// stroke; at speed the cutoff rises, keeping the lag around 3 px instead of trailing the
/// cursor. Applied per axis with the same timestamps, so it is independent of the mouse
/// report rate (125 Hz .. 1000 Hz).
/// </summary>
public sealed class InkSmoother
{
    /// <summary>Cutoff at rest (Hz): lower = smoother, but slower to start moving.</summary>
    public const double MinCutoff = 0.3;

    /// <summary>Speed coefficient (Hz per px/s): higher = more responsive at speed.</summary>
    public const double Beta = 0.02;

    /// <summary>Cutoff of the derivative low-pass (Hz): smooths the speed estimate.</summary>
    public const double DerivativeCutoff = 1.0;

    private double _x, _y;
    private double _dx, _dy;
    private double _lastTime;
    private bool _started;

    /// <summary>Forget the previous stroke; the next sample is returned unchanged.</summary>
    public void Reset() => _started = false;

    /// <summary>Filter one raw pointer sample taken at <paramref name="nowSeconds"/>.</summary>
    public Point Filter(Point raw, double nowSeconds)
    {
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

        _x = LowPass(raw.X, Alpha(MinCutoff + Beta * Math.Abs(_dx), dt), _x);
        _y = LowPass(raw.Y, Alpha(MinCutoff + Beta * Math.Abs(_dy), dt), _y);
        return new Point((int)Math.Round(_x), (int)Math.Round(_y));
    }

    private static double Alpha(double cutoffHz, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoffHz);
        return 1.0 / (1.0 + tau / dt);
    }

    private static double LowPass(double value, double alpha, double previous) =>
        alpha * value + (1 - alpha) * previous;
}
