namespace TinyHand.Logic;

public sealed record ToolbarButton(string Id, string Label, System.Drawing.Rectangle Rect);

/// <summary>
/// Pure toolbar geometry for the M1 prototype: a strip centered at the top of the
/// primary monitor with two buttons (click-through toggle, exit). Rects are in
/// monitor-local physical pixels; hit-testing happens inside the low-level hook
/// because swallowed events never arrive as window messages.
/// </summary>
public static class ToolbarModel
{
    // Prototype sizing (4K @100% makes a 190x40 strip hard to spot); keep in sync
    // with scripts/e2e-m1.ps1, which mirrors this geometry for the injected clicks.
    public const int ButtonHeightPx = 56;
    public const int TopMarginPx = 16;
    public const int GapPx = 12;
    public const int ToggleWidthPx = 280;
    public const int ExitWidthPx = 120;

    public static string ToggleLabel(bool clickThrough) => clickThrough ? "Click-through: ON" : "Click-through: OFF";

    public static int Scale(int dpi) => Math.Max(1, (int)Math.Round(dpi / 96.0, MidpointRounding.AwayFromZero));

    public static List<ToolbarButton> Build(int monitorWidth, int dpi, bool clickThrough)
    {
        int s = Scale(dpi);
        int totalWidth = (ToggleWidthPx + GapPx + ExitWidthPx) * s;
        int x0 = Math.Max(0, (monitorWidth - totalWidth) / 2);
        int y = TopMarginPx * s;
        int h = ButtonHeightPx * s;

        return
        [
            new ToolbarButton("click_through", ToggleLabel(clickThrough),
                new System.Drawing.Rectangle(x0, y, ToggleWidthPx * s, h)),
            new ToolbarButton("exit", "Exit (Esc)",
                new System.Drawing.Rectangle(x0 + (ToggleWidthPx + GapPx) * s, y, ExitWidthPx * s, h)),
        ];
    }

    public static ToolbarButton? HitTest(List<ToolbarButton> buttons, int x, int y)
    {
        foreach (var b in buttons)
            if (b.Rect.Contains(x, y))
                return b;
        return null;
    }
}
