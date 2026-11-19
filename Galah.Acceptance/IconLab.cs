using System.IO;
using System.Runtime.InteropServices;

namespace Galah.Acceptance;

/// <summary>
/// Icon laboratory: compares three ways of turning the glyph pixels into an HICON and reports which
/// one actually reproduces the glyph. A tray icon that renders blank or foreign is invisible to the
/// user, so the conversion step is verified instead of assumed.
/// </summary>
internal static class IconLab
{
    private const uint DibRgbColors = 0;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0001;
    private const uint DiNormal = 0x0003;

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, uint flags);

    /// <summary>Run the comparison for the given glyph buffer.</summary>
    /// <param name="pixels">BGRA glyph pixels.</param>
    /// <param name="size">Edge length.</param>
    public static void Run(byte[] pixels, int size)
    {
        Console.WriteLine("raw glyph          : " + Census(pixels));

        // The production path: write the .ico file and load it back through LoadImage.
        var icoPath = Path.Combine(Path.GetTempPath(), "galah-lab.ico");
        Galah.Services.IconArt.WriteIconFile(icoPath);
        var handle = Galah.Services.IconArt.LoadIconHandle(icoPath, size);
        var rendered = handle == IntPtr.Zero ? null : Draw(handle, size);
        Console.WriteLine("production path    : hicon=" + handle.ToInt64() + " " + (rendered is null ? "<no render>" : Census(rendered)));

        Console.WriteLine("A null mask (bug)  : " + ViaCreateIcon(pixels, size, withMask: false));
        Console.WriteLine("B 1bpp mask        : " + ViaCreateIcon(pixels, size, withMask: true));
    }

    private static string ViaCreateIcon(byte[] pixels, int size, bool withMask)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32, biCompression = 0,
            },
        };
        var bits = IntPtr.Zero;
        var color = CreateDIBSection(dc, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return "no colour dib";
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        IntPtr mask = IntPtr.Zero;
        if (withMask)
        {
            var maskInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 1, biCompression = 0,
                },
            };
            var maskBits = IntPtr.Zero;
            mask = CreateDIBSection(dc, ref maskInfo, DibRgbColors, out maskBits, IntPtr.Zero, 0);
            if (mask != IntPtr.Zero && maskBits != IntPtr.Zero)
            {
                var stride = ((size + 31) / 32) * 4;
                var maskBytes = new byte[stride * size];
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                        if (pixels[(y * size + x) * 4 + 3] == 0)
                            maskBytes[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                Marshal.Copy(maskBytes, 0, maskBits, maskBytes.Length);
            }
        }

        var iconInfo = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref iconInfo);
        var rendered = icon == IntPtr.Zero ? null : Draw(icon, size);
        if (mask != IntPtr.Zero) DeleteObject(mask);
        DeleteObject(color);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        return "hicon=" + icon.ToInt64() + " " + (rendered is null ? "<no render>" : Census(rendered));
    }

    private static string ViaIcoFile(byte[] pixels, int size)
    {
        var path = Path.Combine(Path.GetTempPath(), "galah-icon-" + size + ".ico");
        var stride = ((size + 31) / 32) * 4;
        var andMask = new byte[stride * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (pixels[(y * size + x) * 4 + 3] == 0)
                    andMask[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));

        var dib = new byte[40 + pixels.Length + andMask.Length];
        Write32(dib, 0, 40);
        Write32(dib, 4, size);
        Write32(dib, 8, size * 2);
        Write16(dib, 12, 1);
        Write16(dib, 14, 32);
        Write32(dib, 20, pixels.Length + andMask.Length);
        for (var y = 0; y < size; y++)
            Array.Copy(pixels, (size - 1 - y) * size * 4, dib, 40 + y * size * 4, size * 4);
        Array.Copy(andMask, 0, dib, 40 + pixels.Length, andMask.Length);

        var file = new byte[6 + 16 + dib.Length];
        file[2] = 1; file[3] = 0;
        file[4] = 1; file[5] = 0;
        file[6] = (byte)size; file[7] = (byte)size;
        Write16(file, 10, 1); Write16(file, 12, 32);
        Write32(file, 14, dib.Length);
        Write32(file, 18, 22);
        Array.Copy(dib, 0, file, 22, dib.Length);
        File.WriteAllBytes(path, file);

        var icon = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        var rendered = icon == IntPtr.Zero ? null : Draw(icon, size);
        return "hicon=" + icon.ToInt64() + " " + (rendered is null ? "<no render>" : Census(rendered));
    }

    private static byte[]? Draw(IntPtr icon, int size)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32, biCompression = 0,
            },
        };
        var bits = IntPtr.Zero;
        var bmp = CreateDIBSection(dc, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return null;
        var old = SelectObject(dc, bmp);
        var ok = DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, DiNormal);
        SelectObject(dc, old);
        var pixels = new byte[size * size * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        return ok ? pixels : null;
    }
    /// <summary>Render an icon into a pixel buffer (exposed for the tooling).</summary>
    public static byte[]? DrawForTest(IntPtr icon, int size) => Draw(icon, size);

    /// <summary>Write a 32-bit BMP (exposed for the tooling).</summary>
    public static void WriteBmpForTest(string path, byte[] pixels, int size)
    {
        const int headerSize = 14 + 40;
        var file = new byte[headerSize + pixels.Length];
        file[0] = 66; file[1] = 77;
        Write32(file, 2, file.Length);
        Write32(file, 10, headerSize);
        Write32(file, 14, 40);
        Write32(file, 18, size);
        Write32(file, 22, size);
        Write16(file, 26, 1);
        Write16(file, 28, 32);
        Write32(file, 34, pixels.Length);
        for (var y = 0; y < size; y++)
            Array.Copy(pixels, (size - 1 - y) * size * 4, file, headerSize + y * size * 4, size * 4);
        File.WriteAllBytes(path, file);
    }

    /// <summary>Count transparent / white / blue pixels of a BGRA buffer.</summary>
    private static string Census(byte[] pixels)
    {
        var opaque = 0; var white = 0; var blue = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            opaque++;
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            if (r > 200 && g > 200 && b > 200) white++;
            else if (b > 150 && r < 180) blue++;
        }
        return $"opaque={opaque} white={white} blue={blue}";
    }

    private static void Write32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void Write16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
