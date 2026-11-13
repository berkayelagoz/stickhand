using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using TinyHand.Interop;
using TinyHand.Logic;
using Vortice.Direct2D1;
using Vortice.DCommon;
using D2dAlphaMode = Vortice.DCommon.AlphaMode;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using Rect = Vortice.Mathematics.Rect;
using SizeI = Vortice.Mathematics.SizeI;

namespace TinyHand.Overlay;

/// <summary>Stroke lifecycle event produced by the hook thread.</summary>
public enum InkEventKind
{
    Begin,
    Extend,
    End,
    Clear,
}

/// <summary>One journal entry. <see cref="Arrow"/> is the Ctrl state at append time, so the
/// head baked at <see cref="InkEventKind.End"/> cannot race a later Ctrl release. Points are
/// sub-pixel: the smoothed centreline is never re-quantized to whole pixels.</summary>
public readonly record struct InkEvent(InkEventKind Kind, PointF Point, bool Arrow);

/// <summary>
/// Hook-thread -> compositor ink feed (M2). The hook appends stroke lifecycle events as they
/// happen; every monitor's InkLayer reads only the events it has not applied yet. Clear bumps
/// the generation so readers wipe their bitmaps and replay; a fresh InkLayer (new device,
/// monitor rebuild) replays the whole journal and repaints existing ink without the compositor
/// copying point lists per frame.
/// </summary>
public sealed class InkJournal
{
    private readonly object _lock = new();
    private List<InkEvent> _events = [];
    private int _generation;
    private volatile bool _arrow;

    /// <summary>Ctrl held: the active stroke grows an arrow head (macOS ⌘). Read by the
    /// compositor every frame so pressing/releasing Ctrl updates the head without a move.</summary>
    public bool Arrow
    {
        get => _arrow;
        set => _arrow = value;
    }

    public void Begin(PointF point) => Append(new InkEvent(InkEventKind.Begin, point, _arrow));

    public void Extend(PointF point) => Append(new InkEvent(InkEventKind.Extend, point, _arrow));

    public void End() => Append(new InkEvent(InkEventKind.End, default, _arrow));

    private void Append(InkEvent e)
    {
        lock (_lock) _events.Add(e);
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
    /// journal was cleared: the caller must wipe its bitmaps, then apply the
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
/// Per-monitor ink surface (M2). The active stroke is painted incrementally into a
/// monitor-sized live bitmap (O(new points) per frame, never O(total ink)); when the stroke
/// ends it is frozen into a bounding-box bitmap and fades independently, so overlapping
/// strokes fade on their own schedule like the macOS per-stroke CAShapeLayers.
///
/// Visual contract with macOS Ink.swift: InkChoice colour, round caps/joins, smoothed path
/// (quad curves through midpoints), 1.5 pt point dedup (hook side), black shadow at 0.18
/// opacity with a 1.5 pt radius and a (1, −1.5) offset, arrow head max(14, width × 3.2).
/// The shadow is approximated by an offset, radius-widened stroke pass: at 18 % opacity and
/// a 1.5 pt radius the difference from a blurred copy is below one pixel.
/// </summary>
public sealed class InkLayer : IDisposable
{
    private sealed class FrozenStroke
    {
        public required ID2D1Bitmap1 Bitmap { get; init; }
        public required Rectangle Rect { get; init; }
        public required long EndTimestamp { get; init; }
    }

    /// <summary>One smoothed-path piece: a quad curve (control point) or a straight line.
    /// <see cref="NewFigure"/> forces a separate subpath (the two arrow-head lines both start
    /// at the stroke end; continuing one figure would draw a bar between them).</summary>
    private readonly record struct InkSegment(PointF Start, PointF Control, PointF End, bool IsLine,
        bool NewFigure = false);

    private readonly ID2D1DeviceContext _ctx;
    private readonly ID2D1Bitmap1 _live;
    private readonly ID2D1Bitmap1 _liveShadow;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly ID2D1SolidColorBrush _shadowBrush;
    private readonly ID2D1StrokeStyle _style;
    private readonly ID2D1Factory1 _factory;
    private readonly List<FrozenStroke> _strokes = [];
    private readonly List<PointF> _points = [];
    private readonly Rectangle _monitorBounds;
    private readonly float _offX, _offY;
    private readonly float _width;
    private readonly float _shadowWidth;
    private readonly float _shadowDx, _shadowDy;
    private readonly int _scale;
    private readonly int _blitPad;

    private int _generation = -1; // first sync aligns to the journal's current generation (empty replay)
    private int _index;
    private bool _liveActive;
    private bool _arrow;
    private Rectangle _liveBounds; // screen coords of the stroke centreline painted into the live bitmap
    private volatile int _strokeCount;

    public InkLayer(OverlayRenderer renderer, MonitorInfo monitor, int scale, Color inkColor)
    {
        // Own context: ink can be painted while the frame context targets the back buffer.
        _ctx = renderer.D2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.SetDpi(96, 96);
        _factory = renderer.D2dFactory;
        _monitorBounds = monitor.Bounds;
        _scale = scale;

        _live = CreateTarget(monitor);
        _liveShadow = CreateTarget(monitor);
        _ctx.Target = _live;
        ClearLive(); // CreateBitmap leaves content undefined

        _brush = _ctx.CreateSolidColorBrush(new Color4(inkColor.R / 255f, inkColor.G / 255f, inkColor.B / 255f, 1));
        // Opaque black: the 18 % opacity is applied once when the shadow layer is composited,
        // so overlapping batches cannot darken each other (the round-blob artifact).
        _shadowBrush = _ctx.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
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
        _width = InkMetrics.Width(monitor.Bounds.Height, scale);
        float shadowRadius = 1.5f * scale;
        _shadowWidth = _width + 2 * shadowRadius;
        // macOS shadowOffset (1, −1.5) is y-up (down-right); Direct2D y grows downwards.
        _shadowDx = 1f * scale;
        _shadowDy = 1.5f * scale;
        // Stroke + shadow extent around the centreline: the live blit and the freeze bbox
        // must both include it, otherwise the stroke edges are clipped while drawing.
        _blitPad = (int)Math.Ceiling(_width / 2 + shadowRadius * 3
            + Math.Max(Math.Abs(_shadowDx), Math.Abs(_shadowDy)) + 2);
    }

    private ID2D1Bitmap1 CreateTarget(MonitorInfo monitor) => _ctx.CreateBitmap(
        new SizeI(monitor.Bounds.Width, monitor.Bounds.Height),
        IntPtr.Zero, 0,
        new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, D2dAlphaMode.Premultiplied),
            DpiX = 96,
            DpiY = 96,
            BitmapOptions = BitmapOptions.Target,
        });

    /// <summary>True when the layer holds live or fading ink (skips the blits otherwise).</summary>
    public bool HasInk => _liveActive || _strokes.Count > 0;

    /// <summary>Live + frozen strokes (harness/status read; written on the compositor thread).</summary>
    public int StrokeCount => _strokeCount;

    /// <summary>True while a stroke is being painted into the live bitmap (diagnostics).</summary>
    public bool LiveActive => _liveActive;

    /// <summary>True while a frozen stroke still has to fade: the compositor must keep
    /// rendering even when the pointer is idle, otherwise the fade would stall.</summary>
    public bool NeedsFrame
    {
        get
        {
            double fadeSeconds = Preferences.FadeSeconds;
            if (InkFade.Keep(fadeSeconds)) return false;
            double now = NowSeconds();
            // + margin: one more frame must run after the fade completes so the stroke is
            // actually removed (the last visible frame still has opacity > 0).
            double window = InkFade.DelaySeconds(fadeSeconds) + InkFade.DurationSeconds + 0.25;
            foreach (var stroke in _strokes)
                if (now - ToSeconds(stroke.EndTimestamp) < window)
                    return true;
            return false;
        }
    }

    /// <summary>Apply journal events this layer has not painted yet.</summary>
    public void Sync(InkJournal journal)
    {
        var events = journal.ReadNew(_generation, _index, out int generation, out int index);
        bool cleared = _generation != generation;
        _generation = generation;
        _index = index;
        _arrow = journal.Arrow;

        if (cleared) WipeAll();
        if (events.Length == 0) return;

        var batch = new List<InkSegment>();
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case InkEventKind.Begin:
                    Flush(batch);
                    BeginStroke(e.Point);
                    break;
                case InkEventKind.Extend:
                    if (!_liveActive) BeginStroke(e.Point);
                    else ExtendStroke(e.Point, batch);
                    break;
                case InkEventKind.End:
                    if (_liveActive) EndStroke(batch, e.Arrow);
                    break;
                case InkEventKind.Clear:
                    Flush(batch);
                    WipeAll();
                    break;
            }
        }
        Flush(batch);
        // Keep the harness/status count current even when the layer ends up empty (a stroke
        // clipped away by this monitor's bounds never reaches Draw).
        _strokeCount = _strokes.Count + (_liveActive ? 1 : 0);
    }

    /// <summary>Composite frozen strokes (with their fade opacity) and the live stroke into
    /// the frame. Called between the scene's BeginDraw/EndDraw.</summary>
    public void Draw(ID2D1DeviceContext ctx, MonitorInfo monitor)
    {
        double now = NowSeconds();
        double fadeSeconds = Preferences.FadeSeconds;

        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            float opacity = InkFade.Opacity(now - ToSeconds(stroke.EndTimestamp), fadeSeconds);
            if (opacity <= 0)
            {
                stroke.Bitmap.Dispose();
                _strokes.RemoveAt(i);
                continue;
            }
            var dest = new Rect(
                stroke.Rect.X - monitor.Bounds.X, stroke.Rect.Y - monitor.Bounds.Y,
                stroke.Rect.Width, stroke.Rect.Height);
            ctx.DrawBitmap(stroke.Bitmap, dest, opacity, BitmapInterpolationMode.NearestNeighbor,
                new Rect(0, 0, stroke.Rect.Width, stroke.Rect.Height));
        }

        if (_liveActive && _liveBounds.Width > 0)
        {
            // Blit the stroke + shadow extent, not just the centreline bounds: the outer half
            // of the stroke used to be clipped until the stroke froze (the "missing parts
            // appear after the drawing ends" report).
            var blit = Rectangle.Inflate(_liveBounds, _blitPad, _blitPad);
            blit.Intersect(_monitorBounds);
            var local = new Rect(
                blit.X - monitor.Bounds.X, blit.Y - monitor.Bounds.Y, blit.Width, blit.Height);
            // Shadow layer first, at 18 %: a single composite, so overlapping batches never
            // accumulate into darker round blobs along the stroke.
            ctx.DrawBitmap(_liveShadow, local, 0.18f, BitmapInterpolationMode.NearestNeighbor, local);
            ctx.DrawBitmap(_live, local, 1f, BitmapInterpolationMode.NearestNeighbor, local);
            if (_arrow) DrawHead(ctx);
        }

        _strokeCount = _strokes.Count + (_liveActive ? 1 : 0);
    }

    // =====================================================================
    // Stroke painting (compositor thread)
    // =====================================================================

    private void BeginStroke(PointF point)
    {
        if (_liveActive) Freeze(); // defensive: a Begin without End closes the previous stroke
        ClearLive();
        _liveActive = true;
        _points.Clear();
        _points.Add(point);
        ExpandLiveBounds(point);
    }

    /// <summary>macOS <c>Ink.smoothPath</c> incrementally: each new point adds the quad from
    /// the previous midpoint to the new midpoint, controlled by the previous point.</summary>
    private void ExtendStroke(PointF point, List<InkSegment> batch)
    {
        var prev = _points[^1];
        if (_points.Count == 1)
        {
            batch.Add(new InkSegment(_points[0], _points[0], InkGeometry.Mid(_points[0], point), false));
        }
        else
        {
            var prevPrev = _points[^2];
            batch.Add(new InkSegment(InkGeometry.Mid(prevPrev, prev), prev, InkGeometry.Mid(prev, point), false));
        }
        _points.Add(point);
        ExpandLiveBounds(point);
    }

    private void EndStroke(List<InkSegment> batch, bool arrow)
    {
        if (_points.Count > 1)
        {
            var last = _points[^1];
            batch.Add(new InkSegment(InkGeometry.Mid(_points[^2], last), default, last, true));
        }
        else if (_points.Count == 1)
        {
            // Single-point stroke: a zero-length round-capped line renders as a dot.
            batch.Add(new InkSegment(_points[0], default, _points[0], true));
        }

        if (arrow && InkGeometry.TryHead(_points, _width, _scale, out var end, out var left, out var right))
        {
            batch.Add(new InkSegment(end, default, left, true, NewFigure: true));
            batch.Add(new InkSegment(end, default, right, true, NewFigure: true));
            ExpandLiveBounds(left);
            ExpandLiveBounds(right);
        }

        Flush(batch);
        Freeze();
        _liveActive = false;
        _points.Clear();
    }

    private void Flush(List<InkSegment> batch)
    {
        if (batch.Count == 0) return;
        using var geometry = BuildGeometry(batch);
        Paint(geometry);
        batch.Clear();
    }

    private ID2D1PathGeometry BuildGeometry(List<InkSegment> segments)
    {
        var geometry = _factory.CreatePathGeometry();
        using var sink = geometry.Open();
        bool open = false;
        PointF current = default;
        foreach (var segment in segments)
        {
            // Disjoint pieces need their own figure; continuing the previous one would
            // connect them with an extra line (the arrow-head bar bug).
            if (!open || segment.NewFigure || segment.Start != current)
            {
                if (open) sink.EndFigure(FigureEnd.Open);
                sink.BeginFigure(ToLocal(segment.Start), FigureBegin.Filled);
                open = true;
            }
            if (segment.IsLine) sink.AddLine(ToLocal(segment.End));
            else sink.AddQuadraticBezier(new QuadraticBezierSegment
            {
                Point1 = ToLocal(segment.Control),
                Point2 = ToLocal(segment.End),
            });
            current = segment.End;
        }
        if (open) sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geometry;
    }

    private void Paint(ID2D1Geometry geometry)
    {
        // Shadow layer: opaque black, offset. It is composited once at 18 % under the ink,
        // so overlapping batches cannot accumulate alpha (the round-blob artifact).
        _ctx.Target = _liveShadow;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.CreateTranslation(_shadowDx, _shadowDy);
        _ctx.DrawGeometry(geometry, _shadowBrush, _shadowWidth, _style);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.EndDraw();

        _ctx.Target = _live;
        _ctx.BeginDraw();
        _ctx.DrawGeometry(geometry, _brush, _width, _style);
        _ctx.EndDraw();
    }

    /// <summary>Copy the finished stroke out of the live bitmap into a bounding-box bitmap so
    /// it can fade on its own; the live bitmap is then reused for the next stroke.</summary>
    private void Freeze()
    {
        if (_liveBounds.Width <= 0 || _liveBounds.Height <= 0)
        {
            ClearLive();
            return;
        }

        // macOS Ink.freeze pad: lineWidth/2 + shadowRadius × 3 + max(|offset|) + 2.
        var bbox = Rectangle.Inflate(_liveBounds, _blitPad, _blitPad);
        bbox.Intersect(_monitorBounds);
        if (bbox.Width <= 0 || bbox.Height <= 0)
        {
            ClearLive();
            return;
        }

        var bitmap = _ctx.CreateBitmap(new SizeI(bbox.Width, bbox.Height), IntPtr.Zero, 0, new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, D2dAlphaMode.Premultiplied),
            DpiX = 96,
            DpiY = 96,
            BitmapOptions = BitmapOptions.Target,
        });

        _ctx.Target = bitmap;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0));
        var src = new Rect(bbox.X - _monitorBounds.X, bbox.Y - _monitorBounds.Y, bbox.Width, bbox.Height);
        var dest = new Rect(0, 0, bbox.Width, bbox.Height);
        // Bake the shadow under the ink once, so the frozen stroke fades as one image.
        _ctx.DrawBitmap(_liveShadow, dest, 0.18f, BitmapInterpolationMode.NearestNeighbor, src);
        _ctx.DrawBitmap(_live, dest, 1f, BitmapInterpolationMode.NearestNeighbor, src);
        _ctx.EndDraw();
        _ctx.Target = _live;

        _strokes.Add(new FrozenStroke
        {
            Bitmap = bitmap,
            Rect = bbox,
            EndTimestamp = Stopwatch.GetTimestamp(),
        });
        ClearLive();
    }

    private void DrawHead(ID2D1DeviceContext ctx)
    {
        if (!InkGeometry.TryHead(_points, _width, _scale, out var end, out var left, out var right)) return;
        var a = ToLocal(end);
        var b = ToLocal(left);
        var c = ToLocal(right);
        var offset = new Vector2(_shadowDx, _shadowDy);
        ctx.DrawLine(a + offset, b + offset, _shadowBrush, _shadowWidth, _style);
        ctx.DrawLine(a + offset, c + offset, _shadowBrush, _shadowWidth, _style);
        ctx.DrawLine(a, b, _brush, _width, _style);
        ctx.DrawLine(a, c, _brush, _width, _style);
    }

    private void ClearLive()
    {
        _ctx.Target = _live;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0));
        _ctx.EndDraw();

        _ctx.Target = _liveShadow;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0));
        _ctx.EndDraw();

        _ctx.Target = _live;
        _liveBounds = Rectangle.Empty;
    }

    private void WipeAll()
    {
        foreach (var stroke in _strokes) stroke.Bitmap.Dispose();
        _strokes.Clear();
        _points.Clear();
        _liveActive = false;
        ClearLive();
        _strokeCount = 0;
    }

    private void ExpandLiveBounds(PointF point)
    {
        // Floor + 1: the centreline is sub-pixel, so the blit and freeze boxes must cover the
        // whole pixel the stroke touches. A zero-width box would reset the extent.
        int left = (int)Math.Floor(point.X);
        int top = (int)Math.Floor(point.Y);
        int right = left + 1;
        int bottom = top + 1;
        if (_liveBounds.Width <= 0 || _liveBounds.Height <= 0)
        {
            _liveBounds = Rectangle.FromLTRB(left, top, right, bottom);
            return;
        }
        _liveBounds = Rectangle.FromLTRB(
            Math.Min(_liveBounds.Left, left),
            Math.Min(_liveBounds.Top, top),
            Math.Max(_liveBounds.Right, right),
            Math.Max(_liveBounds.Bottom, bottom));
    }

    private Vector2 ToLocal(PointF point) => new(point.X + _offX, point.Y + _offY);

    private static double NowSeconds() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private static double ToSeconds(long timestamp) => (double)timestamp / Stopwatch.Frequency;

    public void Dispose()
    {
        foreach (var stroke in _strokes) stroke.Bitmap.Dispose();
        _strokes.Clear();
        _brush.Dispose();
        _shadowBrush.Dispose();
        _style.Dispose();
        _live.Dispose();
        _liveShadow.Dispose();
        _ctx.Dispose();
    }
}
