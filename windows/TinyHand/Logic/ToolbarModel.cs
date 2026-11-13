namespace TinyHand.Logic;

public enum ToolbarButtonKind
{
    /// <summary>Pick a pointer style (36 built-in hands + the laser dot).</summary>
    Style,

    /// <summary>Toggle the tap sound.</summary>
    Sound,

    /// <summary>Close the overlay (macOS toolbar ✕).</summary>
    Exit,
}

public sealed record ToolbarButton(
    string Id,
    string Label,
    System.Drawing.Rectangle Rect,
    ToolbarButtonKind Kind,
    bool Selected = false);

/// <summary>Panel rect plus the button grid, monitor-local physical pixels.</summary>
public sealed record ToolbarLayout(System.Drawing.Rectangle Panel, List<ToolbarButton> Buttons);

/// <summary>
/// M3 toolbar: every pointer style (36 hands + laser), the tap-sound toggle and exit, laid
/// out as a dark-glass strip at the top of the primary monitor. Rects are monitor-local
/// physical pixels; hit-testing happens inside the low-level hook because swallowed events
/// never arrive as window messages (the M1 finding).
///
/// Visual contract with macOS <c>Toolbar.swift</c>: dark translucent panel, 16 pt corner
/// radius, 6 pt spacing between buttons, selected style highlighted. The macOS panel shows
/// the pinned styles (300×56); the Windows panel exposes the whole set, so the grid wraps
/// when the monitor is too narrow for one row.
/// </summary>
public static class ToolbarModel
{
    public const int ButtonSizePx = 40;
    public const int GapPx = 6;
    public const int PaddingPx = 10;
    public const int TopMarginPx = 16;
    public const int RadiusPx = 16;
    public const int SideMarginPx = 24;

    public static int Scale(int dpi) => Math.Max(1, (int)Math.Round(dpi / 96.0, MidpointRounding.AwayFromZero));

    public static string SoundLabel(bool soundOn) => soundOn ? "Sound on" : "Sound off";

    public static ToolbarLayout Build(int monitorWidth, int dpi, string selectedStyle, bool soundOn)
    {
        int s = Scale(dpi);
        int button = ButtonSizePx * s;
        int gap = GapPx * s;
        int padding = PaddingPx * s;

        var styles = PointerStyle.All;
        int count = styles.Count + 2; // + sound + exit
        int available = Math.Max(button, monitorWidth - 2 * SideMarginPx * s);
        int columns = Math.Clamp((available - 2 * padding + gap) / (button + gap), 1, count);
        int rows = (count + columns - 1) / columns;

        int panelWidth = columns * button + (columns - 1) * gap + 2 * padding;
        int panelHeight = rows * button + (rows - 1) * gap + 2 * padding;
        int x0 = Math.Max(0, (monitorWidth - panelWidth) / 2);
        int y0 = TopMarginPx * s;
        var panel = new System.Drawing.Rectangle(x0, y0, panelWidth, panelHeight);

        var buttons = new List<ToolbarButton>(count);
        int index = 0;

        System.Drawing.Rectangle Slot() => new(
            x0 + padding + (index % columns) * (button + gap),
            y0 + padding + (index / columns) * (button + gap),
            button, button);

        foreach (var style in styles)
        {
            buttons.Add(new ToolbarButton(style.Id, style.Title, Slot(), ToolbarButtonKind.Style,
                Selected: style.Id == selectedStyle));
            index++;
        }
        buttons.Add(new ToolbarButton("sound", SoundLabel(soundOn), Slot(), ToolbarButtonKind.Sound));
        index++;
        buttons.Add(new ToolbarButton("exit", "Exit (Esc)", Slot(), ToolbarButtonKind.Exit));

        return new ToolbarLayout(panel, buttons);
    }

    public static ToolbarButton? HitTest(ToolbarLayout layout, int x, int y)
    {
        foreach (var button in layout.Buttons)
            if (button.Rect.Contains(x, y))
                return button;
        return null;
    }

    /// <summary>True when the point is inside the panel (the strip swallows clicks even
    /// between buttons, so a near-miss does not draw on the app below).</summary>
    public static bool PanelContains(ToolbarLayout layout, int x, int y) => layout.Panel.Contains(x, y);
}
