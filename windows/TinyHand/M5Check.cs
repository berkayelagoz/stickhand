using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;
using TinyHand.Overlay;

namespace TinyHand;

/// <summary>
/// M5 distribution + multi-monitor harness. Covers what the unit tests cannot:
///
///  - the monitor inventory (bounds, DPI, primary at the virtual origin, no overlap),
///  - one overlay window per monitor with matching bounds and DPI scale,
///  - the hand is drawn on the monitor the cursor is on and nowhere else (the stray
///    second arm on the neighbouring screen was the M5 multi-monitor finding),
///  - ink lands on the monitor it was drawn on and stays off the others,
///  - a display change (WM_DISPLAYCHANGE, the same path WM_DPICHANGED takes) rebuilds
///    the overlay set and keeps rendering,
///  - the real cursor stays hidden on the secondary monitor and comes back on exit,
///  - the DPI scale math at 100/125/150/175/200 % (the 150 % pass),
///  - the Velopack update surface (version, channel, dev-build behaviour).
///
/// Run: TinyHand.exe --m5check
/// </summary>
internal static class M5Check
{
    private static int _failures;
    private static int _checks;
    private static string _temp = "";

    public static int Run(string[] args)
    {
        Program.TryAttachConsole();

        _temp = Path.Combine(Path.GetTempPath(), "tinyhand-m5-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
        Environment.SetEnvironmentVariable("TH_SETTINGS_DIR", _temp);
        Environment.SetEnvironmentVariable("TH_HANDS_DIR", Path.Combine(_temp, "Hands"));
        SettingsStore.Enable();
        Preferences.Reload();

        var monitors = Monitors.All();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault()
            ?? throw new InvalidOperationException("no monitor");
        var secondary = monitors.FirstOrDefault(m => !m.IsPrimary);

        Console.WriteLine($"m5check: {monitors.Count} monitor(s), primary {primary.Bounds.Width}x{primary.Bounds.Height} " +
            $"@ {primary.DpiX} dpi, secondary {(secondary is null ? "none" : $"{secondary.Bounds.Width}x{secondary.Bounds.Height} @ {secondary.DpiX} dpi")}");

        // White backdrops on every monitor: pointer/ink pixels are then unambiguously ours.
        var backdrops = new List<Form>();
        foreach (var monitor in monitors)
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = monitor.Bounds,
                BackColor = Color.White,
                ShowInTaskbar = false,
                TopMost = true,
            };
            form.Show();
            User32.SetWindowPos(form.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            backdrops.Add(form);
        }
        Pump(400);

        try
        {
            CheckMonitors(monitors, primary);
            CheckDpiMath(primary);

            using var controller = new OverlayController();
            controller.SetActive(true);
            Pump(1800);

            CheckOverlays(controller, monitors, primary);
            CheckHandFollowsCursor(controller, primary, secondary);
            CheckInkOnSecondary(controller, primary, secondary);
            CheckDisplayChange(controller, monitors, primary, secondary);
            CheckCursorOnSecondary(controller, primary, secondary);
            CheckUpdateSurface();

            controller.SetActive(false);
            Pump(400);
        }
        catch (Exception ex)
        {
            Check("harness completed", false, ex.ToString());
        }
        finally
        {
            GlobalCursorHider.RestoreSystemScheme();
            foreach (var form in backdrops)
            {
                form.Hide();
                form.Dispose();
            }
            SettingsStore.Disable();
            try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
        }

        Console.WriteLine($"m5check: {_checks - _failures}/{_checks} passed");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Monitor inventory
    // ------------------------------------------------------------------

    private static void CheckMonitors(List<MonitorInfo> monitors, MonitorInfo primary)
    {
        Check("monitors enumerated", monitors.Count >= 1, $"{monitors.Count} monitor(s)");
        Check("primary sits at the virtual origin", primary.Bounds.X == 0 && primary.Bounds.Y == 0,
            $"{primary.Bounds.X},{primary.Bounds.Y}");

        bool overlap = false;
        for (int i = 0; i < monitors.Count; i++)
            for (int j = i + 1; j < monitors.Count; j++)
                if (monitors[i].Bounds.IntersectsWith(monitors[j].Bounds)) overlap = true;
        Check("monitors do not overlap", !overlap, $"{monitors.Count} rects");

        bool dpi = monitors.All(m => m.DpiX >= 96 && m.DpiY >= 96);
        Check("every monitor reports a dpi", dpi,
            string.Join(", ", monitors.Select(m => $"{m.Bounds.Width}x{m.Bounds.Height}@{m.DpiX}")));

        bool scale = monitors.All(m => ToolbarModel.Scale((int)m.DpiX) ==
            Math.Max(1, (int)Math.Round(m.DpiX / 96.0, MidpointRounding.AwayFromZero)));
        Check("dpi scale is the rounded dpi/96", scale,
            string.Join(", ", monitors.Select(m => $"{m.DpiX}->{ToolbarModel.Scale((int)m.DpiX)}")));
    }

    /// <summary>The 150 % pass: the scale is an integer (M1 decision), so 144 dpi rounds to 2.
    /// Verify the geometry stays inside the monitor at every common scale.</summary>
    private static void CheckDpiMath(MonitorInfo primary)
    {
        var expected = new (int Dpi, int Scale)[] { (96, 1), (120, 1), (144, 2), (168, 2), (192, 2) };
        bool scales = expected.All(e => ToolbarModel.Scale(e.Dpi) == e.Scale);
        Check("dpi scale table (100/125/150/175/200 %)", scales,
            string.Join(", ", expected.Select(e => $"{e.Dpi}->{ToolbarModel.Scale(e.Dpi)}")));

        bool fits = true;
        string detail = "";
        foreach (var (dpi, _) in expected)
        {
            foreach (int width in new[] { 1920, 2560, 3840 })
            {
                var layout = ToolbarModel.Build(width, dpi, Preferences.Style.Id, true);
                bool inside = layout.Panel.X >= 0 && layout.Panel.Right <= width && layout.Panel.Y >= 0;
                bool buttonsInside = layout.Buttons.All(b => b.Rect.X >= layout.Panel.X
                    && b.Rect.Right <= layout.Panel.Right && b.Rect.Bottom <= layout.Panel.Bottom);
                if (!inside || !buttonsInside)
                {
                    fits = false;
                    detail = $"{width}px @ {dpi} dpi: panel {layout.Panel}";
                }
            }
        }
        Check("toolbar fits the monitor at every scale", fits, detail.Length == 0 ? "1920/2560/3840 px" : detail);

        // Ink width: max(3 × scale, height × 0.0055) — the DPI term only wins on small screens.
        Check("ink width scales with dpi", InkMetrics.Width(1080, 2) == 6f && InkMetrics.Width(1080, 1) == 5.94f,
            $"1080p: {InkMetrics.Width(1080, 1):0.##} / {InkMetrics.Width(1080, 2):0.##} px");

        var fake = new MonitorInfo(IntPtr.Zero, new Rectangle(0, 0, 1920, 1080), true, 144, 144);
        var hud = HandScene.HudRect(fake, ToolbarModel.Scale(144));
        Check("hud rect fits a 150 % monitor", hud.X >= 0 && hud.Right <= 1920 && hud.Y >= 0,
            $"{hud.X},{hud.Y} {hud.Width}x{hud.Height}");
    }

    // ------------------------------------------------------------------
    // Overlay set
    // ------------------------------------------------------------------

    private static void CheckOverlays(OverlayController controller, List<MonitorInfo> monitors, MonitorInfo primary)
    {
        var overlays = controller.OverlayWindows;
        Check("one overlay per monitor", overlays.Count == monitors.Count,
            $"{overlays.Count} overlay(s) for {monitors.Count} monitor(s)");

        bool bounds = overlays.Count == monitors.Count;
        string detail = "";
        foreach (var monitor in monitors)
        {
            var match = overlays.FirstOrDefault(o => o.Monitor.Handle == monitor.Handle);
            if (match.Handle == IntPtr.Zero || match.Bounds != monitor.Bounds)
            {
                bounds = false;
                detail = $"{monitor.Bounds} vs {match.Bounds}";
            }
        }
        Check("overlay bounds match the monitor", bounds,
            detail.Length == 0 ? string.Join(", ", overlays.Select(o => o.Bounds.ToString())) : detail);

        bool dpi = overlays.All(o => o.Monitor.DpiX >= 96);
        Check("overlay carries the monitor dpi", dpi,
            string.Join(", ", overlays.Select(o => $"{o.Bounds.Width}px@{o.Monitor.DpiX}")));

        var toolbar = controller.Toolbar;
        Check("toolbar laid out for the primary", toolbar is not null
            && toolbar.Panel.X >= 0 && toolbar.Panel.Right <= primary.Bounds.Width,
            toolbar is null ? "no toolbar" : $"{toolbar.Panel}");
    }

    // ------------------------------------------------------------------
    // Hand on the cursor's monitor
    // ------------------------------------------------------------------

    private static void CheckHandFollowsCursor(OverlayController controller, MonitorInfo primary, MonitorInfo? secondary)
    {
        var primaryCenter = Center(primary.Bounds);
        MoveTo(primaryCenter.X, primaryCenter.Y);
        Pump(700);
        int onPrimary = Pointer(primaryCenter.X - 160, primaryCenter.Y - 10, 320, 320);
        Check("hand drawn on the primary monitor", onPrimary > 0, $"pointer pixels={onPrimary}");

        // The topmost drawn pixel is the rotated hand's silhouette edge, not the anchor: it sits
        // a little above and right of the cursor. What must hold across DPI is the *relative*
        // geometry — the same offset as a fraction of the hand size on both monitors.
        var primaryTip = PointerTip(primaryCenter.X - 160, primaryCenter.Y - 10, 320, 320);
        double primaryHand = Preferences.PointerSize * primary.Bounds.Height;

        if (secondary is null)
        {
            Console.WriteLine("  [SKIP] secondary monitor checks (single-monitor machine)");
            return;
        }

        var secondaryCenter = Center(secondary.Bounds);
        MoveTo(secondaryCenter.X, secondaryCenter.Y);
        Pump(900);

        int onSecondary = Pointer(secondaryCenter.X - 160, secondaryCenter.Y - 10, 320, 320);
        Check("hand follows the cursor to the secondary monitor", onSecondary > 0,
            $"pointer pixels={onSecondary}");

        var secondaryTip = PointerTip(secondaryCenter.X - 160, secondaryCenter.Y - 10, 320, 320);
        double secondaryHand = Preferences.PointerSize * secondary.Bounds.Height;
        double rowDelta = Math.Abs((secondaryTip.Row - 10) / secondaryHand - (primaryTip.Row - 10) / primaryHand);
        double xDelta = Math.Abs((secondaryTip.X - 160) / secondaryHand - (primaryTip.X - 160) / primaryHand);
        Check("hand geometry matches across dpi", rowDelta <= 0.01 && xDelta <= 0.01,
            $"primary {primaryTip.Row - 10},{primaryTip.X - 160} @ {primaryHand:0}px ({primary.DpiX} dpi); " +
            $"secondary {secondaryTip.Row - 10},{secondaryTip.X - 160} @ {secondaryHand:0}px ({secondary.DpiX} dpi)");

        // The stray arm used to reach from the primary's bottom edge toward the secondary
        // cursor; the bottom-right quadrant is where it showed.
        int stray = Pointer(primary.Bounds.X + primary.Bounds.Width * 55 / 100,
            primary.Bounds.Y + primary.Bounds.Height * 55 / 100,
            primary.Bounds.Width * 45 / 100, primary.Bounds.Height * 45 / 100 - 60);
        Check("no second arm on the other monitor", stray == 0, $"pointer pixels={stray}");

        MoveTo(primaryCenter.X, primaryCenter.Y);
        Pump(900);
        int back = Pointer(primaryCenter.X - 160, primaryCenter.Y - 10, 320, 320);
        int left = Pointer(secondaryCenter.X - 160, secondaryCenter.Y - 10, 320, 320);
        Check("hand returns to the primary monitor", back > 0 && left == 0,
            $"primary={back} secondary={left}");
    }

    // ------------------------------------------------------------------
    // Ink on the secondary monitor
    // ------------------------------------------------------------------

    private static void CheckInkOnSecondary(OverlayController controller, MonitorInfo primary, MonitorInfo? secondary)
    {
        if (secondary is null)
        {
            Console.WriteLine("  [SKIP] ink on the secondary monitor (single-monitor machine)");
            return;
        }

        var center = Center(secondary.Bounds);
        int x0 = center.X - 300, y0 = center.Y - 120;
        Drag(x0, y0, x0 + 600, y0 + 240, 24);
        Pump(400);

        int ink = Pointer(x0 - 20, y0 - 20, 640, 280);
        Check("ink lands on the secondary monitor", ink > 0, $"ink pixels={ink}");

        var primaryCenter = Center(primary.Bounds);
        int elsewhere = Pointer(primaryCenter.X - 320, primaryCenter.Y - 160, 640, 320);
        Check("ink stays off the primary monitor", elsewhere == 0, $"primary pixels={elsewhere}");

        controller.ClearInk();
        // Move the hand off the secondary first: the capture region would otherwise count the
        // hand's own pixels as leftover ink.
        MoveTo(primaryCenter.X, primaryCenter.Y);
        Pump(500);
        int cleared = Pointer(x0 - 20, y0 - 20, 640, 280);
        Check("ink clears on the secondary monitor", cleared == 0, $"ink pixels={cleared}");
    }

    // ------------------------------------------------------------------
    // Display change (the WM_DPICHANGED path)
    // ------------------------------------------------------------------

    private static void CheckDisplayChange(OverlayController controller, List<MonitorInfo> monitors,
        MonitorInfo primary, MonitorInfo? secondary)
    {
        var before = controller.OverlayWindows;
        if (before.Count == 0)
        {
            Check("display change rebuilds the overlays", false, "no overlays");
            return;
        }

        // A display change is what a DPI change or a monitor hot-plug delivers: the window
        // proc forwards it and the controller rebuilds the whole overlay set.
        User32.SendMessage(before[0].Handle, Win32.WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        Pump(1200);

        var after = controller.OverlayWindows;
        bool rebuilt = after.Count == monitors.Count
            && after.All(o => before.All(b => b.Handle != o.Handle));
        Check("display change rebuilds the overlays", rebuilt,
            $"{before.Count} -> {after.Count} window(s)");

        bool bounds = after.Count == monitors.Count
            && monitors.All(m => after.Any(o => o.Monitor.Handle == m.Handle && o.Bounds == m.Bounds));
        Check("rebuilt overlays keep the monitor bounds", bounds,
            string.Join(", ", after.Select(o => o.Bounds.ToString())));

        var target = secondary is not null ? Center(secondary.Bounds) : Center(primary.Bounds);
        MoveTo(target.X, target.Y);
        Pump(900);
        int hand = Pointer(target.X - 160, target.Y - 10, 320, 320);
        Check("hand renders after the rebuild", hand > 0, $"pointer pixels={hand}");
    }

    // ------------------------------------------------------------------
    // Real cursor on the secondary monitor
    // ------------------------------------------------------------------

    private static void CheckCursorOnSecondary(OverlayController controller, MonitorInfo primary, MonitorInfo? secondary)
    {
        var target = secondary is not null ? Center(secondary.Bounds) : Center(primary.Bounds);
        MoveTo(target.X, target.Y);
        Pump(700);
        Check("real cursor hidden while drawing", !CursorVisible(), $"visible={CursorVisible()}");

        controller.SetActive(false);
        Pump(700);
        Check("real cursor returns after deactivation", CursorVisible(), $"visible={CursorVisible()}");
        controller.SetActive(true);
        Pump(900);
    }

    // ------------------------------------------------------------------
    // Velopack update surface
    // ------------------------------------------------------------------

    private static void CheckUpdateSurface()
    {
        string assembly = typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "";
        Check("updater reports the assembly version", Updater.Version == assembly,
            $"{Updater.Version} vs {assembly}");
        Check("updater channel is win", Updater.Channel == "win", Updater.Channel);
        Check("update feed points at the release repository",
            Updater.RepositoryUrl == "https://github.com/nilbuild/tinyhand", Updater.RepositoryUrl);

        // A dev build is not installed: the check must be a no-op, not a network call. An
        // installed build (the setup's copy) does hit the channel, so only the no-throw and
        // the timing are asserted there.
        var watch = Stopwatch.StartNew();
        bool installed = Updater.IsInstalled;
        var update = Updater.Check();
        watch.Stop();
        string found = update is null ? "none" : Updater.Describe(update.TargetFullRelease);
        if (installed)
        {
            Check("installed build checks the channel", watch.ElapsedMilliseconds < 15000,
                $"update={found} in {watch.ElapsedMilliseconds} ms");
        }
        else
        {
            Check("dev build is not installed", true, "installed=False");
            Check("update check is a no-op in a dev build", update is null && watch.ElapsedMilliseconds < 2000,
                $"update={found} in {watch.ElapsedMilliseconds} ms");
        }

        // The download progress window: the callback arrives on a background thread and must
        // reach the bar through the UI thread.
        using var progress = new UpdateProgressForm("9.9.9");
        progress.Show();
        Pump(200);
        progress.SetProgress(42);
        Pump(300);
        Check("update progress window shows the download", progress.Visible && progress.Percent == 42,
            $"visible={progress.Visible} percent={progress.Percent}");
        progress.Close();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Point Center(Rectangle bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static void Check(string name, bool ok, string detail)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({detail})");
    }

    /// <summary>Pointer pixels: coloured (the lavender hand) or dark (the stem, the ink).</summary>
    private static bool IsPointer(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        double luminance = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return max - min > 15 || luminance < 135;
    }

    private static int Pointer(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        int count = 0;
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
                if (IsPointer(bitmap.GetPixel(px, py))) count++;
        return count;
    }

    /// <summary>Topmost pointer row in the box and that row's x-centre (the fingertip).</summary>
    private static (int Row, int X) PointerTip(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        for (int py = 0; py < height; py++)
        {
            int first = -1, last = -1;
            for (int px = 0; px < width; px++)
            {
                if (!IsPointer(bitmap.GetPixel(px, py))) continue;
                if (first < 0) first = px;
                last = px;
            }
            if (first >= 0) return (py, (first + last) / 2);
        }
        return (-1, -1);
    }

    private static bool CursorVisible()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!User32.GetCursorInfo(ref info)) return true;
        return (info.flags & Win32Cursor.CURSOR_SHOWING) != 0;
    }

    private static void Drag(int x0, int y0, int x1, int y1, int steps)
    {
        MoveTo(x0, y0);
        Pump(80);
        Mouse(Win32Input.MOUSEEVENTF_LEFTDOWN);
        Pump(60);
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            Pump(14);
        }
        Mouse(Win32Input.MOUSEEVENTF_LEFTUP);
        Pump(80);
    }

    /// <summary>Absolute SendInput move over the virtual desktop: MOUSEEVENTF_VIRTUALDESK is
    /// what makes the secondary monitor reachable (without it absolute coordinates map onto
    /// the primary only).</summary>
    private static void MoveTo(int x, int y)
    {
        var screen = SystemInformation.VirtualScreen;
        var input = new INPUT
        {
            type = Win32Input.INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - screen.X) * 65535.0 / Math.Max(1, screen.Width - 1)),
                    dy = (int)Math.Round((y - screen.Y) * 65535.0 / Math.Max(1, screen.Height - 1)),
                    dwFlags = Win32Input.MOUSEEVENTF_MOVE | Win32Input.MOUSEEVENTF_ABSOLUTE
                              | Win32Input.MOUSEEVENTF_VIRTUALDESK,
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
