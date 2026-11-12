using System.Drawing;
using TinyHand.Interop;
using TinyHand.Logic;

namespace TinyHand.Overlay;

/// <summary>Immutable snapshot of the scene, copied under lock for each frame.</summary>
public sealed record SceneSnapshot(
    Point Cursor,
    bool ClickThrough,
    List<List<Point>> Strokes,
    List<ToolbarButton> Toolbar,
    bool IsPrimary,
    int DpiX,
    string Status)
{
    public static SceneSnapshot Empty { get; } = new(new Point(), false, [], [], false, 96, "");
}

/// <summary>
/// GDI+ scene renderer for the M1 prototype. Draws into a 32bpp premultiplied surface:
/// alpha=1 background (invisible, but takes the hit-test so WM_SETCURSOR reaches us),
/// ink strokes, the hand at the cursor, toolbar strip and a diagnostics status line.
/// </summary>
public sealed class HandScene
{
    // Hand asset is 1024x1536 (portrait, fingertip up). M1 anchors the fingertip at the
    // cursor; per-style tip/tipX factors from macOS PointerStyle are ported in M4.
    private const int HandHeightPx = 200;
    private const float TipAnchorX = 0.5f;

    private static readonly Lazy<Bitmap> Hand = new(LoadHand);

    private static Bitmap LoadHand()
    {
        var asm = typeof(HandScene).Assembly;
        using var stream = asm.GetManifestResourceStream("TinyHand.assets.hand_black.png")
            ?? throw new InvalidOperationException("Embedded hand_black.png not found");
        return new Bitmap(stream);
    }

    public void Draw(Graphics g, MonitorInfo monitor, in SceneSnapshot scene)
    {
        g.Clear(Color.FromArgb(1, 0, 0, 0));

        int scale = Math.Max(1, (int)(monitor.DpiX / 96));

        // Screen-coordinate space translated into this monitor's local space.
        g.TranslateTransform(-monitor.Bounds.X, -monitor.Bounds.Y);

        DrawStrokes(g, scene.Strokes, scale);
        DrawHand(g, scene, scale);

        if (scene.IsPrimary)
        {
            DrawToolbar(g, scene.Toolbar, scale);
            DrawStatus(g, monitor, scene.Status, scale);
        }
    }

    private static void DrawStrokes(Graphics g, List<List<Point>> strokes, int scale)
    {
        if (strokes.Count == 0) return;
        using var pen = new Pen(Color.FromArgb(255, 244, 51, 118), 4f * scale)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };
        foreach (var stroke in strokes)
        {
            if (stroke.Count < 2) continue;
            g.DrawLines(pen, stroke.Select(p => (PointF)p).ToArray());
        }
    }

    private static void DrawHand(Graphics g, in SceneSnapshot scene, int scale)
    {
        int h = HandHeightPx * scale;
        float w = h * (Hand.Value.Width / (float)Hand.Value.Height);
        float x = scene.Cursor.X - w * TipAnchorX;
        float y = scene.Cursor.Y - 8 * scale; // fingertip sits at the cursor point

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(Hand.Value, x, y, w, h);

        if (scene.ClickThrough)
        {
            using var font = new Font("Segoe UI", 11f * scale, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(255, 244, 51, 90));
            g.DrawString("click-through", font, brush, x + w + 6 * scale, y + 4 * scale);
        }
    }

    private static void DrawToolbar(Graphics g, List<ToolbarButton> buttons, int scale)
    {
        using var bg = new SolidBrush(Color.FromArgb(235, 28, 28, 32));
        using var border = new Pen(Color.FromArgb(200, 255, 255, 255), scale);
        using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        using var font = new Font("Segoe UI", 12f * scale, FontStyle.Bold);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        foreach (var button in buttons)
        {
            g.FillRectangle(bg, button.Rect);
            g.DrawRectangle(border, button.Rect);
            var rectf = new RectangleF(button.Rect.X, button.Rect.Y, button.Rect.Width, button.Rect.Height);
            g.DrawString(button.Label, font, textBrush, rectf, format);
        }
    }

    private static void DrawStatus(Graphics g, MonitorInfo monitor, string status, int scale)
    {
        if (string.IsNullOrEmpty(status)) return;
        using var font = new Font("Consolas", 12f);
        using var brush = new SolidBrush(Color.FromArgb(200, 40, 40, 46)); // semi-transparent, still hits alpha != 0 budget? keep low alpha
        float x = 14 * scale;
        float y = monitor.Bounds.Height - 28 * scale;
        g.DrawString(status, font, brush, x, y);
    }
}
