using System.Text.Json;
using Galah.Models;
using Galah.Persistence;
using Galah.Services;

namespace Galah.Tests;

/// <summary>
/// T17 - maintenance detection and application (Sprint 2 / ROADMAP C1-C4). The contract:
/// detection never writes, each action writes exactly once, and "already clean" costs no write
/// at all. Icon-cache rebuild must never touch user assets (overrides, tray seed).
/// </summary>
internal static class T17_Maintenance
{
    public static void Run()
    {
        Program.Section("maintenance: detect, clean, sort, rebuild cache");

        var root = Path.Combine(Path.GetTempPath(), "GalahTests-maint-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        var store = new CatalogStore(paths);
        store.Load();

        // real targets so "valid" means something
        var realA = Path.Combine(root, "a.exe");
        var realB = Path.Combine(root, "b.exe");
        File.WriteAllText(realA, "x");
        File.WriteAllText(realB, "x");
        var storedA = PathNorm.ToStored(realA);
        var storedB = PathNorm.ToStored(realB);

        var tab = store.AddTab("维护组");
        var keepA = store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Kind = ItemKinds.App, Name = "乙程序", Target = storedA },
        }).First();
        var dupA = store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Kind = ItemKinds.App, Name = "乙程序副本", Target = storedA, Args = " " },
        }).First();
        var goodB = store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Kind = ItemKinds.App, Name = "甲程序", Target = storedB },
        }).First();
        var dead = store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Kind = ItemKinds.File, Name = "失踪文件", Target = "C:/nope/missing-9f2a.txt" },
        }).First();

        var preview = MaintenanceService.Preview(store.Current);
        Program.Check("preview sees all four items", preview.TotalItems == 4, "got " + preview.TotalItems);
        Program.Check("preview flags exactly the dead entry",
            preview.Invalid.Count == 1 && preview.Invalid[0].Id == dead,
            preview.Invalid.Count + "/" + preview.Invalid[0].Id);
        Program.Check("preview groups the duplicate (args whitespace tolerated)",
            preview.Groups.Count == 1 && preview.Groups[0].Extras.Any(e => e.Id == dupA),
            "groups=" + preview.Groups.Count);
        Program.Check("preview notices the category is unsorted",
            preview.UnsortedTabs.Contains("维护组"));

        // detection is read-only
        var beforeBytes = File.ReadAllBytes(paths.CatalogFile);
        MaintenanceService.Preview(store.Current);
        Program.Check("preview writes nothing",
            beforeBytes.SequenceEqual(File.ReadAllBytes(paths.CatalogFile)));

        // icon cache rebuild must spare user assets
        var icons = paths.IconsDirectory;
        Directory.CreateDirectory(icons);
        var overrideDir = paths.IconOverrideDirectory;
        Directory.CreateDirectory(overrideDir);
        File.WriteAllText(Path.Combine(icons, "aaa-small.png"), "cache");
        File.WriteAllText(Path.Combine(icons, "bbb-large.png"), "cache");
        var keptOverride = Path.Combine(overrideDir, "mine.png");
        File.WriteAllText(keptOverride, "user");
        var seed = Path.Combine(icons, "galah.ico");
        File.WriteAllText(seed, "ico");

        var cleared = MaintenanceService.ClearIconCache(paths);
        Program.Check("icon cache cleared both bitmaps", cleared == 2, "cleared=" + cleared);
        Program.Check("user override image survives", File.Exists(keptOverride));
        Program.Check("tray seed survives", File.Exists(seed));

        // actions
        var removedInvalid = MaintenanceService.RemoveInvalid(store, preview);
        Program.Check("invalid cleanup removed one item", removedInvalid == 1, "got " + removedInvalid);

        var afterInvalid = MaintenanceService.Preview(store.Current);
        Program.Check("invalid entry is gone after cleanup", afterInvalid.Invalid.Count == 0);

        var removedDup = MaintenanceService.RemoveDuplicates(store, afterInvalid);
        Program.Check("duplicate cleanup removed one item", removedDup == 1, "got " + removedDup);

        var reloaded = new CatalogStore(paths);
        reloaded.Load();
        var live = (reloaded.Current.TabsInOrder().FirstOrDefault(t => t.Name == "维护组") ?? reloaded.Current.TabsInOrder().First()).Items.OrderBy(i => i.Order).ToList();
        Program.Check("survivors are the two real apps",
            live.Count == 2 && live.Any(i => i.Id == keepA) && live.Any(i => i.Id == goodB),
            "count=" + live.Count + " ids=[" + string.Join(",", live.Select(i => i.Name + ":" + i.Id)) + "] keep=" + keepA + " good=" + goodB);
        Program.Check("ids are stable through maintenance", live.All(i => i.Id != dead && i.Id != dupA));

        var sorted = MaintenanceService.SortAllTabs(reloaded, MaintenanceService.Preview(reloaded.Current));
        Program.Check("sort reordered at least one item", sorted >= 1, "changed=" + sorted);
        var afterSort = new CatalogStore(paths);
        afterSort.Load();
        var sortTab = afterSort.Current.TabsInOrder().FirstOrDefault(t => t.Name == "维护组") ?? afterSort.Current.TabsInOrder().First();
        var names = sortTab.Items.OrderBy(i => i.Order).Select(i => i.Name).ToList();
        Program.Check("items now read 甲程序 before 乙程序 (zh collation)",
            names.Count == 2 && names[0] == "甲程序" && names[1] == "乙程序",
            string.Join(" > ", names));

        var second = MaintenanceService.SortAllTabs(afterSort, MaintenanceService.Preview(afterSort.Current));
        Program.Check("already-sorted costs no change", second == 0, "changed=" + second);

        var empty = new CatalogStore(new AppDataPaths(Path.Combine(Path.GetTempPath(), "GalahTests-maint-empty-" + Guid.NewGuid().ToString("N")[..8])));
        empty.Load();
        var blank = MaintenanceService.Preview(empty.Current);
        Program.Check("empty catalog previews clean",
            blank.TotalItems == 0 && blank.Invalid.Count == 0 && blank.Groups.Count == 0);
    }
}

/// <summary>
/// T18 - export/import bundle (Sprint 2 / ROADMAP D1). The bundle is the portable unit of the
/// whole library: it must round-trip, refuse foreign/future files loudly, and never overwrite an
/// existing item by id collision when merging.
/// </summary>
internal static class T18_Migration
{
    public static void Run()
    {
        Program.Section("migration: export, round-trip, reject bad bundles");

        var root = Path.Combine(Path.GetTempPath(), "GalahTests-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        var store = new CatalogStore(paths);
        store.Load();
        var settings = new SettingsStore(paths);
        settings.Load();

        var tab = store.AddTab("迁移组");
        store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Kind = ItemKinds.App, Name = "记事本", Target = "C:/Windows/System32/notepad.exe" },
            new CatalogItem { Kind = ItemKinds.Url, Name = "主页", Target = "https://example.com" },
            new CatalogItem { Kind = "hologram", Name = "未来类型", Target = "x" },   // unknown kind
        });
        settings.Update(s => { s.IconSize = IconSizes.Large; s.TabPosition = TabPositions.Left; });

        var file = MigrationService.Export(paths, store.Current, settings.Current);
        Program.Check("export wrote a bundle", File.Exists(file), file);
        Program.Check("bundle lands under GalahData/exports",
            file.Replace('\\', '/').Contains("/exports/galah-export-"));

        var bundle = MigrationService.ReadBundle(file);
        Program.Check("bundle identifies the product", bundle.Product == "Galah");
        Program.Check("settings carried through export",
            bundle.Settings.IconSize == IconSizes.Large && bundle.Settings.TabPosition == TabPositions.Left);

        // import into a fresh library: the unknown kind must be dropped, not half-applied
        var root2 = Path.Combine(Path.GetTempPath(), "GalahTests-mig2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root2);
        var paths2 = new AppDataPaths(root2);
        var store2 = new CatalogStore(paths2);
        store2.Load();
        var settings2 = new SettingsStore(paths2);
        settings2.Load();

        var outcome = MigrationService.Apply(store2, settings2, bundle, replace: true);
        Program.Check("replace-import added two known items", outcome.ItemsAdded == 2, "added=" + outcome.ItemsAdded);
        Program.Check("unknown kind was dropped", outcome.ItemsDropped == 1, "dropped=" + outcome.ItemsDropped);
        Program.Check("import reported replace mode", outcome.Replaced);

        var reloaded = new CatalogStore(paths2);
        reloaded.Load();
        var names = reloaded.Current.TabsInOrder().SelectMany(t => t.Items).Select(i => i.Name).ToList();
        Program.Check("imported names present", names.Contains("记事本") && names.Contains("主页"),
            string.Join(",", names));
        Program.Check("settings imported", settings2.Load().Data.TabPosition == TabPositions.Left);

        // merge must not duplicate what is already there, and must not reuse ids
        var before = reloaded.Current.TabsInOrder().SelectMany(t => t.Items).Select(i => i.Id).ToList();
        var merge = MigrationService.Apply(reloaded, settings2, bundle, replace: false);
        Program.Check("merge added nothing new", merge.ItemsAdded == 0, "added=" + merge.ItemsAdded);

        var after = reloaded.Current.TabsInOrder().SelectMany(t => t.Items).Select(i => i.Id).ToList();
        Program.Check("merge kept the same ids", before.SequenceEqual(after));

        // hostile inputs
        var junk = Path.Combine(root, "junk.json");
        File.WriteAllText(junk, "{\"product\":\"Other\",\"catalog\":{\"tabs\":[]}}");
        Program.Check("foreign product rejected", Throws(() => MigrationService.ReadBundle(junk)));

        var future = Path.Combine(root, "future.json");
        File.WriteAllText(future, "{\"product\":\"Galah\",\"bundleSchema\":99,\"catalog\":{\"tabs\":[]}}");
        Program.Check("future schema rejected", Throws(() => MigrationService.ReadBundle(future)));

        var broken = Path.Combine(root, "broken.json");
        File.WriteAllText(broken, "{ not json ");
        Program.Check("unreadable json rejected", Throws(() => MigrationService.ReadBundle(broken)));

        Program.Check("missing file rejected", Throws(() => MigrationService.ReadBundle(Path.Combine(root, "nope.json"))));

        try { Directory.Delete(root, true); Directory.Delete(root2, true); } catch (Exception) { }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (MigrationException) { return true; }
        catch (Exception) { return false; }
    }
}
