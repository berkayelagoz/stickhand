using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyHand.Interop;

/// <summary>Observer payload for a low-level mouse event (virtual desktop coordinates).</summary>
public sealed record HookMouseEvent(uint Message, int X, int Y, uint Flags, UIntPtr ExtraInfo, uint Time)
{
    public bool IsInjected => (Flags & Win32.LLMHF_INJECTED) != 0;
    public bool IsOwnInjected => IsInjected && ExtraInfo == unchecked((UIntPtr)Win32.ExtraInfoSignature);
}

/// <summary>Observer payload for a low-level keyboard event.</summary>
public sealed record HookKeyEvent(uint Message, uint Vk, uint Flags, UIntPtr ExtraInfo, uint Time)
{
    public bool KeyDown => Message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
    public bool IsInjected => (Flags & Win32.LLKHF_INJECTED) != 0;
    public bool IsOwnInjected => IsInjected && ExtraInfo == unchecked((UIntPtr)Win32.ExtraInfoSignature);
}

/// <summary>
/// Callback duration statistics for a hook. Durations measured from callback entry to the
/// moment the swallow decision is made. Source-to-hook latency is a separate metric that
/// is intentionally not conflated here (see PLAN.md, input mapping notes).
/// </summary>
public sealed class HookStats
{
    private readonly object _lock = new();
    private readonly double[] _samples;
    private int _count;

    public HookStats(int capacity = 4096) => _samples = new double[capacity];

    public void RecordMs(double milliseconds)
    {
        lock (_lock)
        {
            _samples[_count % _samples.Length] = milliseconds;
            _count++;
        }
    }

    public sealed record Snapshot(long Count, double AverageMs, double P50Ms, double P95Ms, double MaxMs);

    public Snapshot TakeSnapshot()
    {
        lock (_lock)
        {
            int n = (int)Math.Min((long)_count, _samples.Length);
            if (n == 0) return new Snapshot(_count, 0, 0, 0, 0);
            var copy = new double[n];
            Array.Copy(_samples, copy, n);
            Array.Sort(copy);
            double Percentile(double p)
            {
                int idx = (int)Math.Clamp(Math.Ceiling(p / 100.0 * n) - 1, 0, n - 1);
                return copy[idx];
            }
            return new Snapshot(_count, copy.Average(), Percentile(50), Percentile(95), copy[n - 1]);
        }
    }
}

/// <summary>
/// WH_MOUSE_LL / WH_KEYBOARD_LL installed on a dedicated thread with its own message
/// loop, isolated from the tray/overlay message pump. Filters decide whether an event
/// is swallowed (return true). The whole filter pipeline runs inside the hook callback
/// and is measured; keep handlers short.
/// </summary>
public sealed class LowLevelHooks : IDisposable
{
    private readonly Thread _thread;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly ManualResetEventSlim _started = new(false);

    private uint _threadId;
    private IntPtr _mouseHook;   // owned by the hook thread
    private IntPtr _keyboardHook;
    private volatile bool _running;

    /// <summary>Mouse filter. Return true to swallow the event. Handlers run on the hook thread.</summary>
    public event Func<HookMouseEvent, bool>? MouseFilter;

    /// <summary>Keyboard filter. Return true to swallow the event. Handlers run on the hook thread.</summary>
    public event Func<HookKeyEvent, bool>? KeyboardFilter;

    public HookStats MouseStats { get; } = new();
    public HookStats KeyboardStats { get; } = new();

    public bool IsRunning => _running;
    public string? LastError { get; private set; }

    public LowLevelHooks()
    {
        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;
        _thread = new Thread(Run)
        {
            Name = "TinyHand.LowLevelHooks",
            IsBackground = true,
        };
    }

    /// <summary>Installs both hooks on the hook thread. Returns true when the hook thread is pumping.</summary>
    public bool Start()
    {
        if (_running || _thread.IsAlive) throw new InvalidOperationException("Hook thread already started");
        LastError = null;
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            LastError = "Hook thread did not signal installation within 5s";
            return false;
        }
        return _running;
    }

    /// <summary>Posts WM_QUIT to the hook thread and joins it. Idempotent.</summary>
    public void Stop()
    {
        if (_thread.IsAlive)
            User32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            LastError = "Hook thread did not exit within 5s";
    }

    public void Dispose() => Stop();

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        try
        {
            IntPtr module = Kernel32.GetModuleHandle(null);
            _mouseHook = User32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = User32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);

            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
                LastError = $"SetWindowsHookEx failed (mouse={_mouseHook != IntPtr.Zero}, kbd={_keyboardHook != IntPtr.Zero}, error={Marshal.GetLastWin32Error()})";

            _running = _mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero;
            _started.Set();

            while (User32.GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero) User32.UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) User32.UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = IntPtr.Zero;
            _keyboardHook = IntPtr.Zero;
            _running = false;
            _started.Set();
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long start = Stopwatch.GetTimestamp();
            bool swallow = false;
            try
            {
                var raw = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var ev = new HookMouseEvent((uint)wParam.ToInt64(), raw.pt.X, raw.pt.Y, raw.flags, raw.dwExtraInfo, raw.time);

                if (MouseFilter is { } filter)
                {
                    foreach (Func<HookMouseEvent, bool> handler in filter.GetInvocationList())
                        if (handler(ev)) swallow = true;
                }
            }
            catch
            {
                // Swallowing inside a hook callback must never depend on broken handler state.
            }
            MouseStats.RecordMs((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            if (swallow) return (IntPtr)1;
        }
        return User32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long start = Stopwatch.GetTimestamp();
            bool swallow = false;
            try
            {
                var raw = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var ev = new HookKeyEvent((uint)wParam.ToInt64(), raw.vkCode, raw.flags, raw.dwExtraInfo, raw.time);

                if (KeyboardFilter is { } filter)
                {
                    foreach (Func<HookKeyEvent, bool> handler in filter.GetInvocationList())
                        if (handler(ev)) swallow = true;
                }
            }
            catch
            {
            }
            KeyboardStats.RecordMs((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            if (swallow) return (IntPtr)1;
        }
        return User32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }
}
