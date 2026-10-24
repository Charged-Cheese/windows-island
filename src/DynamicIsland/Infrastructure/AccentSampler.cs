using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Infrastructure;

/// <summary>
/// 从专辑封面采样强调色。
/// 做法：把封面缩到 24px 解码，按"鲜艳度"加权平均，跳过近黑 / 近白 / 灰像素；
/// 再把结果按当前主题的明暗钳制到合适的饱和度与亮度——
/// 深色主题需要亮一点才够醒目，浅色主题必须压暗，否则白底上看不清。
/// </summary>
public static class AccentSampler
{
    public static Color? FromCover(byte[]? bytes, IslandTheme theme)
        => SampleAverage(bytes) is { } average ? FitToTheme(average, theme) : null;

    /// <summary>
    /// 只做采样，不按主题钳制。切换主题时需要拿原始色重新 <see cref="FitToTheme"/>，
    /// 否则深色主题下校过的亮色会被直接搬到浅色主题上，对比度不达标。
    /// </summary>
    public static Color? SampleAverage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 24;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double sumR = 0, sumG = 0, sumB = 0, sumWeight = 0;
            int accepted = 0;

            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 40)
                {
                    continue;
                }

                double rr = r / 255.0;
                double gg = g / 255.0;
                double bb = b / 255.0;
                double max = Math.Max(rr, Math.Max(gg, bb));
                double min = Math.Min(rr, Math.Min(gg, bb));
                double lightness = (max + min) / 2;

                // 太暗的阴影、太亮的反光、以及灰白黑都没有"色彩信息"
                if (lightness < 0.08 || lightness > 0.94)
                {
                    continue;
                }

                double chroma = max - min;
                if (chroma < 0.08)
                {
                    continue;
                }

                double weight = 0.35 + chroma;
                sumR += rr * weight;
                sumG += gg * weight;
                sumB += bb * weight;
                sumWeight += weight;
                accepted++;
            }

            // 有效彩色像素不到 10%：这是一张黑白 / 灰调封面，交给主题色兜底
            if (accepted < width * height / 10 || sumWeight <= 0.0001)
            {
                return null;
            }

            return Color.FromRgb(
                (byte)Math.Round(sumR / sumWeight * 255),
                (byte)Math.Round(sumG / sumWeight * 255),
                (byte)Math.Round(sumB / sumWeight * 255));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把采样色调整到当前主题可用的强调色（保证对比度与辨识度）。</summary>
    public static Color FitToTheme(Color color, IslandTheme theme)
    {
        (double h, double s, double l) = ToHsl(color);
        bool lightTheme = RelativeLuma(theme.Background) > 0.5;

        double targetS = Math.Clamp(s, 0.52, 0.95);
        double targetL = lightTheme
            ? Math.Clamp(l, 0.36, 0.50)
            : Math.Clamp(l, 0.58, 0.76);

        return FromHsl(h, targetS, targetL);
    }

    private static double RelativeLuma(Color color)
        => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double delta = max - min;

        if (delta < 0.00001)
        {
            return (0, 0, l);
        }

        double s = l <= 0.5 ? delta / (max + min) : delta / (2 - max - min);
        double h;
        if (Math.Abs(max - r) < 0.00001)
        {
            h = (g - b) / delta + (g < b ? 6 : 0);
        }
        else if (Math.Abs(max - g) < 0.00001)
        {
            h = (b - r) / delta + 2;
        }
        else
        {
            h = (r - g) / delta + 4;
        }

        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            byte gray = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueToRgb(p, q, h) * 255),
            (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }
        else if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + (q - p) * (2.0 / 3 - t) * 6;
        }

        return p;
    }
}
