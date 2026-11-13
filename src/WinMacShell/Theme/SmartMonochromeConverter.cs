using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinMacShell.Theme;

public sealed class SmartMonochromeConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<ImageSource, ImageSource> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ImageSource source)
        {
            return value;
        }

        if (Cache.TryGetValue(source, out ImageSource? cached))
        {
            return cached;
        }

        if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return value;
        }

        try
        {
            BitmapSource bgra = bitmap.Format == PixelFormats.Bgra32
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);

            double minLum = 255.0;
            double maxLum = 0.0;
            bool hasVisible = false;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a <= 20) continue;

                // Un-premultiply if needed or compute perceived luminance
                double scale = 255.0 / a;
                double b = Math.Min(255.0, pixels[i] * scale);
                double g = Math.Min(255.0, pixels[i + 1] * scale);
                double r = Math.Min(255.0, pixels[i + 2] * scale);
                double lum = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

                if (lum < minLum) minLum = lum;
                if (lum > maxLum) maxLum = lum;
                hasVisible = true;
            }

            if (!hasVisible)
            {
                return source;
            }

            double lumRange = Math.Max(1.0, maxLum - minLum);
            byte[] outPixels = new byte[pixels.Length];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a == 0) continue;

                double scale = 255.0 / a;
                double b = Math.Min(255.0, pixels[i] * scale);
                double g = Math.Min(255.0, pixels[i + 1] * scale);
                double r = Math.Min(255.0, pixels[i + 2] * scale);
                double lum = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

                double t = (lum - minLum) / lumRange;
                byte aOut = (byte)Math.Clamp(a * (0.35 + (0.65 * t)), 0.0, 255.0);

                // Premultiplied crisp monochrome (R=244, G=244, B=246)
                outPixels[i] = (byte)((246 * aOut) / 255);
                outPixels[i + 1] = (byte)((244 * aOut) / 255);
                outPixels[i + 2] = (byte)((244 * aOut) / 255);
                outPixels[i + 3] = aOut;
            }

            var mono = BitmapSource.Create(
                width,
                height,
                bgra.DpiX > 0 ? bgra.DpiX : 96.0,
                bgra.DpiY > 0 ? bgra.DpiY : 96.0,
                PixelFormats.Bgra32,
                null,
                outPixels,
                stride);
            mono.Freeze();

            Cache.AddOrUpdate(source, mono);
            return mono;
        }
        catch
        {
            return source;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
