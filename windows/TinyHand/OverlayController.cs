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
    }

    // macOS OverlayController reference values.
    private const int BusyIntervalMs = 1000 / 120;
    private const int IdleIntervalMs = 1000 / 20;
    private const long IdleAfterMs = 500;
    private const uint VkEscape = 0x1B;

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
    private int _fps;
    private long _statusRefreshAt;
    private string _status = "";

    private readonly Thread _thread;
    private volatile bool _quit;

    public event Action<bool>? ActiveChanged;

    public bool IsActive => _active;
    public bool IsClickThrough => _clickThrough;
    public bool ActiveRequested { get; private set; }

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
                int interval = _active ? (busy ? BusyIntervalMs : IdleIntervalMs) : 25;

                if (render)
                {
                    RenderFrame(busy);
                    _dirty = false;
                }

                PreciseWait(interval);
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

    private void PreciseWait(int intervalMs)
    {
        long target = Stopwatch.GetTimestamp() + (long)intervalMs * Stopwatch.Frequency / 1000;
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

        _active = true;
        _dirty = true;
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

        foreach (var part in overlays)
        {
            try
            {
                _scene.Draw(part.Surface.Graphics, part.Monitor, snapshot);
                part.Surface.PresentToRedirected(part.Window.Handle);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Render failed on {part.Monitor.Bounds}: {ex.Message}");
            }
        }

        // Real-cursor suppression in drawing mode: Windows calls that succeed only
        // when the cursor is over one of our windows; combined with WM_SETCURSOR this
        // covers the cases where a swallowed event produced no window message at all.
        if (!_clickThrough)
            User32.SetCursor(IntPtr.Zero);

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
                ? $"TinyHand M1 | mode {(_clickThrough ? "click-through" : "draw")} | {_fps} fps | mouse cb p95 {mouseStats?.P95Ms ?? 0:0.000} ms | kbd cb p95 {kbdStats?.P95Ms ?? 0:0.000} ms | strokes {snapshot.Strokes.Count} | Ctrl+Alt+H toggle | Esc quit"
                : $"TinyHand M1 | mode {(_clickThrough ? "click-through" : "draw")} | {_fps} fps | Ctrl+Alt+H toggle | Esc quit";
            _statusRefreshAt = now + 250;
        }
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
            Status: _status);
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
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityMs, _clock.ElapsedMilliseconds);
}
