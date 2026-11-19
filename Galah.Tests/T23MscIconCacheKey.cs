using System.IO;
using Galah.Services;

namespace Galah.Tests;

/// <summary>
/// T23 - the .msc icon cache-key salt. Modern .msc snap-ins (设备管理器/服务/事件查看器…) embed
/// PNG-compressed icon resources that the legacy extraction routes cannot decode; the shell icon
/// handler now extracts them first, and the cache payload carries a <c>v2</c> salt so entries
/// written under the old key — which may hold the gray generic document icon — miss naturally
/// instead of serving a wrong bitmap forever. Hermetic: pure hash logic, no .msc on disk, no COM.
/// </summary>
internal static class T23_MscIconCacheKey
{
    public static void Run()
    {
        Program.Section("msc icon cache key v2 salt");

        // The payload moved from `path|index` to `v2|path|index`; both digests are pinned so a
        // future format change is a conscious act, not an accident that keeps stale gray icons.
        const string legacyHash = "810d2bb8a6f92c36c63ae12a1d43324fea0b5c97";
        const string v2Hash = "9feee91a3d9750f6871e1f3e4ae426a4dc2c4124";

        var key = IconService.CacheKey("C:/Windows/System32/devmgmt.msc", 0);
        Program.Check("cache key is sha1 hex (40 chars)",
            key.Length == 40 && key.All(Uri.IsHexDigit), key);
        Program.Check("v2 salt retires the legacy `path|index` digest", key != legacyHash, key);
        Program.Check("v2 payload hashes to the pinned digest", key == v2Hash, key + " vs " + v2Hash);

        // Normalization must be identical to the pre-salt rules (stored slashes, lowercase).
        var lower = IconService.CacheKey("c:/windows/system32/devmgmt.msc", 0);
        Program.Check("cache key lowercases the path", lower == key, lower);
        var backslash = IconService.CacheKey(@"C:\Windows\System32\devmgmt.msc", 0);
        Program.Check("cache key normalizes backslashes", backslash == key, backslash);

        // The index is part of the payload as before.
        var other = IconService.CacheKey("C:/Windows/System32/devmgmt.msc", 1);
        Program.Check("index still discriminates entries", other != key, other);
    }
}
