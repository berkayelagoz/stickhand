using System.Windows.Forms;
using StickHand.Interop;

namespace StickHand.Logic;

/// <summary>
/// A global hotkey: Win32 modifier mask (<see cref="Win32.MOD_CONTROL"/> etc.) plus a virtual
/// key, with the display text the settings window shows. macOS keeps the same triple in
/// <c>Hotkey</c> (keyCode, modifiers, display); the Windows port stores the modifier mask
/// and the virtual key so <c>RegisterHotKey</c> can be re-run when the user records a new one.
/// </summary>
public sealed record Hotkey(uint Modifiers, uint Vk, string Display)
{
    public static readonly Hotkey ToggleDefault = new(
        Win32.MOD_CONTROL | Win32.MOD_ALT, 0x48, "Ctrl+Alt+H");

    public static readonly Hotkey ClearDefault = new(
        Win32.MOD_CONTROL | Win32.MOD_ALT, 0x58, "Ctrl+Alt+X");

    /// <summary>Build a shortcut from a recorded key press. Returns false when the chord has
    /// no Ctrl/Alt/Shift/Win modifier (a bare key would swallow normal typing).</summary>
    public static bool TryFromKey(Keys key, Keys modifiers, out Hotkey shortcut)
    {
        shortcut = null!;
        uint mods = 0;
        if ((modifiers & Keys.Control) != 0) mods |= Win32.MOD_CONTROL;
        if ((modifiers & Keys.Alt) != 0) mods |= Win32.MOD_ALT;
        if ((modifiers & Keys.Shift) != 0) mods |= Win32.MOD_SHIFT;
        if (mods == 0) return false;

        uint vk = (uint)(key & Keys.KeyCode);
        if (vk is 0 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C) return false; // modifier-only press
        shortcut = new Hotkey(mods, vk, Format(mods, vk));
        return true;
    }

    /// <summary>Display text for a modifier mask + virtual key (<c>Ctrl+Alt+H</c>).</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>(5);
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    /// <summary>Human name for a virtual key (letters, digits, F-keys and the common keys).</summary>
    public static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK 0x{vk:X2}",
        };
    }

    /// <summary>Parse a stored display string (<c>Ctrl+Alt+H</c>). Unknown keys fail so a
    /// corrupt settings file falls back to the default instead of registering garbage.</summary>
    public static bool TryParse(string? text, out Hotkey shortcut)
    {
        shortcut = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        uint vk = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= Win32.MOD_CONTROL;
                    continue;
                case "alt":
                    mods |= Win32.MOD_ALT;
                    continue;
                case "shift":
                    mods |= Win32.MOD_SHIFT;
                    continue;
                case "win" or "windows":
                    mods |= Win32.MOD_WIN;
                    continue;
            }
            if (!TryParseKey(raw, out vk)) return false;
        }

        if (mods == 0 || vk == 0) return false;
        shortcut = new Hotkey(mods, vk, Format(mods, vk));
        return true;
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int f)
            && f is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + f);
            return true;
        }

        vk = name.ToLowerInvariant() switch
        {
            "backspace" => 0x08,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "end" => 0x23,
            "home" => 0x24,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "num*" => 0x6A,
            "num+" => 0x6B,
            "num-" => 0x6D,
            "num." => 0x6E,
            "num/" => 0x6F,
            ";" => 0xBA,
            "=" => 0xBB,
            "," => 0xBC,
            "-" => 0xBD,
            "." => 0xBE,
            "/" => 0xBF,
            "`" => 0xC0,
            "[" => 0xDB,
            "\\" => 0xDC,
            "]" => 0xDD,
            "'" => 0xDE,
            _ => 0,
        };
        return vk != 0;
    }

    public override string ToString() => Display;
}
