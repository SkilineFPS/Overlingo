namespace Overlingo.Core;

/// <summary>Najde obraz hry uvnitř snímku: ořízne černé pruhy (letterbox / pillarbox) kolem. Vrací zlomky snímku, nebo null, když není co ořezávat.</summary>
public static class GameAreaDetector
{
    const int DarkLum = 28;          // jas 0–255, pod nímž je pixel „černý“
    const double MaxLitFraction = 0.01;   // řádek/sloupec je pruh, když je světlých pixelů méně než 1 %
    const double MinBar = 0.02;      // pruhy menší než 2 % se ignorují

    public static RegionF? Detect(Frame f)
    {
        if (f.Width < 64 || f.Height < 64) return null;
        int sx = Math.Max(1, f.Width / 400), sy = Math.Max(1, f.Height / 300);
        bool RowBar(int y)
        {
            int lit = 0, n = 0;
            for (int x = 0; x < f.Width; x += sx) { n++; if (Lum(f, x, y) > DarkLum) lit++; }
            return lit <= n * MaxLitFraction;
        }
        bool ColBar(int x, int y0, int y1)
        {
            int lit = 0, n = 0;
            for (int y = y0; y < y1; y += sy) { n++; if (Lum(f, x, y) > DarkLum) lit++; }
            return n == 0 || lit <= n * MaxLitFraction;
        }
        int top = 0; while (top < f.Height && RowBar(top)) top++;
        if (top >= f.Height - 1) return null;                       // celý snímek černý
        int bottom = f.Height - 1; while (bottom > top && RowBar(bottom)) bottom--;
        int left = 0; while (left < f.Width && ColBar(left, top, bottom + 1)) left++;
        int right = f.Width - 1; while (right > left && ColBar(right, top, bottom + 1)) right--;

        double x0 = (double)left / f.Width, y0 = (double)top / f.Height;
        double x1 = (double)(right + 1) / f.Width, y1 = (double)(bottom + 1) / f.Height;
        if (x0 < MinBar && y0 < MinBar && 1 - x1 < MinBar && 1 - y1 < MinBar) return null;
        if (x0 < MinBar) x0 = 0; if (y0 < MinBar) y0 = 0;
        if (1 - x1 < MinBar) x1 = 1; if (1 - y1 < MinBar) y1 = 1;
        if ((x1 - x0) * (y1 - y0) < 0.3) return null;                  // podezřele málo – raději nic neořezávat
        return new RegionF { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 };
    }

    static int Lum(Frame f, int x, int y)
    {
        int i = (y * f.Width + x) * 4;
        return (f.Bgra[i] * 29 + f.Bgra[i + 1] * 150 + f.Bgra[i + 2] * 77) >> 8;
    }
}
