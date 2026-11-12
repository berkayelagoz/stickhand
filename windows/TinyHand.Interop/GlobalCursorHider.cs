using System.Runtime.InteropServices;

namespace TinyHand.Interop;

/// <summary>
/// Global real-cursor blackout based on SetSystemCursor, with save/restore of the
/// current cursor set. Used in click-through mode: the overlay window then receives
/// no WM_SETCURSOR (WS_EX_TRANSPARENT removes it from hit-testing), while the
/// contract requires the real cursor to stay hidden with the hand visible.
///
/// Bugs fixed 2026-11-12 (both observed in the first manual test run):
/// - The blank cursor is a 32bpp ARGB cursor with alpha 0 everywhere. A mono
///   CreateCursor(AND=FF/XOR=00) rendered as an opaque black square.
/// - Scheme originals are captured ONCE per process. After SetSystemCursor,
///   LoadImage(OCR_*) returns the replaced (blank) cursor, so re-saving on every
///   click-through episode poisoned the restore ("real cursor never comes back").
/// - If a restore call fails, SystemParametersInfo(SPI_SETCURSORS) reloads the
///   user's registry scheme as a fallback.
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

    private const int BlankSize = 32;

    private readonly Dictionary<uint, IntPtr> _saved = new();
    private IntPtr _blank;
    private bool _hidden;

    public bool IsHidden => _hidden;

    public void HideAll()
    {
        if (_hidden) return;
        if (_blank == IntPtr.Zero) _blank = CreateBlankCursor();
        if (_blank == IntPtr.Zero)
            throw new InvalidOperationException($"CreateIconIndirect failed: {Marshal.GetLastWin32Error()}");

        foreach (uint id in CursorIds)
        {
            // Save the scheme original only the first time we hide: later LoadImage
            // calls would return our own blank cursor, not the scheme original.
            if (!_saved.ContainsKey(id))
            {
                IntPtr current = User32.LoadImage(IntPtr.Zero, id, Win32.IMAGE_CURSOR, 0, 0, Win32.LR_SHARED);
                if (current == IntPtr.Zero) continue;
                _saved[id] = User32.CopyIcon(current);
            }
            User32.SetSystemCursor(User32.CopyIcon(_blank), id);
        }
        _hidden = true;
    }

    /// <summary>Restore the saved originals. Returns false if any SetSystemCursor call failed.</summary>
    public bool RestoreAll()
    {
        if (!_hidden) return true;
        bool ok = true;
        foreach (var (id, saved) in _saved)
        {
            if (saved != IntPtr.Zero && !User32.SetSystemCursor(User32.CopyIcon(saved), id))
                ok = false;
        }
        _hidden = false;
        return ok;
    }

    /// <summary>Fallback: reload the system cursors from the user's registry scheme.</summary>
    public static void RestoreSystemScheme() =>
        User32.SystemParametersInfo(User32.SPI_SETCURSORS, 0, IntPtr.Zero, 0);

    public void Dispose()
    {
        try
        {
            if (!RestoreAll())
                RestoreSystemScheme();
        }
        catch
        {
            try { RestoreSystemScheme(); } catch { /* last resort exhausted */ }
        }
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
        // 32bpp ARGB color bitmap with alpha 0 in every pixel: a fully transparent
        // cursor (not a black square). CreateDIBSection zero-fills the bits.
        var colorInfo = new BITMAPINFO();
        colorInfo.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        colorInfo.bmiHeader.biWidth = BlankSize;
        colorInfo.bmiHeader.biHeight = -BlankSize;
        colorInfo.bmiHeader.biPlanes = 1;
        colorInfo.bmiHeader.biBitCount = 32;

        var maskInfo = colorInfo;
        maskInfo.bmiHeader.biBitCount = 1;

        IntPtr hdc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        IntPtr color = Gdi32.CreateDIBSection(hdc, ref colorInfo, Win32.DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
        IntPtr mask = Gdi32.CreateDIBSection(hdc, ref maskInfo, Win32.DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
        Gdi32.DeleteDC(hdc);
        if (color == IntPtr.Zero || mask == IntPtr.Zero)
        {
            if (color != IntPtr.Zero) Gdi32.DeleteObject(color);
            if (mask != IntPtr.Zero) Gdi32.DeleteObject(mask);
            return IntPtr.Zero;
        }

        var iconInfo = new ICONINFO
        {
            fIcon = false, // cursor, not icon
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = mask,
            hbmColor = color,
        };
        IntPtr cursor = User32.CreateIconIndirect(ref iconInfo);
        // CreateIconIndirect copies the bitmaps; ours are no longer needed.
        Gdi32.DeleteObject(mask);
        Gdi32.DeleteObject(color);
        return cursor;
    }
}
