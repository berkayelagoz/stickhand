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

    public event Action<int>? HotkeyPressed;

    public HotkeyHost()
    {
        CreateHandle(new CreateParams { Caption = "TinyHandHotkeyHost" });
    }

    public bool RegisterToggle() =>
        User32.RegisterHotKey(Handle, ToggleId, ToggleModifiers | Win32.MOD_NOREPEAT, ToggleVk);

    public bool RegisterQuit() =>
        User32.RegisterHotKey(Handle, QuitId, ToggleModifiers | Win32.MOD_NOREPEAT, QuitVk);

    public void Dispose()
    {
        User32.UnregisterHotKey(Handle, ToggleId);
        User32.UnregisterHotKey(Handle, QuitId);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Win32.WM_HOTKEY)
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}
