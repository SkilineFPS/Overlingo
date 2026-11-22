using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Overlingo.App;

/// <summary>Měření zalomeného textu, aby se překlad vešel do boxu velikosti originálu zmenšením písma (a ne zvětšením boxu).</summary>
internal static class TextFit
{
    public static double Height(string text, FontFamily fam, FontWeight weight, double size, double maxWidth, double pixelsPerDip)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(fam, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.White, pixelsPerDip)
        { MaxTextWidth = Math.Max(10, maxWidth) };
        return ft.Height;
    }

    /// <summary>Největší velikost písma v rozsahu [min, start], při které se zalomený text vejde do výšky maxHeight; když nevejde, vrátí min.</summary>
    public static double Fit(string text, FontFamily fam, FontWeight weight, double start, double min, double maxWidth, double maxHeight, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text)) return start;
        for (double fs = start; fs > min; fs -= 1)
            if (Height(text, fam, weight, fs, maxWidth, pixelsPerDip) <= maxHeight) return fs;
        return min;
    }
}
