using System.Drawing;
using System.Numerics;
using TinyHand.Interop;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;
using Rect = Vortice.Mathematics.Rect;

namespace TinyHand.Overlay;

/// <summary>One ink segment in screen coordinates (From == To: invisible anchor dot, like the M1 renderer).</summary>
public readonly record struct InkEvent(Point From, Point To);

/// <summary>
/// Hook-thread -> compositor ink feed. The hook appends segments as they are
/// produced; every monitor's InkLayer reads only the events it has not applied
/// yet. Clear bumps the generation so readers wipe their bitmap and replay;
/// a fresh InkLayer (new device, monitor rebuild) replays the whole journal and
/// repaints existing ink without the compositor copying point lists per frame
/// (the M1.5 redux cause of the draw-cost growth while drawing).
/// </summary>
public sealed class InkJournal
{
    private readonly object _lock = new();
    private List<InkEvent> _events = [];
    private int _generation;

    public void AddSegment(Point from, Point to)
    {
        lock (_lock) _events.Add(new InkEvent(from, to));
    }

    /// <summary>Wipes the journal. No-op when nothing was added since the last clear:
    /// redundant Esc/deactivate sequences must not bump the generation and force
    /// layers to clear+replay.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_events.Count == 0) return;
            _generation++;
            _events = [];
        }
    }

    /// <summary>
    /// Events the reader has not applied yet. A generation mismatch means the
    /// journal was cleared: the caller must wipe its bitmap, then apply the
    /// returned (post-clear) events.
    /// </summary>
    public InkEvent[] ReadNew(int knownGeneration, int knownIndex, out int generation, out int index)
    {
        lock (_lock)
        {
            generation = _generation;
            index = _events.Count;
            if (knownGeneration != _generation)
            {
                // full replay of the new journal (cheap: the caller just cleared)
                return _events.ToArray();
            }
            if (knownIndex >= _events.Count || knownIndex < 0) return [];
            var batch = new InkEvent[_events.Count - knownIndex];
            _events.CopyTo(knownIndex, batch, 0, batch.Length);
            return batch;
        }
    }
}

/// <summary>
/// Persistent per-monitor ink bitmap (M1.5 acyclovir-fix). Completed segments are
/// painted into the bitmap exactly once, at append time, on this layer's own
/// Direct2D device context; each frame the scene only blits the bitmap. Frame
/// cost is O(new points), not O(total ink) — the full-redraw DrawLine loop used
/// to grow linearly with ink length and drop the frame budget in long drawings.
/// Ink visual contract with M1: same color, round caps, 4px * DPI scale, drawn
/// under the hand.
/// </summary>
public sealed class InkLayer : IDisposable
{
    private readonly ID2D1DeviceContext _ctx;
    private readonly ID2D1Bitmap1 _bitmap;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly ID2D1StrokeStyle _style;
    private readonly float _offX, _offY, _width;

    private int _generation = -1; // first sync aligns to the journal's current generation (empty replay)
    private int _index;
    private bool _painted;

    public InkLayer(OverlayRenderer renderer, MonitorInfo monitor, int scale)
    {
        // Own context: ink can be painted while the frame context targets the back buffer.
        _ctx = renderer.D2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.SetDpi(96, 96);

        _bitmap = _ctx.CreateBitmap(
            new Vortice.Mathematics.SizeI(monitor.Bounds.Width, monitor.Bounds.Height),
            IntPtr.Zero, 0,
            new BitmapProperties1
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = 96,
                DpiY = 96,
                BitmapOptions = BitmapOptions.Target,
            });
        _ctx.Target = _bitmap;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0)); // CreateBitmap leaves content undefined
        _ctx.EndDraw();

        _brush = _ctx.CreateSolidColorBrush(new Color4(244 / 255f, 51 / 255f, 118 / 255f, 1));
        _style = renderer.D2dFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 1,
            DashStyle = DashStyle.Solid,
            DashOffset = 0,
        });

        _offX = -monitor.Bounds.X;
        _offY = -monitor.Bounds.Y;
        _width = 4f * scale;
    }

    /// <summary>The persistent ink bitmap to blit into each frame (monitor-local pixels).</summary>
    public ID2D1Bitmap1 Bitmap => _bitmap;

    /// <summary>True when the bitmap holds any ink (skips the blit otherwise).</summary>
    public bool HasInk => _painted;

    /// <summary>Apply journal events this layer has not painted yet.</summary>
    public void Sync(InkJournal journal)
    {
        var events = journal.ReadNew(_generation, _index, out int generation, out int index);
        bool cleared = _generation != generation;
        _generation = generation;
        _index = index;

        if (cleared && _painted)
        {
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0, 0, 0, 0));
            _ctx.EndDraw();
            _painted = false;
        }

        if (events.Length == 0) return;

        _ctx.BeginDraw();
        foreach (var e in events)
        {
            if (e.From == e.To) continue; // bare anchor dots stay invisible (M1 behavior)
            var a = new Vector2(e.From.X + _offX, e.From.Y + _offY);
            var b = new Vector2(e.To.X + _offX, e.To.Y + _offY);
            _ctx.DrawLine(a, b, _brush, _width, _style);
        }
        _ctx.EndDraw();
        _painted = true;
    }

    public void Dispose()
    {
        _brush.Dispose();
        _style.Dispose();
        _bitmap.Dispose();
        _ctx.Dispose();
    }
}
