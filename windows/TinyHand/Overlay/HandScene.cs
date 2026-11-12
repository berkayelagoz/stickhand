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
    string Status,
    string FpsHud)
{
    public static SceneSnapshot Empty { get; } = new(new Point(), false, [], [], false, 96, "", "");
}

/// <summary>
/// GDI+ scene renderer for the M1 prototype. Draws into a 32bpp premultiplied surface:
/// alpha=1 background (invisible, but takes the hit-test so WM_SETCURSOR reaches us),
/// ink strokes, the hand at the cursor, toolbar strip and a diagnostics status line.
/// </summary>
public sealed class HandScene
{
    /// <summary>Diagnostics only (fpscheck --opaque): present an opaque background to
    /// compare UpdateLayeredWindow cost against the near-transparent alpha=1 fill.</summary>
    public static bool OpaqueBackground;

    // Hand asset is 1024x1536 (portrait, fingertip up). M1 anchors the fingertip at the
    // cursor; per-style tip/tipX factors from macOS PointerStyle are ported in M4.
    private const int HandHeightPx = 200;
    private const float TipAnchorX = 0.5f;

    private static readonly Lazy<Bitmap> Hand = new(LoadHand);

    private static readonly Dictionary<int, Bitmap> ScaledHandCache = new();
    private static readonly Dictionary<int, Font> ToolbarFontCache = new();
    private static readonly Dictionary<int, Font> LabelFontCache = new();
    private static readonly Dictionary<int, Font> StatusFontCache = new();

    private static Bitmap ScaledHand(int scale)
    {
        lock (ScaledHandCache)
        {
            if (!ScaledHandCache.TryGetValue(scale, out var scaled))
            {
                int h = HandHeightPx * scale;
                int w = (int)Math.Round(h * (Hand.Value.Width / (float)Hand.Value.Height));
                scaled = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(Hand.Value, 0, 0, w, h);
                ScaledHandCache[scale] = scaled;
            }
            return scaled;
        }
    }

    private static Font ToolbarFont(int scale)
    {
        lock (ToolbarFontCache)
            return ToolbarFontCache.TryGetValue(scale, out var f)
                ? f
                : ToolbarFontCache[scale] = new Font("Segoe UI", 15f * scale, FontStyle.Bold);
    }

    private static Font LabelFont(int scale)
    {
        lock (LabelFontCache)
            return LabelFontCache.TryGetValue(scale, out var f)
                ? f
                : LabelFontCache[scale] = new Font("Segoe UI", 11f * scale, FontStyle.Bold);
    }

    private static Font StatusFont(int scale)
    {
        lock (StatusFontCache)
            return StatusFontCache.TryGetValue(scale, out var f)
                ? f
                : StatusFontCache[scale] = new Font("Consolas", 12f);
    }

    private static readonly Dictionary<int, Font> HudFontCache = new();

    private static Font HudFont(int scale)
    {
        lock (HudFontCache)
            return HudFontCache.TryGetValue(scale, out var f)
                ? f
                : HudFontCache[scale] = new Font("Consolas", 13f * scale, FontStyle.Bold);
    }

    private static Bitmap LoadHand()
    {
        var asm = typeof(HandScene).Assembly;
        using var stream = asm.GetManifestResourceStream("TinyHand.assets.hand_black.png")
            ?? throw new InvalidOperationException("Embedded hand_black.png not found");
        return new Bitmap(stream);
    }

    /// <summary>Screen-space destination rect of the hand sprite for the given cursor and scale.</summary>
    public static Rectangle HandRect(Point cursor, int scale)
    {
        int h = HandHeightPx * scale;
        float w = h * (Hand.Value.Width / (float)Hand.Value.Height);
        float x = cursor.X - w * TipAnchorX;
        float y = cursor.Y - 8 * scale;
        // +1 px to cover bicubic edge bleed.
        return Rectangle.Round(new RectangleF(x - 1, y - 1, w + 2, h + 2));
    }

    public void Draw(Graphics g, MonitorInfo monitor, in SceneSnapshot scene)
    {
        Draw(g, monitor, scene, null);
    }

    /// <summary>
    /// Draw with an optional clip rect (monitor-local). Dirty-region frames clip to
    /// the changed area; Graphics.Clear respects the clip, so untouched pixels keep
    /// their previous frame content on the persistent surface.
    /// </summary>
    public void Draw(Graphics g, MonitorInfo monitor, in SceneSnapshot scene, Rectangle? clip)
    {
        if (clip is { } clipRect) g.SetClip(clipRect);

        g.Clear(OpaqueBackground ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(1, 0, 0, 0));

        int scale = Math.Max(1, (int)Math.Round(monitor.DpiX / 96.0, MidpointRounding.AwayFromZero));

        // Screen-coordinate space translated into this monitor's local space.
        g.TranslateTransform(-monitor.Bounds.X, -monitor.Bounds.Y);

        DrawStrokes(g, scene.Strokes, scale);
        DrawHand(g, scene, scale);

        if (scene.IsPrimary)
        {
            DrawToolbar(g, scene.Toolbar, scale);
            DrawStatus(g, monitor, scene.Status, scale);
            DrawHud(g, monitor, scene.FpsHud, scale);
        }

        if (clip.HasValue) g.ResetClip();
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
        var hand = ScaledHand(scale);
        int h = hand.Height;
        float w = hand.Width;
        float x = scene.Cursor.X - w * TipAnchorX;
        float y = scene.Cursor.Y - 8 * scale; // fingertip sits at the cursor point

        // Same-size PARGB blit from the cached prescaled sprite (bicubic ran once
        // per scale at cache build time, not every frame).
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(hand, x, y, w, h);

        if (scene.ClickThrough)
        {
            using var brush = new SolidBrush(Color.FromArgb(255, 244, 51, 90));
            g.DrawString("click-through", LabelFont(scale), brush, x + w + 6 * scale, y + 4 * scale);
        }
    }

    private static void DrawToolbar(Graphics g, List<ToolbarButton> buttons, int scale)
    {
        using var bg = new SolidBrush(Color.FromArgb(245, 28, 28, 32));
        using var border = new Pen(Color.FromArgb(255, 255, 255, 255), 2f * Math.Max(1, scale));
        using var textBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        var font = ToolbarFont(scale);
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
        using var brush = new SolidBrush(Color.FromArgb(200, 40, 40, 46));
        float x = 14 * scale;
        float y = monitor.Bounds.Height - 28 * scale;
        g.DrawString(status, StatusFont(scale), brush, x, y);
    }

    /// <summary>Local-space rect of the live FPS HUD chip (primary monitor, top-right).
    /// The compositor unions this into the dirty region whenever the HUD text changes.</summary>
    public static Rectangle HudRect(MonitorInfo monitor, int scale)
    {
        int h = 48 * scale;
        int w = 340 * scale;
        return new Rectangle(monitor.Bounds.Width - w - 20 * scale, 16 * scale, w, h);
    }

    private static void DrawHud(Graphics g, MonitorInfo monitor, string text, int scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        var rect = HudRect(monitor, scale);
        using var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 26));
        using var border = new Pen(Color.FromArgb(255, 244, 51, 118), 1.5f * scale);
        using var brush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.FillRectangle(bg, rect);
        g.DrawRectangle(border, rect);
        var rectf = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
        g.DrawString(text, HudFont(scale), brush, rectf, format);
    }
}
