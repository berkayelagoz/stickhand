using System.Runtime.InteropServices;

namespace TinyHand.Interop;

/// <summary>
/// Global real-cursor blackout based on SetSystemCursor, with save/restore of the
/// current cursor set. Used in click-through mode: the overlay window then receives
/// no WM_SETCURSOR (WS_EX_TRANSPARENT removes it from hit-testing), while the
/// contract requires the real cursor to stay hidden with the hand visible.
///
/// Fallback after an abnormal exit: SystemParametersInfo(SPI_SETCURSORS) resets from
/// the registry. Plan decision (M1): accepted as the only reliable global mechanism.
/// </summary>
public sealed class GlobalCursorHider : IDisposable
{
    private static readonly uint[] CursorIds =
    {
        Win32.OCR_NORMAL, Win32.OCR_IBEAM, Win32.OCR_WAIT, Win32.OCR_CROSSHAIR,
        Win32.OCR_UP, Win32.OCR_SIZENWSE, Win32.OCR_SIZENESW, Win32.OCR_SIZEWE,
        Win32.OCR_SIZENS, Win32.OCR_SIZEALL, Win32.OCR_NO, Win32.OCR_HAND,
        Win32.OCR_APPSTARTING,
    };

    private readonly Dictionary<uint, IntPtr> _saved = new();
    private IntPtr _blank;
    private bool _hidden;

    public bool IsHidden => _hidden;

    public void HideAll()
    {
        if (_hidden) return;
        _blank = _blank == IntPtr.Zero ? CreateBlankCursor() : _blank;
        if (_blank == IntPtr.Zero) throw new InvalidOperationException("CreateCursor failed for blank cursor");

        foreach (uint id in CursorIds)
        {
            // Save a private copy of the current system cursor before replacing it.
            IntPtr current = User32.LoadImage(IntPtr.Zero, id, Win32.IMAGE_CURSOR, 0, 0, Win32.LR_SHARED);
            if (current == IntPtr.Zero) continue;
            _saved[id] = User32.CopyIcon(current);
            User32.SetSystemCursor(User32.CopyIcon(_blank), id);
        }
        _hidden = true;
    }

    public void RestoreAll()
    {
        if (!_hidden) return;
        foreach (var (id, saved) in _saved)
        {
            if (saved != IntPtr.Zero)
                User32.SetSystemCursor(User32.CopyIcon(saved), id);
        }
        _hidden = false;
    }

    public void Dispose()
    {
        try { RestoreAll(); }
        finally
        {
            foreach (var saved in _saved.Values)
                User32.DestroyCursor(saved);
            _saved.Clear();
            if (_blank != IntPtr.Zero)
            {
                User32.DestroyCursor(_blank);
                _blank = IntPtr.Zero;
            }
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        // 8x8 AND mask all 1s + XOR mask all 0s => fully transparent cursor.
        byte[] andMask = new byte[8];
        byte[] xorMask = new byte[8];
        for (int i = 0; i < andMask.Length; i++) andMask[i] = 0xFF;
        return User32.CreateCursor(IntPtr.Zero, 0, 0, 8, 8, andMask, xorMask);
    }
}
