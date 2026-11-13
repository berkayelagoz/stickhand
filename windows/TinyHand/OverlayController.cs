using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;
using TinyHand.Overlay;

namespace TinyHand;

/// <summary>
/// Owns the overlay lifetime: monitor-per-overlay windows on a dedicated
/// compositor thread, low-level hooks on their own pump, the click-through state
/// machine and the cursor management strategy decided in the M1 plan:
///
/// - Drawing mode: hook swallows button events; moves pass and hit the overlay
///   (WS_EX_NOREDIRECTIONBITMAP windows hit-test by their full rect) so
///   WM_SETCURSOR blank-cursors the real cursor.
/// - Click-through mode (double Shift / interact key): WS_EX_TRANSPARENT removes the
///   overlay from hit-testing (also over opaque hand pixels); hook passes
///   everything; the real cursor is hidden globally via SetSystemCursor with
///   save/restore (contract: hand stays visible, real cursor stays hidden). The
///   hide runs synchronously on the hook thread BEFORE the style flip: as soon as
///   the overlay stops hit-testing, the window below restores its cursor via
///   WM_SETCURSOR, so a queued hide would show the real cursor for a frame. When a
///   mouse button is held at toggle time, the style flip is deferred to the Up
///   event: flipping hit-testing mid-drag lets the app below re-assert its cached
///   (non-blank) cursor handle, which kept the real cursor visible while dragging.
/// - Interact key (M3, macOS ⌥ → Alt): while held, clicks pass through and — unless
///   <see cref="Preferences.InteractKeepsHand"/> — the hand is hidden and the real
///   cursor comes back. Alt alone is required: Ctrl+Alt is the app's hotkey prefix,
///   so a chord must not flash the real cursor. The modifier is never swallowed: a
///   hook that eats a keyup leaves the system key state stuck down.
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
        public required OverlaySurface Surface { get; init; }
        public required HandScene.SceneResources Resources { get; set; }
        public bool IsPrimary => Monitor.IsPrimary;
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
    private const uint VkControl = 0x11;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkMenu = 0x12;
    private const uint VkLeftMenu = 0xA4;
    private const uint VkRightMenu = 0xA5;
    private const uint VkShift = 0x10;
    private const uint VkLeftShift = 0xA0;
    private const uint VkRightShift = 0xA1;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;

    private long _busyIntervalTicks = TicksFromMs(1000.0 / 120);
    private long _nextTick;

    // M1.5 present pacing: flip-model Present queues asynchronously, so the
    // accumulated ticker remains the scheduling authority (SyncInterval 0).
    // TH_PRESENT_VSYNC=1 switches to vsync-queued presents for measurements.
    private static readonly uint PresentSyncInterval =
        Environment.GetEnvironmentVariable("TH_PRESENT_VSYNC") == "1" ? 1u : 0u;

    // Live FPS HUD state: rolling ring of last frame periods (ticks) + the text
    // last painted, so the compositor can dirty just the HUD rect on change.
    // Draw/present/wait sample rings ride the same cursor to attribute jitter.
    private readonly long[] _framePeriodTicks = new long[128];
    private readonly long[] _drawTicksRing = new long[128];
    private readonly long[] _presentTicksRing = new long[128];
    private readonly long[] _waitTicksRing = new long[128];
    private long _lastWaitTicks;
    private int _framePeriodHead;
    private int _framePeriodCount;
    private long _lastFrameTs;
    private string _fpsHud = "";

    // Transient on-screen feedback for hotkey/toolbar changes (smoothing level, fade,
    // clear, click-through mode, style, sound).
    private const long ToastMs = 1500;
    private string _toast = "";
    private long _toastUntilMs;

    /// <summary>Recent per-frame composition statistics (fpscheck + HUD source).</summary>
    public sealed record FrameStats(
        double PeriodP95Ms, double PeriodMaxMs,
        double DrawP95Ms, double DrawMaxMs,
        double PresentP95Ms, double PresentMaxMs);

    // ---- thread-shared state ----
    private volatile bool _active;
    private volatile bool _clickThrough;   // latched by double Shift
    private volatile bool _interactHeld;   // interact key held (temporary click-through)
    private volatile bool _normalCursor;   // real cursor visible + hand hidden
    private volatile bool _held;           // button held: press animation stays at full
    private int _pressMilli;               // press amount × 1000 (hook writes 1000, compositor decays)
    private volatile bool _styleDirty = true;
    private readonly ConcurrentQueue<Command> _commands = new();
    private long _cursorX;
    private long _cursorY;
    private long _lastActivityMs;
    private volatile bool _dirty = true;
    private volatile List<OverlayPart>? _overlays;
    private volatile ToolbarLayout? _toolbar;
    private volatile MonitorInfo? _primaryMonitor;

    // ---- hook-thread state ----
    private readonly MouseDragTracker _dragTracker = new();
    private readonly ShiftTapDetector _doubleShift = new(() => Environment.TickCount64);
    private bool _drawing;
    private int _buttonsDown; // real (non-injected) button downs - ups, hook thread only
    private bool _pendingClickThroughFlip; // WS_EX_TRANSPARENT swap deferred to the Up event
    private bool _ctrlDown;
    private bool _shiftDown;
    private bool _winDown;

    // Ink feed: the hook appends stroke lifecycle events, each monitor's InkLayer paints
    // only the new points into its live bitmap and freezes finished strokes for the fade.
    private readonly InkJournal _journal = new();
    private readonly InkSmoother _smoother = new();
    private readonly InkStabilizer _stabilizer = new();
    private PointF _downPoint;
    private PointF _lastInkPoint;
    private bool _strokeStarted;
    private int _strokeCount;

    // ---- overlay-thread state ----
    private readonly HandScene _scene = new();
    private OverlayRenderer? _renderer;
    private readonly GlobalCursorHider _cursorHider = new();
    private LowLevelHooks? _hooks;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _fpsWindowStart;
    private int _fpsFrames;
    private volatile int _fps;
    private long _statusRefreshAt;
    private string _status = "";
    private Color _inkColor = InkPalette.Resolve(InkChoice.Match, PointerStyle.Default);
    private float _gripX;
    private bool _gripInit;
    private bool _gripSettling;
    private long _lastPoseTs;
    private readonly TapSound _tapSound;

    private readonly Thread _thread;
    private volatile bool _quit;

    public event Action<bool>? ActiveChanged;

    public bool IsActive => _active;

    /// <summary>Effective click-through: latched by double Shift or held by the interact key.</summary>
    public bool IsClickThrough => _clickThrough || _interactHeld;

    /// <summary>True while the interact key is held (temporary click-through).</summary>
    public bool IsInteracting => _interactHeld;

    /// <summary>True when the real cursor is shown and the hand hidden (interact key,
    /// <c>interactKeepsHand</c> off).</summary>
    public bool IsNormalCursor => _normalCursor;

    public bool ActiveRequested { get; private set; }

    /// <summary>Frames rendered in the last full second (composition thread).</summary>
    public int CurrentFps => _fps;

    /// <summary>Current press amount 0..1 (harness: press animation).</summary>
    public float PressAmount => Volatile.Read(ref _pressMilli) / 1000f;

    /// <summary>Tap sound instance (harness: play count / loaded sounds).</summary>
    public TapSound TapSound => _tapSound;

    /// <summary>Live + frozen ink strokes across monitors (harness: fade verification).</summary>
    public int InkStrokeCount
    {
        get
        {
            if (_overlays is not { } overlays) return 0;
            int total = 0;
            foreach (var part in overlays) total += part.Resources.Ink.StrokeCount;
            return total;
        }
    }

    /// <summary>Per-monitor ink state (harness diagnostics).</summary>
    public string InkDebug
    {
        get
        {
            if (_overlays is not { } overlays) return "no overlays";
            return string.Join(" | ", overlays.Select(part =>
                $"{part.Monitor.Bounds.X},{part.Monitor.Bounds.Y} {part.Monitor.Bounds.Width}x{part.Monitor.Bounds.Height}: " +
                $"strokes={part.Resources.Ink.StrokeCount} live={part.Resources.Ink.LiveActive}"));
        }
    }

    /// <summary>Toolbar layout currently hit-tested by the hook (harness: geometry).</summary>
    public ToolbarLayout? Toolbar => _toolbar;

    // Composition timing instrumentation (fpscheck harness).
    private long _drawTicks, _presentTicks, _frameSamples;
    public double DrawAvgMs => Volatile.Read(ref _frameSamples) == 0 ? 0 : _drawTicks / (double)Volatile.Read(ref _frameSamples) * 1000.0 / Stopwatch.Frequency;
    public double PresentAvgMs => Volatile.Read(ref _frameSamples) == 0 ? 0 : _presentTicks / (double)Volatile.Read(ref _frameSamples) * 1000.0 / Stopwatch.Frequency;

    public OverlayController()
    {
        _tapSound = new TapSound(Preferences.TapSound) { Enabled = Preferences.SoundOn };
        _tapSound.Warm();

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
        _tapSound.Dispose();
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

                if (_active) AdvancePose();

                bool busy = _active && (IdleElapsedMs() < IdleAfterMs
                    || Volatile.Read(ref _pressMilli) > 0 || _gripSettling);
                // Fading strokes need frames even while the pointer is idle; run them at the
                // busy cadence so the 0.6 s fade stays smooth.
                bool animating = _active && InkAnimating();
                // The toast must disappear on its own: keep rendering until one frame after
                // its deadline, otherwise the last visible frame would leave it on screen.
                bool toast = _active && _clock.ElapsedMilliseconds < Interlocked.Read(ref _toastUntilMs) + 100;
                // Idle pacing is a real 20 Hz tick (macOS runs its timer at 20 Hz when the
                // pointer rests): the frame counter and the HUD stay meaningful and the pose
                // keeps settling. A dirty flag promotes that frame to the busy cadence so a
                // change is painted immediately instead of up to 50 ms later.
                bool render = _active;
                long intervalTicks = _active
                    ? ((busy || animating || toast || _dirty) ? _busyIntervalTicks : _idleIntervalTicks)
                    : TicksFromMs(25);

                if (render)
                {
                    RenderFrame(busy);
                    _dirty = false;
                }

                // Accumulating ticker: targets advance on a fixed timeline so
                // render time is part of the frame period instead of extra time on
                // top of it (the old wait-after-render ran at ~43 fps on 4K).
                long w0 = Stopwatch.GetTimestamp();
                WaitUntilNextTick(intervalTicks);
                _lastWaitTicks = Stopwatch.GetTimestamp() - w0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Overlay loop crashed: {ex}");
            // Harness modes (--fpscheck etc.) attach the parent console; surface the
            // crash there too, otherwise a failed render stack shows up as plain zero fps.
            try { Console.Error.WriteLine($"Overlay loop crashed: {ex}"); } catch { /* no console */ }
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

    /// <summary>Schedule the next tick on a running timeline. A frame that slightly
    /// overruns its budget must NOT restart the timeline: the old reset-at-one-period
    /// turned any ~18 ms frame into a 33 ms gap (full period of extra sleep), which
    /// is exactly the periodic hitch seen on screen. Resync only after real stalls
    /// (2+ periods late); small overruns catch up on the next immediate tick.</summary>
    private void WaitUntilNextTick(long intervalTicks)
    {
        long now = Stopwatch.GetTimestamp();
        if (_nextTick == 0 || _nextTick < now - 2 * intervalTicks)
            _nextTick = now + intervalTicks; // first tick, or genuine long stall: restart the timeline
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
        TraceLine("apply activate");
        if (_active) return;
        _clickThrough = false;
        _interactHeld = false;
        _normalCursor = false;
        _dragTracker.Reset();
        _doubleShift.Reset();
        _buttonsDown = 0;
        _pendingClickThroughFlip = false;
        // Ctrl may have been released while the hook was uninstalled: start clean so the
        // first stroke after activation cannot inherit a stale arrow head.
        _ctrlDown = false;
        _shiftDown = false;
        _winDown = false;
        _journal.Arrow = false;
        Volatile.Write(ref _pressMilli, 0);
        _held = false;
        _gripInit = false;
        _gripSettling = false;
        _lastPoseTs = 0;
        _styleDirty = true;
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

        _active = true;
        _dirty = true;
        ActiveChanged?.Invoke(true);
    }

    private void ApplyDeactivate()
    {
        TraceLine("apply deactivate");
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
        _interactHeld = false;
        _normalCursor = false;
        _buttonsDown = 0;
        _pendingClickThroughFlip = false;
        _held = false;
        Volatile.Write(ref _pressMilli, 0);

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
                part.Resources.Dispose();
                part.Surface.Dispose();
                part.Window.Dispose();
            }
            _overlays = null;
        }
        _renderer?.Dispose();
        _renderer = null;
    }

    private void RebuildOverlays()
    {
        TraceLine("rebuild overlays");
        var old = _overlays;
        _renderer ??= new OverlayRenderer();

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
        try
        {
            foreach (var monitor in monitors)
            {
                OverlayWindow window = OverlayWindow.Create(monitor.Bounds, layered: false);
                window.CursorHidden += w => _active && !IsClickThrough && !_normalCursor;
                window.SystemMessage += (_, _) => _commands.Enqueue(new Command(CommandKind.RebuildMonitors));
                window.Show();
                var surface = _renderer.CreateSurface(window.Handle, monitor.Bounds.Width, monitor.Bounds.Height);
                parts.Add(new OverlayPart
                {
                    Monitor = monitor,
                    Window = window,
                    Surface = surface,
                    Resources = _scene.CreateResources(_renderer, surface.Context, monitor, _inkColor),
                });
            }
        }
        catch
        {
            // Partial rebuild failed: drop everything just created, keep the old set.
            foreach (var part in parts)
            {
                part.Resources.Dispose();
                part.Surface.Dispose();
                part.Window.Dispose();
            }
            throw;
        }

        _overlays = parts;
        RefreshToolbar();

        if (old is not null)
        {
            foreach (var part in old)
            {
                part.Resources.Dispose();
                part.Surface.Dispose();
                part.Window.Dispose();
            }
        }

        _dirty = true; // fresh frames: repaint once on the next tick
    }

    /// <summary>Rebuild the toolbar buttons from the current style/sound state so the
    /// highlight and the speaker icon follow immediately (called from the hook thread on a
    /// pick; RefreshToolbar writes the volatile field atomically and the compositor picks the
    /// new layout up on its next frame).</summary>
    private void RefreshToolbar()
    {
        _toolbar = _primaryMonitor is { } primary
            ? ToolbarModel.Build(primary.Bounds.Width, (int)primary.DpiX, Preferences.Style.Id, Preferences.SoundOn)
            : new ToolbarLayout(Rectangle.Empty, []);
    }

    private void TryHideGlobalCursor()
    {
        try { _cursorHider.HideAll(); }
        catch (Exception ex) { Debug.WriteLine($"HideAll failed: {ex.Message}"); }
    }

    /// <summary>True while any monitor's ink layer still has a stroke to fade: the compositor
    /// must keep ticking even when the pointer is idle.</summary>
    private bool InkAnimating()
    {
        if (_overlays is not { } overlays) return false;
        foreach (var part in overlays)
            if (part.Resources.Ink.NeedsFrame) return true;
        return false;
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

        // After coming back to drawing mode the restored arrow stays until the next
        // WM_SETCURSOR (i.e. the next mouse move). Post one now so the blank cursor
        // re-applies immediately instead of flashing the real arrow.
        if (_active && !IsClickThrough && !_normalCursor && _overlays is { } overlays)
            foreach (var part in overlays)
                User32.SendMessage(part.Window.Handle, Win32.WM_SETCURSOR, part.Window.Handle, (IntPtr)1 /* HTCLIENT */);
    }

    // =====================================================================
    // Pose (press animation + grip smoothing), advanced once per frame
    // =====================================================================

    private void AdvancePose()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _lastPoseTs == 0 ? 0 : (float)((now - _lastPoseTs) / (double)Stopwatch.Frequency);
        _lastPoseTs = now;
        // Clamp: a stalled frame must not teleport the grip, and the first frame snaps anyway.
        dt = Math.Clamp(dt, 0.001f, 0.1f);

        var primary = _primaryMonitor;
        if (primary is not null)
        {
            float cursorX = Interlocked.Read(ref _cursorX);
            float target = HandPoseSolver.GripTargetX(cursorX, primary.Bounds);
            if (!_gripInit)
            {
                _gripX = target;
                _gripInit = true;
            }
            else
            {
                _gripX = HandPoseSolver.FollowGrip(_gripX, target, dt);
            }
            _gripSettling = Math.Abs(target - _gripX) > 0.5f;
        }

        if (_held) return;
        int press = Volatile.Read(ref _pressMilli);
        if (press > 0)
            Volatile.Write(ref _pressMilli, (int)(press * (1 - HandPoseSolver.PressReleaseFollow)));
    }

    // =====================================================================
    // Composition
    // =====================================================================

    private void RenderFrame(bool busy)
    {
        var overlays = _overlays;
        if (overlays is null || overlays.Count == 0) return;

        if (_styleDirty)
        {
            _styleDirty = false;
            _inkColor = InkPalette.Resolve(Preferences.InkChoice, Preferences.Style);
            foreach (var part in overlays) part.Resources.Ink.SetColor(_inkColor);
        }

        var snapshotBase = BuildSnapshot();

        long drawTicksAcc = 0, presentTicksAcc = 0;
        foreach (var part in overlays)
        {
            try
            {
                // Toolbar/status/HUD ride the primary monitor only (the M1 dirty
                // clip was implicitly limiting them to it; explicit now that
                // every frame is a full redraw).
                var snapshot = snapshotBase with { IsPrimary = part.Monitor.IsPrimary };
                var surface = part.Surface;

                // Feed new ink into the persistent bitmap first (O(new segments) —
                // never proportional to total ink drawn this session).
                part.Resources.Ink.Sync(_journal);

                long d0 = Stopwatch.GetTimestamp();
                surface.BeginDraw();
                _scene.Draw(surface.Context, part.Resources, part.Monitor, in snapshot);
                bool drew = surface.EndDraw();
                long d1 = Stopwatch.GetTimestamp();
                bool presented = drew && surface.Present(PresentSyncInterval);
                long d2 = Stopwatch.GetTimestamp();

                drawTicksAcc += d1 - d0;
                presentTicksAcc += d2 - d1;
                if (!presented)
                    _commands.Enqueue(new Command(CommandKind.RebuildMonitors)); // device loss: rebuild the stack
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Render failed on {part.Monitor.Bounds}: {ex.Message}");
                _commands.Enqueue(new Command(CommandKind.RebuildMonitors));
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
            _drawTicksRing[_framePeriodHead] = drawTicksAcc;
            _presentTicksRing[_framePeriodHead] = presentTicksAcc;
            _waitTicksRing[_framePeriodHead] = _lastWaitTicks;
            _framePeriodHead = (_framePeriodHead + 1) & (_framePeriodTicks.Length - 1);
            if (_framePeriodCount < _framePeriodTicks.Length) _framePeriodCount++;
        }
        _lastFrameTs = ts;

        // Real-cursor suppression in drawing mode is handled in WM_SETCURSOR
        // (the NOREDIRECTIONBITMAP overlay is hit-tested over its whole rect, so
        // the blank cursor is refreshed on input activity).

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
            string fade = Preferences.FadeSeconds > 0 ? $"fade {Preferences.FadeSeconds:0.#}s" : "fade off";
            string smooth = $"smooth {SmoothingPresets.Title(Preferences.SmoothingLevel)}";
            string mode = IsClickThrough ? (_interactHeld ? "interact" : "click-through") : "draw";
            string style = $"{Preferences.Style.Id} #{_inkColor.R:X2}{_inkColor.G:X2}{_inkColor.B:X2}";
            string sound = Preferences.SoundOn ? "sound on" : "sound off";
            _status = busy
                ? $"TinyHand M3 | mode {mode} | {_fps} fps | mouse cb p95 {mouseStats?.P95Ms ?? 0:0.000} ms | kbd cb p95 {kbdStats?.P95Ms ?? 0:0.000} ms | strokes {Volatile.Read(ref _strokeCount)} | {style} | {sound} | {fade} | {smooth} | Ctrl+Alt+X clear | Ctrl+Alt+F fade | Ctrl+Alt+S smooth | Ctrl+Alt+H toggle | Esc close | Ctrl+Alt+Q exit app"
                : $"TinyHand M3 | mode {mode} | {_fps} fps | {style} | {sound} | {fade} | {smooth} | Ctrl+Alt+X clear | Ctrl+Alt+F fade | Ctrl+Alt+S smooth | Ctrl+Alt+H toggle | Esc close | Ctrl+Alt+Q exit app";
            _statusRefreshAt = now + 250;

            var stats = FrameStatsSnapshot();
            _fpsHud = busy
                ? $"{_fps} fps | p95 {stats.PeriodP95Ms:0.0} / max {stats.PeriodMaxMs:0.0} ms | draw {DrawAvgMs:0.0} / present {PresentAvgMs:0.0}"
                : $"{_fps} fps idle | p95 {stats.PeriodP95Ms:0.0} / max {stats.PeriodMaxMs:0.0} ms";
        }
    }

    /// <summary>p95/max of period, draw and present over the recent frame ring. Public for fpscheck.</summary>
    public FrameStats FrameStatsSnapshot()
    {
        int n = Math.Min(_framePeriodCount, _framePeriodTicks.Length);
        if (n == 0) return new FrameStats(0, 0, 0, 0, 0, 0);
        var periods = new long[n];
        var draws = new long[n];
        var presents = new long[n];
        Array.Copy(_framePeriodTicks, periods, n);
        Array.Copy(_drawTicksRing, draws, n);
        Array.Copy(_presentTicksRing, presents, n);
        Array.Sort(periods);
        Array.Sort(draws);
        Array.Sort(presents);
        double ticksToMs = 1000.0 / Stopwatch.Frequency;
        double at(long[] arr) => arr[(int)Math.Round(0.95 * (n - 1))] * ticksToMs;
        double mx(long[] arr) => arr[n - 1] * ticksToMs;
        return new FrameStats(at(periods), mx(periods), at(draws), mx(draws), at(presents), mx(presents));
    }

    /// <summary>Debug: last n frame periods in ms, oldest first (fpscheck histogram source).</summary>
    public double[] RecentFramePeriodsMs(int maxCount)
    {
        return RecentRingMs(_framePeriodTicks, maxCount);
    }

    /// <summary>Debug: last n present durations in ms, aligned with RecentFramePeriodsMs.</summary>
    public double[] RecentPresentsMs(int maxCount)
    {
        return RecentRingMs(_presentTicksRing, maxCount);
    }

    /// <summary>Debug: last n wait durations in ms, aligned with RecentFramePeriodsMs.</summary>
    public double[] RecentWaitsMs(int maxCount)
    {
        return RecentRingMs(_waitTicksRing, maxCount);
    }

    private double[] RecentRingMs(long[] ring, int maxCount)
    {
        int n = Math.Min(maxCount, Math.Min(_framePeriodCount, ring.Length));
        var arr = new double[n];
        double toMs = 1000.0 / Stopwatch.Frequency;
        for (int i = 0; i < n; i++)
            arr[i] = ring[(_framePeriodHead - n + ring.Length + i) % ring.Length] * toMs;
        return arr;
    }

    private SceneSnapshot BuildSnapshot()
    {
        var style = Preferences.Style;
        return new SceneSnapshot(
            Cursor: new Point((int)Interlocked.Read(ref _cursorX), (int)Interlocked.Read(ref _cursorY)),
            Toolbar: _toolbar ?? new ToolbarLayout(Rectangle.Empty, []),
            IsPrimary: _primaryMonitor?.IsPrimary ?? false,
            DpiX: (int)(_primaryMonitor?.DpiX ?? 96),
            Status: _status,
            FpsHud: _fpsHud,
            Toast: _clock.ElapsedMilliseconds < Interlocked.Read(ref _toastUntilMs) ? _toast : "",
            StyleId: style.Id,
            Laser: style.IsLaser,
            HandVisible: !_normalCursor,
            Press: Volatile.Read(ref _pressMilli) / 1000f,
            GripX: _gripX,
            SoundOn: Preferences.SoundOn);
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

        TraceIfWorthwhile(e);
        switch (Classify(e.Message))
        {
            case MouseEventKind.Move:
                if (_drawing) AppendStroke(e.X, e.Y);
                return false;

            case MouseEventKind.Down:
            {
                _buttonsDown++;
                _doubleShift.RegisterMouseDown();
                bool inToolbar = TryGetToolbarButton(e.X, e.Y, out ToolbarButton? button);
                var decision = MousePolicy.Decide(new MousePolicyInput(
                    Kind: MouseEventKind.Down,
                    IsActive: _active,
                    ClickThrough: IsClickThrough,
                    DownSwallowedByUs: _dragTracker.DownSwallowed,
                    InToolbar: inToolbar));
                return HandleDown(decision, button);
            }

            case MouseEventKind.Up:
            {
                if (_buttonsDown > 0) _buttonsDown--;
                bool swallow = _dragTracker.OnUp();
                _held = false;
                if (_drawing)
                {
                    _drawing = false;
                    if (_strokeStarted)
                    {
                        // Catch the stroke up to the release point: the filter and the
                        // stabilizer trail the cursor, so drain them instead of jumping to
                        // the raw point (a jump would leave a straight tail at the end).
                        AppendRelease(e.X, e.Y);
                        _journal.End();
                        _strokeStarted = false;
                    }
                    _dirty = true;
                }
                if (_buttonsDown == 0 && _pendingClickThroughFlip)
                {
                    // All buttons released: apply the deferred WS_EX_TRANSPARENT swap.
                    _pendingClickThroughFlip = false;
                    ApplyWindowMode();
                }
                return swallow;
            }

            case MouseEventKind.Wheel:
                return !IsClickThrough;

            default:
                return false;
        }
    }

    private void TraceIfWorthwhile(HookMouseEvent e)
    {
        if (!TraceEnabled) return;
        var kind = Classify(e.Message);
        if (kind is MouseEventKind.Down or MouseEventKind.Up)
            TraceLine($"mouse {kind} at ({e.X},{e.Y}) ctx={IsClickThrough} draw={_drawing} toolbar={TryGetToolbarButton(e.X, e.Y, out var button)}{(button is null ? "" : $" id={button.Id}")}");
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

        if (decision == MousePolicyResult.ToolbarCommand)
        {
            if (button is not null) RunToolbarCommand(button);
            return true;
        }

        if (tracked)
        {
            // macOS onMouseDown: the press pose and the tap sound fire on every swallowed
            // down; ink only appears once the pointer actually drags (4 pt threshold).
            Press(playSound: true);
            _drawing = true;
            _smoother.Reset();
            _stabilizer.Reset();
            _downPoint = _smoother.Filter(
                new PointF((float)Interlocked.Read(ref _cursorX), (float)Interlocked.Read(ref _cursorY)),
                _clock.Elapsed.TotalSeconds, Preferences.SmoothingLevel);
            _lastInkPoint = _downPoint;
            _strokeStarted = false;
            _dirty = true;
            return true;
        }

        // Interact mode: the click belongs to the app below, but the hand still presses
        // (macOS onInteractDown) — visible only when interactKeepsHand is on.
        if (_interactHeld) Press(playSound: false);
        return false;
    }

    /// <summary>Full press: the hand settles instantly (macOS <c>ArmView.press()</c>); the
    /// compositor decays it per frame once the button is released.</summary>
    private void Press(bool playSound)
    {
        Volatile.Write(ref _pressMilli, 1000);
        _held = true;
        if (playSound) _tapSound.Play();
        _dirty = true;
    }

    private void RunToolbarCommand(ToolbarButton button)
    {
        switch (button.Kind)
        {
            case ToolbarButtonKind.Style:
                SelectStyle(button.Id);
                break;
            case ToolbarButtonKind.Sound:
                ToggleSound();
                break;
            case ToolbarButtonKind.Exit:
                // Single path through SetActive keeps ActiveRequested (hotkey/menu mirror)
                // in sync with the real overlay state.
                SetActive(false);
                break;
        }
    }

    /// <summary>Pick a pointer style (macOS <c>OverlayController.pick</c>): the hand sprite and
    /// the ink colour follow it.</summary>
    public void SelectStyle(string id)
    {
        var style = PointerStyle.ById(id);
        if (style.Id == Preferences.Style.Id) return;
        Preferences.Style = style;
        _styleDirty = true;
        RefreshToolbar();
        ShowToast(style.Title);
        _dirty = true;
    }

    /// <summary>Toolbar speaker button: tap sound on/off.</summary>
    public void ToggleSound()
    {
        Preferences.SoundOn = !Preferences.SoundOn;
        _tapSound.Enabled = Preferences.SoundOn;
        RefreshToolbar();
        ShowToast(Preferences.SoundOn ? "Sound: on" : "Sound: off");
        _dirty = true;
    }

    private void ToggleClickThrough()
    {
        bool newMode = !_clickThrough;
        TraceLine($"toggle click-through -> {newMode}");
        _clickThrough = newMode;
        ShowToast(newMode ? "Click-through: on" : "Click-through: off");
        ApplyPointerMode();
    }

    /// <summary>Recompute the pointer mode after a click-through / interact change. The hand
    /// visibility follows immediately; the window style and the global cursor swap are
    /// deferred while a button is held (see the class comment).</summary>
    private void ApplyPointerMode()
    {
        _normalCursor = _interactHeld && !Preferences.InteractKeepsHand;
        _dirty = true;
        RefreshToolbar();

        if (_buttonsDown > 0)
        {
            _pendingClickThroughFlip = true;
            return;
        }
        ApplyWindowMode();
    }

    /// <summary>Apply the window styles and cursor mode that follow from the effective
    /// click-through state. Runs on the hook thread (immediate toggles and Up-deferred
    /// completions).</summary>
    private void ApplyWindowMode()
    {
        bool through = IsClickThrough;
        bool hideGlobal = through && !_normalCursor;

        // Hide the real cursor synchronously on the hook thread BEFORE flipping
        // WS_EX_TRANSPARENT: the moment the overlay stops hit-testing, the window
        // below answers WM_SETCURSOR and restores its cursor. The hide used to ride
        // the command queue (one overlay-loop period later, ~8-50 ms), which showed
        // the real cursor for a frame on every double-Shift toggle.
        if (hideGlobal)
            TryHideGlobalCursor();

        // Flip WS_EX_TRANSPARENT immediately from the hook thread (SetWindowLong on
        // extended styles does not send window messages); the compositor thread only
        // repaints the status/toolbar.
        if (_overlays is { } overlays)
            foreach (var part in overlays)
                part.Window.ClickThrough = through;

        // Coming back to drawing mode: the app below may have been raised above the overlay
        // by the click-through click, so re-assert the topmost band (macOS re-orders the
        // overlay window the same way when it returns to the active space).
        if (!through && _overlays is { } parts)
            foreach (var part in parts)
                User32.SetWindowPos(part.Window.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        // Restoring (SPI_SETCURSORS fallback) can be slow: keep it off the hook thread.
        _commands.Enqueue(new Command(hideGlobal ? CommandKind.HideGlobalCursor : CommandKind.RestoreGlobalCursor));
    }

    private bool OnKeyboardFilter(HookKeyEvent e)
    {
        if (!_active) return false;
        MarkActivity();

        // The tap detector sees every key: it needs the Shift edges, the other modifiers
        // (a tap only counts when Shift is alone) and the input count (typing a capital
        // letter must not toggle click-through).
        bool doubleTap = _doubleShift.RegisterKey(e.Vk, e.KeyDown);

        // Ctrl is the arrow modifier (macOS ⌘): track both edges so the head appears and
        // disappears without waiting for the next mouse move.
        if (e.Vk is VkControl or VkLeftControl or VkRightControl)
        {
            if (e.KeyDown != _ctrlDown)
            {
                _ctrlDown = e.KeyDown;
                _journal.Arrow = _ctrlDown;
                _dirty = true;
                TraceLine($"ctrl {(_ctrlDown ? "down" : "up")}");
            }
            return false; // never swallow Ctrl
        }

        if (e.Vk is VkShift or VkLeftShift or VkRightShift)
            _shiftDown = e.KeyDown;
        if (e.Vk is VkLeftWin or VkRightWin)
            _winDown = e.KeyDown;

        // Interact key: temporary click-through while held (macOS ⌥ → Alt).
        if (IsInteractKey(e.Vk))
            return HandleInteractKey(e.KeyDown);

        // The tap detector fires on the Shift *up* edge (a tap is down+up), so the trigger
        // must be evaluated before the keydown-only section below.
        if (doubleTap && Preferences.DoubleShiftClickThrough)
            ToggleClickThrough();

        if (!e.KeyDown) return false;

        if (e.Vk == VkEscape)
        {
            ClearStrokes();
            SetActive(false); // keep ActiveRequested in sync (see RunToolbarCommand)
            return true; // swallowed: the app below must not see TinyHand's Esc
        }

        if (e.IsOwnInjected) return false;

        TraceLine($"vk=0x{e.Vk:X2} down own={e.IsOwnInjected}");
        return false;
    }

    private bool IsInteractKey(uint vk) => Preferences.InteractKey switch
    {
        InteractKey.Alt => vk is VkMenu or VkLeftMenu or VkRightMenu,
        InteractKey.Ctrl => vk is VkControl or VkLeftControl or VkRightControl,
        InteractKey.Shift => vk is VkShift or VkLeftShift or VkRightShift,
        _ => false,
    };

    /// <summary>
    /// Interact key edge. The modifier is never swallowed: a low-level hook that eats a keyup
    /// leaves the system's key state stuck down (every later keystroke would arrive as
    /// Alt+key), so the Alt keyup always reaches the app. The consequence is the standard
    /// Windows behaviour of a lone Alt tap — the app below highlights its menu bar — which is
    /// documented in PLAN.md; the interact key is configurable (M4 settings).
    /// </summary>
    private bool HandleInteractKey(bool down)
    {
        if (Preferences.InteractKey == InteractKey.Off) return false;

        if (down)
        {
            if (_interactHeld) return false;
            // Alt alone: Ctrl+Alt is the app's hotkey prefix, so a chord must not flash the
            // real cursor (Windows refinement of the macOS ⌥ behaviour).
            if (_ctrlDown || _shiftDown || _winDown) return false;
            _interactHeld = true;
            TraceLine("interact down");
            ApplyPointerMode();
            return false;
        }

        if (!_interactHeld) return false;
        _interactHeld = false;
        TraceLine("interact up");
        ApplyPointerMode();
        return false;
    }

    private bool TryGetToolbarButton(int screenX, int screenY, out ToolbarButton? button)
    {
        button = null;
        if (IsClickThrough) return false;
        if (_primaryMonitor is not { } primary) return false;
        var bounds = primary.Bounds;
        if (!bounds.Contains(screenX, screenY)) return false;
        if (_toolbar is not { } layout) return false;
        int localX = screenX - bounds.X;
        int localY = screenY - bounds.Y;
        if (!ToolbarModel.PanelContains(layout, localX, localY)) return false;
        button = ToolbarModel.HitTest(layout, localX, localY);
        return true;
    }

    /// <summary>macOS OverlayController.drag(): the stroke starts only after 4 pt of movement
    /// (a plain click leaves no ink), then every point at least 1.5 pt away is appended.
    /// Points pass through the One Euro filter and then the streamline stabilizer, so the
    /// hand's jitter and the mouse's 1 px quantization do not wobble the line.</summary>
    private void AppendStroke(int x, int y)
    {
        int scale = PrimaryScale();
        var to = _stabilizer.Push(
            _smoother.Filter(new PointF(x, y), _clock.Elapsed.TotalSeconds, Preferences.SmoothingLevel),
            SmoothingPresets.Window(Preferences.SmoothingLevel) * scale);
        if (!_strokeStarted)
        {
            if (InkGeometry.Distance(_downPoint, to) < 4 * scale) return;
            _journal.Begin(_downPoint);
            _strokeStarted = true;
            _lastInkPoint = _downPoint;
            Interlocked.Increment(ref _strokeCount);
        }
        AppendPoint(to, scale);
    }

    /// <summary>Release catch-up: the stabilizer trails the cursor by ~5 px, so shrink its
    /// window to zero while pushing the release point. The weighted average converges to the
    /// cursor along a smooth curve instead of jumping there with a straight tail.</summary>
    private void AppendRelease(int x, int y)
    {
        int scale = PrimaryScale();
        var release = new PointF(x, y);
        double window = SmoothingPresets.Window(Preferences.SmoothingLevel) * scale;
        foreach (var point in _stabilizer.Drain(release, window))
            AppendPoint(point, scale);
        AppendPoint(release, scale); // the stroke must end exactly under the cursor
    }

    private void AppendPoint(PointF point, int scale)
    {
        if (!InkMetrics.ShouldAppend(_lastInkPoint, point, scale)) return;
        _journal.Extend(point);
        _lastInkPoint = point;
        _dirty = true;
    }

    private int PrimaryScale()
    {
        var primary = _primaryMonitor;
        return primary is null ? 1 : Math.Max(1, (int)Math.Round(primary.DpiX / 96.0, MidpointRounding.AwayFromZero));
    }

    private void ClearStrokes()
    {
        _journal.Clear();
        _strokeStarted = false;
        _drawing = false;
        Interlocked.Exchange(ref _strokeCount, 0);
        _dirty = true;
    }

    /// <summary>Ctrl+Alt+X: wipe all ink without leaving drawing mode (macOS clear shortcut).</summary>
    public void ClearInk()
    {
        ClearStrokes();
        ShowToast("Ink cleared");
    }

    /// <summary>Transient on-screen feedback for hotkey/toolbar changes.</summary>
    private void ShowToast(string text)
    {
        _toast = text;
        Interlocked.Exchange(ref _toastUntilMs, _clock.ElapsedMilliseconds + ToastMs);
        _dirty = true;
    }

    /// <summary>Ctrl+Alt+F: toggle the per-stroke auto-fade (macOS <c>fadeSeconds ≤ 0</c> keeps
    /// ink forever). Layers read <see cref="Preferences.FadeSeconds"/> every frame, so the
    /// change applies immediately — including to strokes that are already fading.</summary>
    public void ToggleFade()
    {
        Preferences.FadeSeconds = Preferences.FadeSeconds > 0 ? 0 : Preferences.DefaultFadeSeconds;
        ShowToast(Preferences.FadeSeconds > 0 ? $"Fade: {Preferences.FadeSeconds:0.#} s" : "Fade: off");
        _dirty = true;
    }

    /// <summary>Ctrl+Alt+S: cycle the pointer smoothing strength (off → light → … → max).</summary>
    public void ToggleSmoothing()
    {
        Preferences.SmoothingLevel = SmoothingPresets.Next(Preferences.SmoothingLevel);
        ShowToast($"Smoothing: {SmoothingPresets.Title(Preferences.SmoothingLevel)}");
        _dirty = true;
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityMs, _clock.ElapsedMilliseconds);

    private long IdleElapsedMs() => _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastActivityMs);

    // TH_TRACE=1: hook decisions + lifecycle events appended to tinyhand-trace.log
    // next to the executable (works for GUI mode, where there is no console).
    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("TH_TRACE") == "1";
    private static void TraceLine(string message)
    {
        if (!TraceEnabled) return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "tinyhand-trace.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"trace write failed: {ex.Message}");
        }
    }
}
