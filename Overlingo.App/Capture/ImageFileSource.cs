using Overlingo.Core;

namespace Overlingo.App.Capture;

/// <summary>Testovací režim: statický obrázek (screenshot) místo živého zdroje.</summary>
internal sealed class ImageFileSource : ICaptureSource
{
    readonly string _path;
    Frame? _frame;
    public ImageFileSource(string path) { _path = path; }
    public string Description => $"Obrázek: {System.IO.Path.GetFileName(_path)}";
    public double Fps => 0;
    public string? LastError { get; private set; }
    public DateTime LastFrameUtc { get; private set; }
    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(_path) || !System.IO.File.Exists(_path))
            throw new System.IO.FileNotFoundException("Testovací obrázek neexistuje – vyberte ho v záložce Test.");
        _frame = await BitmapUtil.LoadFileAsync(_path);
        LastFrameUtc = DateTime.UtcNow;
    }
    public Task<Frame?> GrabAsync(RegionF region)
    {
        LastFrameUtc = DateTime.UtcNow;
        return Task.FromResult(_frame == null ? null : FrameCrop.Crop(_frame, region));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
