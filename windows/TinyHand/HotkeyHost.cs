using System.Runtime.InteropServices;
using TinyHand.Interop;

namespace TinyHand;

/// <summary>
/// Message-only helper window that receives WM_HOTKEY for RegisterHotKey
/// registrations (Ctrl+Alt+H toggle). Runs on the WinForms UI thread.
/// </summary>
internal sealed class HotkeyHost : NativeWindow, IDisposable
{
    public const int ToggleId = 1;
    public const uint ToggleModifiers = Win32.MOD_CONTROL | Win32.MOD_ALT;
    public const uint ToggleVk = 0x48; // 'H'

    public const int QuitId = 2;
    public const uint QuitVk = 0x51; // 'Q' — full app quit (tray menu also has Exit)

    public const int ClearId = 3;
    public const uint ClearVk = 0x58; // 'X' — clear ink (macOS ⌃⌥X)

    public const int FadeId = 4;
    public const uint FadeVk = 0x46; // 'F' — toggle the ink auto-fade

    public const int SmoothId = 5;
    public const uint SmoothVk = 0x53; // 'S' — cycle the pointer smoothing strength

    public event Action<int>? HotkeyPressed;

    public HotkeyHost()
    {
        CreateHandle(new CreateParams { Caption = "TinyHandHotkeyHost" });
    }

    public bool RegisterToggle() =>
        User32.RegisterHotKey(Handle, ToggleId, ToggleModifiers | Win32.MOD_NOREPEAT, ToggleVk);

    public bool RegisterQuit() =>
        User32.RegisterHotKey(Handle, QuitId, ToggleModifiers | Win32.MOD_NOREPEAT, QuitVk);

    public bool RegisterClear() =>
        User32.RegisterHotKey(Handle, ClearId, ToggleModifiers | Win32.MOD_NOREPEAT, ClearVk);

    public bool RegisterFade() =>
        User32.RegisterHotKey(Handle, FadeId, ToggleModifiers | Win32.MOD_NOREPEAT, FadeVk);

    public bool RegisterSmooth() =>
        User32.RegisterHotKey(Handle, SmoothId, ToggleModifiers | Win32.MOD_NOREPEAT, SmoothVk);

    public void Dispose()
    {
        User32.UnregisterHotKey(Handle, ToggleId);
        User32.UnregisterHotKey(Handle, QuitId);
        User32.UnregisterHotKey(Handle, ClearId);
        User32.UnregisterHotKey(Handle, FadeId);
        User32.UnregisterHotKey(Handle, SmoothId);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Win32.WM_HOTKEY)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}
