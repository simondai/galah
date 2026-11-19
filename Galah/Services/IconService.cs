using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Galah.Models;
using Galah.Persistence;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>
/// Icon extraction + PNG cache (design §1.1 S11, task I). Everything is derived data:
/// <c>GalahData/icons</c> may be deleted at any time and simply gets rebuilt, and no icon work ever
/// writes to the authoritative files (INV-4). Extraction goes through shell APIs only, so the app
/// ships zero image assets.
/// </summary>
public static class IconService
{
    private static AppDataPaths? _paths;
    private static readonly string[] ImageFileExtensions = [".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>Point the service at a data root (called once during boot; tests use a scratch dir).</summary>
    /// <param name="paths">Data paths of the running instance.</param>
    public static void Initialize(AppDataPaths paths) => _paths = paths;

    /// <summary>
    /// Get the icon of an item: override source first, otherwise the target's shell icon. Lazy and
    /// asynchronous — the tile renders a placeholder immediately and swaps when the bitmap arrives.
    /// </summary>
    /// <param name="item">Item whose icon is requested (read-only).</param>
    /// <param name="large">True for the medium/large step, false for the small step.</param>
    public static async Task<ImageSource> GetAsync(CatalogItem item, bool large)
    {
        var (source, index) = ResolveSource(item);
        return await GetAsync(source, index, large).ConfigureAwait(false);
    }

    /// <summary>
    /// Get an icon for an explicit source path + index, using the on-disk cache.
    /// </summary>
    /// <param name="storedSource">Stored (forward-slash) path of the icon host.</param>
    /// <param name="index">Icon index inside that file (0 for the shell default).</param>
    /// <param name="large">True for the large size.</param>
    public static async Task<ImageSource> GetAsync(string storedSource, int index, bool large)
    {
        var placeholderName = Path.GetFileName(storedSource.Replace('/', '\\'));
        if (string.IsNullOrWhiteSpace(storedSource))
            return InvokeOnUi(() => CreatePlaceholder(placeholderName));

        try
        {
            var cached = await Task.Run(() => LoadCached(storedSource, index, large)).ConfigureAwait(false);
            if (cached is not null) return cached;

            var image = await Task.Run(() => ExtractAndCache(storedSource, index, large)).ConfigureAwait(false);
            if (image is not null) return image;
        }
        catch
        {
            // Icon work is cosmetic: never surface a failure as a crash.
        }

        return InvokeOnUi(() => CreatePlaceholder(placeholderName));
    }

    /// <summary>
    /// Copy a user-chosen image into the override store and return its stored path, severing the
    /// link with the source file (spec §4.2: 源文件可删).
    /// </summary>
    /// <param name="itemId">Item owning the override.</param>
    /// <param name="sourceImagePath">Path picked by the user.</param>
    /// <returns>Stored path of the copy, or null when copying failed.</returns>
    public static string? StoreOverrideImage(string itemId, string sourceImagePath)
    {
        if (_paths is null) return null;
        var source = PathNorm.ToOs(sourceImagePath);
        if (!File.Exists(source)) return null;
        try
        {
            Directory.CreateDirectory(_paths.IconOverrideDirectory);
            var hash = ShortHash(File.ReadAllBytes(source));
            var target = Path.Combine(_paths.IconOverrideDirectory, $"{itemId}-{hash}.png");
            if (!File.Exists(target))
            {
                using var stream = File.Create(target);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(new BitmapImage(new Uri(source))));
                encoder.Save(stream);
            }
            return PathNorm.ToStored(target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Drop the cached bitmaps of an item so the next render re-extracts (design §4 Evict).</summary>
    /// <param name="item">Item whose cache entries are removed.</param>
    public static void Evict(CatalogItem item)
    {
        if (_paths is null) return;
        var (source, index) = ResolveSource(item);
        foreach (var large in new[] { true, false })
        {
            try
            {
                var file = CacheFile(source, index, large);
                if (file is not null && File.Exists(file)) File.Delete(file);
            }
            catch (IOException) { /* cache is disposable by contract */ }
        }
    }

    /// <summary>Cache key: sha1 of the normalized source path plus the icon index (task I).</summary>
    /// <param name="storedSource">Stored path.</param>
    /// <param name="index">Icon index.</param>
    public static string CacheKey(string storedSource, int index)
    {
        var payload = PathNorm.ToStored(storedSource).ToLowerInvariant() + "|" + index.ToString(CultureInfo.InvariantCulture);
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Absolute path of a cache entry, null when the service is not initialized.</summary>
    public static string? CacheFile(string storedSource, int index, bool large)
    {
        if (_paths is null) return null;
        return Path.Combine(_paths.IconsDirectory, CacheKey(storedSource, index) + (large ? "-large.png" : "-small.png"));
    }

    /// <summary>Split an icon override into (host path, index): "a/b.dll,3" → ("a/b.dll", 3).</summary>
    /// <param name="overrideSource">Raw override value.</param>
    public static (string Path, int Index) ParseIconLocation(string overrideSource)
    {
        var text = overrideSource.Trim();
        var comma = text.LastIndexOf(',');
        if (comma > 1 && int.TryParse(text[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return (text[..comma], index);
        return (text, 0);
    }

    /// <summary>Resolve which file/index an item's icon should come from.</summary>
    public static (string Source, int Index) ResolveSource(CatalogItem item)
    {
        if (item.Icon.Mode == IconModes.Override && !string.IsNullOrWhiteSpace(item.Icon.OverrideSource))
        {
            var (path, index) = ParseIconLocation(item.Icon.OverrideSource!);
            if (index == 0 && IsImageFile(path)) return (path, 0);
            return (path, index);
        }
        return (item.Target, 0);
    }

    private static bool IsImageFile(string storedPath)
    {
        var extension = Path.GetExtension(PathNorm.ToOs(storedPath)).ToLowerInvariant();
        return Array.IndexOf(ImageFileExtensions, extension) >= 0;
    }

    private static ImageSource? LoadCached(string storedSource, int index, bool large)
    {
        var file = CacheFile(storedSource, index, large);
        if (file is null || !File.Exists(file)) return null;
        try
        {
            return DecodeFile(file);
        }
        catch (Exception)
        {
            return null;   // damaged cache entry: treated as a miss
        }
    }

    private static ImageSource? ExtractAndCache(string storedSource, int index, bool large)
    {
        var osPath = PathNorm.ToOs(storedSource);
        if (string.IsNullOrEmpty(osPath)) return null;

        BitmapSource? bitmap = null;

        // An image override points straight at a picture file — no shell extraction needed.
        if (index == 0 && IsImageFile(storedSource) && File.Exists(osPath))
            bitmap = DecodeFile(osPath);

        bitmap ??= ExtractByIndex(osPath, index, large) ?? ExtractShellIcon(osPath, large);
        if (bitmap is null) return null;

        CacheToFile(bitmap, storedSource, index, large);
        return bitmap;
    }

    private static BitmapSource? ExtractByIndex(string osPath, int index, bool large)
    {
        if (index == 0) return null;
        try
        {
            var count = NativeMethods.ExtractIconExW(osPath, index, out var big, out var small, 1);
            if (count == 0) return null;
            var handle = large ? big : (small != IntPtr.Zero ? small : big);
            try
            {
                return handle == IntPtr.Zero ? null : FreezeSafely(Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
            }
            finally
            {
                if (big != IntPtr.Zero) Win32.DestroyIcon(big);
                if (small != IntPtr.Zero) Win32.DestroyIcon(small);
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Run a UI-affine rendering call on the dispatcher when one exists (headless tests fall back to
    /// running it inline).
    /// </summary>
    private static T InvokeOnUi<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return action();
        return dispatcher.Invoke(action);
    }

    private static BitmapSource? ExtractShellIcon(string osPath, bool large)
    {
        if (!File.Exists(osPath) && !Directory.Exists(osPath)) return null;
        var info = new SHFILEINFOW { szDisplayName = "", szTypeName = "" };
        var flags = Win32.SHGFI_ICON | (large ? Win32.SHGFI_LARGEICON : Win32.SHGFI_SMALLICON);
        try
        {
            var result = Win32.SHGetFileInfoW(osPath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                return FreezeSafely(Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
            }
            finally
            {
                Win32.DestroyIcon(info.hIcon);
            }
        }
        catch (Exception) { return null; }
    }

    private static BitmapSource DecodeFile(string osPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.None;
        bitmap.UriSource = new Uri(osPath, UriKind.Absolute);
        bitmap.EndInit();
        return FreezeSafely(bitmap);
    }

    private static BitmapSource FreezeSafely(BitmapSource source)
    {
        try { if (!source.IsFrozen) source.Freeze(); } catch (InvalidOperationException) { }
        return source;
    }

    private static void CacheToFile(BitmapSource bitmap, string storedSource, int index, bool large)
    {
        var file = CacheFile(storedSource, index, large);
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception)
        {
            // The cache is optional; a failed write only costs a re-extraction.
            try { if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp"); } catch (IOException) { }
        }
    }

    private static string ShortHash(byte[] payload)
    {
        var hash = SHA1.HashData(payload);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Self-drawn placeholder: a rounded colored tile with the first character of the name. Used when
    /// extraction fails or is still running (red line: no shipped image assets).
    /// </summary>
    /// <param name="seedText">Name/path whose first character and color are derived.</param>
    public static ImageSource CreatePlaceholder(string seedText)
    {
        var size = 96;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(PickColor(seedText));
            var rect = new Rect(0, 0, size, size);
            context.DrawRoundedRectangle(brush, new Pen(new SolidColorBrush(Color.FromRgb(0, 0, 0)) { Opacity = 0.08 }, 1), rect, 14, 14);

            var glyph = FirstGlyph(seedText);
            var type = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 46, Brushes.White, 1.0);
            context.DrawText(type, new Point((size - type.Width) / 2, (size - type.Height) / 2));
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static string FirstGlyph(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return "·";
        var name = Path.GetFileName(trimmed.Replace('/', '\\'));
        return name.Length == 0 ? "·" : name[..1].ToUpperInvariant();
    }

    private static Color PickColor(string seed)
    {
        var palette = new[]
        {
            Color.FromRgb(0x4C, 0xAF, 0x50), Color.FromRgb(0x21, 0x96, 0xF3), Color.FromRgb(0x9C, 0x27, 0xB0),
            Color.FromRgb(0xFF, 0x98, 0x00), Color.FromRgb(0x00, 0x96, 0x88), Color.FromRgb(0x79, 0x55, 0x48),
            Color.FromRgb(0x60, 0x7D, 0x8B), Color.FromRgb(0xE9, 0x1E, 0x63), Color.FromRgb(0x3F, 0x51, 0xB5),
        };
        var hash = 0;
        foreach (var c in seed ?? "") hash = unchecked(hash * 31 + c);
        return palette[Math.Abs(hash % palette.Length)];
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
    }
}
