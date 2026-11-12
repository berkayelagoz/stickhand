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

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        int seconds = 5;
        int movesPerSec = 200;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
            if (args[i] == "--movesps") movesPerSec = int.Parse(args[i + 1]);
            if (args[i] == "--opaque") HandScene.OpaqueBackground = true;
        }
        _ = movesPerSec;

        var mode = Monitors.CurrentMode();
        Console.WriteLine($"fpscheck: display={mode?.width}x{mode?.height} @ {mode?.hz} Hz, {seconds}s of injected moves");

        using var controller = new OverlayController();
        controller.SetActive(true);

        // Wait for activation (hooks + overlays + initial frame).
        Thread.Sleep(1500);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int flip = 1;
        int lastFps = -1;
        int samples = 0;
        double sum = 0;
        int moveIntervalMs = 1000 / Math.Max(1, movesPerSec);
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            User32.mouse_event(MOUSEEVENTF_MOVE, 2 * flip, 0, 0, UIntPtr.Zero);
            flip = -flip;
            Thread.Sleep(moveIntervalMs);

            if (controller.CurrentFps != lastFps)
            {
                lastFps = controller.CurrentFps;
                if (lastFps > 0)
                {
                    Console.WriteLine($"  fps={lastFps}");
                    sum += lastFps;
                    samples++;
                }
            }
        }

        controller.SetActive(false);
        Thread.Sleep(500);

        double avg = samples > 0 ? sum / samples : 0;
        Console.WriteLine($"fpscheck done: steady-state samples={samples} avg={avg:0.0} fps");
        Console.WriteLine($"  avg draw: {controller.DrawAvgMs:0.00} ms, avg present: {controller.PresentAvgMs:0.00} ms");
        return 0;
    }
}
