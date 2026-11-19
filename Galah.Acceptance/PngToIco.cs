using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Galah.Acceptance;

/// <summary>
/// Build-time tool: turns the authored PNG into the multi-size app.ico that ships embedded in
/// Galah.exe (file icon, tray icon, window icon). High-quality WPF scaling per size; the ICO
/// container itself comes from the product's own builder so tooling and runtime agree on format.
/// </summary>
internal static class PngToIco
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64 };

    /// <summary>Run the conversion.</summary>
    /// <param name="pngPath">Source PNG (with alpha).</param>
    /// <param name="icoPath">Target .ico file.</param>
    /// <returns>Process exit code.</returns>
    public static int Convert(string pngPath, string icoPath)
    {
        if (!File.Exists(pngPath)) { Console.WriteLine("source png not found: " + pngPath); return 1; }

        var source = new BitmapImage();
        using (var stream = File.OpenRead(pngPath))
        {
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
        }
        source.Freeze();
        Console.WriteLine($"source: {source.PixelWidth}x{source.PixelHeight}");

        var pixelsBySize = new Dictionary<int, byte[]>();
        foreach (var size in Sizes)
        {
            pixelsBySize[size] = RenderPixels(source, size);
            var opaque = 0;
            for (var i = 3; i < pixelsBySize[size].Length; i += 4)
                if (pixelsBySize[size][i] > 0) opaque++;
            Console.WriteLine($"  {size}x{size}: opaque pixels {opaque}/{size * size}");
        }

        var ico = Galah.Services.IconArt.BuildIco(size => pixelsBySize[size], Sizes);
        File.WriteAllBytes(icoPath, ico);
        Console.WriteLine($"wrote {icoPath} ({ico.Length} bytes, {Sizes.Length} sizes)");
        return 0;
    }

    private static byte[] RenderPixels(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, size, size));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var bmp = new WriteableBitmap(rtb);
        var pixels = new byte[size * size * 4];
        bmp.CopyPixels(pixels, size * 4, 0);
        // Pbgra32 is premultiplied; icons expect straight alpha. Un-premultiply per channel.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
            pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        return pixels;
    }
}
