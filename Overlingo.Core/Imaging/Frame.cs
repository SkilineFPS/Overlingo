namespace Overlingo.Core;

/// <summary>Snímek BGRA (4 bajty na pixel, řádek = Width*4).</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public Frame(int w, int h, byte[]? data = null)
    {
        Width = w; Height = h; Bgra = data ?? new byte[w * h * 4];
        if (Bgra.Length < w * h * 4) throw new ArgumentException("Buffer je menší než w*h*4.");
    }
}

public interface ICaptureSource : IAsyncDisposable
{
    string Description { get; }
    double Fps { get; }
    string? LastError { get; }
    DateTime LastFrameUtc { get; }
    Task StartAsync();
    /// <summary>Vrátí výřez (normalizovaná oblast) posledního snímku, nebo null, pokud snímek zatím není.</summary>
    Task<Frame?> GrabAsync(RegionF region);
}

/// <summary>Volitelné rozšíření OCR enginu: poloha posledního rozpoznaného textu jako zlomky posledního snímku (přesnější než hledání světlých pixelů).</summary>
public interface IOcrBounds { RegionF? LastBounds { get; } }

public interface IOcrEngine
{
    int MaxDimension { get; }
    /// <summary>Vrátí rozpoznaný text; řádky spojené mezerou.</summary>
    Task<string> RecognizeAsync(Frame frame);
}

/// <summary>Řádek textu s polohou (zlomky celého snímku).</summary>
public sealed record OcrLine(string Text, double X, double Y, double W, double H);

public interface IScreenOcr
{
    int MaxDimension { get; }
    Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Frame frame);
}

public static class FrameCrop
{
    public static Frame Crop(Frame src, RegionF region)
    {
        var r = region.Clamp();
        int x = (int)Math.Round(r.X * src.Width), y = (int)Math.Round(r.Y * src.Height);
        int w = Math.Max(1, (int)Math.Round(r.W * src.Width)), h = Math.Max(1, (int)Math.Round(r.H * src.Height));
        x = Math.Clamp(x, 0, src.Width - 1); y = Math.Clamp(y, 0, src.Height - 1);
        w = Math.Min(w, src.Width - x); h = Math.Min(h, src.Height - y);
        var dst = new Frame(w, h);
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src.Bgra, ((y + row) * src.Width + x) * 4, dst.Bgra, row * w * 4, w * 4);
        return dst;
    }
}
