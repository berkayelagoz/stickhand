using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TinyHand.Logic;

/// <summary>
/// macOS <c>HandAsset</c> measurements, all fractions of the sprite size (0..1, y down).
/// The built-in PNGs already contain the hand plus its stem, so the rod/hand split is a
/// crop of the same image — no extra compose work on Windows.
/// </summary>
public sealed record HandMetrics(
    float Aspect,
    float HandTop,
    float Tip,
    float Wrist,
    float RodEnd,
    float TipX,
    float RodX,
    float HandMinX,
    float HandMaxX)
{
    /// <summary>Height of the hand part (wrist → hand top) as a fraction of the sprite.</summary>
    public float HandSpan => Wrist - HandTop;

    /// <summary>Height of the stem part (rod end → wrist) as a fraction of the sprite.</summary>
    public float RodSpan => RodEnd - Wrist;
}

/// <summary>
/// Embedded hand sprites (the 36 built-in styles) with the macOS <c>HandAsset.measure</c>
/// port, thumbnails for the toolbar and the pre-blurred shadow silhouette the plan calls
/// for (real-time blur is avoided; the sprite is blurred once per style).
///
/// Decoding a 1024×1536 PNG takes tens of milliseconds, so full sprites live in a 2-entry
/// LRU (macOS keeps the same cache limit) and thumbnails are preloaded on a background
/// thread. Everything is safe to call from the compositor thread.
/// </summary>
public static class HandAssets
{
    private const string ResourcePrefix = "TinyHand.assets.";
    private const int FullCacheLimit = 2;
    private const int ThumbnailSide = 96;
    private const int ShadowDivisor = 4;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, HandMetrics> MetricsCache = new();
    private static readonly Dictionary<string, Bitmap> ThumbnailCache = new();
    private static readonly Dictionary<(string Style, int ScreenHeight), ShadowSet> ShadowCache = new();
    private static readonly Dictionary<string, Bitmap> FullCache = new();
    private static readonly List<string> FullOrder = new();

    /// <summary>Sprite resource name for a style id (<c>hand_lavender.png</c>).</summary>
    public static string ResourceName(string styleId) => $"hand_{styleId}.png";

    /// <summary>Measurements for a style, measuring (and caching) the sprite on first use.</summary>
    public static HandMetrics? Metrics(string styleId)
    {
        lock (Gate)
        {
            if (MetricsCache.TryGetValue(styleId, out var cached)) return cached;
        }
        var bitmap = Sprite(styleId);
        if (bitmap is null) return null;
        lock (Gate)
        {
            if (MetricsCache.TryGetValue(styleId, out var cached)) return cached;
            var measured = Measure(bitmap);
            if (measured is not null) MetricsCache[styleId] = measured;
            return measured;
        }
    }

    /// <summary>Full sprite for a style (2-entry LRU; callers must not dispose it).</summary>
    public static Bitmap? Sprite(string styleId)
    {
        lock (Gate)
        {
            if (FullCache.TryGetValue(styleId, out var cached))
            {
                Touch(styleId);
                return cached;
            }
        }

        Bitmap? bitmap = LoadEmbedded(ResourceName(styleId));
        if (bitmap is null) return null;

        lock (Gate)
        {
            if (FullCache.TryGetValue(styleId, out var raced))
            {
                bitmap.Dispose();
                Touch(styleId);
                return raced;
            }
            FullCache[styleId] = bitmap;
            Touch(styleId);
            while (FullOrder.Count > FullCacheLimit)
            {
                string evicted = FullOrder[0];
                FullOrder.RemoveAt(0);
                // Evicted sprites are dropped, not disposed: the compositor may still be
                // prescaling one while the background thumbnail preload evicts it. The GDI+
                // finalizer releases the handle once the last reader is done.
                FullCache.Remove(evicted);
            }
            return bitmap;
        }
    }

    private static void Touch(string styleId)
    {
        FullOrder.Remove(styleId);
        FullOrder.Add(styleId);
    }

    /// <summary>Toolbar thumbnail (square crop of the hand region, 96×96).</summary>
    public static Bitmap? Thumbnail(string styleId)
    {
        lock (Gate)
        {
            if (ThumbnailCache.TryGetValue(styleId, out var cached)) return cached;
        }

        var sprite = Sprite(styleId);
        if (sprite is null) return null;
        var metrics = Metrics(styleId);
        if (metrics is null) return null;

        var thumb = BuildThumbnail(sprite, metrics);
        lock (Gate)
        {
            if (ThumbnailCache.TryGetValue(styleId, out var raced))
            {
                thumb.Dispose();
                return raced;
            }
            ThumbnailCache[styleId] = thumb;
            return thumb;
        }
    }

    /// <summary>Pre-blurred black silhouette of the sprite (1/4 resolution) used as the
    /// hand's drop shadow. The blur radius is derived from the same fractions the pose uses,
    /// so it scales with the screen exactly like the macOS CALayer shadow. <see cref="Padding"/>
    /// is the transparent margin around the silhouette that the blur spreads into: without it
    /// the shadow is cut at the sprite edges (the fingertip lost its shadow when pressed).</summary>
    public sealed record ShadowSprite(Bitmap Bitmap, int Padding);

    /// <summary>Two pre-blurred silhouettes: the soft one for the resting hand (large lift) and
    /// the tight one for the pressed hand. The scene cross-fades them by the pose's shadow
    /// radius, so the shadow tightens with the press instead of staying a soft blob.</summary>
    public sealed record ShadowSet(ShadowSprite Soft, ShadowSprite Tight);

    public static ShadowSet? Shadow(string styleId, int screenHeight)
    {
        var key = (styleId, screenHeight);
        lock (Gate)
        {
            if (ShadowCache.TryGetValue(key, out var cached)) return cached;
        }

        var sprite = Sprite(styleId);
        if (sprite is null) return null;
        var metrics = Metrics(styleId);
        if (metrics is null) return null;

        // The macOS radius range: rest (press 0) → full press (press 1). The radius is in
        // screen pixels, so the sprite-space blur depends on the monitor height.
        var soft = BuildShadow(sprite, metrics, HandPoseSolver.ShadowRadius(screenHeight, 0), screenHeight);
        var tight = BuildShadow(sprite, metrics, HandPoseSolver.ShadowRadius(screenHeight, 1), screenHeight);
        var set = new ShadowSet(soft, tight);
        lock (Gate)
        {
            if (ShadowCache.TryGetValue(key, out var raced))
            {
                soft.Bitmap.Dispose();
                tight.Bitmap.Dispose();
                return raced;
            }
            ShadowCache[key] = set;
            return set;
        }
    }

    /// <summary>Decode every thumbnail on a background thread so the first toolbar frame does
    /// not stall on 36 PNG decodes (macOS preloads thumbnails the same way).</summary>
    public static void PreloadThumbnails(IEnumerable<string> styleIds, Action? done = null)
    {
        var ids = styleIds.ToArray();
        var thread = new Thread(() =>
        {
            foreach (string id in ids)
            {
                try { Thumbnail(id); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"thumbnail {id} failed: {ex.Message}"); }
            }
            done?.Invoke();
        })
        {
            Name = "TinyHand.Thumbnails",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>macOS <c>HandAsset.measure</c>: topmost opaque row, fist/wrist/handle rows from
    /// the row-width profile, the fingertip x at <c>top + h/200</c> and the rod axis x at the
    /// middle of the stem.</summary>
    public static HandMetrics? Measure(Bitmap bitmap)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        var alpha = AlphaPlane(bitmap);
        if (alpha is null) return null;

        int Count(int y)
        {
            int row = y * w;
            int n = 0;
            for (int x = 0; x < w; x++)
                if (alpha[row + x] > 128) n++;
            return n;
        }

        float Center(int y)
        {
            int row = y * w;
            int lo = -1, hi = -1;
            for (int x = 0; x < w; x++)
            {
                if (alpha[row + x] <= 128) continue;
                if (lo < 0) lo = x;
                hi = x;
            }
            return lo < 0 ? 0.5f : (lo + hi) / 2f / w;
        }

        var counts = new int[h];
        for (int y = 0; y < h; y++) counts[y] = Count(y);

        int top = Array.FindIndex(counts, c => c > 0);
        if (top < 0) return null;

        int searchEnd = Math.Min(h, top + (int)(0.45 * h));
        int maxW = 0;
        for (int y = top; y < searchEnd; y++) maxW = Math.Max(maxW, counts[y]);
        if (maxW <= 0) return null;

        int fist = -1;
        for (int y = top; y < h; y++)
            if (counts[y] > 0.6 * maxW) { fist = y; break; }
        if (fist < 0) return null;

        int wrist = -1;
        for (int y = fist; y < h; y++)
            if (counts[y] < 0.35 * maxW) { wrist = y; break; }
        if (wrist < 0) return null;

        int rodW = counts[Math.Min(h - 1, wrist + h / 50)];
        int lastRow = h - 1;
        for (int y = h - 1; y >= 0; y--)
            if (counts[y] > 0) { lastRow = y; break; }

        int from = Math.Min(h - 1, wrist + h / 33);
        int rodEnd = lastRow;
        for (int y = from; y < h; y++)
            if (counts[y] > 1.8 * rodW) { rodEnd = y; break; }

        int minX = w, maxX = 0;
        for (int y = top; y < wrist; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (alpha[row + x] <= 128) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        }
        if (minX > maxX) { minX = 0; maxX = w - 1; }

        float fh = h;
        return new HandMetrics(
            Aspect: (float)w / fh,
            HandTop: Math.Max(0, top - 0.004f * fh) / fh,
            Tip: top / fh,
            Wrist: wrist / fh,
            RodEnd: rodEnd / fh,
            TipX: Center(Math.Min(h - 1, top + h / 200)),
            RodX: Center((wrist + rodEnd) / 2),
            HandMinX: minX / (float)w,
            HandMaxX: maxX / (float)w);
    }

    private static byte[]? AlphaPlane(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            var alpha = new byte[w * h];
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, data.Stride);
                for (int x = 0; x < w; x++) alpha[y * w + x] = row[x * 4 + 3];
            }
            return alpha;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"alpha plane failed: {ex.Message}");
            return null;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap BuildThumbnail(Bitmap sprite, HandMetrics metrics)
    {
        int w = sprite.Width;
        int h = sprite.Height;
        float handW = (metrics.HandMaxX - metrics.HandMinX) * w;
        float handH = metrics.HandSpan * h;
        float side = Math.Max(handW, handH) * 1.06f;
        float cx = (metrics.HandMinX + metrics.HandMaxX) / 2 * w;
        float cy = (metrics.HandTop + metrics.Wrist) / 2 * h;
        var crop = Rectangle.Round(new RectangleF(cx - side / 2, cy - side / 2, side, side));
        crop.Intersect(new Rectangle(0, 0, w, h));
        if (crop.Width <= 0 || crop.Height <= 0) crop = new Rectangle(0, 0, w, h);

        var thumb = new Bitmap(ThumbnailSide, ThumbnailSide, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);
            g.DrawImage(sprite, new Rectangle(0, 0, ThumbnailSide, ThumbnailSide), crop,
                GraphicsUnit.Pixel);
        }
        return thumb;
    }

    /// <summary>Black silhouette with a blurred alpha channel. The blur radius follows the
    /// pose's shadow radius (<c>lift × 0.35</c> with <c>lift = 0.06 × screenH</c>) expressed in
    /// sprite pixels, so the on-screen softness matches the macOS CALayer shadow at any
    /// resolution.</summary>
    private static ShadowSprite BuildShadow(Bitmap sprite, HandMetrics metrics, float radiusScreen,
        int screenHeight)
    {
        int sw = Math.Max(1, sprite.Width / ShadowDivisor);
        int sh = Math.Max(1, sprite.Height / ShadowDivisor);

        // The hand part is drawn at PointerSize × screenH tall for HandSpan × spriteHeight
        // pixels, so sprite px per screen px = HandSpan × h / (PointerSize × screenH).
        float spritePxPerScreenPx = metrics.HandSpan * sprite.Height
            / (Preferences.PointerSize * Math.Max(1, screenHeight));
        float radiusAsset = radiusScreen * spritePxPerScreenPx;
        int radius = Math.Max(1, (int)Math.Round(radiusAsset / ShadowDivisor));

        // Transparent margin so the blur can spread past the sprite edges (three box passes
        // reach ~1.5 × radius; two radii of headroom is plenty).
        int pad = radius * 2;
        int w = sw + 2 * pad;
        int h = sh + 2 * pad;

        var alpha = new byte[w * h];
        using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.Transparent);
                g.DrawImage(sprite, new Rectangle(pad, pad, sw, sh));
            }
            var plane = AlphaPlane(small);
            if (plane is null) return new ShadowSprite(new Bitmap(1, 1, PixelFormat.Format32bppArgb), 0);
            alpha = plane;
        }

        BoxBlur(alpha, w, h, radius);
        BoxBlur(alpha, w, h, radius);
        BoxBlur(alpha, w, h, radius);

        var shadow = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = shadow.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(row);
                for (int x = 0; x < w; x++)
                {
                    // Premultiplied black: B=G=R=0, A=blurred alpha.
                    row[x * 4 + 3] = alpha[y * w + x];
                }
                Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), data.Stride);
            }
        }
        finally
        {
            shadow.UnlockBits(data);
        }
        return new ShadowSprite(shadow, pad);
    }

    /// <summary>Separable box blur (three passes approximate a Gaussian).</summary>
    private static void BoxBlur(byte[] plane, int w, int h, int radius)
    {
        if (radius <= 0) return;
        var temp = new byte[plane.Length];
        int window = radius * 2 + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int sum = 0;
            for (int x = -radius; x <= radius; x++) sum += plane[row + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                temp[row + x] = (byte)(sum / window);
                sum -= plane[row + Math.Clamp(x - radius, 0, w - 1)];
                sum += plane[row + Math.Clamp(x + radius + 1, 0, w - 1)];
            }
        }

        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = -radius; y <= radius; y++) sum += temp[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                plane[y * w + x] = (byte)(sum / window);
                sum -= temp[Math.Clamp(y - radius, 0, h - 1) * w + x];
                sum += temp[Math.Clamp(y + radius + 1, 0, h - 1) * w + x];
            }
        }
    }

    private static Bitmap? LoadEmbedded(string name)
    {
        var asm = typeof(HandAssets).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourcePrefix + name);
        if (stream is null)
        {
            System.Diagnostics.Debug.WriteLine($"embedded hand sprite missing: {name}");
            return null;
        }
        return new Bitmap(stream);
    }

    /// <summary>GDI+ 32bppArgb lock (memory order BGRA, straight alpha) converted to
    /// premultiplied BGRA for Direct2D.</summary>
    public static (byte[] Bytes, int Pitch) PremultipliedBytes(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int pitch = data.Stride;
            var bytes = new byte[pitch * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * pitch), bytes, y * pitch, pitch);

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
}
