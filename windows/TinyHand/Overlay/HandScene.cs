using System.Drawing;
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
    ToolbarLayout Toolbar,
    bool IsPrimary,
    int DpiX,
    string Status,
    string FpsHud,
    string Toast,
    string StyleId,
    bool Laser,
    bool HandVisible,
    float Press,
    float GripX,
    bool SoundOn)
{
    public static SceneSnapshot Empty { get; } = new(
        new Point(), new ToolbarLayout(Rectangle.Empty, []), false, 96, "", "", "",
        PointerStyle.Default.Id, false, true, 0, 0, true);
}

/// <summary>
/// Direct2D scene renderer (M1.5/M2/M3). Paints the scene on a DirectComposition flip-model
/// back buffer: transparent background (the WS_EX_NOREDIRECTIONBITMAP window takes the
/// hit-test over its whole rect), ink strokes, the posed hand at the cursor, the style
/// toolbar, a diagnostics status line and the FPS HUD chip.
///
/// M3 hand visual parity with macOS <c>ArmView</c>: the sprite is split into stem and hand,
/// the stem stretches to reach the cursor, the hand keeps its length (minus the press
/// shrink), the whole arm rotates around a grip below the screen edge, the drop shadow
/// follows lift/press and the laser style draws a glowing dot instead of a hand.
/// </summary>
public sealed class HandScene
{
    /// <summary>Diagnostics only (fpscheck --opaque): present an opaque background to
    /// compare present cost against the transparent clear.</summary>
    public static bool OpaqueBackground;

    private static readonly Lazy<IDWriteFactory> Write = new(
        () => DWriteApi.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared));

    private enum TextKind { Status, Hud, Toast }

    private static readonly object FormatLock = new();
    private static readonly Dictionary<(TextKind, int), IDWriteTextFormat> Formats = new();

    /// <summary>Local-space rect of the live FPS HUD chip (primary monitor, top-right).</summary>
    public static Rectangle HudRect(MonitorInfo monitor, int scale)
    {
        int h = 48 * scale;
        int w = 340 * scale;
        return new Rectangle(monitor.Bounds.Width - w - 20 * scale, 16 * scale, w, h);
    }

    /// <summary>Per-monitor Direct2D resources: device-dependent brushes, the round-cap stroke
    /// style, lazily prescaled hand sprites (per style and DPI scale) and toolbar thumbnails.</summary>
    public sealed class SceneResources : IDisposable
    {
        public required ID2D1DeviceContext Context { get; init; }
        public required ID2D1Factory1 Factory { get; init; }
        public required ID2D1Device D2dDevice { get; init; }
        public required ID2D1StrokeStyle RoundStroke { get; init; }
        public required InkLayer Ink { get; init; }
        public required ID2D1SolidColorBrush ToolbarBg { get; init; }
        public required ID2D1SolidColorBrush ToolbarBorder { get; init; }
        public required ID2D1SolidColorBrush ToolbarHighlight { get; init; }
        public required ID2D1SolidColorBrush ToolbarIcon { get; init; }
        public required ID2D1SolidColorBrush StatusBrush { get; init; }
        public required ID2D1SolidColorBrush HudBg { get; init; }
        public required ID2D1SolidColorBrush HudBorder { get; init; }
        public required ID2D1SolidColorBrush HudText { get; init; }
        public required ID2D1SolidColorBrush ToastBg { get; init; }
        public required ID2D1SolidColorBrush ToastBorder { get; init; }
        public required ID2D1SolidColorBrush ToastText { get; init; }
        public required ID2D1SolidColorBrush LaserDot { get; init; }
        public required ID2D1SolidColorBrush LaserCore { get; init; }
        public required ID2D1RadialGradientBrush LaserGlow { get; init; }

        private readonly Dictionary<(string Style, int Scale), ID2D1Bitmap1> _handCache = new();
        private readonly List<(string Style, int Scale)> _handOrder = [];
        /// <summary>One pre-blurred shadow level: the bitmap plus the transparent padding
        /// around the silhouette (in shadow-bitmap pixels) that the blur spreads into.</summary>
        public sealed record ShadowLevel(ID2D1Bitmap1 Bitmap, int Padding);

        /// <summary>Soft (resting hand) and tight (pressed hand) shadow levels; the scene
        /// cross-fades them by the pose's shadow radius.</summary>
        public sealed record ShadowTarget(ShadowLevel Soft, ShadowLevel Tight);

        private readonly Dictionary<(string Style, int Scale), ShadowTarget> _shadowCache = new();
        private readonly List<(string Style, int Scale)> _shadowOrder = [];
        private readonly Dictionary<string, ID2D1Bitmap1> _thumbnails = new();
        private const int HandCacheLimit = 2;

        // The toolbar is static between picks: 39 buttons with 36 thumbnail blits cost
        // ~4.5 ms per frame when painted directly. It is painted once into its own bitmap
        // and blitted afterwards (invalidated by the layout/style/sound/thumbnail count).
        private readonly object _toolbarLock = new();
        private ID2D1DeviceContext? _toolbarContext;
        private ID2D1Bitmap1? _toolbarBitmap;
        private string _toolbarKey = "";

        public ID2D1Bitmap1? CachedToolbar(int width, int height, string key, Action<ID2D1DeviceContext> paint)
        {
            lock (_toolbarLock)
            {
                if (_toolbarBitmap is not null && _toolbarKey == key) return _toolbarBitmap;
                if (width <= 0 || height <= 0) return null;
                _toolbarContext ??= D2dDevice.CreateDeviceContext(DeviceContextOptions.None);
                _toolbarContext.SetDpi(96, 96);
                _toolbarBitmap?.Dispose();
                _toolbarBitmap = _toolbarContext.CreateBitmap(new Vortice.Mathematics.SizeI(width, height),
                    IntPtr.Zero, 0, new BitmapProperties1
                    {
                        PixelFormat = new D2dPixelFormat(Format.B8G8R8A8_UNorm, D2dAlphaMode.Premultiplied),
                        DpiX = 96,
                        DpiY = 96,
                        BitmapOptions = BitmapOptions.Target,
                    });
                _toolbarContext.Target = _toolbarBitmap;
                _toolbarContext.BeginDraw();
                _toolbarContext.Clear(new Color4(0, 0, 0, 0));
                paint(_toolbarContext);
                _toolbarContext.EndDraw();
                _toolbarContext.Target = null;
                _toolbarKey = key;
                return _toolbarBitmap;
            }
        }

        /// <summary>Number of decoded thumbnails (part of the toolbar cache key: a thumbnail
        /// arriving from the background preload must repaint the strip).</summary>
        public int ThumbnailCount
        {
            get { lock (_thumbnails) return _thumbnails.Count; }
        }

        /// <summary>Prescaled full sprite for a style: the hand part is drawn at
        /// <c>PointerSize × monitorHeight</c>, so the bitmap is prescaled to that size once.</summary>
        public ID2D1Bitmap1? HandBitmap(string styleId, int scale, HandMetrics metrics, int monitorHeight)
        {
            var key = (styleId, scale);
            lock (_handCache)
            {
                if (_handCache.TryGetValue(key, out var cached)) return cached;
                var sprite = HandAssets.Sprite(styleId);
                if (sprite is null) return null;

                int handPx = Math.Max(8, (int)Math.Round(Preferences.PointerSize * monitorHeight));
                int height = Math.Max(8, (int)Math.Round(handPx / metrics.HandSpan));
                int width = Math.Max(8, (int)Math.Round(height * metrics.Aspect));
                using var prescaled = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(prescaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(sprite, 0, 0, width, height);
                }
                var (bytes, pitch) = HandAssets.PremultipliedBytes(prescaled);
                var bitmap = CreateBitmap(bytes, pitch, width, height);

                _handCache[key] = bitmap;
                _handOrder.Add(key);
                while (_handOrder.Count > HandCacheLimit)
                {
                    var evicted = _handOrder[0];
                    _handOrder.RemoveAt(0);
                    if (_handCache.Remove(evicted, out var stale)) stale.Dispose();
                }
                return bitmap;
            }
        }

        /// <summary>Pre-blurred silhouettes for the hand's drop shadow (1/4 sprite resolution,
        /// padded so the blur is not cut at the sprite edges).</summary>
        public ShadowTarget? ShadowBitmap(string styleId, int scale, int screenHeight)
        {
            var key = (styleId, scale);
            lock (_shadowCache)
            {
                if (_shadowCache.TryGetValue(key, out var cached)) return cached;
                var shadow = HandAssets.Shadow(styleId, screenHeight);
                if (shadow is null) return null;
                var target = new ShadowTarget(CreateShadowLevel(shadow.Soft), CreateShadowLevel(shadow.Tight));
                _shadowCache[key] = target;
                _shadowOrder.Add(key);
                while (_shadowOrder.Count > HandCacheLimit)
                {
                    var evicted = _shadowOrder[0];
                    _shadowOrder.RemoveAt(0);
                    if (_shadowCache.Remove(evicted, out var stale))
                    {
                        stale.Soft.Bitmap.Dispose();
                        stale.Tight.Bitmap.Dispose();
                    }
                }
                return target;
            }
        }

        private ShadowLevel CreateShadowLevel(HandAssets.ShadowSprite sprite)
        {
            var (bytes, pitch) = HandAssets.PremultipliedBytes(sprite.Bitmap);
            return new ShadowLevel(CreateBitmap(bytes, pitch, sprite.Bitmap.Width, sprite.Bitmap.Height),
                sprite.Padding);
        }

        /// <summary>Toolbar thumbnail for a style (null until the background preload finishes).</summary>
        public ID2D1Bitmap1? Thumbnail(string styleId)
        {
            lock (_thumbnails)
            {
                if (_thumbnails.TryGetValue(styleId, out var cached)) return cached;
                var thumb = HandAssets.Thumbnail(styleId);
                if (thumb is null) return null;
                var (bytes, pitch) = HandAssets.PremultipliedBytes(thumb);
                var bitmap = CreateBitmap(bytes, pitch, thumb.Width, thumb.Height);
                _thumbnails[styleId] = bitmap;
                return bitmap;
            }
        }

        private ID2D1Bitmap1 CreateBitmap(byte[] bytes, int pitch, int width, int height)
        {
            var props = new BitmapProperties1
            {
                PixelFormat = new D2dPixelFormat(Format.B8G8R8A8_UNorm, D2dAlphaMode.Premultiplied),
                DpiX = 96,
                DpiY = 96,
                BitmapOptions = BitmapOptions.None,
            };
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Context.CreateBitmap(new Vortice.Mathematics.SizeI(width, height),
                    handle.AddrOfPinnedObject(), (uint)pitch, props);
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            lock (_handCache)
            {
                foreach (var bitmap in _handCache.Values) bitmap.Dispose();
                _handCache.Clear();
                _handOrder.Clear();
            }
            lock (_shadowCache)
            {
                foreach (var target in _shadowCache.Values)
                {
                    target.Soft.Bitmap.Dispose();
                    target.Tight.Bitmap.Dispose();
                }
                _shadowCache.Clear();
                _shadowOrder.Clear();
            }
            lock (_thumbnails)
            {
                foreach (var bitmap in _thumbnails.Values) bitmap.Dispose();
                _thumbnails.Clear();
            }
            lock (_toolbarLock)
            {
                _toolbarBitmap?.Dispose();
                _toolbarBitmap = null;
                _toolbarContext?.Dispose();
                _toolbarContext = null;
                _toolbarKey = "";
            }
            ToolbarBg.Dispose();
            ToolbarBorder.Dispose();
            ToolbarHighlight.Dispose();
            ToolbarIcon.Dispose();
            StatusBrush.Dispose();
            HudBg.Dispose();
            HudBorder.Dispose();
            HudText.Dispose();
            ToastBg.Dispose();
            ToastBorder.Dispose();
            ToastText.Dispose();
            LaserDot.Dispose();
            LaserCore.Dispose();
            LaserGlow.Dispose();
            RoundStroke.Dispose();
            Ink.Dispose();
            // Context is owned by OverlaySurface, not the scene resources.
        }
    }

    public SceneResources CreateResources(OverlayRenderer renderer, ID2D1DeviceContext context, MonitorInfo monitor,
        Color inkColor)
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

        var glowStops = context.CreateGradientStopCollection(
        [
            new GradientStop(0f, new Color4(1f, 0.16f, 0.12f, 0.55f)),
            new GradientStop(0.55f, new Color4(1f, 0.16f, 0.12f, 0.18f)),
            new GradientStop(1f, new Color4(1f, 0.16f, 0.12f, 0f)),
        ], Gamma.StandardRgb, ExtendMode.Clamp);
        var glow = context.CreateRadialGradientBrush(
            new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), new BrushProperties(1f), glowStops);
        glowStops.Dispose();

        return new SceneResources
        {
            Context = context,
            Factory = renderer.D2dFactory,
            D2dDevice = renderer.D2dDevice,
            RoundStroke = roundStroke,
            Ink = new InkLayer(renderer, monitor, scale, inkColor),
            // macOS darkGlass: translucent dark panel, 16 pt radius, white 18 % hairline.
            ToolbarBg = brush(24 / 255f, 24 / 255f, 28 / 255f, 214 / 255f),
            ToolbarBorder = brush(1, 1, 1, 0.18f),
            ToolbarHighlight = brush(1, 1, 1, 0.18f),
            ToolbarIcon = brush(1, 1, 1, 0.85f),
            StatusBrush = brush(40 / 255f, 40 / 255f, 46 / 255f, 200 / 255f),
            HudBg = brush(20 / 255f, 20 / 255f, 26 / 255f, 220 / 255f),
            HudBorder = brush(244 / 255f, 51 / 255f, 118 / 255f, 1),
            HudText = brush(1, 1, 1, 1),
            ToastBg = brush(20 / 255f, 20 / 255f, 26 / 255f, 240 / 255f),
            ToastBorder = brush(244 / 255f, 51 / 255f, 118 / 255f, 1),
            ToastText = brush(1, 1, 1, 1),
            LaserDot = brush(1f, 0.16f, 0.12f, 1f),
            LaserCore = brush(1f, 0.86f, 0.82f, 0.95f),
            LaserGlow = glow,
        };
    }

    private static IDWriteTextFormat TextFormat(TextKind kind, int scale)
    {
        lock (FormatLock)
        {
            if (Formats.TryGetValue((kind, scale), out var cached)) return cached;
            var (family, weight, size, align, para) = kind switch
            {
                TextKind.Toast => ("Segoe UI", FontWeight.Bold, 18f * scale, TextAlignment.Center, ParagraphAlignment.Center),
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
        if (scene.HandVisible)
            DrawPointer(ctx, res, monitor, scene, scale, ox, oy);

        if (scene.IsPrimary)
        {
            DrawToolbar(ctx, res, scene.Toolbar, scale, ox, oy);
            DrawToast(ctx, res, monitor, scene.Toast, scale);
            DrawStatus(ctx, res, monitor, scene.Status, scale);
            DrawHud(ctx, res, monitor, scene.FpsHud, scale);
        }
    }

    /// <summary>Composite the per-monitor ink layer: frozen strokes with their fade opacity,
    /// then the live stroke and its arrow head. Skipped while the layer is empty so a fresh
    /// overlay does not pay the bitmap blit.</summary>
    private static void DrawInk(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor)
    {
        if (!res.Ink.HasInk) return;
        res.Ink.Draw(ctx, monitor);
    }

    // =====================================================================
    // Pointer (hand or laser)
    // =====================================================================

    private static void DrawPointer(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor,
        in SceneSnapshot scene, int scale, float ox, float oy)
    {
        var cursor = new PointF(scene.Cursor.X, scene.Cursor.Y);
        if (scene.Laser)
        {
            DrawLaser(ctx, res, monitor, cursor, scene.Press, ox, oy);
            return;
        }

        var metrics = HandAssets.Metrics(scene.StyleId);
        if (metrics is null) return;
        var hand = res.HandBitmap(scene.StyleId, scale, metrics, monitor.Bounds.Height);
        if (hand is null) return;

        var pose = HandPoseSolver.Solve(cursor, monitor.Bounds, metrics, scene.Press, scene.GripX,
            Preferences.ShowShadow);

        // Sprite space → monitor-local space. The sprite is drawn upright: the hand part (the
        // top of the PNG, fingertip first) sits at sprite y ∈ [0, HandLength] and the stem
        // below it, so the fingertip ends up at the far end of the arm, on the cursor.
        var transform = pose.SpriteTransform(metrics, ox, oy);

        float bw = hand.Size.Width;
        float bh = hand.Size.Height;
        var handSource = new Rect(0, metrics.HandTop * bh, bw, metrics.HandSpan * bh);
        var rodSource = new Rect(0, metrics.Wrist * bh, bw, metrics.RodSpan * bh);
        var handDest = new Rect(0, 0, pose.Width, pose.HandLength);
        var rodDest = new Rect(0, pose.HandLength, pose.Width, pose.RodLength);

        // Drop shadow first: the pre-blurred silhouette, offset down-right by the lift. The
        // source and destination rects are expanded by the sprite's transparent padding so the
        // blur spill is drawn too — cropping it at the hand part's edge left the fingertip
        // without a shadow when the hand pressed down. The blur radius follows the press
        // (macOS 45 px → 14 px on 4K): the soft and tight levels are cross-faded so the shadow
        // keeps the hand's silhouette instead of staying a blob.
        if (pose.ShadowOpacity > 0
            && res.ShadowBitmap(scene.StyleId, scale, monitor.Bounds.Height) is { } shadow)
        {
            float softRadius = HandPoseSolver.ShadowRadius(monitor.Bounds.Height, 0);
            float tightRadius = HandPoseSolver.ShadowRadius(monitor.Bounds.Height, 1);
            float t = Math.Clamp((softRadius - pose.ShadowRadius) / MathF.Max(0.001f, softRadius - tightRadius),
                0f, 1f);
            ctx.Transform = transform * Matrix3x2.CreateTranslation(pose.ShadowDx, pose.ShadowDy);
            DrawShadowLevel(ctx, shadow.Soft, pose.ShadowOpacity * (1 - t), metrics, pose);
            DrawShadowLevel(ctx, shadow.Tight, pose.ShadowOpacity * t, metrics, pose);
        }

        ctx.Transform = transform;
        ctx.DrawBitmap(hand, handDest, 1f, BitmapInterpolationMode.Linear, handSource);
        ctx.DrawBitmap(hand, rodDest, 1f, BitmapInterpolationMode.Linear, rodSource);
        ctx.Transform = Matrix3x2.Identity;
    }

    /// <summary>Blit one shadow level (hand part + stem part) with the current transform.</summary>
    private static void DrawShadowLevel(ID2D1DeviceContext ctx, SceneResources.ShadowLevel level,
        float opacity, HandMetrics metrics, HandPose pose)
    {
        if (opacity <= 0.001f) return;
        float pad = level.Padding;
        float sw = level.Bitmap.Size.Width - 2 * pad;
        float sh = level.Bitmap.Size.Height - 2 * pad;
        var (handSource, handDest) = ShadowPart(pad, sw, sh,
            metrics.HandTop * sh, metrics.HandSpan * sh, 0, pose.HandLength, pose.Width);
        var (rodSource, rodDest) = ShadowPart(pad, sw, sh,
            metrics.Wrist * sh, metrics.RodSpan * sh, pose.HandLength, pose.RodLength, pose.Width);
        ctx.DrawBitmap(level.Bitmap, handDest, opacity, BitmapInterpolationMode.Linear, handSource);
        ctx.DrawBitmap(level.Bitmap, rodDest, opacity, BitmapInterpolationMode.Linear, rodSource);
    }

    /// <summary>Source/destination rects for one shadow part, expanded by the sprite padding so
    /// the blurred spill outside the part is included.</summary>
    private static (Rect Source, Rect Dest) ShadowPart(float pad, float sw, float sh,
        float sourceY, float sourceHeight, float destY, float destHeight, float width)
    {
        float sx = width / sw;
        float sy = destHeight / sourceHeight;
        return (
            new Rect(0, sourceY, sw + 2 * pad, sourceHeight + 2 * pad),
            new Rect(-pad * sx, destY - pad * sy, width + 2 * pad * sx, destHeight + 2 * pad * sy));
    }

    /// <summary>macOS <c>ArmView.aimLaser</c>: a red dot with a soft glow and a light core.</summary>
    private static void DrawLaser(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor,
        PointF cursor, float press, float ox, float oy)
    {
        float r = HandPoseSolver.LaserRadius(monitor.Bounds.Height, press);
        float glow = HandPoseSolver.LaserGlowRadius(r, press);
        var center = new Vector2(cursor.X + ox, cursor.Y + oy);

        res.LaserGlow.Center = center;
        res.LaserGlow.RadiusX = glow;
        res.LaserGlow.RadiusY = glow;
        ctx.FillEllipse(new Ellipse(center, glow, glow), res.LaserGlow);
        ctx.FillEllipse(new Ellipse(center, r, r), res.LaserDot);
        float core = r * 0.45f;
        ctx.FillEllipse(new Ellipse(center, core, core), res.LaserCore);
    }

    // =====================================================================
    // Toolbar
    // =====================================================================

    private static void DrawToolbar(ID2D1DeviceContext ctx, SceneResources res, ToolbarLayout layout, int scale,
        float ox, float oy)
    {
        if (layout.Buttons.Count == 0) return;
        var panel = layout.Panel;
        string key = $"{panel}|{scale}|{Preferences.Style.Id}|{Preferences.SoundOn}|{res.ThumbnailCount}";
        var cached = res.CachedToolbar(panel.Width, panel.Height, key,
            target => PaintToolbar(target, res, layout, scale, panel));
        if (cached is not null)
        {
            var dest = new Rect(panel.X + ox, panel.Y + oy, panel.Width, panel.Height);
            ctx.DrawBitmap(cached, dest, 1f, BitmapInterpolationMode.NearestNeighbor,
                new Rect(0, 0, panel.Width, panel.Height));
            return;
        }
        PaintToolbar(ctx, res, layout, scale, panel);
    }

    /// <summary>Paint the strip in panel-local coordinates (the cache target's origin).</summary>
    private static void PaintToolbar(ID2D1DeviceContext ctx, SceneResources res, ToolbarLayout layout, int scale,
        Rectangle panel)
    {
        float radius = ToolbarModel.RadiusPx * scale;
        var rounded = new RoundedRectangle(new RectangleF(0, 0, panel.Width, panel.Height), radius, radius);
        ctx.FillRoundedRectangle(rounded, res.ToolbarBg);
        ctx.DrawRoundedRectangle(rounded, res.ToolbarBorder, 1.5f * scale);

        float inset = 4 * scale;
        foreach (var button in layout.Buttons)
        {
            var rect = new RectangleF(button.Rect.X - panel.X, button.Rect.Y - panel.Y,
                button.Rect.Width, button.Rect.Height);
            if (button.Selected)
                ctx.FillRoundedRectangle(new RoundedRectangle(rect, 10 * scale, 10 * scale), res.ToolbarHighlight);

            switch (button.Kind)
            {
                case ToolbarButtonKind.Style:
                    DrawStyleIcon(ctx, res, button, rect, inset, scale);
                    break;
                case ToolbarButtonKind.Sound:
                    DrawSoundIcon(ctx, res, rect, scale);
                    break;
                case ToolbarButtonKind.Exit:
                    DrawExitIcon(ctx, res, rect, scale);
                    break;
            }
        }
    }

    private static void DrawStyleIcon(ID2D1DeviceContext ctx, SceneResources res, ToolbarButton button,
        RectangleF rect, float inset, int scale)
    {
        var style = PointerStyle.ById(button.Id);
        if (style.IsLaser)
        {
            var center = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            float r = rect.Width * 0.16f;
            float glow = r * 2.6f;
            res.LaserGlow.Center = center;
            res.LaserGlow.RadiusX = glow;
            res.LaserGlow.RadiusY = glow;
            ctx.FillEllipse(new Ellipse(center, glow, glow), res.LaserGlow);
            ctx.FillEllipse(new Ellipse(center, r, r), res.LaserDot);
            ctx.FillEllipse(new Ellipse(center, r * 0.45f, r * 0.45f), res.LaserCore);
            return;
        }

        if (res.Thumbnail(button.Id) is { } thumb)
        {
            var dest = new Rect(rect.X + inset, rect.Y + inset, rect.Width - 2 * inset, rect.Height - 2 * inset);
            ctx.DrawBitmap(thumb, dest, 1f, BitmapInterpolationMode.Linear,
                new Rect(0, 0, thumb.Size.Width, thumb.Size.Height));
            return;
        }

        // Thumbnail still decoding: a dot in the style's ink colour keeps the slot readable.
        var dot = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        float radius = rect.Width * 0.22f;
        using var brush = ctx.CreateSolidColorBrush(new Color4(
            style.Ink.R / 255f, style.Ink.G / 255f, style.Ink.B / 255f, 1f));
        ctx.FillEllipse(new Ellipse(dot, radius, radius), brush);
    }

    /// <summary>Speaker glyph: body + cone + two waves; a slash when muted.</summary>
    private static void DrawSoundIcon(ID2D1DeviceContext ctx, SceneResources res, RectangleF rect, int scale)
    {
        float w = rect.Width;
        float h = rect.Height;
        float cx = rect.X + w / 2;
        float cy = rect.Y + h / 2;
        float u = w * 0.5f;
        bool on = Preferences.SoundOn;

        ctx.FillRectangle(new Rect(cx - u * 0.62f, cy - u * 0.16f, u * 0.26f, u * 0.32f), res.ToolbarIcon);
        using (var cone = res.Factory.CreatePathGeometry())
        {
            using (var sink = cone.Open())
            {
                sink.BeginFigure(new Vector2(cx - u * 0.36f, cy - u * 0.16f), FigureBegin.Filled);
                sink.AddLine(new Vector2(cx - u * 0.06f, cy - u * 0.46f));
                sink.AddLine(new Vector2(cx - u * 0.06f, cy + u * 0.46f));
                sink.AddLine(new Vector2(cx - u * 0.36f, cy + u * 0.16f));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            ctx.FillGeometry(cone, res.ToolbarIcon);
        }

        float stroke = 2f * scale;
        if (on)
        {
            ctx.DrawLine(new Vector2(cx + u * 0.08f, cy - u * 0.22f), new Vector2(cx + u * 0.30f, cy),
                res.ToolbarIcon, stroke, res.RoundStroke);
            ctx.DrawLine(new Vector2(cx + u * 0.30f, cy), new Vector2(cx + u * 0.08f, cy + u * 0.22f),
                res.ToolbarIcon, stroke, res.RoundStroke);
            ctx.DrawLine(new Vector2(cx + u * 0.24f, cy - u * 0.40f), new Vector2(cx + u * 0.52f, cy),
                res.ToolbarIcon, stroke, res.RoundStroke);
            ctx.DrawLine(new Vector2(cx + u * 0.52f, cy), new Vector2(cx + u * 0.24f, cy + u * 0.40f),
                res.ToolbarIcon, stroke, res.RoundStroke);
        }
        else
        {
            ctx.DrawLine(new Vector2(cx + u * 0.10f, cy - u * 0.42f), new Vector2(cx + u * 0.52f, cy + u * 0.42f),
                res.ToolbarIcon, stroke * 1.2f, res.RoundStroke);
        }
    }

    private static void DrawExitIcon(ID2D1DeviceContext ctx, SceneResources res, RectangleF rect, int scale)
    {
        float cx = rect.X + rect.Width / 2;
        float cy = rect.Y + rect.Height / 2;
        float u = rect.Width * 0.22f;
        float stroke = 2.4f * scale;
        ctx.DrawLine(new Vector2(cx - u, cy - u), new Vector2(cx + u, cy + u),
            res.ToolbarIcon, stroke, res.RoundStroke);
        ctx.DrawLine(new Vector2(cx + u, cy - u), new Vector2(cx - u, cy + u),
            res.ToolbarIcon, stroke, res.RoundStroke);
    }

    // =====================================================================
    // Status / toast / HUD
    // =====================================================================

    /// <summary>Transient feedback chip under the toolbar (hotkey changes: smoothing level,
    /// fade, clear, click-through mode).</summary>
    private static void DrawToast(ID2D1DeviceContext ctx, SceneResources res, MonitorInfo monitor, string text,
        int scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        int w = 460 * scale;
        int h = 56 * scale;
        var rect = new Rect((monitor.Bounds.Width - w) / 2, 92 * scale, w, h);
        ctx.FillRectangle(rect, res.ToastBg);
        ctx.DrawRectangle(rect, res.ToastBorder, 2f * scale);
        ctx.DrawText(text, TextFormat(TextKind.Toast, scale), rect, res.ToastText);
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
