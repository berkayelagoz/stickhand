using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Overlay;

namespace TinyHand;

/// <summary>
/// M1 fps harness: activates the overlay, injects continuous mouse moves for a few
/// seconds and reports the achieved composition fps (busy mode). The real cursor
/// wiggles a few pixels while this runs.
///
/// Run: TinyHand.exe --fpscheck --seconds 5
/// Expected on this machine (4K @ 60 Hz): fought-for ~60 fps after the ticker fix
/// (the old wait-after-render scheduler produced ~43 fps).
/// </summary>
internal static class FpsCheck
{
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        int seconds = 5;
        int movesPerSec = 200;
        int px = 2;
        bool ink = false;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
            if (args[i] == "--movesps") movesPerSec = int.Parse(args[i + 1]);
            if (args[i] == "--px") px = int.Parse(args[i + 1]);
            if (args[i] == "--opaque") HandScene.OpaqueBackground = true;
            if (args[i] == "--ink") ink = true;
        }
        _ = movesPerSec;

        var mode = Monitors.CurrentMode();
        string pattern = px <= 2 ? "wiggle" : $"circle radius {px}px";
        Console.WriteLine($"fpscheck: display={mode?.width}x{mode?.height} @ {mode?.hz} Hz, {seconds}s, {pattern}{(ink ? ", ink drawing" : "")}");

        using var controller = new OverlayController();
        controller.SetActive(true);

        // Wait for activation (hooks + overlays + initial frame); also report the
        // effective timer resolution by then (TimeBeginPeriod is set by the
        // overlay thread; a 15.6 ms result here would explain pacing hurts).
        Thread.Sleep(1500);
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) Thread.Sleep(1);
        Console.WriteLine($"  Thread.Sleep(1) avg: {sw1.Elapsed.TotalMilliseconds / 50:0.00} ms");

        double angle = 0;
        // ~0.75 revolutions per 300 injections: bounded circle, no screen-edge drift.
        const double stepRad = 2 * Math.PI / 300;
        double prevCos = 1, prevSin = 0;
        int wiggleFlip = 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int moveIntervalMs = 1000 / Math.Max(1, movesPerSec);
        // Steady-state stats: sampled twice a second after the warmup (activation
        // full repaints are seconds behind and would skew the averages).
        const double warmupSeconds = 2.5;
        int samples = 0;
        double sumFps = 0, sumDraw = 0, sumPresent = 0, sumP95 = 0, sumMax = 0;
        double nextSample = warmupSeconds;
        // --ink: hold the left button so the wiggle/circle feeds real ink into the
        // overlay; strokes grow at movesPerSec points/sec. Exposes draw cost that
        // scales with total ink length (the M1.5 report: fps drops while drawing).
        if (ink) User32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            int dx, dy;
            if (px < 4)
            {
                // Circle deltas round to zero at tiny radii: wiggle instead.
                dx = px * wiggleFlip;
                wiggleFlip = -wiggleFlip;
                dy = 0;
            }
            else
            {
                angle += stepRad;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                dx = (int)Math.Round(px * (cos - prevCos));
                dy = (int)Math.Round(px * (sin - prevSin));
                prevCos = cos; prevSin = sin;
            }
            User32.mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, UIntPtr.Zero);
            Thread.Sleep(moveIntervalMs);

            double t = sw.Elapsed.TotalSeconds;
            if (t >= nextSample && t < seconds - 0.25)
            {
                nextSample += 0.5;
                if (controller.CurrentFps > 0)
                {
                    var stats = controller.FrameStatsSnapshot();
                    Console.WriteLine($"  t={t:0.0}s fps={controller.CurrentFps} period p95={stats.PeriodP95Ms:0.0}/max={stats.PeriodMaxMs:0.0} draw p95={stats.DrawP95Ms:0.0}/max={stats.DrawMaxMs:0.0} present p95={stats.PresentP95Ms:0.0}/max={stats.PresentMaxMs:0.0}");
                    sumFps += controller.CurrentFps;
                    sumDraw += controller.DrawAvgMs;
                    sumPresent += controller.PresentAvgMs;
                    sumP95 += stats.PeriodP95Ms;
                    sumMax += stats.PeriodMaxMs;
                    samples++;
                }
            }
        }

        if (ink) User32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        controller.SetActive(false);
        Thread.Sleep(500);

        Console.WriteLine($"fpscheck done: steady-state samples={samples}");
        Console.WriteLine($"  avg fps: {(samples > 0 ? sumFps / samples : 0):0.0}");
        Console.WriteLine($"  avg draw: {(samples > 0 ? sumDraw / samples : 0):0.00} ms, avg present: {(samples > 0 ? sumPresent / samples : 0):0.00} ms");
        Console.WriteLine($"  avg frame p95: {(samples > 0 ? sumP95 / samples : 0):0.00} ms, avg frame max: {(samples > 0 ? sumMax / samples : 0):0.00} ms");

        Console.WriteLine("  recent frame periods (ms):");
        Console.WriteLine("  " + string.Join(" ", controller.RecentFramePeriodsMs(60).Select(p => p.ToString("0.0"))));
        Console.WriteLine("  presents:");
        Console.WriteLine("  " + string.Join(" ", controller.RecentPresentsMs(60).Select(p => p.ToString("0.0"))));
        Console.WriteLine("  waits:");
        Console.WriteLine("  " + string.Join(" ", controller.RecentWaitsMs(60).Select(p => p.ToString("0.0"))));
        return 0;
    }
}
