using System.Drawing;

namespace StickHand.Logic;

/// <summary>
/// macOS <c>Preferences.InkChoice</c> (birebir): the default <see cref="Match"/> draws in
/// the selected hand style's colour ("el hangi renkse o renkte çizer"); the fixed options
/// use the same hex values as the macOS table. The settings UI that writes this lands in M4.
/// </summary>
public enum InkChoice
{
    Match,
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Purple,
    White,
    Black,
}

/// <summary>macOS <c>Ink.swift</c> colour constants plus the <c>InkChoice</c> fixed palette.</summary>
public static class InkPalette
{
    /// <summary><c>Ink.purple</c> — sRGB(0.53, 0.33, 0.95).</summary>
    public static readonly Color Purple = Rgb(0x8754F2);

    /// <summary><c>Ink.red</c> — sRGB(1.0, 0.16, 0.12).</summary>
    public static readonly Color Red = Color.FromArgb(255, 255, 41, 31);

    public static Color Rgb(uint hex) => Color.FromArgb(
        255, (int)((hex >> 16) & 0xFF), (int)((hex >> 8) & 0xFF), (int)(hex & 0xFF));

    /// <summary>The fixed colour of an <see cref="InkChoice"/>, or null for <see cref="InkChoice.Match"/>.</summary>
    public static Color? Fixed(InkChoice choice) => choice switch
    {
        InkChoice.Match => null,
        InkChoice.Red => Red,
        InkChoice.Orange => Rgb(0xFF8A00),
        InkChoice.Yellow => Rgb(0xFFCC00),
        InkChoice.Green => Rgb(0x30C25A),
        InkChoice.Blue => Rgb(0x0A84FF),
        InkChoice.Purple => Rgb(0x8754F2),
        InkChoice.White => Rgb(0xFFFFFF),
        InkChoice.Black => Rgb(0x1D1D21),
        _ => null,
    };

    /// <summary>macOS <c>InkChoice.resolved(for:)</c>: fixed colour, else the style's ink colour.</summary>
    public static Color Resolve(InkChoice choice, PointerStyle style) => Fixed(choice) ?? style.Ink;
}

/// <summary>
/// macOS <c>PointerStyle</c> table: id, title and ink colour per built-in hand. The M3
/// toolbar exposes all 36 styles plus the laser dot; the ink colour contract resolves
/// through the same table.
/// </summary>
public sealed record PointerStyle(string Id, string Title, Color Ink)
{
    public static readonly PointerStyle Lavender = new("lavender", "Lavender toy", InkPalette.Purple);
    public static readonly PointerStyle Glove = new("glove", "Cursor glove", InkPalette.Purple);
    public static readonly PointerStyle Coral = new("coral", "Coral vinyl", InkPalette.Red);
    public static readonly PointerStyle Plush = new("plush", "Plush", InkPalette.Rgb(0x2F86F0));
    public static readonly PointerStyle Robot = new("robot", "Robot", InkPalette.Rgb(0x16A87A));
    public static readonly PointerStyle Pink = new("pink", "Bubblegum", InkPalette.Rgb(0xEE4F98));
    public static readonly PointerStyle Blue = new("blue", "Sky", InkPalette.Rgb(0x2F86F0));
    public static readonly PointerStyle Mint = new("mint", "Mint", InkPalette.Rgb(0x16A87A));
    public static readonly PointerStyle Lemon = new("lemon", "Lemon", InkPalette.Rgb(0xF2B705));
    public static readonly PointerStyle Black = new("black", "Midnight", InkPalette.Rgb(0x1D1D21));
    public static readonly PointerStyle Chrome = new("chrome", "Chrome", InkPalette.Rgb(0x6B7380));
    public static readonly PointerStyle Holo = new("holo", "Holo", InkPalette.Purple);
    public static readonly PointerStyle Fuzzy = new("fuzzy", "Fuzzy", InkPalette.Purple);
    public static readonly PointerStyle Wood = new("wood", "Wood", InkPalette.Rgb(0x9A5E2C));
    public static readonly PointerStyle Marble = new("marble", "Marble", InkPalette.Rgb(0x6B7380));
    public static readonly PointerStyle Terrazzo = new("terrazzo", "Terrazzo", InkPalette.Rgb(0xF0561D));
    public static readonly PointerStyle Glass = new("glass", "Glass", InkPalette.Rgb(0x1597E5));
    public static readonly PointerStyle Jelly = new("jelly", "Jelly", InkPalette.Rgb(0xA33FE6));
    public static readonly PointerStyle Pastel = new("pastel", "Pastel", InkPalette.Rgb(0xEE4F98));
    public static readonly PointerStyle Pixel = new("pixel", "Pixel", InkPalette.Rgb(0x1D1D21));
    public static readonly PointerStyle LowPoly = new("lowpoly", "Low poly", InkPalette.Purple);
    public static readonly PointerStyle Knit = new("knit", "Knit", InkPalette.Purple);
    public static readonly PointerStyle Skeleton = new("skeleton", "Skeleton", InkPalette.Rgb(0x9A5E2C));
    public static readonly PointerStyle Astro = new("astro", "Astronaut", InkPalette.Rgb(0x2F86F0));
    public static readonly PointerStyle Space = new("space", "Space", InkPalette.Red);
    public static readonly PointerStyle Monster = new("monster", "Monster", InkPalette.Rgb(0x3E9E2E));
    public static readonly PointerStyle Water = new("water", "Water", InkPalette.Rgb(0x1597E5));
    public static readonly PointerStyle Lava = new("lava", "Lava", InkPalette.Rgb(0xF0561D));
    public static readonly PointerStyle Grass = new("grass", "Grass", InkPalette.Rgb(0x3E9E2E));
    public static readonly PointerStyle Cactus = new("cactus", "Cactus", InkPalette.Rgb(0x3E9E2E));
    public static readonly PointerStyle Choco = new("choco", "Chocolate", InkPalette.Rgb(0x9A5E2C));
    public static readonly PointerStyle Cloud = new("cloud", "Cloud", InkPalette.Rgb(0x2F86F0));
    public static readonly PointerStyle Mech = new("mech", "Mech", InkPalette.Rgb(0xF0561D));
    public static readonly PointerStyle Neon = new("neon", "Neon", InkPalette.Rgb(0xE0309A));
    public static readonly PointerStyle Leaf = new("leaf", "Leaf", InkPalette.Rgb(0x3E9E2E));
    public static readonly PointerStyle Cookie = new("cookie", "Gingerbread", InkPalette.Rgb(0x9A5E2C));

    /// <summary>macOS <c>PointerStyle.laser</c>: no hand sprite, red ink.</summary>
    public static readonly PointerStyle Laser = new("laser", "Laser dot", InkPalette.Red);

    /// <summary>The 36 built-in hands, in the macOS table order (toolbar order in M3).</summary>
    public static IReadOnlyList<PointerStyle> BuiltIn { get; } =
    [
        Lavender, Glove, Coral, Plush, Robot, Pink, Blue, Mint, Lemon, Black, Chrome, Holo,
        Fuzzy, Wood, Marble, Terrazzo, Glass, Jelly, Pastel, Pixel, LowPoly, Knit, Skeleton,
        Astro, Space, Monster, Water, Lava, Grass, Cactus, Choco, Cloud, Mech, Neon, Leaf, Cookie,
    ];

    /// <summary>
    /// The style the M1/M1.5 prototype drew: <c>hand_black.png</c> is the macOS "Midnight"
    /// asset, so <see cref="InkChoice.Match"/> resolved to #1D1D21. M3 selects the style from
    /// the toolbar; the macOS default is lavender (#8754F2).
    /// </summary>
    public static PointerStyle Default => Lavender;

    /// <summary>True for the laser dot: no hand sprite, a glowing red dot at the pointer.</summary>
    public bool IsLaser => Id == Laser.Id;

    /// <summary>True for a user-made hand (macOS <c>PointerStyle.isCustom</c>).</summary>
    public bool IsCustom => CustomHands.Hand(Id) is not null;

    /// <summary>Sprite name: <c>hand_lavender</c> for built-ins, the raw id for custom hands
    /// (their PNG lives in the hands directory), null for the laser.</summary>
    public string? AssetName => IsLaser ? null : IsCustom ? Id : $"hand_{Id}";

    /// <summary>All 36 built-in hands, the user's custom hands, then the laser dot — the
    /// macOS toolbar order. Computed on each call so a new custom hand shows up immediately.</summary>
    public static IReadOnlyList<PointerStyle> All =>
    [
        .. BuiltIn,
        .. CustomHands.All.Select(hand => new PointerStyle(hand.Id, hand.Name, InkPalette.Rgb(hand.Ink))),
        Laser,
    ];

    public static PointerStyle ById(string id)
    {
        if (CustomHands.Hand(id) is { } custom)
            return new PointerStyle(custom.Id, custom.Name, InkPalette.Rgb(custom.Ink));
        if (id == Laser.Id) return Laser;
        return BuiltIn.FirstOrDefault(style => style.Id == id) ?? Default;
    }
}

/// <summary>macOS <c>ArmView.inkWidth</c> / <c>Ink.addHead</c> / <c>Ink.extend</c> metrics.</summary>
public static class InkMetrics
{
    /// <summary><c>ArmView.inkWidth</c>: <c>max(3, screenHeight × 0.0055)</c>.</summary>
    public const float WidthFactor = 0.0055f;

    /// <summary>Minimum stroke width in points (scaled by DPI on Windows).</summary>
    public const float MinWidthPoints = 3f;

    /// <summary><c>Ink.extend</c> drops points closer than 1.5 pt to the previous one.</summary>
    public const float DedupPoints = 1.5f;

    /// <summary><c>Ink.addHead</c>: <c>size = max(14, lineWidth × 3.2)</c>.</summary>
    public const float HeadFactor = 3.2f;

    /// <summary>Minimum arrow-head size in points (scaled by DPI on Windows).</summary>
    public const float MinHeadPoints = 14f;

    /// <summary>Stroke width in physical pixels for a monitor of the given height.</summary>
    public static float Width(int monitorHeightPx, int scale) =>
        Math.Max(MinWidthPoints * scale, monitorHeightPx * WidthFactor);

    /// <summary>Arrow-head size in physical pixels for the given stroke width.</summary>
    public static float HeadSize(float lineWidth, int scale) =>
        Math.Max(MinHeadPoints * scale, lineWidth * HeadFactor);

    /// <summary>True when the new point is far enough from the last one to be appended.</summary>
    public static bool ShouldAppend(PointF last, PointF next, int scale)
    {
        double dx = next.X - last.X;
        double dy = next.Y - last.Y;
        double min = DedupPoints * scale;
        return dx * dx + dy * dy >= min * min;
    }
}

/// <summary>
/// macOS <c>Ink.fade</c>: per-stroke, delay <c>max(0.5, fadeSeconds)</c>, duration 0.6 s,
/// easeIn; the stroke is removed from its source when the fade completes. <c>fadeSeconds ≤ 0</c>
/// keeps ink forever (<c>Ink.keep</c>).
/// </summary>
public static class InkFade
{
    public const double DurationSeconds = 0.6;

    public static bool Keep(double fadeSeconds) => fadeSeconds <= 0;

    public static double DelaySeconds(double fadeSeconds) => Math.Max(0.5, fadeSeconds);

    /// <summary>Stroke opacity at <paramref name="elapsedSeconds"/> after the stroke ended.</summary>
    public static float Opacity(double elapsedSeconds, double fadeSeconds)
    {
        if (Keep(fadeSeconds)) return 1f;
        double delay = DelaySeconds(fadeSeconds);
        if (elapsedSeconds <= delay) return 1f;
        double t = (elapsedSeconds - delay) / DurationSeconds;
        if (t >= 1) return 0f;
        return (float)(1 - EaseIn(t));
    }

    /// <summary>CAMediaTimingFunction <c>.easeIn</c> = cubic-bezier(0.42, 0, 1, 1).</summary>
    public static double EaseIn(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        // Newton iterations on the x component, then evaluate y.
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Bezier(t, 0.42, 1.0) - x;
            double slope = BezierSlope(t, 0.42, 1.0);
            if (Math.Abs(slope) < 1e-6) break;
            t = Math.Clamp(t - error / slope, 0, 1);
        }
        return Bezier(t, 0.0, 1.0);
    }

    private static double Bezier(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double BezierSlope(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }
}

/// <summary>macOS <c>Ink.smoothPath</c> / <c>Ink.addHead</c> geometry, kept pure for tests.
/// Points are sub-pixel: the smoothed centreline must not be re-quantized to whole pixels,
/// otherwise the stroke edge inherits a 1 px staircase.</summary>
public static class InkGeometry
{
    /// <summary>
    /// Arrow head at the end of a stroke: two lines from the end point, aimed along the last
    /// stretch (a wobble at the very end must not twist the head). Mirrors <c>Ink.addHead</c>.
    /// </summary>
    public static bool TryHead(IReadOnlyList<PointF> points, float lineWidth, int scale,
        out PointF end, out PointF left, out PointF right)
    {
        end = left = right = default;
        if (points.Count == 0) return false;

        end = points[^1];
        float size = InkMetrics.HeadSize(lineWidth, scale);

        // Last point at least size × 1.2 away from the end; the first point otherwise.
        PointF back = points[0];
        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (Distance(points[i], end) >= size * 1.2)
            {
                back = points[i];
                break;
            }
        }

        double length = Distance(back, end);
        if (length <= 1) return false;

        float head = (float)Math.Min(size, length * 0.8);
        double angle = Math.Atan2(end.Y - back.Y, end.X - back.X);
        const double spread = Math.PI / 7;
        left = Offset(end, angle + Math.PI + spread, head);
        right = Offset(end, angle + Math.PI - spread, head);
        return true;
    }

    /// <summary>Midpoint of two points (the smooth path's quad-curve endpoints).</summary>
    public static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    public static double Distance(PointF a, PointF b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PointF Offset(PointF origin, double angle, float distance) => new(
        origin.X + (float)(Math.Cos(angle) * distance),
        origin.Y + (float)(Math.Sin(angle) * distance));
}

/// <summary>
/// macOS <c>Preferences.InteractKey</c>: the key held down for a temporary click-through.
/// macOS defaults to ⌥ Option; the Windows mapping is Alt (⌘ → Ctrl, ⌃ → Shift).
/// </summary>
public enum InteractKey
{
    Alt,
    Ctrl,
    Shift,
    Off,
}

/// <summary>
/// Preference values (macOS <c>Preferences</c>). M4 persists them to
/// <c>%APPDATA%\StickHand\settings.json</c> through <see cref="SettingsStore"/>; environment
/// variables still override the initial values for diagnostics (<c>TH_FADE=0</c> keeps ink
/// forever, <c>TH_STYLE=laser</c>, <c>TH_INTERACT=off</c>, <c>TH_KEEPS_HAND=1</c>,
/// <c>TH_SOUND=0</c>, <c>TH_TAPSOUND=click</c>) and the hotkeys / toolbar / settings window
/// change them at runtime.
/// </summary>
public static class Preferences
{
    public const double DefaultFadeSeconds = 3;

    /// <summary>macOS <c>Preferences.pointerSize</c> default (hand length as a screen fraction).</summary>
    public const float DefaultPointerSize = 0.13f;

    private static double _fadeSeconds = ReadFadeSeconds();

    /// <summary>Auto-fade delay in seconds; 0 keeps ink forever (macOS <c>fadeSeconds ≤ 0</c>).</summary>
    public static double FadeSeconds
    {
        get => Volatile.Read(ref _fadeSeconds);
        set
        {
            Volatile.Write(ref _fadeSeconds, value);
            SettingsStore.Update(d => d.FadeSeconds = value);
        }
    }

    private static int _inkChoice = (int)ReadInkChoice();

    /// <summary>Ink colour choice (macOS <c>Preferences.inkChoice</c>, default match).</summary>
    public static InkChoice InkChoice
    {
        get => (InkChoice)Volatile.Read(ref _inkChoice);
        set
        {
            Volatile.Write(ref _inkChoice, (int)value);
            SettingsStore.Update(d => d.InkChoice = value.ToString());
        }
    }

    private static int _smoothingLevel = (int)ReadSmoothing();

    /// <summary>Pointer smoothing strength; Ctrl+Alt+S cycles it at runtime.</summary>
    public static SmoothingLevel SmoothingLevel
    {
        get => (SmoothingLevel)Volatile.Read(ref _smoothingLevel);
        set
        {
            Volatile.Write(ref _smoothingLevel, (int)value);
            SettingsStore.Update(d => d.SmoothingLevel = value.ToString());
        }
    }

    private static PointerStyle _style = ReadStyle();

    /// <summary>Selected pointer style (toolbar pick). The ink colour follows it in
    /// <see cref="InkChoice.Match"/> mode.</summary>
    public static PointerStyle Style
    {
        get => Volatile.Read(ref _style);
        set
        {
            Volatile.Write(ref _style, value);
            SettingsStore.Update(d => d.Style = value.Id);
        }
    }

    private static float _pointerSize = ReadFloat("TH_POINTER_SIZE", DefaultPointerSize);

    /// <summary>Hand length as a fraction of the screen height (macOS 0.08…0.20).</summary>
    public static float PointerSize
    {
        get => Volatile.Read(ref _pointerSize);
        set
        {
            float clamped = Math.Clamp(value, 0.08f, 0.20f);
            Volatile.Write(ref _pointerSize, clamped);
            SettingsStore.Update(d => d.PointerSize = clamped);
        }
    }

    private static int _showShadow = ReadFlag("TH_SHADOW", true) ? 1 : 0;

    /// <summary>Draw the hand's drop shadow (macOS <c>Preferences.showShadow</c>).</summary>
    public static bool ShowShadow
    {
        get => Volatile.Read(ref _showShadow) != 0;
        set
        {
            Volatile.Write(ref _showShadow, value ? 1 : 0);
            SettingsStore.Update(d => d.ShowShadow = value);
        }
    }

    private static int _interactKey = (int)ReadInteractKey();

    /// <summary>Key held for a temporary click-through (macOS ⌥ Option → Alt).</summary>
    public static InteractKey InteractKey
    {
        get => (InteractKey)Volatile.Read(ref _interactKey);
        set
        {
            Volatile.Write(ref _interactKey, (int)value);
            SettingsStore.Update(d => d.InteractKey = value.ToString());
        }
    }

    private static int _interactKeepsHand = ReadFlag("TH_KEEPS_HAND", false) ? 1 : 0;

    /// <summary>macOS <c>interactKeepsHand</c>: true keeps the hand visible while the interact
    /// key is held; false (default) shows the real cursor and hides the hand.</summary>
    public static bool InteractKeepsHand
    {
        get => Volatile.Read(ref _interactKeepsHand) != 0;
        set
        {
            Volatile.Write(ref _interactKeepsHand, value ? 1 : 0);
            SettingsStore.Update(d => d.InteractKeepsHand = value);
        }
    }

    private static int _soundOn = ReadFlag("TH_SOUND", true) ? 1 : 0;

    /// <summary>Tap sound on/off (toolbar speaker button).</summary>
    public static bool SoundOn
    {
        get => Volatile.Read(ref _soundOn) != 0;
        set
        {
            Volatile.Write(ref _soundOn, value ? 1 : 0);
            SettingsStore.Update(d => d.SoundOn = value);
        }
    }

    private static int _doubleShiftClickThrough = ReadFlag("TH_DOUBLE_SHIFT", true) ? 1 : 0;

    /// <summary>macOS <c>doubleShiftClickThrough</c>: double-tapping Shift latches click-through.</summary>
    public static bool DoubleShiftClickThrough
    {
        get => Volatile.Read(ref _doubleShiftClickThrough) != 0;
        set
        {
            Volatile.Write(ref _doubleShiftClickThrough, value ? 1 : 0);
            SettingsStore.Update(d => d.DoubleShiftClickThrough = value);
        }
    }

    private static int _tapSound = (int)ReadTapSound();

    /// <summary>Tap sound choice (macOS <c>Preferences.tapSound</c>, default thock).</summary>
    public static TapSoundChoice TapSound
    {
        get => (TapSoundChoice)Volatile.Read(ref _tapSound);
        set
        {
            Volatile.Write(ref _tapSound, (int)value);
            SettingsStore.Update(d => d.TapSound = value.ToString());
        }
    }

    private static Hotkey _toggleShortcut = ReadShortcut("toggleShortcut", Hotkey.ToggleDefault);

    /// <summary>Global shortcut that turns the pointer on or off (macOS ⌃⌥H).</summary>
    public static Hotkey ToggleShortcut
    {
        get => Volatile.Read(ref _toggleShortcut);
        set
        {
            Volatile.Write(ref _toggleShortcut, value);
            SettingsStore.Update(d => d.ToggleShortcut = value.Display);
        }
    }

    private static Hotkey _clearShortcut = ReadShortcut("clearShortcut", Hotkey.ClearDefault);

    /// <summary>Global shortcut that clears the drawing (macOS ⌃⌥X).</summary>
    public static Hotkey ClearShortcut
    {
        get => Volatile.Read(ref _clearShortcut);
        set
        {
            Volatile.Write(ref _clearShortcut, value);
            SettingsStore.Update(d => d.ClearShortcut = value.Display);
        }
    }

    private static int _welcomeShown = ReadFlag("TH_WELCOME_SHOWN", false) ? 1 : 0;

    /// <summary>macOS <c>welcomeShown</c>: the first-run card is shown once.</summary>
    public static bool WelcomeShown
    {
        get => Volatile.Read(ref _welcomeShown) != 0;
        set
        {
            Volatile.Write(ref _welcomeShown, value ? 1 : 0);
            SettingsStore.Update(d => d.WelcomeShown = value);
        }
    }

    /// <summary>Re-read every value from <see cref="SettingsStore"/> (settings window reset,
    /// harness round-trip checks). Environment overrides still win.</summary>
    public static void Reload()
    {
        var data = SettingsStore.Data;
        Volatile.Write(ref _fadeSeconds, ReadFadeSeconds());
        Volatile.Write(ref _inkChoice, (int)ReadInkChoice());
        Volatile.Write(ref _smoothingLevel, (int)ReadSmoothing());
        Volatile.Write(ref _style, ReadStyle());
        Volatile.Write(ref _pointerSize, ReadFloat("TH_POINTER_SIZE", data.PointerSize ?? DefaultPointerSize));
        Volatile.Write(ref _showShadow, ReadFlag("TH_SHADOW", data.ShowShadow ?? true) ? 1 : 0);
        Volatile.Write(ref _interactKey, (int)ReadInteractKey());
        Volatile.Write(ref _interactKeepsHand, ReadFlag("TH_KEEPS_HAND", data.InteractKeepsHand ?? false) ? 1 : 0);
        Volatile.Write(ref _soundOn, ReadFlag("TH_SOUND", data.SoundOn ?? true) ? 1 : 0);
        Volatile.Write(ref _doubleShiftClickThrough, ReadFlag("TH_DOUBLE_SHIFT", data.DoubleShiftClickThrough ?? true) ? 1 : 0);
        Volatile.Write(ref _tapSound, (int)ReadTapSound());
        Volatile.Write(ref _toggleShortcut, ReadShortcut("toggleShortcut", Hotkey.ToggleDefault));
        Volatile.Write(ref _clearShortcut, ReadShortcut("clearShortcut", Hotkey.ClearDefault));
        Volatile.Write(ref _welcomeShown, ReadFlag("TH_WELCOME_SHOWN", data.WelcomeShown ?? false) ? 1 : 0);
    }

    /// <summary>macOS <c>Preferences.resetAll</c>: every value back to its default.</summary>
    public static void ResetAll()
    {
        SettingsStore.Reset();
        Reload();
    }

    private static double ReadFadeSeconds()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_FADE");
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            return value;
        return SettingsStore.Data.FadeSeconds ?? DefaultFadeSeconds;
    }

    private static float ReadFloat(string name, float fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }

    private static bool ReadFlag(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw)) return fallback;
        return raw is not ("0" or "false" or "off");
    }

    private static PointerStyle ReadStyle()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_STYLE");
        if (!string.IsNullOrEmpty(raw)) return PointerStyle.ById(raw);
        string? saved = SettingsStore.Data.Style;
        return string.IsNullOrEmpty(saved) ? PointerStyle.Default : PointerStyle.ById(saved);
    }

    private static InteractKey ReadInteractKey()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_INTERACT") ?? SettingsStore.Data.InteractKey;
        return raw?.ToLowerInvariant() switch
        {
            "off" or "none" => InteractKey.Off,
            "ctrl" or "control" => InteractKey.Ctrl,
            "shift" => InteractKey.Shift,
            _ => InteractKey.Alt,
        };
    }

    private static TapSoundChoice ReadTapSound()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_TAPSOUND") ?? SettingsStore.Data.TapSound;
        return Enum.TryParse<TapSoundChoice>(raw, ignoreCase: true, out var choice)
            ? choice
            : TapSoundChoice.Thock;
    }

    private static InkChoice ReadInkChoice()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_INK") ?? SettingsStore.Data.InkChoice;
        return Enum.TryParse<InkChoice>(raw, ignoreCase: true, out var choice)
            ? choice
            : InkChoice.Match;
    }

    private static SmoothingLevel ReadSmoothing()
    {
        string? raw = Environment.GetEnvironmentVariable("TH_SMOOTH") ?? SettingsStore.Data.SmoothingLevel;
        return Enum.TryParse<SmoothingLevel>(raw, ignoreCase: true, out var level)
            ? level
            : SmoothingLevel.Max;
    }

    private static Hotkey ReadShortcut(string key, Hotkey fallback)
    {
        string? raw = key == "toggleShortcut" ? SettingsStore.Data.ToggleShortcut : SettingsStore.Data.ClearShortcut;
        return Hotkey.TryParse(raw, out var shortcut) ? shortcut : fallback;
    }
}
