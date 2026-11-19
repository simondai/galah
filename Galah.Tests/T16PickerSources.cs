using Galah.Models;
using Galah.Persistence;
using Galah.Services;
using Galah.Services.CatalogSources;

namespace Galah.Tests;

/// <summary>
/// T16 - the Sprint 1 picker sources (ROADMAP B1/B2/B4/B5). Each source must produce a usable,
/// honest candidate list on THIS machine: real paths, allowed protocols, no duplicates, and every
/// seed must pass the same validator the edit dialog uses (a seed that cannot be committed is a
/// broken tile waiting to happen).
/// </summary>
internal static class T16_PickerSources
{
    public static void Run()
    {
        Program.Section("picker catalog sources (Sprint 1)");
        var service = new CatalogSourceService();

        Program.Check("four categories declared", service.Categories.Count == 4,
            "count=" + service.Categories.Count);

        var tools = service.Enumerate(SeedCategory.SystemTools);
        Program.Check("system tools: at least 10 entries", tools.Count >= 10, "got " + tools.Count);
        Program.Check("system tools: all kinds are app/file",
            tools.All(s => s.Kind is ItemKinds.App or ItemKinds.File));
        Program.Check("system tools: all targets exist on disk",
            tools.All(s => File.Exists(PathNorm.ToOs(s.Target))));
        // Uniqueness is per launchable (kind+target+args): control.exe with different applet
        // arguments is legitimately several tiles over one binary.
        var toolKeys = tools.Select(s => s.Kind + "|" + s.Target + "|" + (s.Args ?? ""));
        Program.Check("system tools: no duplicate launchables",
            toolKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tools.Count);
        var dupTargets = tools.GroupBy(s => s.Target, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " x" + g.Count() + " [" + string.Join(";", g.Select(x => x.Args ?? "-")) + "]");
        Program.Check("system tools: shared targets differ by args",
            dupTargets.All(t => !t.Contains(";-;-]")), string.Join(" | ", dupTargets));

        var settings = service.Enumerate(SeedCategory.SettingsPages);
        Program.Check("settings pages: at least 40 entries", settings.Count >= 40, "got " + settings.Count);
        Program.Check("settings pages: all kind=protocol", settings.All(s => s.Kind == ItemKinds.Protocol));
        Program.Check("settings pages: every URI passes the protocol allowlist",
            settings.All(s => ProtocolSafety.IsAllowed(s.Target, out _)));

        var folders = service.Enumerate(SeedCategory.ShellFolders);
        Program.Check("shell folders: at least 8 entries", folders.Count >= 8, "got " + folders.Count);
        Program.Check("shell folders: all kind=folder", folders.All(s => s.Kind == ItemKinds.Folder));
        Program.Check("shell folders: all directories exist",
            folders.All(s => Directory.Exists(PathNorm.ToOs(s.Target))));

        var apps = service.Enumerate(SeedCategory.InstalledApps);
        Program.Check("installed apps: at least one entry", apps.Count >= 1, "got " + apps.Count);
        Program.Check("installed apps: all targets are existing exes",
            apps.All(s => s.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                       && File.Exists(PathNorm.ToOs(s.Target))));
        Program.Check("installed apps: capped and deduped",
            apps.Count <= InstalledAppSource.MaxEntries
            && apps.Select(s => s.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() == apps.Count);
        Program.Check("installed apps: cache is stable across calls",
            service.Enumerate(SeedCategory.InstalledApps).Count == apps.Count);

        // The picker must never offer something the commit path would reject.
        var rejected = new List<string>();
        foreach (var seed in tools.Concat(settings).Concat(folders).Concat(apps))
        {
            var item = new CatalogItem
            {
                Id = "t16",
                Kind = seed.Kind,
                Name = seed.Name,
                Target = seed.Target,
                Args = seed.Args,
            };
            var errors = ItemValidator.Validate(item, isNew: true);
            if (errors.Count > 0)
                rejected.Add(seed.Name + ": " + string.Join(",", errors.Select(e => e.Message)));
        }
        Program.Check("every seed passes ItemValidator", rejected.Count == 0,
            string.Join(" | ", rejected.Take(3)));

        Program.Check("seed names are non-empty and short",
            tools.Concat(settings).Concat(folders).Concat(apps).All(s => s.Name.Length is > 0 and <= 60));
    }
}
