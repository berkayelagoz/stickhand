using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;

namespace TinyHand;

/// <summary>
/// M3 interaction harness: verifies the interaction-quality milestone on screen with real
/// injected input. Covers what unit tests cannot:
///
///  - the style toolbar is painted and its buttons route from the hook (style pick, sound
///    toggle, exit),
///  - the ink colour follows the picked style (macOS <c>InkChoice.match</c>),
///  - the laser style draws a glowing dot instead of a hand,
///  - the press animation shrinks the hand on mouse down and settles back on release,
///  - the tap sound fires on every swallowed down,
///  - the interact key (Alt) gives a temporary click-through: the click reaches the app
///    below, the hand hides and the real cursor comes back; releasing restores drawing,
///  - double Shift latches click-through with the hand still visible (macOS contract),
///  - busy/idle composition pacing (60 fps busy, 20 fps idle).
///
/// Run: TinyHand.exe --m3check
/// </summary>
internal static class M3Check
{
    private const byte VkMenu = 0x12;
    private const byte VkShift = 0x10;

    private static int _failures;
    private static int _clicks;

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        var primary = Monitors.Primary() ?? throw new InvalidOperationException("no monitor");
        var mode = Monitors.CurrentMode();
        int scale = Math.Max(1, (int)Math.Round(primary.DpiX / 96.0, MidpointRounding.AwayFromZero));
        int cx = primary.Bounds.X + primary.Bounds.Width / 2;
        int cy = primary.Bounds.Y + primary.Bounds.Height / 2;

        Console.WriteLine($"m3check: display={mode?.width}x{mode?.height} @ {mode?.hz} Hz, dpi scale {scale}, " +
            $"style {Preferences.Style.Id}, interact {Preferences.InteractKey}, keepsHand {Preferences.InteractKeepsHand}");

        // White backdrop that counts clicks: in click-through mode the click must reach it,
        // in drawing mode it must not. TopMost keeps it above a terminal; the overlay windows
        // are created later and stay above the backdrop in the topmost band.
        using var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(cx - 700, cy - 500, 1400, 1000),
            BackColor = Color.White,
            ShowInTaskbar = false,
            TopMost = true,
        };
        backdrop.MouseDown += (_, _) => _clicks++;
        backdrop.Show();
        User32.SetWindowPos(backdrop.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Pump(300);

        using var controller = new OverlayController();
        controller.SetActive(true);
        Pump(1800);

        // 1. Toolbar: painted, complete, and hit-tested from the hook.
        var layout = controller.Toolbar ?? throw new InvalidOperationException("no toolbar");
        Check("toolbar has 37 styles + sound + exit", layout.Buttons.Count == 39,
            $"buttons={layout.Buttons.Count}");
        var panel = layout.Panel;
        var painted = Capture(primary.Bounds.X + panel.X, primary.Bounds.Y + panel.Y,
            panel.Width, panel.Height);
        Check("toolbar painted", painted.Dark > 2000, $"dark={painted.Dark} panel={panel}");

        // The strip is a pass-through zone: a click between two buttons is swallowed (it must
        // not draw on the app below) and picks nothing.
        var first = layout.Buttons[0].Rect;
        var second = layout.Buttons[1].Rect;
        int gapX = primary.Bounds.X + (first.Right + second.X) / 2;
        int gapY = primary.Bounds.Y + first.Y + first.Height / 2;
        int gapClicks = _clicks;
        ClickAt(gapX, gapY);
        Pump(250);
        Check("toolbar gap swallowed", _clicks == gapClicks && Preferences.Style.Id == "lavender",
            $"clicks={_clicks} style={Preferences.Style.Id}");

        // 2. Style pick: the ink colour follows the style (macOS InkChoice.match).
        ClickButton(controller, "coral", primary);
        Pump(300);
        Check("toolbar pick selects the style", Preferences.Style.Id == "coral",
            $"style={Preferences.Style.Id}");
        Drag(cx - 300, cy, cx + 100, cy, steps: 20);
        MoveTo(cx, cy + 420);
        Pump(300);
        int coral = CountNear(cx - 300, cy - 20, 200, 40, InkPalette.Red, 12);
        Check("ink follows the picked style", coral > 500, $"coral pixels={coral}");
        controller.ClearInk();
        Pump(250);

        // 3. Laser: a glowing dot at the pointer, no hand sprite.
        ClickButton(controller, "laser", primary);
        Pump(300);
        MoveTo(cx, cy);
        Pump(400);
        var laser = Capture(cx - 40, cy - 40, 80, 80);
        Check("laser dot drawn", laser.Red > 40, $"red={laser.Red}");
        Check("laser has no hand", laser.Dark == 0, $"dark={laser.Dark}");
        ClickButton(controller, "lavender", primary);
        Pump(300);

        // 4. Sound toggle.
        bool soundBefore = Preferences.SoundOn;
        ClickButton(controller, "sound", primary);
        Pump(250);
        Check("sound button toggles", Preferences.SoundOn != soundBefore,
            $"{soundBefore} -> {Preferences.SoundOn}");
        ClickButton(controller, "sound", primary);
        Pump(250);
        Check("sound button toggles back", Preferences.SoundOn == soundBefore,
            $"sound={Preferences.SoundOn}");

        // 5. Hand orientation: the arm hangs from below the screen edge, so the hand body sits
        // below the cursor and nothing is drawn above it (the sprite used to be inverted).
        MoveTo(cx, cy - 200);
        Pump(500);
        int below = Ink(cx - 40, cy - 160, 200, 160);
        int above = Ink(cx - 40, cy - 360, 200, 140);
        Check("hand hangs below the cursor", below > 1000 && above == 0,
            $"below={below} above={above}");
        // The arm runs from the grip (below-right) to the cursor: the wide hand sits near the
        // cursor, the narrow stem further down the arm (the sprite used to be inverted).
        int handSpan = RowSpan(cx - 150, cy - 140, 400);
        int stemSpan = RowSpan(cx - 150, cy + 200, 600);
        Check("hand above the stem", handSpan > stemSpan * 2, $"handSpan={handSpan} stemSpan={stemSpan}");
        int rest = Ink(cx - 60, cy - 240, 320, 320);
        int plays = controller.TapSound.PlayCount;
        Mouse(Win32Input.MOUSEEVENTF_LEFTDOWN);
        Pump(300);
        int pressed = Ink(cx - 60, cy - 240, 320, 320);
        Check("press shrinks the hand", pressed > 0 && pressed < rest * 0.95,
            $"rest={rest} pressed={pressed}");
        Check("tap sound on down", controller.TapSound.PlayCount == plays + 1,
            $"plays={controller.TapSound.PlayCount} (was {plays})");
        Mouse(Win32Input.MOUSEEVENTF_LEFTUP);
        Pump(700);
        int released = Ink(cx - 60, cy - 240, 320, 320);
        Check("press settles back", released > pressed * 1.02,
            $"pressed={pressed} released={released}");

        // 6. Interact key: temporary click-through, hand hidden, real cursor back.
        MoveTo(cx, cy - 200);
        Pump(400);
        int clicksBefore = _clicks;
        Key(VkMenu, true);
        Pump(350);
        Check("interact key held", controller.IsInteracting, $"interacting={controller.IsInteracting}");
        int handDuring = Ink(cx - 60, cy - 240, 320, 320);
        if (Preferences.InteractKeepsHand)
            Check("hand stays while interacting (keepsHand)", handDuring > 0, $"dark={handDuring}");
        else
            Check("hand hidden while interacting", handDuring == 0, $"dark={handDuring}");
        ClickAt(cx, cy - 200);
        Pump(300);
        Check("interact click reaches the app", _clicks == clicksBefore + 1,
            $"clicks={_clicks} (was {clicksBefore})");
        Key(VkMenu, false);
        Pump(500);
        Check("interact key released", !controller.IsInteracting, $"interacting={controller.IsInteracting}");
        int handAfter = Ink(cx - 60, cy - 240, 320, 320);
        Check("hand back after interact", handAfter > 0, $"dark={handAfter}");

        // 7. Double Shift: latched click-through, hand stays visible (macOS contract).
        DoubleShift();
        Pump(500);
        Check("double Shift latches click-through", controller.IsClickThrough,
            $"clickThrough={controller.IsClickThrough}");
        int handThrough = Ink(cx - 60, cy - 240, 320, 320);
        Check("hand stays visible in click-through", handThrough > 0, $"dark={handThrough}");
        clicksBefore = _clicks;
        ClickAt(cx, cy - 200);
        Pump(300);
        Check("click-through click reaches the app", _clicks == clicksBefore + 1,
            $"clicks={_clicks} (was {clicksBefore})");
        DoubleShift();
        Pump(500);
        Check("double Shift unlatches", !controller.IsClickThrough,
            $"clickThrough={controller.IsClickThrough}");
        clicksBefore = _clicks;
        ClickAt(cx, cy - 200);
        Pump(300);
        Check("draw click swallowed", _clicks == clicksBefore, $"clicks={_clicks} (was {clicksBefore})");

        // 8. Busy/idle pacing: 60 fps while the pointer moves, 20 fps once it rests.
        // Two full seconds of movement so the last completed fps window is entirely busy.
        for (int i = 0; i < 120; i++)
        {
            MoveTo(cx + (i % 20) * 4, cy - 200);
            Pump(20);
        }
        Pump(1100);
        int busyFps = controller.CurrentFps;
        Check("busy fps", busyFps >= 50, $"fps={busyFps}");
        Pump(3000);
        int idleFps = controller.CurrentFps;
        Check("idle fps", idleFps is >= 15 and <= 25, $"fps={idleFps}");

        controller.SetActive(false);
        Pump(300);

        Console.WriteLine(_failures == 0 ? "m3check: PASS" : $"m3check: FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Print the toolbar geometry for the primary monitor (e2e scripts click it).</summary>
    public static int PrintGeometry()
    {
        Program.TryAttachConsole();
        var primary = Monitors.Primary() ?? throw new InvalidOperationException("no monitor");
        var layout = ToolbarModel.Build(primary.Bounds.Width, (int)primary.DpiX,
            Preferences.Style.Id, Preferences.SoundOn);
        Console.WriteLine($"panel {layout.Panel.X} {layout.Panel.Y} {layout.Panel.Width} {layout.Panel.Height}");
        foreach (var button in layout.Buttons)
            Console.WriteLine($"{button.Id} {button.Kind} {button.Rect.X} {button.Rect.Y} {button.Rect.Width} {button.Rect.Height}");
        return 0;
    }

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} — {detail}");
        if (!ok) _failures++;
    }

    private static void ClickButton(OverlayController controller, string id, MonitorInfo primary)
    {
        var layout = controller.Toolbar ?? throw new InvalidOperationException("no toolbar");
        var button = layout.Buttons.FirstOrDefault(b => b.Id == id)
            ?? throw new InvalidOperationException($"toolbar button {id} missing");
        ClickAt(primary.Bounds.X + button.Rect.X + button.Rect.Width / 2,
            primary.Bounds.Y + button.Rect.Y + button.Rect.Height / 2);
    }

    private readonly record struct CaptureResult(int Dark, int Red, Color Dominant);

    /// <summary>Screen pixels in the rect that are dark (hand/ink) or red (laser).</summary>
    private static CaptureResult Capture(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));

        int dark = 0, red = 0;
        var counts = new Dictionary<int, int>();
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.R < 100 && c.G < 100 && c.B < 100)
                {
                    dark++;
                    int key = (c.R << 16) | (c.G << 8) | c.B;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                if (c.R > 150 && c.G < 120 && c.B < 120) red++;
            }
        }

        var dominant = Color.Black;
        if (counts.Count > 0)
        {
            var best = counts.MaxBy(kv => kv.Value);
            dominant = Color.FromArgb((best.Key >> 16) & 0xFF, (best.Key >> 8) & 0xFF, best.Key & 0xFF);
        }
        return new CaptureResult(dark, red, dominant);
    }

    private static int Dark(int x, int y, int width, int height) => Capture(x, y, width, height).Dark;

    /// <summary>Pointer pixels: coloured (the lavender hand) or dark (the stem). The neutral
    /// 18 % shadow over white is grey and stays out, so the counts track the sprite itself.</summary>
    private static bool IsPointer(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return max - min > 15 || luminance < 135;
    }

    private static int Ink(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        int count = 0;
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
                if (IsPointer(bitmap.GetPixel(px, py))) count++;
        return count;
    }

    /// <summary>Pointer pixels in a single row: the hand is wide, the stem narrow.</summary>
    private static int RowSpan(int x, int y, int width)
    {
        using var bitmap = new Bitmap(width, 1);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, 1));
        int count = 0;
        for (int px = 0; px < width; px++)
            if (IsPointer(bitmap.GetPixel(px, 0))) count++;
        return count;
    }

    private static int CountNear(int x, int y, int width, int height, Color target, int tolerance)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        int count = 0;
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                var c = bitmap.GetPixel(px, py);
                if (Math.Abs(c.R - target.R) <= tolerance && Math.Abs(c.G - target.G) <= tolerance
                    && Math.Abs(c.B - target.B) <= tolerance)
                    count++;
            }
        }
        return count;
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

    private static void ClickAt(int x, int y)
    {
        MoveTo(x, y);
        Pump(120);
        Mouse(Win32Input.MOUSEEVENTF_LEFTDOWN);
        Pump(90);
        Mouse(Win32Input.MOUSEEVENTF_LEFTUP);
        Pump(150);
    }

    private static void DoubleShift()
    {
        Key(VkShift, true);
        Pump(80);
        Key(VkShift, false);
        Pump(150);
        Key(VkShift, true);
        Pump(80);
        Key(VkShift, false);
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
