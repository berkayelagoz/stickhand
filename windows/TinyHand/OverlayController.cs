using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;
using TinyHand.Overlay;

namespace TinyHand;

/// <summary>
/// Owns the overlay lifetime: monitor-per-overlay layered windows on a dedicated
/// compositor thread, low-level hooks on their own pump, the click-through state
/// machine and the cursor management strategy decided in the M1 plan:
///
/// - Drawing mode: hook swallows button events; moves pass and hit the overlay
///   (alpha=1 background) so WM_SETCURSOR blank-cur sors the real cursor.
/// - Click-through mode (double Shift / toolbar): WS_EX_TRANSPARENT removes the
///   overlay from hit-testing (also over opaque hand pixels); hook passes
///   everything; the real cursor is hidden globally via SetSystemCursor with
///   save/restore (contract: hand stays visible, real cursor stays hidden).
/// - Down/up consistency: an up event is swallowed only if its down was swallowed.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private enum CommandKind
    {
        Activate,
        Deactivate,
        RebuildMonitors,
        HideGlobalCursor,
        RestoreGlobalCursor,
        Quit,
    }

    private sealed record Command(CommandKind Kind);

    private sealed class OverlayPart
    {
        public required MonitorInfo Monitor { get; init; }
        public required OverlayWindow Window { get; init; }
        public required LayeredSurface Surface { get; set; }
        public bool IsPrimary => Monitor.IsPrimary;
        // Dirty-region tracking (screen-space hand rect of the previous frame).
        public Rectangle PrevHandLocal { get; set; } = Rectangle.Empty;
        public bool HasPrevHand { get; set; }
    }

    // macOS OverlayController reference values. Busy target is capped by the primary
    // display's refresh rate: rendering faster than the display only burns CPU
    // (60 Hz monitor -> ~60 fps busy, not 120). Interval is kept in Stopwatch ticks,
    // NOT integer milliseconds: 1000/60 truncates to 16 ms (62.5 fps) and the 0.67 ms
    // beat against the display's 16.67 ms period shows as periodic judder.
    private static long TicksFromMs(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000.0);
    private static long TicksFromFps(int fps) => (long)Math.Round((double)Stopwatch.Frequency / fps);
    private readonly long _idleIntervalTicks = TicksFromMs(IdleIntervalMs);
    private const int IdleIntervalMs = 1000 / 20;
    private const long IdleAfterMs = 500;
    private const uint VkEscape = 0x1B;

    private long _busyIntervalTicks = TicksFromMs(1000.0 / 120);
    private long _nextTick;

    // Dirty-region composition state. Set on events that change pixels anywhere
    // (activate, mode toggle, monitor rebuild, stroke clear); motion-driven frames
    // repaint only the hand/stroke/status regions and publish via ULWI.
    private volatile bool _fullRefresh = true;
    private string _lastRenderedStatus = "";
    private Point _lastDrawnTail;
    private int _lastDrawnStrokeCount;

    // Live FPS HUD state: rolling ring of last frame periods (ticks) + the text
    // last painted, so the compositor can dirty just the HUD rect on change.
    private readonly long[] _framePeriodTicks = new long[128];
    private int _framePeriodHead;
    private int _framePeriodCount;
    private long _lastFrameTs;
    private string _lastRenderedHud = "";
    private string _fpsHud = "";

    // ---- thread-shared state ----
    private volatile bool _active;
    private volatile bool _clickThrough;
    private readonly ConcurrentQueue<Command> _commands = new();
    private long _cursorX;
    private long _cursorY;
    private long _lastActivityMs;
    private volatile bool _dirty = true;
    private volatile List<OverlayPart>? _overlays;
    private volatile List<ToolbarButton>? _toolbar;
    private volatile MonitorInfo? _primaryMonitor;

    // ---- hook-thread state ----
    private readonly MouseDragTracker _dragTracker = new();
    private readonly DoublePressDetector _doubleShift = new(() => Environment.TickCount64);
    private bool _drawing;
    private readonly object _strokeLock = new();
    private List<List<Point>> _strokes = [];

    // ---- overlay-thread state ----
    private readonly HandScene _scene = new();
    private readonly GlobalCursorHider _cursorHider = new();
    private LowLevelHooks? _hooks;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _fpsWindowStart;
    private int _fpsFrames;
    private volatile int _fps;
    private long _statusRefreshAt;
    private string _status = "";

    private readonly Thread _thread;
    private volatile bool _quit;

    public event Action<bool>? ActiveChanged;

    public bool IsActive => _active;
    public bool IsClickThrough => _clickThrough;
    public bool ActiveRequested { get; private set; }

    /// <summary>Frames rendered in the last full second (composition thread).</summary>
    public int CurrentFps => _fps;

    // Composition timing instrumentation (fpscheck harness).
    private long _drawTicks, _presentTicks, _frameSamples;
    public double DrawAvgMs => Volatile.Read(ref _frameSamples) == 0 ? 0 : _drawTicks / (double)Volatile.Read(ref _frameSamples) * 1000.0 / Stopwatch.Frequency;
    public double PresentAvgMs => Volatile.Read(ref _frameSamples) == 0 ? 0 : _presentTicks / (double)Volatile.Read(ref _frameSamples) * 1000.0 / Stopwatch.Frequency;

    public OverlayController()
    {
        _thread = new Thread(OverlayLoop)
        {
            Name = "TinyHand.Overlay",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Asynchronously activate or deactivate the whole capture stack.</summary>
    public bool SetActive(bool active)
    {
        ActiveRequested = active;
        _commands.Enqueue(new Command(active ? CommandKind.Activate : CommandKind.Deactivate));
        return active;
    }

    public void Dispose()
    {
        _quit = true;
        _commands.Enqueue(new Command(CommandKind.Quit));
        _thread.Join(TimeSpan.FromSeconds(4));
    }

    // =====================================================================
    // Overlay thread: message pump + composition loop
    // =====================================================================

    private void OverlayLoop()
    {
        WinMM.timeBeginPeriod(1);
        try
        {
            while (!_quit)
            {
                PumpMessages();
                if (ApplyCommands()) break;

                bool busy = _active && _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastActivityMs) < IdleAfterMs;
                bool render = _active && (busy || _dirty);
                long intervalTicks = _active
                    ? (busy ? _busyIntervalTicks : _idleIntervalTicks)
                    : TicksFromMs(25);

                if (render)
                {
                    RenderFrame(busy);
                    _dirty = false;
                }

                // Accumulating ticker: targets advance on a fixed timeline so
                // render time is part of the frame period instead of extra time on
                // top of it (the old wait-after-render ran at ~43 fps on 4K).
                WaitUntilNextTick(intervalTicks);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Overlay loop crashed: {ex}");
        }
        finally
        {
            Teardown();
            WinMM.timeEndPeriod(1);
        }
    }

    private static void PumpMessages()
    {
        while (User32.PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
    }

    /// <summary>Schedule the next tick on a running timeline; resets after long stalls instead of catch-up bursts.</summary>
    private void WaitUntilNextTick(long intervalTicks)
    {
        long now = Stopwatch.GetTimestamp();
        if (_nextTick == 0 || _nextTick < now - intervalTicks)
            _nextTick = now + intervalTicks; // first tick, or mode change / long stall: restart the timeline
        long target = _nextTick;
        _nextTick += intervalTicks;

        while (true)
        {
            long remainingTicks = target - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) break;
            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.5) Thread.Sleep((int)(remainingMs - 2));
            else Thread.SpinWait(64);
        }
    }

    private bool ApplyCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case CommandKind.Activate:
                    ApplyActivate();
                    break;
                case CommandKind.Deactivate:
                    ApplyDeactivate();
                    break;
                case CommandKind.RebuildMonitors:
                    RebuildOverlays();
                    break;
                case CommandKind.HideGlobalCursor:
                    TryHideGlobalCursor();
                    break;
                case CommandKind.RestoreGlobalCursor:
                    TryRestoreGlobalCursor();
                    break;
                case CommandKind.Quit:
                    return true;
            }
        }
        return false;
    }

    private void ApplyActivate()
    {
        if (_active) return;
        _clickThrough = false;
        _dragTracker.Reset();
        _doubleShift.Reset();
        ClearStrokes();
        RebuildOverlays();

        _hooks = new LowLevelHooks();
        _hooks.MouseFilter += OnMouseFilter;
        _hooks.KeyboardFilter += OnKeyboardFilter;
        if (!_hooks.Start())
            Debug.WriteLine($"Hook install failed: {_hooks.LastError}");

        User32.GetCursorPos(out POINT p);
        Interlocked.Exchange(ref _cursorX, p.X);
        Interlocked.Exchange(ref _cursorY, p.Y);
        MarkActivity();
        _fpsFrames = 0;
        _fpsWindowStart = _clock.ElapsedMilliseconds;
        _framePeriodCount = 0;
        _framePeriodHead = 0;
        _lastFrameTs = 0;
        _lastRenderedHud = "";

        _active = true;
        _dirty = true;
        _fullRefresh = true;
        ActiveChanged?.Invoke(true);
    }

    private void ApplyDeactivate()
    {
        if (!_active && _hooks is null) return;
        _active = false;

        if (_hooks is { } hooks)
        {
            hooks.Stop();
            hooks.Dispose();
            _hooks = null;
        }

        TryRestoreGlobalCursor();
        _clickThrough = false;
        _fullRefresh = true;

        if (_overlays is { } overlays)
            foreach (var part in overlays)
                part.Window.Hide();

        ClearStrokes();
        _dragTracker.Reset();
        _drawing = false;

        // Bring the real cursor back: next WM_SETCURSOR of the app below would restore
        // it anyway; forcing the arrow makes the Esc exit criterion immediate.
        IntPtr arrow = User32.LoadImage(IntPtr.Zero, Win32.OCR_NORMAL, Win32.IMAGE_CURSOR, 0, 0, Win32.LR_SHARED | Win32.LR_DEFAULTSIZE);
        User32.SetCursor(arrow);

        ActiveChanged?.Invoke(false);
    }

    private void Teardown()
    {
        _active = false;
        _hooks?.Stop();
        _hooks?.Dispose();
        _hooks = null;
        TryRestoreGlobalCursor();
        _cursorHider.Dispose();
        if (_overlays is { } overlays)
        {
            foreach (var part in overlays)
            {
                part.Surface.Dispose();
                part.Window.Dispose();
            }
            _overlays = null;
        }
    }

    private void RebuildOverlays()
    {
        var old = _overlays;
        var monitors = Monitors.All();
        _primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

        // Busy composition target capped by the primary display's refresh rate:
        // presenting faster than the display only burns CPU (M1 note; full
        // per-monitor fps management is M3). Tick precision keeps the true period:
        // 60 Hz is 16.667 ms, an integer-ms timeline would run at 16 ms / 62.5 fps
        // and beat against the compositor.
        int refreshHz = Monitors.CurrentMode()?.hz ?? 60;
        int busyFps = Math.Clamp(refreshHz, 60, 120);
        _busyIntervalTicks = TicksFromFps(busyFps);

        var parts = new List<OverlayPart>();
        foreach (var monitor in monitors)
        {
            OverlayWindow window = OverlayWindow.Create(monitor.Bounds);
            window.CursorHidden += w => _active && !_clickThrough;
            window.SystemMessage += (_, _) => _commands.Enqueue(new Command(CommandKind.RebuildMonitors));
            window.Show();
            parts.Add(new OverlayPart
            {
                Monitor = monitor,
                Window = window,
                Surface = new LayeredSurface(monitor.Bounds.Width, monitor.Bounds.Height),
            });
        }

        _overlays = parts;
        _toolbar = _primaryMonitor is { } primary
            ? ToolbarModel.Build(primary.Bounds.Width, (int)primary.DpiX, _clickThrough)
            : [];

        if (old is not null)
        {
            foreach (var part in old)
            {
                part.Surface.Dispose();
                part.Window.Dispose();
            }
        }

        _dirty = true;
        _fullRefresh = true; // fresh surfaces: repaint everything once
    }

    private void TryHideGlobalCursor()
    {
        try { _cursorHider.HideAll(); }
        catch (Exception ex) { Debug.WriteLine($"HideAll failed: {ex.Message}"); }
    }

    private void TryRestoreGlobalCursor()
    {
        bool restored;
        try { restored = _cursorHider.RestoreAll(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestoreAll failed: {ex.Message}");
            restored = false;
        }
        // Saved-copy restore loses a few semi-transparent edge pixels; reloading the
        // registry scheme (SPI_SETCURSORS) restores the cursor pixel-perfect, and is
        // also the fallback when a SetSystemCursor call failed.
        GlobalCursorHider.RestoreSystemScheme();
    }

    // =====================================================================
    // Composition
    // =====================================================================

    private void RenderFrame(bool busy)
    {
        var overlays = _overlays;
        if (overlays is null || overlays.Count == 0) return;

        var snapshot = BuildSnapshot();
        Point cursor = snapshot.Cursor;

        // Region-stage: full repaint (rare events) or union of changed areas.
        bool full = _fullRefresh;
        _fullRefresh = false;

        Rectangle strokeDeltaScreen = Rectangle.Empty;
        if (!full)
            strokeDeltaScreen = StrokeDeltaScreenRect(snapshot.Strokes, penInflate: 14);

        bool statusChanged = !ReferenceEquals(snapshot.Status, _lastRenderedStatus)
                             && snapshot.Status != _lastRenderedStatus;
        bool hudChanged = snapshot.FpsHud != _lastRenderedHud;

        long drawTicksAcc = 0, presentTicksAcc = 0;
        foreach (var part in overlays)
        {
            try
            {
                int scale = Math.Max(1, (int)Math.Round(part.Monitor.DpiX / 96.0, MidpointRounding.AwayFromZero));
                var bounds = part.Monitor.Bounds;
                var newHandScreen = HandScene.HandRect(cursor, scale);
                // The click-through label rides to the right of the hand.
                if (snapshot.ClickThrough)
                    newHandScreen.Width += 170 * scale;
                var newHandLocal = Rectangle.Intersect(
                    new Rectangle(newHandScreen.X - bounds.X, newHandScreen.Y - bounds.Y, newHandScreen.Width, newHandScreen.Height),
                    new Rectangle(0, 0, bounds.Width, bounds.Height));

                Rectangle? clip = null;
                if (!full)
                {
                    var dirty = newHandLocal;
                    if (part.HasPrevHand) dirty = Rectangle.Union(dirty, part.PrevHandLocal);
                    if (!strokeDeltaScreen.IsEmpty)
                    {
                        var strokeLocal = Rectangle.Intersect(
                            new Rectangle(strokeDeltaScreen.X - bounds.X, strokeDeltaScreen.Y - bounds.Y,
                                          strokeDeltaScreen.Width, strokeDeltaScreen.Height),
                            new Rectangle(0, 0, bounds.Width, bounds.Height));
                        dirty = Rectangle.Union(dirty, strokeLocal);
                    }
                    if (statusChanged && part.IsPrimary)
                    {
                        var strip = new Rectangle(0, bounds.Height - 130 * scale, bounds.Width, 130 * scale);
                        dirty = Rectangle.Union(dirty, strip);
                    }
                    if (hudChanged && part.IsPrimary)
                    {
                        var hud = HandScene.HudRect(part.Monitor, scale);
                        dirty = Rectangle.Union(dirty, hud);
                    }
                    dirty.Intersect(new Rectangle(0, 0, bounds.Width, bounds.Height));
                    if (dirty.Width > 0 && dirty.Height > 0) clip = dirty;
                    else continue; // nothing changed on this monitor this frame
                }

                long d0 = Stopwatch.GetTimestamp();
                _scene.Draw(part.Surface.Graphics, part.Monitor, snapshot, clip);
                long d1 = Stopwatch.GetTimestamp();
                bool presented = clip is null
                    ? part.Surface.PresentToRedirected(part.Window.Handle)
                    : part.Surface.PresentDirtyToRedirected(part.Window.Handle, clip.Value);
                long d2 = Stopwatch.GetTimestamp();
                drawTicksAcc += d1 - d0;
                presentTicksAcc += d2 - d1;
                if (!presented)
                    _fullRefresh = true; // lost content: repaint fully next frame

                part.PrevHandLocal = newHandLocal;
                part.HasPrevHand = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Render failed on {part.Monitor.Bounds}: {ex.Message}");
                _fullRefresh = true;
            }
        }
        Interlocked.Add(ref _drawTicks, drawTicksAcc);
        Interlocked.Add(ref _presentTicks, presentTicksAcc);
        Interlocked.Increment(ref _frameSamples);

        // Rolling frame-period history for the HUD (jitter visibility: p95/max
        // expose pacing hitches that a plain fps count hides).
        long ts = Stopwatch.GetTimestamp();
        if (_lastFrameTs != 0)
        {
            _framePeriodTicks[_framePeriodHead] = ts - _lastFrameTs;
            _framePeriodHead = (_framePeriodHead + 1) & (_framePeriodTicks.Length - 1);
            if (_framePeriodCount < _framePeriodTicks.Length) _framePeriodCount++;
        }
        _lastFrameTs = ts;

        // Real-cursor suppression in drawing mode is handled in WM_SETCURSOR
        // (the alpha=1 background keeps the overlay in the hit-test for every
        // delivered move, so the blank cursor is refreshed on input activity).

        _fpsFrames++;
        long now = _clock.ElapsedMilliseconds;
        if (now - _fpsWindowStart >= 1000)
        {
            _fps = _fpsFrames;
            _fpsFrames = 0;
            _fpsWindowStart = now;
        }

        if (now >= _statusRefreshAt)
        {
            var mouseStats = _hooks?.MouseStats.TakeSnapshot();
            var kbdStats = _hooks?.KeyboardStats.TakeSnapshot();
            _status = busy
                ? $"TinyHand M1 | mode {(_clickThrough ? "click-through" : "draw")} | {_fps} fps | mouse cb p95 {mouseStats?.P95Ms ?? 0:0.000} ms | kbd cb p95 {kbdStats?.P95Ms ?? 0:0.000} ms | strokes {snapshot.Strokes.Count} | Ctrl+Alt+H toggle | Esc close | Ctrl+Alt+Q exit app"
                : $"TinyHand M1 | mode {(_clickThrough ? "click-through" : "draw")} | {_fps} fps | Ctrl+Alt+H toggle | Esc close | Ctrl+Alt+Q exit app";
            _statusRefreshAt = now + 250;

            var (p95, max) = FramePeriodStatsMs();
            _fpsHud = busy
                ? $"{_fps} fps | p95 {p95:0.0} / max {max:0.0} ms | draw {DrawAvgMs:0.0} / present {PresentAvgMs:0.0}"
                : $"{_fps} fps idle | p95 {p95:0.0} / max {max:0.0} ms";
        }
        _lastRenderedStatus = snapshot.Status;
        _lastRenderedHud = snapshot.FpsHud;
    }

    /// <summary>p95 and max of recent frame periods in ms (sorted copy of the ring).</summary>
    private (double p95, double max) FramePeriodStatsMs()
    {
        int n = Math.Min(_framePeriodCount, _framePeriodTicks.Length);
        if (n == 0) return (0, 0);
        var arr = new long[n];
        Array.Copy(_framePeriodTicks, arr, n);
        Array.Sort(arr);
        double p95 = arr[(int)Math.Round(0.95 * (n - 1))] * 1000.0 / Stopwatch.Frequency;
        double max = arr[n - 1] * 1000.0 / Stopwatch.Frequency;
        return (p95, max);
    }

    /// <summary>
    /// Bounding box (screen coords) of ink drawn since the previous frame: a new
    /// stroke's first point, or the last segment of the active stroke. Updates the
    /// trackers; returns Rectangle.Empty when nothing moved.
    /// </summary>
    private Rectangle StrokeDeltaScreenRect(List<List<Point>> strokes, int penInflate)
    {
        var delta = Rectangle.Empty;
        var expanded = (Point p) => new Rectangle(p.X - penInflate, p.Y - penInflate, penInflate * 2, penInflate * 2);

        if (strokes.Count == 0)
        {
            _lastDrawnStrokeCount = 0;
            return delta;
        }

        var active = strokes[^1];
        if (active.Count == 0)
        {
            _lastDrawnStrokeCount = strokes.Count;
            return delta;
        }

        if (strokes.Count > _lastDrawnStrokeCount)
        {
            delta = expanded(active[0]); // brand-new stroke: its first dot is fresh ink
        }
        else
        {
            var tail = active[^1];
            var seg = new Rectangle(
                Math.Min(tail.X, _lastDrawnTail.X) - penInflate,
                Math.Min(tail.Y, _lastDrawnTail.Y) - penInflate,
                Math.Abs(tail.X - _lastDrawnTail.X) + penInflate * 2,
                Math.Abs(tail.Y - _lastDrawnTail.Y) + penInflate * 2);
            delta = seg;
        }

        _lastDrawnTail = active[^1];
        _lastDrawnStrokeCount = strokes.Count;
        return delta;
    }

    private SceneSnapshot BuildSnapshot()
    {
        List<List<Point>> strokes;
        lock (_strokeLock)
        {
            strokes = _strokes.Select(s => new List<Point>(s)).ToList();
        }
        return new SceneSnapshot(
            Cursor: new Point((int)Interlocked.Read(ref _cursorX), (int)Interlocked.Read(ref _cursorY)),
            ClickThrough: _clickThrough,
            Strokes: strokes,
            Toolbar: _toolbar ?? [],
            IsPrimary: _primaryMonitor?.IsPrimary ?? false,
            DpiX: (int)(_primaryMonitor?.DpiX ?? 96),
            Status: _status,
            FpsHud: _fpsHud);
    }

    // =====================================================================
    // Hook filters (hook thread)
    // =====================================================================

    private bool OnMouseFilter(HookMouseEvent e)
    {
        if (e.IsOwnInjected) return false;

        Interlocked.Exchange(ref _cursorX, e.X);
        Interlocked.Exchange(ref _cursorY, e.Y);
        MarkActivity();

        if (!_active) return false;

        switch (Classify(e.Message))
        {
            case MouseEventKind.Move:
                if (_drawing) AppendStroke(e.X, e.Y);
                return false;

            case MouseEventKind.Down:
            {
                bool inToolbar = TryGetToolbarButton(e.X, e.Y, out ToolbarButton? button);
                var decision = MousePolicy.Decide(new MousePolicyInput(
                    Kind: MouseEventKind.Down,
                    IsActive: _active,
                    ClickThrough: _clickThrough,
                    DownSwallowedByUs: _dragTracker.DownSwallowed,
                    InToolbar: inToolbar));
                return HandleDown(decision, button);
            }

            case MouseEventKind.Up:
            {
                bool swallow = _dragTracker.OnUp();
                if (_drawing)
                {
                    _drawing = false;
                    _dirty = true;
                }
                return swallow;
            }

            case MouseEventKind.Wheel:
                return !_clickThrough;

            default:
                return false;
        }
    }

    private MouseEventKind Classify(uint message) => message switch
    {
        Win32.WM_MOUSEMOVE => MouseEventKind.Move,
        Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN => MouseEventKind.Down,
        Win32.WM_LBUTTONUP or Win32.WM_RBUTTONUP or Win32.WM_MBUTTONUP or Win32.WM_XBUTTONUP => MouseEventKind.Up,
        Win32.WM_MOUSEWHEEL => MouseEventKind.Wheel,
        _ => MouseEventKind.Other,
    };

    private bool HandleDown(MousePolicyResult decision, ToolbarButton? button)
    {
        bool tracked = _dragTracker.OnDown(decision) == MousePolicyResult.Swallow;

        if (decision == MousePolicyResult.ToolbarCommand && button is not null)
        {
            RunToolbarCommand(button);
            return true;
        }

        if (tracked)
        {
            _drawing = true;
            var p = new Point((int)Interlocked.Read(ref _cursorX), (int)Interlocked.Read(ref _cursorY));
            lock (_strokeLock) _strokes.Add([p]);
            _dirty = true;
            return true;
        }

        return false;
    }

    private void RunToolbarCommand(ToolbarButton button)
    {
        switch (button.Id)
        {
            case "click_through":
                ToggleClickThrough();
                break;
            case "exit":
                // Single path through SetActive keeps ActiveRequested (hotkey/menu mirror)
                // in sync with the real overlay state.
                SetActive(false);
                break;
        }
    }

    private void ToggleClickThrough()
    {
        bool newMode = !_clickThrough;
        _clickThrough = newMode;

        // Flip WS_EX_TRANSPARENT immediately from the hook thread (SetWindowLong on
        // extended styles does not send window messages); the compositor thread only
        // repaints the status/toolbar.
        if (_overlays is { } overlays)
            foreach (var part in overlays)
                part.Window.ClickThrough = newMode;

        _commands.Enqueue(new Command(newMode ? CommandKind.HideGlobalCursor : CommandKind.RestoreGlobalCursor));
        _dirty = true;
        _fullRefresh = true; // toolbar labels, click-through chip and status all change
    }

    private bool OnKeyboardFilter(HookKeyEvent e)
    {
        if (!_active || !e.KeyDown) return false;
        MarkActivity();

        if (e.Vk == VkEscape)
        {
            ClearStrokes();
            SetActive(false); // keep ActiveRequested in sync (see RunToolbarCommand)
            return true; // swallowed: the app below must not see TinyHand's Esc
        }

        if (e.IsOwnInjected) return false;

        if (_doubleShift.RegisterKeyDown(e.Vk))
            ToggleClickThrough();

        return false;
    }

    private bool TryGetToolbarButton(int screenX, int screenY, out ToolbarButton? button)
    {
        button = null;
        if (_clickThrough) return false;
        if (_primaryMonitor is not { } primary) return false;
        var bounds = primary.Bounds;
        if (!bounds.Contains(screenX, screenY)) return false;
        var localX = screenX - bounds.X;
        var localY = screenY - bounds.Y;
        button = ToolbarModel.HitTest(_toolbar ?? [], localX, localY);
        return button is not null;
    }

    private void AppendStroke(int x, int y)
    {
        lock (_strokeLock)
        {
            if (_strokes.Count > 0) _strokes[^1].Add(new Point(x, y));
        }
        _dirty = true;
    }

    private void ClearStrokes()
    {
        lock (_strokeLock) _strokes = new List<List<Point>>(8);
        _drawing = false;
        _dirty = true;
        _fullRefresh = true;
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityMs, _clock.ElapsedMilliseconds);
}
