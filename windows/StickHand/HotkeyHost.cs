using System.Runtime.InteropServices;
using StickHand.Interop;
using StickHand.Logic;

namespace StickHand;

/// <summary>
/// Message-only helper window that receives WM_HOTKEY for RegisterHotKey registrations.
/// Runs on the WinForms UI thread. The toggle and clear chords come from
/// <see cref="Preferences"/> (macOS ⌃⌥H / ⌃⌥X defaults) and are re-registered when the
/// settings window records a new one; fade, smoothing and quit stay fixed.
/// </summary>
internal sealed class HotkeyHost : NativeWindow, IDisposable
{
    public const int ToggleId = 1;
    public const int QuitId = 2;
    public const int ClearId = 3;
    public const int FadeId = 4;
    public const int SmoothId = 5;

    public static readonly Hotkey QuitShortcut = new(Win32.MOD_CONTROL | Win32.MOD_ALT, 0x51, "Ctrl+Alt+Q");
    public static readonly Hotkey FadeShortcut = new(Win32.MOD_CONTROL | Win32.MOD_ALT, 0x46, "Ctrl+Alt+F");
    public static readonly Hotkey SmoothShortcut = new(Win32.MOD_CONTROL | Win32.MOD_ALT, 0x53, "Ctrl+Alt+S");

    public event Action<int>? HotkeyPressed;

    public HotkeyHost()
    {
        CreateHandle(new CreateParams { Caption = "StickHandHotkeyHost" });
    }

    /// <summary>Register (or re-register) the pointer toggle chord.</summary>
    public bool RegisterToggle(Hotkey shortcut) => Register(ToggleId, shortcut);

    /// <summary>Register (or re-register) the clear-ink chord.</summary>
    public bool RegisterClear(Hotkey shortcut) => Register(ClearId, shortcut);

    public bool RegisterQuit() => Register(QuitId, QuitShortcut);

    public bool RegisterFade() => Register(FadeId, FadeShortcut);

    public bool RegisterSmooth() => Register(SmoothId, SmoothShortcut);

    /// <summary>Release the toggle chord while the settings window records a new one
    /// (macOS unregisters its hotkeys during recording for the same reason).</summary>
    public void UnregisterToggle() => User32.UnregisterHotKey(Handle, ToggleId);

    public void UnregisterClear() => User32.UnregisterHotKey(Handle, ClearId);

    private bool Register(int id, Hotkey shortcut)
    {
        User32.UnregisterHotKey(Handle, id);
        return User32.RegisterHotKey(Handle, id, shortcut.Modifiers | Win32.MOD_NOREPEAT, shortcut.Vk);
    }

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
