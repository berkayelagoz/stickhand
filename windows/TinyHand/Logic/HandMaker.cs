using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TinyHand.Logic;

/// <summary>
/// macOS <c>HandMaker</c> port: validates a generated hand PNG with the exact same rules,
/// composes it onto the 1024×1536 hand-plus-stem canvas and extracts the ink colour with the
/// same algorithm. The prompt template is shared verbatim (the plan's contract).
/// </summary>
public static class HandMaker
{
    public const string Prompt = """
    Make a pointer image for TinyHand, a Mac app that shows a toy hand on screen.

    Draw one cartoon right hand in this style: [describe your hand, e.g. knitted wool in mustard yellow]

    Rules:
    - We see the BACK of the hand. The palm faces away from us. The knuckles and the smooth back of the hand face the viewer.
    - The index finger points straight up. The other three fingers are folded down, so we only see their knuckles, never their fingertips or fingernails.
    - The raised index finger sits at the far left of the knuckle row, right next to the thumb. Three folded knuckles sit to its right. It must never look like a middle finger.
    - The thumb is tucked against the left side of the hand. Nothing crosses the front of the fist: no thumb, no fingertips, no fingernails, no palm.
    - The fingertip is the highest point of the image. Nothing above or beside it.
    - The hand ends at the bottom in a wrist cuff or a clean flat wrist, facing straight down, centred.
    - Upright, not tilted. Whole hand visible, nothing cropped.
    - Transparent background. No shadow, no floor, no outline glow, no text, no stick, no arm.
    - Soft 3D toy look, bright and readable at small sizes.
    - Portrait image, hand filling most of the height with a little empty space around it.
    """;

    public const int CanvasWidth = 1024;
    public const int CanvasHeight = 1536;

    /// <summary>Validation failure with the user-facing message (macOS <c>HandMaker.Problem</c>).</summary>
    public sealed class Problem : Exception
    {
        public Problem(string message) : base(message) { }
    }

    public sealed record Result(Bitmap Image, uint Ink);

    /// <summary>Fill the prompt template with the user's idea (macOS <c>prompt(for:)</c>).</summary>
    public static string PromptFor(string idea)
    {
        string trimmed = (idea ?? "").Trim();
        string style = trimmed.Length == 0 ? "a soft vinyl toy in a bright colour" : trimmed;
        return Prompt.Replace("[describe your hand, e.g. knitted wool in mustard yellow]", style);
    }

    /// <summary>Read, validate and compose a hand PNG. Throws <see cref="Problem"/> with the
    /// macOS message when the image breaks a rule.</summary>
    public static Result Make(string path)
    {
        Bitmap source;
        try
        {
            source = new Bitmap(path);
        }
        catch (Exception)
        {
            throw new Problem("That file isn't an image TinyHand can read.");
        }

        using (source)
            return Make(source);
    }

    /// <summary>Same pipeline for an already decoded bitmap (settings preview, tests).</summary>
    public static Result Make(Bitmap source)
    {
        var pixels = Read(source);
        Check(pixels);
        var composed = Compose(source, pixels)
            ?? throw new Problem("TinyHand couldn't put that hand on a stick.");
        return new Result(composed, Ink(pixels));
    }

    /// <summary>Save a composed hand as PNG (macOS <c>HandMaker.write</c>).</summary>
    public static void Write(Bitmap image, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        image.Save(path, ImageFormat.Png);
    }

    // ------------------------------------------------------------------
    // Pixels
    // ------------------------------------------------------------------

    private sealed record Pixels(int Width, int Height, byte[] Rgba)
    {
        public byte Alpha(int x, int y) => Rgba[(y * Width + x) * 4 + 3];
    }

    private static Pixels Read(Bitmap image)
    {
        if (!Image.IsAlphaPixelFormat(image.PixelFormat))
            throw new Problem("The image needs a transparent background. Save it as a PNG with transparency.");

        int w = image.Width;
        int h = image.Height;
        if (w < 64 || h < 64)
            throw new Problem("The image is too small. Use one at least 512 pixels tall.");

        var rgba = new byte[w * h * 4];
        var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, data.Stride);
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    // GDI+ 32bppArgb is BGRA with straight alpha; the macOS reader
                    // unpremultiplies, so straight values are what the rules expect.
                    rgba[i + 0] = row[x * 4 + 2];
                    rgba[i + 1] = row[x * 4 + 1];
                    rgba[i + 2] = row[x * 4 + 0];
                    rgba[i + 3] = row[x * 4 + 3];
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return new Pixels(w, h, rgba);
    }

    // ------------------------------------------------------------------
    // Validation (macOS HandMaker.check, rule for rule)
    // ------------------------------------------------------------------

    private static void Check(Pixels p)
    {
        int w = p.Width;
        int h = p.Height;

        int edge = 0;
        int edgeSolid = 0;
        for (int x = 0; x < w; x++)
        {
            edge += 2;
            edgeSolid += (p.Alpha(x, 0) > 32 ? 1 : 0) + (p.Alpha(x, h - 1) > 32 ? 1 : 0);
        }
        for (int y = 0; y < h; y++)
        {
            edge += 2;
            edgeSolid += (p.Alpha(0, y) > 32 ? 1 : 0) + (p.Alpha(w - 1, y) > 32 ? 1 : 0);
        }
        if ((double)edgeSolid / edge > 0.02)
            throw new Problem("The background isn't transparent, or the hand touches the edge of the image.");

        int step = Math.Max(1, Math.Min(w, h) / 256);
        int gw = w / step;
        int gh = h / step;
        var grid = new bool[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
                if (p.Alpha(gx * step, gy * step) > 128)
                    grid[gy * gw + gx] = true;

        var seen = new bool[gw * gh];
        var sizes = new List<int>();
        for (int start = 0; start < grid.Length; start++)
        {
            if (!grid[start] || seen[start]) continue;
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            int n = 0;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                n++;
                int x = i % gw;
                int y = i / gw;
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    int j = ny * gw + nx;
                    if (!grid[j] || seen[j]) continue;
                    seen[j] = true;
                    stack.Push(j);
                }
            }
            sizes.Add(n);
        }

        if (sizes.Count == 0)
            throw new Problem("The image is empty.");
        int biggest = sizes.Max();
        if ((double)biggest / sizes.Sum() < 0.97)
            throw new Problem("The image has more than one thing in it. Use a single hand.");

        var box = Bounds(p) ?? throw new Problem("The image is empty.");
        int bw = box.MaxX - box.MinX + 1;
        int bh = box.MaxY - box.MinY + 1;
        double aspect = (double)bh / bw;
        if (aspect < 1.05 || aspect > 2.8)
            throw new Problem("The hand should stand upright, taller than it is wide.");
        if ((double)bh / h < 0.5)
            throw new Problem("The hand is too small in the image. Crop it closer.");

        int topBand = Math.Max(2, bh * 8 / 100);
        int fingerMin = w;
        int fingerMax = 0;
        for (int y = box.MinY; y < box.MinY + topBand; y++)
            for (int x = box.MinX; x <= box.MaxX; x++)
                if (p.Alpha(x, y) > 128)
                {
                    fingerMin = Math.Min(fingerMin, x);
                    fingerMax = Math.Max(fingerMax, x);
                }
        if ((double)(fingerMax - fingerMin + 1) / bw > 0.4)
            throw new Problem("The top of the image should be a single finger pointing up.");

        var baseSpan = BottomSpan(p, box);
        if ((double)baseSpan.Width / bw < 0.2)
            throw new Problem("The hand should end in a flat wrist or cuff at the bottom.");

        int soft = 0;
        int any = 0;
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                byte a = p.Alpha(x, y);
                if (a <= 20) continue;
                any++;
                if (a < 200) soft++;
            }
        if ((double)soft / Math.Max(1, any) > 0.08)
            throw new Problem("The hand has a soft shadow or glow around it. Ask for no shadow.");
    }

    private static (int MinX, int MinY, int MaxX, int MaxY)? Bounds(Pixels p)
    {
        int minX = p.Width, minY = p.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                if (p.Alpha(x, y) <= 128) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }

    private static (int Width, double Center) BottomSpan(Pixels p, (int MinX, int MinY, int MaxX, int MaxY) box)
    {
        int bh = box.MaxY - box.MinY + 1;
        int lo = p.Width;
        int hi = -1;
        int from = Math.Max(box.MinY, box.MaxY - Math.Max(2, bh * 4 / 100));
        for (int y = from; y <= box.MaxY; y++)
            for (int x = box.MinX; x <= box.MaxX; x++)
            {
                if (p.Alpha(x, y) <= 128) continue;
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
        return hi < 0
            ? (0, (box.MinX + box.MaxX) / 2.0)
            : (hi - lo + 1, (lo + hi) / 2.0);
    }

    // ------------------------------------------------------------------
    // Compose (macOS HandMaker.compose)
    // ------------------------------------------------------------------

    private static Bitmap? Compose(Bitmap source, Pixels p)
    {
        var box = Bounds(p);
        if (box is null) return null;

        const float handTop = 150;
        const float handHeight = 560;
        float bw = box.Value.MaxX - box.Value.MinX + 1;
        float bh = box.Value.MaxY - box.Value.MinY + 1;
        float scale = Math.Min(handHeight / bh, 700 / bw);
        float drawW = p.Width * scale;
        float drawH = p.Height * scale;
        double wristX = (BottomSpan(p, box.Value).Center + 0.5) * scale;
        float originX = CanvasWidth / 2f - (float)wristX;
        float originTop = handTop - box.Value.MinY * scale;
        float handBottom = handTop + bh * scale;

        var canvas = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceOver;
        g.Clear(Color.Transparent);

        float rodW = Math.Min(76, Math.Max(54, bw * scale * 0.18f));
        float rodTop = handBottom - 30;
        const float rodBottom = 1462;
        var rod = new RectangleF(CanvasWidth / 2f - rodW / 2, rodTop, rodW, rodBottom - rodTop);
        using (var path = RoundedRect(rod, rodW / 2))
        using (var brush = new LinearGradientBrush(
            new PointF(rod.Left, 0), new PointF(rod.Right, 0),
            Color.Black, Color.Black))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    Color.FromArgb(255, 0x00, 0x00, 0x00),
                    Color.FromArgb(255, 0x12, 0x12, 0x14),
                    Color.FromArgb(255, 0x3A, 0x3A, 0x3E),
                    Color.FromArgb(255, 0x12, 0x12, 0x14),
                    Color.FromArgb(255, 0x00, 0x00, 0x00),
                ],
                Positions = [0f, 0.22f, 0.45f, 0.75f, 1f],
            };
            g.FillPath(brush, path);
        }

        g.DrawImage(source, new RectangleF(originX, originTop, drawW, drawH));
        return canvas;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------------
    // Ink colour (macOS HandMaker.ink)
    // ------------------------------------------------------------------

    private static uint Ink(Pixels p)
    {
        var best = (Score: -1.0, R: 0.53, G: 0.33, B: 0.95);
        var buckets = new Dictionary<int, (int N, double R, double G, double B)>();

        for (int y = 0; y < p.Height; y += 3)
            for (int x = 0; x < p.Width; x += 3)
            {
                int i = (y * p.Width + x) * 4;
                double a = p.Rgba[i + 3];
                if (a <= 200) continue;
                double r = p.Rgba[i] / 255.0;
                double g = p.Rgba[i + 1] / 255.0;
                double b = p.Rgba[i + 2] / 255.0;
                int key = (int)(r * 5) * 36 + (int)(g * 5) * 6 + (int)(b * 5);
                var e = buckets.TryGetValue(key, out var found) ? found : (0, 0.0, 0.0, 0.0);
                buckets[key] = (e.Item1 + 1, e.Item2 + r, e.Item3 + g, e.Item4 + b);
            }

        foreach (var e in buckets.Values)
        {
            double r = e.R / e.N;
            double g = e.G / e.N;
            double b = e.B / e.N;
            double hi = Math.Max(r, Math.Max(g, b));
            double lo = Math.Min(r, Math.Min(g, b));
            double sat = hi == 0 ? 0 : (hi - lo) / hi;
            double lum = 0.3 * r + 0.59 * g + 0.11 * b;
            double score = e.N * (0.25 + sat) * (lum > 0.12 && lum < 0.85 ? 1 : 0.15);
            if (score > best.Score) best = (score, r, g, b);
        }

        double max = Math.Max(best.R, Math.Max(best.G, best.B));
        double min = Math.Min(best.R, Math.Min(best.G, best.B));
        double rr = best.R, gg = best.G, bb = best.B;
        if (max - min < 0.08)
        {
            rr = 0.42;
            gg = 0.45;
            bb = 0.5;
        }
        else if (max > 0.82)
        {
            rr *= 0.82 / max;
            gg *= 0.82 / max;
            bb *= 0.82 / max;
        }
        return (uint)(Math.Min(255, rr * 255)) << 16
            | (uint)(Math.Min(255, gg * 255)) << 8
            | (uint)Math.Min(255, bb * 255);
    }
}
