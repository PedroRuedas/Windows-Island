using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using global::Windows.Storage.Streams;

namespace WindowsIsland.Core;

public static class ImageLoader
{
    /// <summary>Loads a WinRT stream reference (album art, app logo) into a frozen, cross-thread WPF image.</summary>
    public static async Task<BitmapSource?> LoadAsync(IRandomAccessStreamReference? reference, int decodeWidth)
    {
        if (reference is null)
            return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            buffer.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Vivid color that represents the image (weighted towards saturated pixels), lifted so it reads
    /// on the island's black background. Used to tint the equalizer and progress bar like on iOS.
    /// </summary>
    public static Color AccentFrom(BitmapSource? image)
    {
        if (image is null || image.PixelWidth == 0 || image.PixelHeight == 0)
            return Colors.White;
        try
        {
            var small = new TransformedBitmap(image, new ScaleTransform(24.0 / image.PixelWidth, 24.0 / image.PixelHeight));
            var pixels = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth, height = pixels.PixelHeight;
            var data = new byte[width * height * 4];
            pixels.CopyPixels(data, width * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < data.Length; i += 4)
            {
                double pb = data[i], pg = data[i + 1], pr = data[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                double weight = saturation * saturation * (max / 255) + 0.002;
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }
            r /= total;
            g /= total;
            b /= total;

            // Brighten dark results so they stay visible on black, then soften slightly towards white.
            double peak = Math.Max(r, Math.Max(g, b));
            if (peak < 1)
                return Colors.White;
            double scale = Math.Max(1, 235 / peak);
            static byte Mix(double channel, double scale) => (byte)Math.Clamp(channel * scale * 0.85 + 255 * 0.15, 0, 255);
            return Color.FromRgb(Mix(r, scale), Mix(g, scale), Mix(b, scale));
        }
        catch
        {
            return Colors.White;
        }
    }
}
