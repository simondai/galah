using System.Runtime.InteropServices;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>
/// The launcher's own artwork. Everything is generated: no image library is available to the WPF
/// markup compile, and a tray icon that renders blank is worse than no icon at all.
///
/// Two things are easy to get wrong and are therefore verified rather than assumed:
/// 1. <c>CreateIconIndirect</c> refuses a NULL AND mask (it returns zero), so the icon would fall
///    back to the generic Windows application icon. The mask is always supplied.
/// 2. A single 32 px bitmap gets resampled by the shell down to 16/20/24 px, so the glyph is drawn
///    4x oversized and box-filtered: the small sizes stay clean instead of stair-stepping.
/// </summary>
public static class IconArt
{
    /// <summary>Sizes stored in the generated .ico file.</summary>
    public static readonly int[] IconSizes = { 16, 24, 32 };

    /// <summary>Pack URI of the embedded application icon.</summary>
    public const string EmbeddedIconUri = "pack://application:,,,/app.ico";

    /// <summary>
    /// The canonical icon bytes: the embedded app.ico when present, otherwise the generated glyph.
    /// This is what GalahData/icons/galah.ico is seeded with and compared against, so a user
    /// replacement is detected by byte difference rather than by mtime.
    /// </summary>
    public static byte[] CanonicalBytes()
    {
        // Two contexts, one contract:
        //  - the running app has an Application instance, so the pack URI resolves the icon
        //    embedded in the assembly;
        //  - tooling and tests run without an Application (pack URIs are unavailable there),
        //    so they read the app.ico copied next to the entry assembly.
        // Either way the bytes are the same file the build produced.
        try
        {
            if (System.Windows.Application.Current is not null)
            {
                var part = System.Windows.Application.GetResourceStream(new Uri(EmbeddedIconUri, UriKind.Absolute));
                if (part is not null)
                {
                    using var stream = part.Stream;
                    using var mem = new MemoryStream();
                    stream.CopyTo(mem);
                    if (mem.Length > 22) return mem.ToArray();
                }
            }

            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > 22) return bytes;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Line("icon", "嵌入图标读取失败,回退生成图标:" + ex.Message);
        }
        return BuildIcoBytes();
    }

    /// <summary>Build the multi-size .ico payload from the generated glyph (the fallback artwork).</summary>
    public static byte[] BuildIcoBytes() => BuildIco(RenderGlyphPixels, IconSizes);

    /// <summary>
    /// Write the generated-glyph icon file (kept for tooling and tests; the product seeds with
    /// CanonicalBytes instead).
    /// </summary>
    /// <param name="path">Target file path.</param>
    /// <returns>True when the file was written.</returns>
    public static bool WriteIconFile(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, BuildIcoBytes());
        return true;
    }

    /// <summary>
    /// Load the tray icon handle. The data-folder file is the user's slot for their own artwork:
    /// missing files are seeded with the canonical bytes, our own legacy seeds are upgraded,
    /// and anything else is the user's and wins. The in-memory glyph is only the last resort.
    /// </summary>
    /// <param name="iconPath">Path of the .ico file in the data folder.</param>
    /// <param name="size">Preferred edge length.</param>
    /// <returns>An HICON, or IntPtr.Zero when nothing could be loaded.</returns>
    public static IntPtr LoadIconHandle(string iconPath, int size = 32)
    {
        var canonical = CanonicalBytes();
        var legacyGlyph = BuildIcoBytes();
        try
        {
            if (File.Exists(iconPath))
            {
                var existing = File.ReadAllBytes(iconPath);
                if (!SameBytes(existing, canonical) && SameBytes(existing, legacyGlyph))
                {
                    File.WriteAllBytes(iconPath, canonical);   // roll out icon upgrades
                    DiagnosticsLog.Line("icon", "种子图标已升级到当前版本");
                }
                else if (!SameBytes(existing, canonical))
                {
                    DiagnosticsLog.Line("icon", "使用自定义图标文件 " + iconPath);
                }
            }
            else
            {
                var folder = Path.GetDirectoryName(iconPath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllBytes(iconPath, canonical);
            }

            var fromFile = Win32.LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, size, size,
                0x0010 /*LR_LOADFROMFILE*/);
            if (fromFile != IntPtr.Zero) return fromFile;
            DiagnosticsLog.Line("tray", "图标文件无法加载,改用生成图标");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Line("tray", "图标处理失败:" + ex.Message);
        }
        return CreateIconWithMask(RenderGlyphPixels(size), size);
    }

    /// <summary>
    /// Build a valid .ico payload from a pixel provider. Each entry is a 32bpp BGRA bitmap with an
    /// explicit AND mask (bit set where the pixel is transparent), which is what keeps
    /// CreateIconIndirect/LoadImage from ever producing a blank icon (learned the hard way).
    /// </summary>
    /// <param name="pixelsFor">Returns row-major BGRA pixels for the requested edge length.</param>
    /// <param name="sizes">Icon sizes to embed, ascending.</param>
    /// <returns>The .ico file bytes.</returns>
    public static byte[] BuildIco(Func<int, byte[]> pixelsFor, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            var pixels = pixelsFor(size);
            var stride = ((size + 31) / 32) * 4;
            var andMask = new byte[stride * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (pixels[(y * size + x) * 4 + 3] < 128)
                        andMask[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }

            var image = new byte[40 + pixels.Length + andMask.Length];
            WriteInt32(image, 0, 40);
            WriteInt32(image, 4, size);
            WriteInt32(image, 8, size * 2);
            WriteInt16(image, 12, 1);
            WriteInt16(image, 14, 32);
            WriteInt32(image, 20, pixels.Length + andMask.Length);
            for (var y = 0; y < size; y++)
                Array.Copy(pixels, (size - 1 - y) * size * 4, image, 40 + y * size * 4, size * 4);
            Array.Copy(andMask, 0, image, 40 + pixels.Length, andMask.Length);
            images.Add(image);
        }

        var header = new byte[6 + 16 * images.Count];
        WriteInt16(header, 2, 1);
        WriteInt16(header, 4, images.Count);
        var offset = header.Length;
        for (var i = 0; i < images.Count; i++)
        {
            var entry = 6 + i * 16;
            header[entry] = (byte)sizes[i];
            header[entry + 1] = (byte)sizes[i];
            WriteInt16(header, entry + 4, 1);
            WriteInt16(header, entry + 6, 32);
            WriteInt32(header, entry + 8, images[i].Length);
            WriteInt32(header, entry + 12, offset);
            offset += images[i].Length;
        }

        var file = new byte[header.Length + images.Sum(i => i.Length)];
        Array.Copy(header, 0, file, 0, header.Length);
        var cursor = header.Length;
        foreach (var image in images)
        {
            Array.Copy(image, 0, file, cursor, image.Length);
            cursor += image.Length;
        }
        return file;
    }

    /// <summary>Byte-wise comparison of two buffers.</summary>
    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    /// <summary>
    /// Create an HICON from BGRA pixels plus an explicit monochrome AND mask. The mask is not
    /// optional: without it CreateIconIndirect fails and the shell shows the generic Windows
    /// application icon instead of ours.
    /// </summary>
    public static IntPtr CreateIconWithMask(byte[] pixels, int size)
    {
        var screen = Win32.GetDC(IntPtr.Zero);
        var dc = Win32.CreateCompatibleDC(screen);
        try
        {
            var info = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BiRgb,
                },
            };
            var color = Win32.CreateDIBSection(dc, ref info, Win32.DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            var maskInfo = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = size,
                    biHeight = -size,
                    biPlanes = 1,
                    biBitCount = 1,
                    biCompression = Win32.BiRgb,
                },
            };
            var mask = Win32.CreateDIBSection(dc, ref maskInfo, Win32.DibRgbColors, out var maskBits, IntPtr.Zero, 0);
            if (mask != IntPtr.Zero && maskBits != IntPtr.Zero)
            {
                var stride = ((size + 31) / 32) * 4;
                var maskBytes = new byte[stride * size];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        if (pixels[(y * size + x) * 4 + 3] < 128)
                            maskBytes[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                    }
                }
                Marshal.Copy(maskBytes, 0, maskBits, maskBytes.Length);
            }

            var iconInfo = new Win32.ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = mask,
                hbmColor = color,
            };
            var icon = Win32.CreateIconIndirect(ref iconInfo);
            if (mask != IntPtr.Zero) Win32.DeleteObject(mask);
            Win32.DeleteObject(color);
            return icon;
        }
        finally
        {
            Win32.DeleteDC(dc);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Draw the fallback launcher mark as BGRA pixels: a rounded accent badge with a light
    /// launch chevron, supersampled 4x and box-filtered for clean edges at tray sizes.
    /// Only used when no artwork file exists or the icon file cannot be loaded.
    /// </summary>
    /// <param name="size">Edge length in pixels.</param>
    /// <returns>Row-major BGRA bytes, size*size*4 long.</returns>
    public static byte[] RenderGlyphPixels(int size)
    {
        const int samples = 4;
        var big = RenderGlyphCore(size * samples);
        return Downsample(big, size * samples, size);
    }

    private static byte[] RenderGlyphCore(int size)
    {
        var pixels = new byte[size * size * 4];
        var accent = new byte[] { 237, 111, 74 };   // B, G, R
        var white = new byte[] { 255, 255, 255 };

        var inset = size / 8;
        var side = size - inset * 2;
        var radius = Math.Max(2, side / 5);
        var stroke = Math.Max(2f, size / 8f);
        var arm = side / 4.2f;
        var cx = size / 2f;
        var cy = size / 2f;

        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var x = px + 0.5f;
                var y = py + 0.5f;
                if (!InsideRoundedSquare(x - inset, y - inset, side, radius)) continue;
                var onChevron =
                    DistanceToSegment(x, y, cx - arm, cy - arm, cx + arm * 0.4f, cy) <= stroke / 2f ||
                    DistanceToSegment(x, y, cx + arm * 0.4f, cy, cx - arm, cy + arm) <= stroke / 2f;
                var color = onChevron ? white : accent;
                var offset = (py * size + px) * 4;
                pixels[offset + 0] = color[0];
                pixels[offset + 1] = color[1];
                pixels[offset + 2] = color[2];
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>Box-filter a supersampled buffer down to the target size.</summary>
    private static byte[] Downsample(byte[] source, int sourceSize, int targetSize)
    {
        var ratio = sourceSize / targetSize;
        var result = new byte[targetSize * targetSize * 4];
        for (var y = 0; y < targetSize; y++)
        {
            for (var x = 0; x < targetSize; x++)
            {
                long aSum = 0, rSum = 0, gSum = 0, bSum = 0;
                for (var sy = 0; sy < ratio; sy++)
                {
                    for (var sx = 0; sx < ratio; sx++)
                    {
                        var o = ((y * ratio + sy) * sourceSize + (x * ratio + sx)) * 4;
                        var alpha = source[o + 3];
                        aSum += alpha;
                        rSum += (long)source[o + 2] * alpha;
                        gSum += (long)source[o + 1] * alpha;
                        bSum += (long)source[o + 0] * alpha;
                    }
                }
                var total = (long)ratio * ratio;
                var d = (y * targetSize + x) * 4;
                var a = (int)(aSum / total);
                result[d + 3] = (byte)a;
                if (a == 0) continue;
                result[d + 2] = (byte)(rSum / aSum);
                result[d + 1] = (byte)(gSum / aSum);
                result[d + 0] = (byte)(bSum / aSum);
            }
        }
        return result;
    }

    private static bool InsideRoundedSquare(float x, float y, float side, float radius)
    {
        if (x < 0 || y < 0 || x > side || y > side) return false;
        var cx = Math.Min(Math.Max(x, radius), side - radius);
        var cy = Math.Min(Math.Max(y, radius), side - radius);
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        var vx = bx - ax;
        var vy = by - ay;
        var wx = px - ax;
        var wy = py - ay;
        var lengthSquared = vx * vx + vy * vy;
        var t = lengthSquared <= 0f ? 0f : Math.Min(1f, Math.Max(0f, (wx * vx + wy * vy) / lengthSquared));
        var dx = wx - t * vx;
        var dy = wy - t * vy;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }
}
