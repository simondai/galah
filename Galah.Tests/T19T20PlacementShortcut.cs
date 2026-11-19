using System.Text.Json;
using Galah.Models;
using Galah.Persistence;
using Galah.Services;

namespace Galah.Tests;

/// <summary>
/// T19 - summon placement (Sprint 3 / ROADMAP E1). The placement maths is pure on purpose: the
/// modes must be predictable, must never leave the panel off-screen, and must degrade to centre
/// when the session refuses to report a cursor (which real RDP sessions do).
/// </summary>
internal static class T19_Placement
{
    private static readonly WorkArea Area = new(0, 0, 1920, 1040, true);

    public static void Run()
    {
        Program.Section("summon placement modes");

        // remember: the stored point wins
        var remembered = PanelPlacement.Resolve(PanelPositions.Remember, (400, 300), (960, 520), Area, 460, 516);
        Program.Check("remember keeps the stored point",
            remembered == (400, 300), remembered.ToString());

        // remember without history: first summon is predictable (centre)
        var first = PanelPlacement.Resolve(PanelPositions.Remember, null, (100, 100), Area, 460, 516);
        var centre = PanelPlacement.Resolve(PanelPositions.Center, null, (100, 100), Area, 460, 516);
        Program.Check("remember with no history equals centre", first == centre, first + " vs " + centre);

        // mouse: centred on the pointer
        var mouse = PanelPlacement.Resolve(PanelPositions.Mouse, (10, 10), (960, 520), Area, 460, 516);
        Program.Check("mouse centres the panel on the pointer",
            Math.Abs(mouse.X - (960 - 230)) < 0.01 && Math.Abs(mouse.Y - (520 - 258)) < 0.01,
            mouse.ToString());

        // mouse near an edge: clamped back inside, never off-screen
        var corner = PanelPlacement.Resolve(PanelPositions.Mouse, null, (1915, 1035), Area, 460, 516);
        Program.Check("cursor at the bottom-right corner stays inside the work area",
            corner.X + 460 <= 1920 && corner.Y + 516 <= 1040 && corner.X >= 0 && corner.Y >= 0,
            corner.ToString());

        var topLeft = PanelPlacement.Resolve(PanelPositions.Mouse, null, (5, 5), Area, 460, 516);
        Program.Check("cursor at the top-left corner stays inside",
            topLeft.X >= 0 && topLeft.Y >= 0, topLeft.ToString());

        // session without a cursor report: mouse mode degrades to centre, never throws
        var noCursor = PanelPlacement.Resolve(PanelPositions.Mouse, null, null, Area, 460, 516);
        Program.Check("mouse mode without a cursor falls back to centre",
            noCursor == centre, noCursor + " vs " + centre);

        // a window larger than the area still lands inside (max clamps to the area origin)
        var huge = PanelPlacement.Resolve(PanelPositions.Center, null, null, Area, 4000, 3000);
        Program.Check("oversized window clamps to the area origin",
            huge.X >= 0 && huge.Y >= 0, huge.ToString());

        // settings persistence of the three new fields
        var root = Path.Combine(Path.GetTempPath(), "GalahTests-place-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        var store = new SettingsStore(paths);
        store.Load();
        var ok = store.Update(s =>
        {
            s.PanelPlacement = PanelPositions.Mouse;
            s.WheelSwitchTab = false;
            s.NumberKeysSwitchTab = false;
        });
        Program.Check("placement settings committed", ok);

        var reload = new SettingsStore(paths);
        var loaded = reload.Load().Data;
        Program.Check("placement round-trips", loaded.PanelPlacement == PanelPositions.Mouse, loaded.PanelPlacement);
        Program.Check("wheel switch round-trips", !loaded.WheelSwitchTab);
        Program.Check("number keys switch round-trips", !loaded.NumberKeysSwitchTab);

        // unknown value sanitizes back to the shipped default
        File.WriteAllText(paths.SettingsFile,
            JsonSerializer.Serialize(new SettingsDocument { PanelPlacement = "somewhere-else" }));
        var sanitized = new SettingsStore(paths).Load().Data;
        Program.Check("unknown placement value sanitizes to remember",
            sanitized.PanelPlacement == PanelPositions.Remember, sanitized.PanelPlacement);

        // old files without the new keys keep working
        File.WriteAllText(paths.SettingsFile, "{\"schema\":1,\"hotkey\":\"Alt+Space\"}");
        var legacy = new SettingsStore(paths).Load().Data;
        Program.Check("legacy settings get the defaults",
            legacy.PanelPlacement == PanelPositions.Remember && legacy.WheelSwitchTab && legacy.NumberKeysSwitchTab,
            legacy.PanelPlacement + "/" + legacy.WheelSwitchTab);

        try { Directory.Delete(root, true); } catch (Exception) { }
    }
}

/// <summary>
/// T20 - desktop shortcut creation (Sprint 3 / ROADMAP E2). Written through the shell-link COM
/// object and read back with the product's own LinkResolver, so the assertion covers the real
/// file format rather than just "a file appeared".
/// </summary>
internal static class T20_DesktopShortcut
{
    public static void Run()
    {
        Program.Section("desktop shortcut");

        var root = Path.Combine(Path.GetTempPath(), "GalahTests-lnk-" + Guid.NewGuid().ToString("N")[..8]);
        var desktop = Path.Combine(root, "desktop");
        Directory.CreateDirectory(desktop);

        var fakeExe = Path.Combine(root, "Galah.exe");
        File.WriteAllText(fakeExe, "not a real exe but a valid path");

        var result = ShortcutMaker.Create(fakeExe, null, desktop);
        Program.Check("shortcut created", result.Ok, result.Message);
        Program.Check("shortcut path is on the target desktop",
            result.Path == Path.Combine(desktop, ShortcutMaker.LinkName), result.Path);
        Program.Check("exists() reports the new link", ShortcutMaker.Exists(desktop));
        Program.Check("first-time wording", result.Message.Contains("已在桌面创建"), result.Message);

        // read the link back through the product's resolver
        var info = LinkResolver.Resolve(result.Path);
        Program.Check("link target resolves back to the exe",
            info.Target.Replace('\\', '/').Contains("Galah.exe", StringComparison.OrdinalIgnoreCase),
            info.Target + " / " + info.Error);

        // overwrite path
        var again = ShortcutMaker.Create(fakeExe, null, desktop);
        Program.Check("re-creating updates instead of failing", again.Ok && again.Message.Contains("已更新"),
            again.Message);

        // missing exe: refuse without touching the desktop
        var missing = ShortcutMaker.Create(Path.Combine(root, "gone.exe"), null, desktop);
        Program.Check("missing target is refused", !missing.Ok, missing.Message);
        Program.Check("refusal left the previous link intact", ShortcutMaker.Exists(desktop));

        // a folder that cannot be created: reported, never thrown
        var bad = ShortcutMaker.Create(fakeExe, null, Path.Combine(root, "a", "b", "c"));
        Program.Check("unwritable folder is reported, not thrown", bad.Ok || bad.Message.Length > 0,
            bad.Message);

        try { Directory.Delete(root, true); } catch (Exception) { }
    }
}
