using System.IO;
using System.Linq;
using System.Windows;
using Galah.Models;
using Galah.Persistence;
using Galah.Services;

namespace Galah.Tests;

/// <summary>
/// T22 - the URL icon fetch (Sprint 4 / ROADMAP F2) against a local server. The behaviour that
/// matters is not "can we download a file" but the guard rails: only http/https, only the site
/// itself (no third-party proxy), a size cap that rejects instead of truncating, a magic-number
/// gate so a friendly 404 page never becomes an icon, and a hard timeout so a dead host cannot
/// hang the dialog. A local listener exercises all of that without touching the internet.
/// </summary>
internal static class T22_Favicon
{
    public static void Run()
    {
        Program.Section("favicon fetch (manual, site-only, bounded)");

        var root = Path.Combine(Path.GetTempPath(), "GalahTests-icon-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        var itemId = "i-test-favicon";

        // a minimal but real PNG (1x1, the shortest valid one)
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        var html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><body>not an icon</body></html>");

        var listener = new System.Net.HttpListener();
        var port = FreePort();
        var prefix = "http://127.0.0.1:" + port + "/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serve = new Task(() => ServeLoop(listener, png, html));
        serve.Start();

        // 1. happy path: /favicon.ico exists
        var ok = FaviconService.FetchAsync(prefix.TrimEnd('/'), itemId, paths).GetAwaiter().GetResult();
        Program.Check("fetches /favicon.ico", ok.Ok, ok.Message);
        Program.Check("stored path exists on disk",
            ok.StoredPath is not null && File.Exists(PathNorm.ToOs(ok.StoredPath)), ok.StoredPath ?? "null");
        Program.Check("stored file is the bytes we served",
            ok.StoredPath is not null && File.ReadAllBytes(PathNorm.ToOs(ok.StoredPath)).SequenceEqual(png));
        Program.Check("stored under the override folder (attributable name)",
            ok.StoredPath is not null && Path.GetFileName(PathNorm.ToOs(ok.StoredPath)!).StartsWith("web-i-test-favicon-"),
            ok.StoredPath);

        // 2. idempotent: a second fetch of the same icon reuses the file
        var countBefore = Directory.GetFiles(paths.IconOverrideDirectory).Length;
        var again = FaviconService.FetchAsync(prefix.TrimEnd('/'), itemId, paths).GetAwaiter().GetResult();
        Program.Check("repeat fetch does not duplicate the file",
            again.Ok && Directory.GetFiles(paths.IconOverrideDirectory).Length == countBefore,
            Directory.GetFiles(paths.IconOverrideDirectory).Length + " vs " + countBefore);

        // 3. non-http targets are refused before any socket is opened
        var fileTarget = FaviconService.FetchAsync("file:///C:/windows", itemId, paths).GetAwaiter().GetResult();
        Program.Check("file:// is refused", !fileTarget.Ok && fileTarget.Message.Contains("http/https"),
            fileTarget.Message);
        var junkTarget = FaviconService.FetchAsync("notaurl", itemId, paths).GetAwaiter().GetResult();
        Program.Check("garbage target is refused", !junkTarget.Ok, junkTarget.Message);

        // 4. html served with 200 is rejected by the magic-number gate
        listener.Stop();
        var htmlListener = new System.Net.HttpListener();
        var port2 = FreePort();
        var htmlPrefix = "http://127.0.0.1:" + port2 + "/";
        htmlListener.Prefixes.Add(htmlPrefix);
        htmlListener.Start();
        var htmlServe = Task.Run(() =>
        {
            while (htmlListener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = htmlListener.GetContext(); } catch { return; }
                var payload = ctx.Request.Url!.AbsolutePath.Contains("apple-touch-icon") ? Array.Empty<byte>() : html;
                ctx.Response.ContentType = "text/html";
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(payload);
                ctx.Response.OutputStream.Close();
            }
        });
        var rejected = FaviconService.FetchAsync(htmlPrefix.TrimEnd('/'), "i-html", paths).GetAwaiter().GetResult();
        Program.Check("html with status 200 is rejected as a non-image",
            !rejected.Ok && rejected.Message.Contains("不是图片"), rejected.Message);
        htmlListener.Stop();

        // 5. dead host: bounded, never hangs
        var deadPort = FreePort();
        var dead = FaviconService.FetchAsync("http://127.0.0.1:" + deadPort, "i-dead", paths).GetAwaiter().GetResult();
        Program.Check("unreachable host returns a message, not an exception",
            !dead.Ok && dead.Message.Length > 0, dead.Message);

        // 6. 404 everywhere: reported as "no icon", nothing stored
        var notFoundPort = FreePort();
        var nfListener = new System.Net.HttpListener();
        nfListener.Prefixes.Add("http://127.0.0.1:" + notFoundPort + "/");
        nfListener.Prefixes.Clear();
        nfListener.Prefixes.Add("http://127.0.0.1:" + notFoundPort + "/");
        nfListener.Start();
        var nfServe = Task.Run(() =>
        {
            while (nfListener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = nfListener.GetContext(); } catch { return; }
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
            }
        });
        var none = FaviconService.FetchAsync("http://127.0.0.1:" + notFoundPort, "i-none", paths).GetAwaiter().GetResult();
        Program.Check("404 everywhere reports no icon", !none.Ok && none.Message.Contains("没有提供图标"), none.Message);
        Program.Check("nothing stored for a 404 site", none.StoredPath is null);
        nfListener.Stop();

        // 7. the size cap rejects instead of truncating
        var bigPort = FreePort();
        var bigListener = new System.Net.HttpListener();
        bigListener.Prefixes.Add("http://127.0.0.1:" + bigPort + "/");
        bigListener.Start();
        var bigServe = Task.Run(() =>
        {
            while (bigListener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = bigListener.GetContext(); } catch { return; }
                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = false;
                var junk = new byte[FaviconService.MaxBytes + 4096];
                // keep the PNG header so only the size rule can stop it
                junk[0] = 0x89; junk[1] = 0x50; junk[2] = 0x4E; junk[3] = 0x47;
                ctx.Response.ContentLength64 = junk.Length;
                ctx.Response.OutputStream.Write(junk);
                ctx.Response.OutputStream.Close();
            }
        });
        var big = FaviconService.FetchAsync("http://127.0.0.1:" + bigPort, "i-big", paths).GetAwaiter().GetResult();
        Program.Check("oversized body is rejected", !big.Ok, big.Message);
        Program.Check("oversized body is not stored", big.StoredPath is null);
        bigListener.Stop();

        try { Directory.Delete(root, true); } catch (Exception) { }
    }

    private static void ServeLoop(System.Net.HttpListener listener, byte[] png, byte[] html)
    {
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            if (path.Contains("favicon"))
            {
                ctx.Response.ContentType = "image/x-icon";
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(png);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.OutputStream.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
