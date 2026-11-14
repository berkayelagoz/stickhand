using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StickHand.Interop;

/// <summary>
/// A 32bpp premultiplied-ARGB DIB that GDI+ draws into directly, ready for
/// UpdateLayeredWindow. Create on the thread that owns the target window.
/// </summary>
public sealed class LayeredSurface : IDisposable
{
    private IntPtr _memDC;
    private IntPtr _dib;
    private IntPtr _oldObj;

    public int Width { get; }
    public int Height { get; }
    public Bitmap Bitmap { get; }
    public Graphics Graphics { get; }

    public LayeredSurface(int width, int height)
    {
        Width = width;
        Height = height;

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // top-down, matches GDI+ scan0
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        _memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (_memDC == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
        _dib = Gdi32.CreateDIBSection(_memDC, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        _oldObj = Gdi32.SelectObject(_memDC, _dib);

        Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        Graphics = Graphics.FromImage(Bitmap);
    }

    /// <summary>Publish the current bitmap content to a layered window via UpdateLayeredWindow.</summary>
    public bool PresentToRedirected(IntPtr hwnd)
    {
        var size = new SIZE { Width = Width, Height = Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        return User32.UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDC, ref src, 0, ref blend, Win32.ULW_ALPHA);
    }

    /// <summary>
    /// Dirty-region publish via UpdateLayeredWindowIndirect: only the given window-space
    /// rectangle is copied from the DIB; the rest of the surface keeps its last content.
    /// </summary>
    public unsafe bool PresentDirtyToRedirected(IntPtr hwnd, System.Drawing.Rectangle dirty)
    {
        int left = Math.Clamp(dirty.Left, 0, Width);
        int top = Math.Clamp(dirty.Top, 0, Height);
        int right = Math.Clamp(dirty.Right, 0, Width);
        int bottom = Math.Clamp(dirty.Bottom, 0, Height);
        if (right <= left || bottom <= top) return false;

        var size = new SIZE { Width = Width, Height = Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };

        var info = new UPDATELAYEREDWINDOWINFO
        {
            cbSize = (uint)sizeof(UPDATELAYEREDWINDOWINFO),
            hdcDst = IntPtr.Zero,
            pptDst = null,
            psize = size,
            hdcSrc = _memDC,
            pptSrc = &src,
            crKey = 0,
            pblend = &blend,
            dwFlags = Win32.ULW_ALPHA,
            prcDirty = &rect,
        };
        return User32.UpdateLayeredWindowIndirect(hwnd, ref info);
    }

    public void Dispose()
    {
        Graphics.Dispose();
        Bitmap.Dispose();
        if (_memDC != IntPtr.Zero)
        {
            if (_oldObj != IntPtr.Zero) Gdi32.SelectObject(_memDC, _oldObj);
            if (_dib != IntPtr.Zero) Gdi32.DeleteObject(_dib);
            Gdi32.DeleteDC(_memDC);
            _memDC = IntPtr.Zero;
        }
    }
}
