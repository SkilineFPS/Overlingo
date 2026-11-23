namespace Overlingo.Core;

public readonly record struct LightSignature(float[] Cells, double LightFraction);

/// <summary>Předzpracování snímku pro OCR a detekci změny (čistě CPU, bez závislostí).</summary>
public static class ImagePrep
{
    public static Frame ScaleBilinear(Frame src, int factor)
    {
        if (factor <= 1) return src;
        int w = src.Width * factor, h = src.Height * factor;
        var dst = new Frame(w, h);
        for (int y = 0; y < h; y++)
        {
            float fy = (y + 0.5f) / factor - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, src.Height - 1), y1 = Math.Min(y0 + 1, src.Height - 1);
            float ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) / factor - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, src.Width - 1), x1 = Math.Min(x0 + 1, src.Width - 1);
                float tx = Math.Clamp(fx - x0, 0, 1);
                int o = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float a = src.Bgra[(y0 * src.Width + x0) * 4 + c], b = src.Bgra[(y0 * src.Width + x1) * 4 + c];
                    float d = src.Bgra[(y1 * src.Width + x0) * 4 + c], e = src.Bgra[(y1 * src.Width + x1) * 4 + c];
                    float top = a + (b - a) * tx, bot = d + (e - d) * tx;
                    dst.Bgra[o + c] = (byte)Math.Clamp(top + (bot - top) * ty + 0.5f, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>Izoluje světlý (bílý) text: pixel s min(B,G,R) &gt;= threshold → černá na bílém, okolo bílý okraj.</summary>
    public static Frame IsolateLight(Frame src, int threshold, int padding = 12)
    {
        int w = src.Width + 2 * padding, h = src.Height + 2 * padding;
        var dst = new Frame(w, h);
        Array.Fill(dst.Bgra, (byte)255);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int i = (y * src.Width + x) * 4;
                int m = Math.Min(src.Bgra[i], Math.Min(src.Bgra[i + 1], src.Bgra[i + 2]));
                if (m >= threshold)
                {
                    int o = ((y + padding) * w + x + padding) * 4;
                    dst.Bgra[o] = dst.Bgra[o + 1] = dst.Bgra[o + 2] = 0;
                }
            }
        return dst;
    }

    /// <summary>Podíl pixelů s min(B,G,R) &gt;= threshold (0..1).</summary>
    public static double LightFraction(Frame f, int threshold)
    {
        long n = 0; int total = f.Width * f.Height;
        for (int i = 0; i < total; i++)
        {
            int o = i * 4;
            if (Math.Min(f.Bgra[o], Math.Min(f.Bgra[o + 1], f.Bgra[o + 2])) >= threshold) n++;
        }
        return total == 0 ? 0 : (double)n / total;
    }

    /// <summary>
    /// Izolace pro světlé scény (obloha, písek, sníh): pixel je text jen když je velmi světlý a zároveň výrazně světlejší
    /// než nejtmavší pixel v okolí (radius) – tj. leží u stínu/obrysu, který hra pod titulek kreslí. Světlé plochy bez obrysu zmizí.
    /// </summary>
    public static Frame IsolateLightOutlined(Frame src, int minChannel = 200, int contrast = 70, int radius = 5, int padding = 12)
    {
        int W = src.Width, H = src.Height;
        var m = new byte[W * H];
        for (int i = 0; i < W * H; i++)
        {
            int o = i * 4;
            m[i] = (byte)Math.Min(src.Bgra[o], Math.Min(src.Bgra[o + 1], src.Bgra[o + 2]));
        }
        var tmp = new byte[W * H]; var mn = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte v = 255; int x0 = Math.Max(0, x - radius), x1 = Math.Min(W - 1, x + radius);
                for (int k = x0; k <= x1; k++) if (m[y * W + k] < v) v = m[y * W + k];
                tmp[y * W + x] = v;
            }
        for (int y = 0; y < H; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(H - 1, y + radius);
            for (int x = 0; x < W; x++)
            {
                byte v = 255;
                for (int k = y0; k <= y1; k++) if (tmp[k * W + x] < v) v = tmp[k * W + x];
                mn[y * W + x] = v;
            }
        }
        int w = W + 2 * padding, h = H + 2 * padding;
        var dst = new Frame(w, h);
        Array.Fill(dst.Bgra, (byte)255);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (m[i] >= minChannel && m[i] - mn[i] >= contrast)
                {
                    int o = ((y + padding) * w + x + padding) * 4;
                    dst.Bgra[o] = dst.Bgra[o + 1] = dst.Bgra[o + 2] = 0;
                }
            }
        return dst;
    }

    /// <summary>Mřížka podílu světlých pixelů (grid 32x4) + celkový podíl. Lehké a robustní vůči pohyblivému pozadí.</summary>
    public static LightSignature LightMaskSignature(Frame f, int threshold, int gx = 32, int gy = 4)
    {
        var cells = new float[gx * gy]; var counts = new int[gx * gy];
        long light = 0;
        for (int y = 0; y < f.Height; y++)
        {
            int cy = Math.Min(gy - 1, y * gy / f.Height);
            for (int x = 0; x < f.Width; x++)
            {
                int i = (y * f.Width + x) * 4;
                int m = Math.Min(f.Bgra[i], Math.Min(f.Bgra[i + 1], f.Bgra[i + 2]));
                int cx = Math.Min(gx - 1, x * gx / f.Width);
                int ci = cy * gx + cx;
                counts[ci]++;
                if (m >= threshold) { cells[ci]++; light++; }
            }
        }
        for (int i = 0; i < cells.Length; i++) cells[i] = counts[i] == 0 ? 0 : cells[i] / counts[i];
        return new LightSignature(cells, (double)light / Math.Max(1, f.Width * f.Height));
    }

    /// <summary>BMP 24 bit (zdola nahoru, řádky zarovnané na 4 bajty) – formát, který bez problémů načte Tesseract.</summary>
    public static byte[] ToBmp24(Frame f)
    {
        int stride = (f.Width * 3 + 3) & ~3, size = 54 + stride * f.Height;
        var b = new byte[size];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2); BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14); BitConverter.GetBytes(f.Width).CopyTo(b, 18); BitConverter.GetBytes(f.Height).CopyTo(b, 22);
        b[26] = 1; b[28] = 24; BitConverter.GetBytes(stride * f.Height).CopyTo(b, 34);
        for (int y = 0; y < f.Height; y++)
        {
            int dst = 54 + (f.Height - 1 - y) * stride;
            for (int x = 0; x < f.Width; x++)
            {
                int i = (y * f.Width + x) * 4;
                b[dst + x * 3] = f.Bgra[i]; b[dst + x * 3 + 1] = f.Bgra[i + 1]; b[dst + x * 3 + 2] = f.Bgra[i + 2];
            }
        }
        return b;
    }

    /// <summary>
    /// Ohraničení světlého textu ve výřezu (zlomky výřezu 0..1) – bez ojedinělých zbloudilých pixelů (percentily 2–98 %).
    /// Slouží k vystředění překladu přesně nad originální titulek. Vrací null, když světlého textu není dost.
    /// </summary>
    public static (double X0, double Y0, double X1, double Y1)? LightBounds(Frame f, int threshold)
    {
        var rows = new int[f.Height]; long total = 0;
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                int i = (y * f.Width + x) * 4;
                if (Math.Min(f.Bgra[i], Math.Min(f.Bgra[i + 1], f.Bgra[i + 2])) >= threshold) { rows[y]++; total++; }
            }
        if (total < 20) return null;
        // 1) řádky: nejhustší souvislé pásmo (text), osamocené jasné prvky scény (neony, lampy) mimo pásmo se zahodí
        int peak = rows.Max();
        var rb = BestCluster(rows, Math.Max(2, (int)(f.Height * 0.08)), Math.Max(2, (int)(peak * 0.12)));
        if (rb == null) return null;
        // 2) sloupce jen z vybraných řádků, opět nejhustší souvislý shluk (mezery mezi slovy se přemostí)
        var cols = new int[f.Width];
        for (int y = rb.Value.Lo; y <= rb.Value.Hi; y++)
            for (int x = 0; x < f.Width; x++)
            {
                int i = (y * f.Width + x) * 4;
                if (Math.Min(f.Bgra[i], Math.Min(f.Bgra[i + 1], f.Bgra[i + 2])) >= threshold) cols[x]++;
            }
        var cb = BestCluster(cols, Math.Max(4, (int)(f.Width * 0.04)), 1);
        if (cb == null) return null;
        return (cb.Value.Lo / (double)f.Width, rb.Value.Lo / (double)f.Height, (cb.Value.Hi + 1) / (double)f.Width, (rb.Value.Hi + 1) / (double)f.Height);
    }

    /// <summary>Souvislé shluky „zapnutých“ buněk (h[i] ≥ minCount), mezery do gapTol se přemostí; vrací shluk s největším součtem.</summary>
    static (int Lo, int Hi)? BestCluster(int[] h, int gapTol, int minCount)
    {
        (int Lo, int Hi)? best = null; long bestSum = 0;
        int lo = -1, last = -1; long sum = 0;
        void Close() { if (lo >= 0 && sum > bestSum) { bestSum = sum; best = (lo, last); } lo = -1; sum = 0; }
        for (int i = 0; i < h.Length; i++)
        {
            if (h[i] < minCount) continue;
            if (lo >= 0 && i - last > gapTol) Close();
            if (lo < 0) lo = i;
            last = i; sum += h[i];
        }
        Close();
        return best;
    }

    /// <summary>Hrubá signatura jasu celého snímku (mřížka 48x27) – levná detekce změny scény pro režim celé obrazovky.</summary>
    public static LightSignature LumaSignature(Frame f, int gx = 48, int gy = 27)
    {
        var sum = new double[gx * gy]; var cnt = new int[gx * gy];
        int stepY = Math.Max(1, f.Height / (gy * 4)), stepX = Math.Max(1, f.Width / (gx * 4));
        for (int y = 0; y < f.Height; y += stepY)
        {
            int cy = Math.Min(gy - 1, y * gy / f.Height);
            for (int x = 0; x < f.Width; x += stepX)
            {
                int i = (y * f.Width + x) * 4;
                double l = (f.Bgra[i] * 0.114 + f.Bgra[i + 1] * 0.587 + f.Bgra[i + 2] * 0.299) / 255.0;
                int ci = cy * gx + Math.Min(gx - 1, x * gx / f.Width);
                sum[ci] += l; cnt[ci]++;
            }
        }
        var cells = new float[gx * gy];
        for (int i = 0; i < cells.Length; i++) cells[i] = cnt[i] == 0 ? 0 : (float)(sum[i] / cnt[i]);
        return new LightSignature(cells, 0);
    }

    /// <summary>Průměrný rozdíl dvou signatur (0..1).</summary>
    public static double Distance(LightSignature a, LightSignature b)
    {
        int n = Math.Min(a.Cells.Length, b.Cells.Length);
        if (n == 0) return 1;
        double s = 0;
        for (int i = 0; i < n; i++) s += Math.Abs(a.Cells[i] - b.Cells[i]);
        return s / n;
    }
}
