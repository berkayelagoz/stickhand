using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using TinyHand.Interop;
using TinyHand.Logic;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.DirectWrite;
using Vortice.DXGI;
using FontStyle = Vortice.DirectWrite.FontStyle;
using D2dPixelFormat = Vortice.DCommon.PixelFormat;
using D2dAlphaMode = Vortice.DCommon.AlphaMode;
using Color4 = Vortice.Mathematics.Color4;
using Rect = Vortice.Mathematics.Rect;
using DWriteApi = Vortice.DirectWrite.DWrite;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;

namespace TinyHand.Overlay;

/// <summary>Immutable snapshot of the scene, copied under lock for each frame.
/// Ink is NOT in the snapshot: strokes live in the InkJournal and are painted
/// incrementally into each monitor's InkLayer bitmap (O(new points) per frame,
/// not O(total ink)).</summary>
public sealed record SceneSnapshot(
    Point Cursor,
    List<ToolbarButton> Toolbar,
    bool IsPrimary,
    int DpiX,
    string Status,
    string FpsHud)
{
    public static SceneSnapshot Empty { get; } = new(new Point(), [], false, 96, "", "");
}

/// <summary>
/// Direct2D scene renderer (M1.5). Paints the M1 scene on a DirectComposition
/// flip-model back buffer: transparent background (the WS_EX_NOREDIRECTIONBITMAP
/// window takes the hit-test over its whole rect, so the M1 alpha=1 background
/// hack is gone), ink strokes, the hand at the cursor, toolbar strip, a
/// diagnostics status line and the FPS HUD chip.
///
/// Visual contract with the M1 GDI+ scene: same geometry methods, same colors
/// (GDI+ ARGB => Color4), same DPI integer-scale handling, same prescaled hand
/// sprites (bicubic once per scale, not per frame).
/// </summary>
public sealed class HandScene
{
    /// <summary>Diagnostics only (fpscheck --opaque): present an opaque background to
    /// compare present cost against the transparent clear.</summary>
    public static bool OpaqueBackground;

    // Hand asset is 1024x1536 (portrait, fingertip up). M1 anchors the fingertip at the
    // cursor; per-style tip/tipX factors from macOS PointerStyle are ported in M4.
    private const int HandHeightPx = 200;
    private const float TipAnchorX = 0.5f;

    // Asset decode + prescale stay on System.Drawing (load-time only, per scale).
    private static readonly Lazy<Bitmap> Hand = new(LoadHand);

    private static readonly Lazy<IDWriteFactory> Write = new(
        () => DWriteApi.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared));

    private enum TextKind { Toolbar, Status, Hud }

    private static readonly object FormatLock = new();
    private static readonly Dictionary<(TextKind, int), IDWriteTextFormat> Formats = new();

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
        // +1 px to cover filtering edge bleed.
        return Rectangle.Round(new RectangleF(x - 1, y - 1, w + 2, h + 2));
    }

    /// <summary>Local-space rect of the live FPS HUD chip (primary monitor, top-right).</summary>
    public static Rectangle HudRect(MonitorInfo monitor, int scale)
    {
        int h = 48 * scale;
        int w = 340 * scale;
        return new Rectangle(monitor.Bounds.Width - w - 20 * scale, 16 * scale, w, h);
    }

    /// <summary>Per-monitor Direct2D resources: device-dependent brushes plus the round-cap
    /// stroke style and lazily prescaled hand bitmaps (per DPI scale).</summary>
    public sealed class SceneResources : IDisposable
    {
        public required ID2D1DeviceContext Context { get; init; }
        public required ID2D1StrokeStyle RoundStroke { get; init; }
        public required InkLayer Ink { get; init; }
        public required ID2D1SolidColorBrush ToolbarBg { get; init; }
        public required ID2D1SolidColorBrush ToolbarBorder { get; init; }
        public required ID2D1SolidColorBrush ToolbarText { get; init; }
        public required ID2D1SolidColorBrush StatusBrush { get; init; }
        public required ID2D1SolidColorBrush HudBg { get; init; }
        public required ID2D1SolidColorBrush HudBorder { get; init; }
        public required ID2D1SolidColorBrush HudText { get; init; }

        private readonly Dictionary<int, ID2D1Bitmap1> _handCache = new();

        public ID2D1Bitmap1 HandBitmap(int scale)
        {
            lock (_handCache)
            {
                if (_handCache.TryGetValue(scale, out var cached)) return cached;
                var bitmap = BuildHandBitmap(scale);
                _handCache[scale] = bitmap;
                return bitmap;
            }
        }

        private ID2D1Bitmap1 BuildHandBitmap(int scale)
        {
            int h = HandHeightPx * scale;
            int w = (int)Math.Round(h * (Hand.Value.Width / (float)Hand.Value.Height));
            using var prescaled = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(prescaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(Hand.Value, 0, 0, w, h);
            }
            var (bytes, pitch) = PremultipliedBytes(prescaled);
            var props = new BitmapProperties1
            {
                PixelFormat = new D2dPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, D2dAlphaMode.Premultiplied),
                DpiX = 96,
                DpiY = 96,
                BitmapOptions = BitmapOptions.None,
            };
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Context.CreateBitmap(
                    new Vortice.Mathematics.SizeI(w, h), handle.AddrOfPinnedObject(), (uint)pitch, props);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>GDI+ 32bppArgb lock (memory order BGRA, straight alpha) converted to
        /// premultiplied BGRA for Direct2D.</summary>
        private static (byte[] bytes, int pitch) PremultipliedBytes(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int pitch = data.Stride;
                var bytes = new byte[pitch * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int src = y * pitch;
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + src), bytes, src, pitch);
                }
                // Premultiply B, G, R by A (GDI+ keeps straight alpha here).
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    byte a = bytes[i + 3];
                    if (a == 0) { bytes[i] = bytes[i + 1] = bytes[i + 2] = 0; continue; }
                    if (a == 255) continue;
                    bytes[i + 0] = (byte)(bytes[i + 0] * a / 255);
                    bytes[i + 1] = (byte)(bytes[i + 1] * a / 255);
                    bytes[i + 2] = (byte)(bytes[i + 2] * a / 255);
                }
                return (bytes, pitch);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            lock (_handCache)
            {
                foreach (var bitmap in _handCache.Values) bitmap.Dispose();
                _handCache.Clear();
            }
            ToolbarBg.Dispose();
            ToolbarBorder.Dispose();
            ToolbarText.Dispose();
            StatusBrush.Dispose();
            HudBg.Dispose();
            HudBorder.Dispose();
            HudText.Dispose();
            RoundStroke.Dispose();
            Ink.Dispose();
            // Context is owned by OverlaySurface, not the scene resources.
        }
    }

    public SceneResources CreateResources(OverlayRenderer renderer, ID2D1DeviceContext context, MonitorInfo monitor)
    {
        int scale = Math.Max(1, (int)Math.Round(monitor.DpiX / 96.0, MidpointRounding.AwayFromZero));
        var roundStroke = renderer.D2dFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 1,
            DashStyle = DashStyle.Solid,
            DashOffset = 0,
        });
        ID2D1SolidColorBrush brush(float r, float g, float b, float a) =>
            context.CreateSolidColorBrush(new Color4(r, g, b, a));
        return new SceneResources
        {
            Context = context,
            RoundStroke = roundStroke,
            Ink = new InkLayer(renderer, monitor, scale),
            ToolbarBg = brush(28 / 255f, 28 / 255f, 32 / 255f, 245 / 255f),
            ToolbarBorder = brush(1, 1, 1, 1),
            ToolbarText = brush(1, 1, 1, 1),
            StatusBrush = brush(40 / 255f, 40 / 255f, 46 / 255f, 200 / 255f),
            HudBg = brush(20 / 255f, 20 / 255f, 26 / 255f, 220 / 255f),
            HudBorder = brush(244 / 255f, 51 / 255f, 118 / 255f, 1),
            HudText = brush(1, 1, 1, 1),
        };
    }

    private static IDWriteTextFormat TextFormat(TextKind kind, int scale)
    {
        lock (FormatLock)
        {
            if (Formats.TryGetValue((kind, scale), out var cached)) return cached;
            var (family, weight, size, align, para) = kind switch
            {
                TextKind.Toolbar => ("Segoe UI", FontWeight.Bold, 15f * scale, TextAlignment.Center, ParagraphAlignment.Center),
                TextKind.Status => ("Consolas", FontWeight.Normal, 12f, TextAlignment.Leading, ParagraphAlignment.Near),
                _ => ("Consolas", FontWeight.Bold, 13f * scale, TextAlignment.Center, ParagraphAlignment.Center),
            };
            var format = Write.Value.CreateTextFormat(family, null, weight, FontStyle.Normal, FontStretch.Normal,
                size, "en-us");
            format.TextAlignment = align;
            format.ParagraphAlignment = para;
            if (kind == TextKind.Status) format.WordWrapping = WordWrapping.NoWrap;
            Formats[(kind, scale)] = format;
            return format;
        }
    }

    /// <summary>Full-frame paint of the scene into this monitor's back buffer. The
    /// caller wraps this in BeginDraw/EndDraw + Present on the OverlaySurface.</summary>
    public void Draw(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor, in SceneSnapshot scene)
    {
        int scale = Math.Max(1, (int)Math.Round(monitor.DpiX / 96.0, MidpointRounding.AwayFromZero));

        ctx.Clear(OpaqueBackground ? new Color4(0, 0, 0, 1) : new Color4(0, 0, 0, 0));

        // Screen-coordinate space translated into this monitor's local space.
        float ox = -monitor.Bounds.X;
        float oy = -monitor.Bounds.Y;

        DrawInk(ctx, res, monitor);
        DrawHand(ctx, res, scene, scale, ox, oy);

        if (scene.IsPrimary)
        {
            DrawToolbar(ctx, res, scene.Toolbar, scale);
            DrawStatus(ctx, res, monitor, scene.Status, scale);
            DrawHud(ctx, res, monitor, scene.FpsHud, scale);
        }
    }

    /// <summary>Blit the persistent per-monitor ink bitmap (segments painted once at
    /// append time by InkLayer, not per frame anymore). Skipped while the layer is
    /// empty so a fresh overlay does not pay the bitmap blit.</summary>
    private static void DrawInk(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor)
    {
        if (!res.Ink.HasInk) return;
        var ink = res.Ink.Bitmap;
        ctx.DrawBitmap(ink, new Rect(0, 0, monitor.Bounds.Width, monitor.Bounds.Height), 1f,
            BitmapInterpolationMode.Linear, new Rect(0, 0, ink.Size.Width, ink.Size.Height));
    }

    private static void DrawHand(ID2D1DeviceContext ctx, SceneResources res, in SceneSnapshot scene,
        int scale, float ox, float oy)
    {
        var hand = res.HandBitmap(scale);
        float h = hand.Size.Height;
        float w = hand.Size.Width;
        float x = scene.Cursor.X + ox - w * TipAnchorX;
        float y = scene.Cursor.Y + oy - 8 * scale; // fingertip sits at the cursor point

        ctx.DrawBitmap(hand, new Rect(x, y, w, h), 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, w, h));
    }

    private static void DrawToolbar(ID2D1DeviceContext ctx, SceneResources res, List<ToolbarButton> buttons, int scale)
    {
        float border = 2f * Math.Max(1, scale);
        foreach (var button in buttons)
        {
            var rect = new Rect(button.Rect.X, button.Rect.Y, button.Rect.Width, button.Rect.Height);
            ctx.FillRectangle(rect, res.ToolbarBg);
            ctx.DrawRectangle(rect, res.ToolbarBorder, border);
            ctx.DrawText(button.Label, TextFormat(TextKind.Toolbar, scale), rect, res.ToolbarText);
        }
    }

    private static void DrawStatus(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor, string status,
        int scale)
    {
        if (string.IsNullOrEmpty(status)) return;
        float y = monitor.Bounds.Height - 28 * scale;
        ctx.DrawText(status, TextFormat(TextKind.Status, scale),
            new Rect(14 * scale, y, monitor.Bounds.Width - 28 * scale, 40 * scale), res.StatusBrush);
    }

    private static void DrawHud(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor, string text, int scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        var local = HudRect(monitor, scale);
        var rect = new Rect(local.X, local.Y, local.Width, local.Height);
        ctx.FillRectangle(rect, res.HudBg);
        ctx.DrawRectangle(rect, res.HudBorder, 1.5f * scale);
        ctx.DrawText(text, TextFormat(TextKind.Hud, scale), rect, res.HudText);
    }
}
