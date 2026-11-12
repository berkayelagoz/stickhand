using System.Drawing;
using System.Runtime.InteropServices;

namespace TinyHand.Interop;

/// <summary>
/// Raw Win32 layered overlay window: one per monitor. Created with
/// WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, painted
/// exclusively through UpdateLayeredWindow from a premultiplied surface.
///
/// Hit-testing notes (verified by the M1 prototype, see PLAN.md):
/// - Layered windows pass hit-test through where alpha == 0. A background cleared
///   to alpha == 1 is invisible but takes the hit, so the overlay receives
///   WM_SETCURSOR over the whole monitor.
/// - Click-through is a window-style-level decision: WS_EX_TRANSPARENT removes the
///   window from hit-testing entirely, including over opaque hand pixels.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "TinyHandOverlay";

    private static readonly WndProcDelegate WndProc = OverlayWndProc;
    private static ushort s_classAtom;
    private static readonly object s_classLock = new();

    public IntPtr Handle { get; private set; }
    public Rectangle Bounds { get; private set; }

    /// <summary>Return true to hide the real cursor while it is over this window (WM_SETCURSOR).</summary>
    public event Func<OverlayWindow, bool>? CursorHidden;

    /// <summary>Plane change / mode change notification (WM_DISPLAYCHANGE, WM_DPICHANGED).</summary>
    public event Action<OverlayWindow, uint>? SystemMessage;

    private OverlayWindow(IntPtr handle, Rectangle bounds)
    {
        Handle = handle;
        Bounds = bounds;
    }

    public static OverlayWindow Create(Rectangle bounds)
    {
        lock (s_classLock)
        {
            if (s_classAtom == 0)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
                    hInstance = Kernel32.GetModuleHandle(null),
                    lpszMenuName = "",
                    lpszClassName = ClassName,
                };
                s_classAtom = User32.RegisterClassEx(ref wc);
                if (s_classAtom == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            }
        }

        uint exStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW;
        IntPtr handle = User32.CreateWindowEx(
            exStyle, ClassName, "", Win32.WS_POPUP,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, Kernel32.GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        var window = new OverlayWindow(handle, bounds);
        s_windows[handle] = window;
        return window;
    }

    private static readonly Dictionary<IntPtr, OverlayWindow> s_windows = new();

    private static IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        OverlayWindow? window = null;
        lock (s_windows) s_windows.TryGetValue(hWnd, out window);
        if (window is null) return User32.DefWindowProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case Win32.WM_SETCURSOR:
                // LOWORD(lParam) hit-test code; HTCLIENT = 1.
                if ((lParam.ToInt64() & 0xFFFF) == 1)
                {
                    bool hidden = window.CursorHidden?.Invoke(window) ?? false;
                    if (hidden)
                    {
                        User32.SetCursor(IntPtr.Zero);
                        return (IntPtr)1;
                    }
                }
                break;
            case Win32.WM_DPICHANGED:
                window.SystemMessage?.Invoke(window, (uint)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            case Win32.WM_DISPLAYCHANGE:
                window.SystemMessage?.Invoke(window, msg);
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                lock (s_windows) s_windows.Remove(hWnd);
                break;
        }
        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Show() => User32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);

    public void Hide() => User32.ShowWindow(Handle, Win32.SW_HIDE);

    public void Present(LayeredSurface surface)
    {
        if (!surface.PresentToRedirected(Handle))
            System.Diagnostics.Debug.WriteLine($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>Move/resize the overlay to new monitor bounds.</summary>
    public void MoveTo(Rectangle bounds)
    {
        Bounds = bounds;
        User32.SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private bool _clickThrough;

    /// <summary>
    /// Toggle WS_EX_TRANSPARENT. Note: the setter may be called from the hook thread so
    /// the style flips before the next composition tick; the owning thread issue is benign
    /// because SetWindowLong on extended styles does not send window messages.
    /// </summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            int exStyle = User32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
            exStyle = value
                ? exStyle | unchecked((int)Win32.WS_EX_TRANSPARENT)
                : exStyle & ~unchecked((int)Win32.WS_EX_TRANSPARENT);
            User32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, exStyle);
            _clickThrough = value;
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            User32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
