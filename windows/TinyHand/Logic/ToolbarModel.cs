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
    public const int ButtonHeightPx = 40;
    public const int TopMarginPx = 12;
    public const int GapPx = 8;
    public const int ToggleWidthPx = 190;
    public const int ExitWidthPx = 80;

    public static string ToggleLabel(bool clickThrough) => clickThrough ? "Click-through: ON" : "Click-through: OFF";

    public static int Scale(int dpi) => Math.Max(1, dpi / 96);

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
            new ToolbarButton("exit", "Exit",
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
