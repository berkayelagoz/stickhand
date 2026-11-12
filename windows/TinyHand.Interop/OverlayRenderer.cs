using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace TinyHand.Interop;

/// <summary>
/// M1.5 render stack owner: one D3D11 device (BGRA support for Direct2D interop),
/// one DXGI factory, one Direct2D factory/device, one DirectComposition device.
/// Everything is created and used on the overlay compositor thread; overlays must
/// not outlive it. Replaces the M1 GDI+/UpdateLayeredWindow surface, whose present
/// blocked 7-16 ms inside DWM redirection sync (see PROGRESS.md M1.5 decision).
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    public ID3D11Device1 Device { get; }
    public ID3D11DeviceContext DeviceContext { get; }
    public IDXGIFactory2 DxgiFactory { get; }
    public ID2D1Factory1 D2dFactory { get; }
    public ID2D1Device D2dDevice { get; }
    public IDCompositionDevice CompositionDevice { get; }

    private bool _disposed;

    public OverlayRenderer()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1],
            out ID3D11Device device).CheckError();
        Device = device.QueryInterface<ID3D11Device1>();
        DeviceContext = device.ImmediateContext;

        var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        DxgiFactory = dxgiDevice.GetAdapter().GetParent<IDXGIFactory2>();

        D2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded, DebugLevel.None);
        D2dDevice = D2dFactory.CreateDevice(dxgiDevice);
        CompositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
    }

    public OverlaySurface CreateSurface(IntPtr hwnd, int width, int height)
    {
        ThrowIfDisposed();
        return new OverlaySurface(this, hwnd, width, height);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OverlayRenderer));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionDevice.Dispose();
        D2dDevice.Dispose();
        D2dFactory.Dispose();
        DxgiFactory.Dispose();
        DeviceContext.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// Per-monitor presentation surface (M1.5): a flip-model composition swap chain
/// (BGRA8, premultiplied alpha) bound to a DirectComposition visual on an
/// WS_EX_NOREDIRECTIONBITMAP overlay window, painted through a Direct2D device
/// context. There is no persistent surface anymore: every frame is a full redraw
/// into the current back buffer, then an asynchronous queue-natured Present
/// (the M1.5 exit criterion: present under 1 ms, no DWM redirection blocking).
/// Monitor changes are handled by rebuilding the whole overlay (no ResizeBuffers
/// path in the M1.5 scope).
/// </summary>
public sealed class OverlaySurface : IDisposable
{
    public const uint DefaultSyncInterval = 0;
    private const uint BufferCount = 2; // flip model minimum; queue depth 1 is enough at these frame times

    private readonly OverlayRenderer _renderer;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private ID2D1Bitmap1? _targetBitmap;

    /// <summary>Direct2D device context that paints this surface (96 DPI: units are pixels).</summary>
    public ID2D1DeviceContext Context { get; }

    public OverlaySurface(OverlayRenderer renderer, IntPtr hwnd, int width, int height)
    {
        _renderer = renderer;

        var desc = new SwapChainDescription1(
            (uint)width, (uint)height, Vortice.DXGI.Format.B8G8R8A8_UNorm, stereo: false,
            Usage.RenderTargetOutput, BufferCount, Scaling.Stretch,
            SwapEffect.FlipSequential, Vortice.DXGI.AlphaMode.Premultiplied, SwapChainFlags.None)
        {
            SampleDescription = new SampleDescription(1, 0),
        };
        _swapChain = renderer.DxgiFactory.CreateSwapChainForComposition(renderer.Device, desc);

        renderer.CompositionDevice.CreateVisual(out _visual).CheckError();
        _visual.SetContent(_swapChain);
        renderer.CompositionDevice.CreateTargetForHwnd(hwnd, topmost: true, out _target).CheckError();
        _target.SetRoot(_visual);
        renderer.CompositionDevice.Commit();

        Context = renderer.D2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        // Pixel units: scene geometry is monitor-local pixels, DPI scaling is done
        // explicitly by the scene (integer scale factor), like the M1 renderer.
        Context.SetDpi(96, 96);
    }

    /// <summary>Bind the current back buffer as the render target and start batching.</summary>
    public void BeginDraw()
    {
        using var texture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var surface = texture.QueryInterface<IDXGISurface>();
        _targetBitmap = Context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1
        {
            PixelFormat = new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96,
            DpiY = 96,
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
        });
        Context.Target = _targetBitmap;
        Context.BeginDraw();
    }

    /// <summary>
    /// Flush the Direct2D batch and present. Flip-model Present is asynchronous
    /// (queued to DWM) unlike UpdateLayeredWindow, which blocked in redirection sync.
    /// Returns false on device loss / failed present; callers rebuild the stack.
    /// </summary>
    public bool EndDraw()
    {
        Result result = Context.EndDraw();
        Context.Target = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        return result.Success;
    }

    public bool Present(uint syncInterval = DefaultSyncInterval)
    {
        return _swapChain.Present(syncInterval, PresentFlags.None).Success;
    }

    public void Dispose()
    {
        _targetBitmap?.Dispose();
        Context.Dispose();
        _target.Dispose();
        _visual.Dispose();
        _swapChain.Dispose();
    }
}
