using System.Diagnostics;
using Overlingo.Core;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace Overlingo.App.Capture;

/// <summary>
/// Trvalá WGC relace (okno nebo monitor). Obsluha snímků jen drží nejnovější snímek (levné);
/// kopie do paměti se dělá až na požádání při OCR tiku, takže zátěž neroste s 60 FPS zdroje.
/// </summary>
internal sealed class WgcSource : ICaptureSource
{
    readonly Func<GraphicsCaptureItem> _makeItem;
    readonly string _desc;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    readonly object _lk = new();
    Direct3D11CaptureFrame? _held;
    long _arrived, _copied;
    Frame? _lastFrame;
    int _w, _h;
    long _fpsCount; readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    double _fps;
    bool _disposed;

    public WgcSource(Func<GraphicsCaptureItem> makeItem, string description) { _makeItem = makeItem; _desc = description; }

    public string Description => _desc;
    public double Fps => _fps;
    public string? LastError { get; private set; }
    public DateTime LastFrameUtc { get; private set; }

    public async Task StartAsync()
    {
        _item = _makeItem();
        _w = _item.Size.Width; _h = _item.Size.Height;
        _item.Closed += (_, _) => { LastError = "Zdrojové okno bylo zavřeno nebo se změnilo."; };
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(D3DInterop.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        await DisableBorderAsync().ConfigureAwait(false);
        _session.StartCapture();
    }

    /// <summary>
    /// Vypne žlutý rám, který Windows kreslí kolem zachytávaného okna. Funguje jen na Windows 11 (build 22000+);
    /// na Windows 10 to systém neumožňuje – tam je řešením zdroj „Elgato přímo“ (bez záchytu okna).
    /// </summary>
    async Task DisableBorderAsync()
    {
        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                AppLog.Warn("Tento Windows neumí vypnout žlutý rám záchytu (potřebuje Windows 11). Řešení: zdroj „Elgato přímo“, nebo Windows 11.");
                return;
            }
            try
            {
                if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
                {
                    var st = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().ConfigureAwait(false);
                    AppLog.Info($"Povolení záchytu bez rámu: {st}");
                }
            }
            catch (Exception e) { AppLog.Info($"Povolení záchytu bez rámu (nevadí, zkouším dál): {e.Message}"); }
            _session!.IsBorderRequired = false;
            AppLog.Info("Žlutý rám záchytu: požadavek na vypnutí odeslán.");
        }
        catch (Exception e) { AppLog.Warn($"Žlutý rám se nepodařilo vypnout: {e.Message}"); }
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        if (_disposed) return;
        var f = pool.TryGetNextFrame();
        if (f == null) return;
        LastFrameUtc = DateTime.UtcNow;
        _fpsCount++;
        if (_fpsClock.ElapsedMilliseconds >= 1000) { _fps = _fpsCount * 1000.0 / _fpsClock.ElapsedMilliseconds; _fpsCount = 0; _fpsClock.Restart(); }
        var size = f.ContentSize;
        if (size.Width != _w || size.Height != _h)
        {
            _w = size.Width; _h = size.Height;
            try { pool.Recreate(D3DInterop.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size); } catch (Exception e) { LastError = e.Message; }
        }
        lock (_lk) { _held?.Dispose(); _held = f; _arrived++; }
    }

    public async Task<Frame?> GrabAsync(RegionF region)
    {
        Direct3D11CaptureFrame? f; long arrivedNow;
        lock (_lk) { f = _held; _held = null; arrivedNow = _arrived; }
        try
        {
            if (f != null && arrivedNow != _copied)
            {
                using var bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(f.Surface).AsTask();
                _lastFrame = BitmapUtil.ToFrame(bmp);
                _copied = arrivedNow;
            }
        }
        catch (Exception e) { LastError = $"Kopie snímku selhala: {e.Message}"; }
        finally
        {
            if (f != null) lock (_lk) { if (_held == null) _held = f; else f.Dispose(); }
        }
        var last = _lastFrame;
        if (last == null) return null;
        if (LastError != null && !LastError.StartsWith("Zdrojové")) LastError = null;
        return FrameCrop.Crop(last, region);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        try { if (_pool != null) _pool.FrameArrived -= OnFrame; } catch { }
        try { _session?.Dispose(); } catch { }
        lock (_lk) { _held?.Dispose(); _held = null; }
        try { _pool?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
