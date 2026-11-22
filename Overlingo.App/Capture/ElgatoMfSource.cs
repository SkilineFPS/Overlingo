using System.Diagnostics;
using Overlingo.Core;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Overlingo.App.Capture;

/// <summary>Přímé čtení z Elgato HD60 Pro přes Media Foundation (SharedReadOnly – ostatní programy kartu dál mohou používat). Kopíruje snímek jen na požádání.</summary>
internal sealed class ElgatoMfSource : ICaptureSource
{
    MediaCapture? _mc;
    MediaFrameReader? _reader;
    volatile bool _want;
    TaskCompletionSource<Frame?>? _tcs;
    Frame? _last;
    long _fpsCount; readonly Stopwatch _fpsClock = Stopwatch.StartNew(); double _fps;
    string _desc = "Elgato (Media Foundation)";

    public string Description => _desc;
    public double Fps => _fps;
    public string? LastError { get; private set; }
    public DateTime LastFrameUtc { get; private set; }

    public static bool IsElgato(string name) =>
        name.Contains("elgato", StringComparison.OrdinalIgnoreCase) || name.Contains("hd60", StringComparison.OrdinalIgnoreCase)
        || name.Contains("game capture", StringComparison.OrdinalIgnoreCase) || name.Contains("4k60", StringComparison.OrdinalIgnoreCase);

    readonly string _deviceName;
    public ElgatoMfSource(string deviceName = "") { _deviceName = deviceName ?? ""; }

    static readonly string[] CardWords = { "elgato", "hd60", "game capture", "4k60", "cam link", "avermedia", "live gamer", "magewell",
        "razer ripsaw", "blackmagic", "decklink", "hdmi", "capture", "usb video", "usb3 video", "ezcap", "startech", "atomos" };
    public static bool LooksLikeCaptureCard(string name) => CardWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Všechna video zařízení, která Windows nabízí (záznamové karty první, pak ostatní, např. webkamery).</summary>
    public static async Task<List<string>> ListDevicesAsync()
    {
        var res = new List<string>();
        try
        {
            foreach (var g in await MediaFrameSourceGroup.FindAllAsync())
                if (g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color) && !res.Contains(g.DisplayName)) res.Add(g.DisplayName);
        }
        catch (Exception e) { AppLog.Warn("Výpis záznamových zařízení selhal: " + e.Message); }
        return res.OrderByDescending(LooksLikeCaptureCard).ThenBy(x => x).ToList();
    }

    public async Task StartAsync()
    {
        var groups = (await MediaFrameSourceGroup.FindAllAsync()).Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color)).ToList();
        MediaFrameSourceGroup? group = null;
        if (_deviceName.Length > 0)
        {
            group = groups.FirstOrDefault(g => g.DisplayName == _deviceName)
                ?? throw new InvalidOperationException($"Záznamové zařízení „{_deviceName}“ nebylo nalezeno (je připojené?).");
        }
        else
        {
            group = groups.FirstOrDefault(g => IsElgato(g.DisplayName)) ?? groups.FirstOrDefault(g => LooksLikeCaptureCard(g.DisplayName))
                ?? throw new InvalidOperationException("Nenalezena žádná záznamová karta. Připojte ji nebo ji vyberte ručně v nastavení zdroje.");
        }
        _mc = new MediaCapture();
        await _mc.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });
        var src = _mc.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new InvalidOperationException("Elgato neposkytuje barevný video zdroj.");
        try { _reader = await _mc.CreateFrameReaderAsync(src, MediaEncodingSubtypes.Bgra8); }
        catch { _reader = await _mc.CreateFrameReaderAsync(src); }
        _reader.FrameArrived += OnFrame;
        var st = await _reader.StartAsync();
        if (st != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"Start čtení z Elgata selhal: {st}");
        _desc = $"Záznamová karta: {group.DisplayName} (SharedReadOnly)";
    }

    void OnFrame(MediaFrameReader r, MediaFrameArrivedEventArgs _)
    {
        using var fr = r.TryAcquireLatestFrame();
        var sb = fr?.VideoMediaFrame?.SoftwareBitmap;
        if (sb == null) return;
        LastFrameUtc = DateTime.UtcNow; _fpsCount++;
        if (_fpsClock.ElapsedMilliseconds >= 1000) { _fps = _fpsCount * 1000.0 / _fpsClock.ElapsedMilliseconds; _fpsCount = 0; _fpsClock.Restart(); }
        if (!_want) return;
        _want = false;
        try { var f = BitmapUtil.ToFrame(sb); _last = f; _tcs?.TrySetResult(f); LastError = null; }
        catch (Exception e) { LastError = e.Message; _tcs?.TrySetResult(null); }
    }

    public async Task<Frame?> GrabAsync(RegionF region)
    {
        _tcs = new TaskCompletionSource<Frame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _want = true;
        var done = await Task.WhenAny(_tcs.Task, Task.Delay(600));
        Frame? f = done == _tcs.Task ? _tcs.Task.Result : _last;
        if (f == null) { LastError ??= "Z Elgata nepřicházejí snímky."; return null; }
        return FrameCrop.Crop(f, region);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_reader != null) { _reader.FrameArrived -= OnFrame; await _reader.StopAsync(); _reader.Dispose(); } } catch { }
        try { _mc?.Dispose(); } catch { }
    }
}
