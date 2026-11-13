using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;

namespace TinyHand;

/// <summary>
/// M2 ink harness: draws real strokes over a white backdrop and verifies the pixels on
/// screen. Covers the M2 exit criteria that unit tests cannot: the stroke reaches the
/// screen in the InkChoice colour, the arrow head appears only while Ctrl is held, the
/// per-stroke fade removes the ink after delay + 0.6 s, and Ctrl+Alt+X clears immediately.
///
/// Run: TinyHand.exe --inkcheck
/// </summary>
internal static class InkCheck
{
    private const byte VkControl = 0x11;
    private const byte VkMenu = 0x12;
    private const byte VkX = 0x58;

    private static int _failures;

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        var primary = Monitors.Primary() ?? throw new InvalidOperationException("no monitor");
        var mode = Monitors.CurrentMode();
        int cx = primary.Bounds.X + primary.Bounds.Width / 2;
        int cy = primary.Bounds.Y + primary.Bounds.Height / 2;
        int scale = Math.Max(1, (int)Math.Round(primary.DpiX / 96.0, MidpointRounding.AwayFromZero));
        double fade = Preferences.FadeSeconds;
        var ink = InkPalette.Resolve(Preferences.InkChoice, PointerStyle.Current);

        Console.WriteLine($"inkcheck: display={mode?.width}x{mode?.height} @ {mode?.hz} Hz, dpi scale {scale}, " +
            $"fade {fade}s, ink #{ink.R:X2}{ink.G:X2}{ink.B:X2} ({PointerStyle.Current.Id})");

        // White backdrop: ink pixels are unambiguous against it.
        using var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(cx - 520, cy - 260, 1040, 700),
            BackColor = Color.White,
            ShowInTaskbar = false,
        };
        backdrop.Show();
        backdrop.BringToFront();
        Pump(300);

        using var controller = new OverlayController();
        using var hotkeys = new HotkeyHost();
        hotkeys.HotkeyPressed += id =>
        {
            if (id == HotkeyHost.ClearId) controller.ClearInk();
        };
        if (!hotkeys.RegisterClear())
            Console.WriteLine("  WARN: RegisterHotKey Ctrl+Alt+X failed");
        controller.SetActive(true);
        Pump(1500);

        // 1. Plain stroke: ink reaches the screen in the resolved colour.
        Drag(cx - 300, cy, cx + 100, cy, steps: 20);
        MoveTo(cx, cy + 420);
        Pump(300);
        var body = Capture(cx - 300, cy - 20, 200, 40);
        Check("stroke painted", body.Dark > 500, $"dark={body.Dark}");
        Check("ink colour", Near(body.Dominant, ink, 4),
            $"dominant=#{body.Dominant.R:X2}{body.Dominant.G:X2}{body.Dominant.B:X2} expected=#{ink.R:X2}{ink.G:X2}{ink.B:X2}");
        Check("stroke counted", controller.InkStrokeCount == 1,
            $"strokes={controller.InkStrokeCount} [{controller.InkDebug}]");
        if (Environment.GetEnvironmentVariable("TH_INKCHECK_DEBUG") == "1")
            DumpRows(cx - 300, cy - 20, 200, 40);

        // 2. Arrow head: only while Ctrl is held (macOS ⌘).
        Drag(cx - 300, cy + 200, cx + 100, cy + 200, steps: 20);
        MoveTo(cx, cy + 420);
        Pump(300);
        var plainHead = Capture(cx + 60, cy + 208, 45, 14);
        Check("no head without Ctrl", plainHead.Dark == 0, $"dark={plainHead.Dark}");

        Ctrl(true);
        Pump(60);
        Drag(cx - 300, cy + 300, cx + 100, cy + 300, steps: 20);
        Ctrl(false);
        MoveTo(cx, cy + 420);
        Pump(300);
        var arrowHead = Capture(cx + 60, cy + 308, 45, 14);
        Check("head with Ctrl", arrowHead.Dark > 0, $"dark={arrowHead.Dark}");
        // The head must be an open V: the two lines start at the stroke end and must not be
        // joined by a bar (the single-figure geometry bug drew one across the tips). The
        // probe sits in the gap between the upper head line and the stroke body.
        var headBar = Capture(cx + 64, cy + 292, 7, 2);
        Check("head is open (no bar)", headBar.Dark <= 2, $"dark={headBar.Dark}");
        if (Environment.GetEnvironmentVariable("TH_INKCHECK_DEBUG") == "1")
            DumpRows(cx + 55, cy + 275, 55, 50);

        // 3. Hand tip: the measured fingertip must sit on the real cursor (the sprite used to
        // be anchored by its top-centre, leaving the tip ~6 px left / ~14 px below).
        MoveTo(cx, cy - 100);
        Pump(300);
        var tip = Capture(cx - 40, cy - 120, 80, 40);
        Check("hand tip on cursor", Math.Abs(tip.TopRow - 20) <= 3 && Math.Abs(tip.TopCenterX - 40) <= 4,
            $"topRow={tip.TopRow} (want 20) topCenterX={tip.TopCenterX} (want 40)");

        // 4. Per-stroke fade: delay max(0.5, fadeSeconds) + 0.6 s, then the stroke is gone.
        if (InkFade.Keep(fade))
        {
            Pump(1500);
            var kept = Capture(cx - 300, cy - 20, 200, 40);
            Check("ink kept (fade off)", kept.Dark > 500, $"dark={kept.Dark}");
        }
        else
        {
            Pump((int)((InkFade.DelaySeconds(fade) + InkFade.DurationSeconds + 1.0) * 1000));
            var faded = Capture(cx - 300, cy - 20, 200, 40);
            Check("stroke faded", faded.Dark == 0, $"dark={faded.Dark}");
            Check("faded strokes dropped", controller.InkStrokeCount == 0, $"strokes={controller.InkStrokeCount}");
        }

        // 4. Ctrl+Alt+X clears the remaining ink immediately.
        InjectClearHotkey();
        Pump(400);
        var cleared = Capture(cx - 300, cy + 280, 200, 40);
        Check("Ctrl+Alt+X clears", cleared.Dark == 0, $"dark={cleared.Dark}");
        Check("clear drops strokes", controller.InkStrokeCount == 0, $"strokes={controller.InkStrokeCount}");

        controller.SetActive(false);
        Pump(300);

        Console.WriteLine(_failures == 0 ? "inkcheck: PASS" : $"inkcheck: FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} — {detail}");
        if (!ok) _failures++;
    }

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private readonly record struct CaptureResult(int Dark, Color Dominant, int TopRow, int TopCenterX);

    /// <summary>Screen pixels in the rect that are dark (ink/hand), the most common dark
    /// colour, and the topmost dark row with its x-centre (hand-tip alignment check).</summary>
    private static CaptureResult Capture(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));

        int dark = 0;
        int topRow = -1, topLo = -1, topHi = -1;
        var counts = new Dictionary<int, int>();
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.R >= 100 || c.G >= 100 || c.B >= 100) continue;
                dark++;
                if (topRow < 0) topRow = py;
                if (py == topRow)
                {
                    if (topLo < 0) topLo = px;
                    topHi = px;
                }
                int key = (c.R << 16) | (c.G << 8) | c.B;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var dominant = Color.Black;
        if (counts.Count > 0)
        {
            var best = counts.MaxBy(kv => kv.Value);
            dominant = Color.FromArgb((best.Key >> 16) & 0xFF, (best.Key >> 8) & 0xFF, best.Key & 0xFF);
        }
        int topCenterX = topRow < 0 ? -1 : (topLo + topHi) / 2;
        return new CaptureResult(dark, dominant, topRow, topCenterX);
    }

    /// <summary>Debug: dark pixels per row of a capture (TH_INKCHECK_DEBUG=1).</summary>
    private static void DumpRows(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        string path = Path.Combine(Path.GetTempPath(), "tinyhand-inkcheck.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"    capture saved: {path}");
        for (int py = 0; py < height; py++)
        {
            int dark = 0;
            for (int px = 0; px < width; px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.R < 100 && c.G < 100 && c.B < 100) dark++;
            }
            if (dark > 0) Console.WriteLine($"    row {py}: {dark}");
        }
    }

    private static void Drag(int x0, int y0, int x1, int y1, int steps)
    {
        MoveTo(x0, y0);
        Pump(60);
        Mouse(Win32Input.MOUSEEVENTF_LEFTDOWN);
        Pump(40);
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            Pump(12);
        }
        Mouse(Win32Input.MOUSEEVENTF_LEFTUP);
        Pump(60);
    }

    /// <summary>Absolute SendInput move: relative mouse_event deltas are mickeys scaled by
    /// pointer acceleration, which bends the injected path away from the intended line.
    /// Absolute coordinates map onto the primary monitor, so normalize against its bounds.</summary>
    private static void MoveTo(int x, int y)
    {
        var primary = Monitors.Primary()?.Bounds ?? SystemInformation.VirtualScreen;
        var input = new INPUT
        {
            type = Win32Input.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - primary.X) * 65535.0 / Math.Max(1, primary.Width - 1)),
                    dy = (int)Math.Round((y - primary.Y) * 65535.0 / Math.Max(1, primary.Height - 1)),
                    dwFlags = Win32Input.MOUSEEVENTF_MOVE | Win32Input.MOUSEEVENTF_ABSOLUTE,
                },
            },
        };
        User32.SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void Mouse(uint flags)
    {
        var input = new INPUT
        {
            type = Win32Input.INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
        };
        User32.SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void Key(byte vk, bool down)
    {
        var input = new INPUT
        {
            type = Win32Input.INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : Win32Input.KEYEVENTF_KEYUP },
            },
        };
        User32.SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void Ctrl(bool down) => Key(VkControl, down);

    private static void InjectClearHotkey()
    {
        Key(VkControl, true);
        Key(VkMenu, true);
        Key(VkX, true);
        Key(VkX, false);
        Key(VkMenu, false);
        Key(VkControl, false);
    }

    private static void Pump(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }
}
