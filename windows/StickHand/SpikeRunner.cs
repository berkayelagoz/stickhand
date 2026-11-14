using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using StickHand.Interop;

namespace StickHand;

/// <summary>
/// M1 spike: 4K fullscreen UpdateLayeredWindow at 120 Hz with GDI+.
/// Measures achieved fps, render (GDI+) and present (ULW) frame times,
/// CPU usage; writes a JSON report and exits. See PLAN.md "Erken doğrulanacak varsayım".
/// </summary>
internal static class SpikeRunner
{
    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        int seconds = 8;
        int width = 3840;
        int height = 2160;
        int targetFps = 120;
        string scene = "stress"; // stress | realistic | dirty
        string path = "spike-result.json";

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[i + 1]); break;
                case "--width": width = int.Parse(args[i + 1]); break;
                case "--height": height = int.Parse(args[i + 1]); break;
                case "--fps": targetFps = int.Parse(args[i + 1]); break;
                case "--scene": scene = args[i + 1]; break;
                case "--out": path = args[i + 1]; break;
            }
        }

        Console.WriteLine($"Spike: {width}x{height} ULW @ {targetFps} Hz target, {seconds}s, scene={scene}");

        var report = RunOnSpikeThread(width, height, targetFps, seconds, scene);
        report.Machine = Environment.MachineName;
        report.OsVersion = Environment.OSVersion.VersionString;
        report.ProcessorCount = Environment.ProcessorCount;

        var mode = Monitors.CurrentMode();
        report.DisplayMode = mode is { } m ? $"{m.width}x{m.height} @ {m.hz} Hz" : "unknown";

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        Console.WriteLine($"frames={report.FramesRendered} achievedFps={report.AchievedFps:0.0} " +
                          $"totalAvg={report.TotalAvgMs:0.00}ms p95={report.TotalP95Ms:0.00}ms " +
                          $"renderAvg={report.RenderAvgMs:0.00}ms ulwAvg={report.UlwAvgMs:0.00}ms " +
                          $"cpu={report.CpuPercent:0.0}% display={report.DisplayMode}");
        Console.WriteLine($"report: {Path.GetFullPath(path)}");
        return 0;
    }

    private sealed class SpikeReport
    {
        public string Machine { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string DisplayMode { get; set; } = "";
        public int ProcessorCount { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public int TargetFps { get; set; }
        public int Seconds { get; set; }
        public int FramesRendered { get; set; }
        public double AchievedFps { get; set; }
        public double RenderAvgMs { get; set; }
        public double RenderP95Ms { get; set; }
        public double UlwAvgMs { get; set; }
        public double UlwP95Ms { get; set; }
        public double TotalAvgMs { get; set; }
        public double TotalP95Ms { get; set; }
        public double TotalMaxMs { get; set; }
        public double CpuPercent { get; set; }
        public string Scene { get; set; } = "";
    }

    private static SpikeReport RunOnSpikeThread(int width, int height, int targetFps, int seconds, string scene)
    {
        SpikeReport report = new()
        {
            WindowWidth = width,
            WindowHeight = height,
            TargetFps = targetFps,
            Seconds = seconds,
            Scene = scene,
        };

        var thread = new Thread(() =>
        {
            WinMM.timeBeginPeriod(1);
            try
            {
                RunSpikeLoop(report, width, height, targetFps, seconds, scene);
            }
            finally
            {
                WinMM.timeEndPeriod(1);
            }
        })
        {
            Name = "StickHand.Spike",
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return report;
    }

    private static void RunSpikeLoop(SpikeReport report, int width, int height, int targetFps, int seconds, string scene)
    {
        // Position at the primary monitor's origin; a window larger than the display
        // still pays for the whole DIB render + ULW copy.
        using var window = OverlayWindow.Create(new Rectangle(0, 0, width, height));
        using var surface = new LayeredSurface(width, height);

        window.Show();

        using var hand = LoadHand();

        // Scene-specific static content and a fixed set of "ink" strokes.
        var random = new Random(0xC0FFEE);
        var strokes = BuildStrokes(random, width, height);

        long interval = Stopwatch.Frequency / targetFps;
        long frameStart = Stopwatch.GetTimestamp();
        long endTime = frameStart + (long)seconds * Stopwatch.Frequency;

        double[] renderMs = new double[seconds * targetFps * 2];
        double[] ulwMs = new double[renderMs.Length];
        double[] totalMs = new double[renderMs.Length];
        int frames = 0;

        var process = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long wallStart = Stopwatch.GetTimestamp();

        if (scene == "dirty")
            DrawStaticBackground(surface.Graphics, width, height);

        Rectangle prevDirty = Rectangle.Empty;
        long next = frameStart;
        while (Stopwatch.GetTimestamp() < endTime)
        {
            long t0 = Stopwatch.GetTimestamp();
            double t = (t0 - frameStart) / (double)Stopwatch.Frequency;

            Rectangle dirty = Rectangle.Empty;
            switch (scene)
            {
                case "stress":
                    DrawStressFrame(surface.Graphics, width, height, t, hand);
                    break;
                case "realistic":
                    DrawRealisticFrame(surface.Graphics, width, height, t, hand, strokes);
                    break;
                case "dirty":
                    dirty = DrawDirtyFrame(surface.Graphics, width, height, t, hand, prevDirty);
                    break;
                default:
                    throw new ArgumentException($"unknown scene {scene}");
            }

            long t1 = Stopwatch.GetTimestamp();
            bool ok = dirty.IsEmpty
                ? surface.PresentToRedirected(window.Handle)
                : surface.PresentDirtyToRedirected(window.Handle, dirty);
            long t2 = Stopwatch.GetTimestamp();
            if (!ok) Debug.WriteLine("present failed");

            if (frames < renderMs.Length)
            {
                renderMs[frames] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                ulwMs[frames] = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
                totalMs[frames] = (t2 - t0) * 1000.0 / Stopwatch.Frequency;
            }
            frames++;
            prevDirty = dirty;

            next += interval;
            long sleepUntil = next - Stopwatch.GetTimestamp();
            if (sleepUntil > 0)
            {
                double sleepMs = sleepUntil * 1000.0 / Stopwatch.Frequency;
                if (sleepMs > 2.5) Thread.Sleep((int)(sleepMs - 2));
                else Thread.SpinWait(64);
            }
        }

        double wallS = (Stopwatch.GetTimestamp() - wallStart) / (double)Stopwatch.Frequency;
        double cpuS = (process.TotalProcessorTime - cpuStart).TotalSeconds;

        int n = Math.Min(frames, renderMs.Length);
        (double avg, double p95) Stats(double[] arr)
        {
            var slice = new double[n];
            Array.Copy(arr, slice, n);
            Array.Sort(slice);
            int p95Idx = Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1);
            return (slice.Average(), slice[p95Idx]);
        }

        (double rAvg, double rP95) = Stats(renderMs);
        (double uAvg, double uP95) = Stats(ulwMs);
        (double tAvg, double tP95) = Stats(totalMs);
        double tMax = totalMs.Take(n).Max();

        report.FramesRendered = frames;
        report.AchievedFps = frames / wallS;
        report.RenderAvgMs = rAvg;
        report.RenderP95Ms = rP95;
        report.UlwAvgMs = uAvg;
        report.UlwP95Ms = uP95;
        report.TotalAvgMs = tAvg;
        report.TotalP95Ms = tP95;
        report.TotalMaxMs = tMax;
        report.CpuPercent = cpuS / wallS / Environment.ProcessorCount * 100.0;
    }

    private static List<List<PointF>> BuildStrokes(Random random, int width, int height)
    {
        var strokes = new List<List<PointF>>();
        for (int s = 0; s < 3; s++)
        {
            var pts = new List<PointF>();
            float x = (float)(random.NextDouble() * width * 0.8 + width * 0.1);
            float y = (float)(random.NextDouble() * height * 0.7 + height * 0.1);
            for (int i = 0; i < 300; i++)
            {
                x += (float)(random.NextDouble() - 0.5) * 40;
                y += (float)(random.NextDouble() - 0.5) * 40;
                x = Math.Clamp(x, 0, width - 1);
                y = Math.Clamp(y, 0, height - 1);
                pts.Add(new PointF(x, y));
            }
            strokes.Add(pts);
        }
        return strokes;
    }

    /// <summary>Stress scene: every 4K pixel repainted every frame (gradient + hand + ellipses).</summary>
    private static void DrawStressFrame(Graphics g, int width, int height, double t, Bitmap hand)
    {
        using (var brush = new LinearGradientBrush(
                   new Rectangle(0, 0, width, height),
                   Color.FromArgb(255, (int)(30 + 60 * (1 + Math.Sin(t))), 30, 46),
                   Color.FromArgb(255, 30, (int)(40 + 50 * (1 + Math.Cos(t))), 90),
                   LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(brush, 0, 0, width, height);
        }

        float hx = (float)((Math.Sin(t * 2.000) * 0.30 + 0.5) * width);
        float hy = (float)((Math.Sin(t * 3.100) * 0.28 + 0.5) * height);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(hand, hx, hy, 800, 1200);

        for (int i = 0; i < 24; i++)
        {
            float x = (float)((Math.Sin(t * (0.7 + i * 0.01)) * 0.42 + 0.5) * width);
            float y = (float)((Math.Cos(t * (0.8 + i * 0.02)) * 0.42 + 0.5) * height);
            int alpha = 40 + (i * 13) % 200;
            using var b = new SolidBrush(Color.FromArgb(alpha, (i * 29) % 255, (i * 53) % 255, (i * 97) % 255));
            g.FillEllipse(b, x, y, 300, 300);
        }
    }

    /// <summary>Realistic scene: the actual M1 scene shape — alpha-1 clear, strokes, hand, toolbar.</summary>
    private static void DrawRealisticFrame(Graphics g, int width, int height, double t, Bitmap hand, List<List<PointF>> strokes)
    {
        g.Clear(Color.FromArgb(1, 0, 0, 0));

        using (var pen = new Pen(Color.FromArgb(255, 244, 51, 118), 8f)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
                   LineJoin = LineJoin.Round,
               })
        {
            foreach (var stroke in strokes)
                g.DrawLines(pen, stroke.ToArray());
        }

        float hx = (float)((Math.Sin(t * 2.000) * 0.30 + 0.5) * width);
        float hy = (float)((Math.Sin(t * 3.100) * 0.28 + 0.5) * height);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(hand, hx, hy, 400, 600);

        using var bg = new SolidBrush(Color.FromArgb(235, 28, 28, 32));
        g.FillRectangle(bg, width / 2 - 270, 12, 540, 60);
    }

    /// <summary>
    /// Dirty-region scene: static background outside the hand area; only the union of the
    /// previous and current hand rects is repainted, published via UpdateLayeredWindowIndirect.
    /// </summary>
    private static Rectangle DrawDirtyFrame(Graphics g, int width, int height, double t, Bitmap hand, Rectangle prevDirty)
    {
        float hx = (float)((Math.Sin(t * 2.000) * 0.30 + 0.5) * width - 400);
        float hy = (float)((Math.Sin(t * 3.100) * 0.28 + 0.5) * height - 600);
        var handRect = new Rectangle((int)hx, (int)hy, 800, 1200);

        var dirty = prevDirty.IsEmpty
            ? handRect
            : Rectangle.Union(prevDirty, handRect);
        dirty.Inflate(8, 8);
        dirty.Intersect(new Rectangle(0, 0, width, height));

        // Repaint only the dirty band: background color there + the hand sprite.
        g.SetClip(dirty);
        using (var bg = new SolidBrush(Color.FromArgb(255, 24, 24, 30)))
        {
            g.FillRectangle(bg, dirty);
        }
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(hand, hx, hy, 800, 1200);
        g.ResetClip();
        return dirty;
    }

    private static void DrawStaticBackground(Graphics g, int width, int height)
    {
        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            Color.FromArgb(255, 24, 24, 30),
            Color.FromArgb(255, 40, 40, 52),
            LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, 0, 0, width, height);
    }

    private static Bitmap LoadHand()
    {
        var asm = typeof(SpikeRunner).Assembly;
        using var stream = asm.GetManifestResourceStream("StickHand.assets.hand_black.png")
            ?? throw new InvalidOperationException("Embedded hand_black.png not found");
        return new Bitmap(stream);
    }
}
