using System.Drawing;

namespace TinyHand.Interop;

/// <summary>
/// Physical monitor description (top-left origin of the virtual desktop).
/// </summary>
public sealed record MonitorInfo(IntPtr Handle, Rectangle Bounds, bool IsPrimary, uint DpiX, uint DpiY)
{
    public double Scale => DpiX / 96.0;
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        var proc = new MonitorEnumProc((hMonitor, hdc, ref rect, data) =>
        {
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (User32.GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                try { Shcore.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out dpiX, out dpiY); }
                catch (EntryPointNotFoundException) { /* pre-Win10 fallback; PMv2 manifest present anyway */ }
                var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                bool primary = (info.dwFlags & Win32.MONITORF_PRIMARY) != 0;
                result.Add(new MonitorInfo(hMonitor, bounds, primary, dpiX, dpiY));
            }
            return true;
        });
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return result;
    }

    public static MonitorInfo? Primary() => All().FirstOrDefault(m => m.IsPrimary) ?? All().FirstOrDefault();

    /// <summary>Refresh rate and resolution of the given monitor's current mode.</summary>
    public static (int width, int height, int hz)? CurrentMode()
    {
        var dm = new DEVMODE { dmDeviceName = new string('\0', 32), dmFormName = new string('\0', 32) };
        dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>();
        if (User32.EnumDisplaySettings(null, Win32.ENUM_CURRENT_SETTINGS, ref dm))
            return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
        return null;
    }
}
