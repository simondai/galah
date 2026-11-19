using System.Windows;
using Galah.Models;
using Galah.Persistence;

namespace Galah.Services;

/// <summary>A ready-to-commit item produced by an import (spec §2.1 Draft) plus optional notes.</summary>
public sealed record ImportDraft(CatalogItem Item, string? Note);

/// <summary>Batch import outcome: what became drafts, what was skipped, and why.</summary>
public sealed record ImportResult(IReadOnlyList<ImportDraft> Drafts, IReadOnlyList<string> Skipped)
{
    /// <summary>Number of accepted entries.</summary>
    public int AddedCount => Drafts.Count;

    /// <summary>Number of rejected entries (rendered as 「已跳过 n 项」).</summary>
    public int SkippedCount => Skipped.Count;

    /// <summary>Empty result.</summary>
    public static ImportResult Empty { get; } = new(Array.Empty<ImportDraft>(), Array.Empty<string>());
}

/// <summary>
/// Drop-payload → Draft mapping, closed in one layer (design §4: ImportService.FromDrop).
/// The mapping table is spec §2.1 and is implemented headlessly in <see cref="FromFilePaths"/> so the
/// semantics are unit-testable without UI.
/// </summary>
public static class ImportService
{
    /// <summary>
    /// Turn Explorer file paths into drafts: .lnk → resolved app triple (severed from the source),
    /// .url → url item, directory → folder, anything else → file. Unsupported entries are skipped with
    /// a reason (spec §2.1: 混合/不支持 → toast「已跳过 n 项」).
    /// </summary>
    /// <param name="paths">Dropped paths (OS or stored form; both are accepted).</param>
    public static ImportResult FromFilePaths(IEnumerable<string> paths)
    {
        var drafts = new List<ImportDraft>();
        var skipped = new List<string>();

        foreach (var raw in paths)
        {
            var stored = PathNorm.ToStored(raw);
            var osPath = PathNorm.ToOs(stored);
            if (string.IsNullOrEmpty(stored))
            {
                skipped.Add("(空)");
                continue;
            }

            if (Directory.Exists(osPath))
            {
                drafts.Add(new ImportDraft(NewItem(ItemKinds.Folder, Path.GetFileName(osPath.TrimEnd('\\', '/')), stored), null));
                continue;
            }

            if (!File.Exists(osPath))
            {
                skipped.Add(stored);
                continue;
            }

            var extension = Path.GetExtension(osPath).ToLowerInvariant();
            switch (extension)
            {
                case ".lnk":
                    ImportShellLink(stored, osPath, drafts, skipped);
                    break;

                case ".url":
                    ImportInternetShortcut(stored, osPath, drafts, skipped);
                    break;

                case ".exe":
                    // spec §2.1: 拖放 .exe → kind=app, args/workdir 空
                    drafts.Add(new ImportDraft(NewItem(ItemKinds.App, Path.GetFileNameWithoutExtension(osPath), stored), null));
                    break;

                default:
                    drafts.Add(new ImportDraft(NewItem(ItemKinds.File, Path.GetFileName(osPath), stored), null));
                    break;
            }
        }

        return new ImportResult(drafts, skipped);
    }

    /// <summary>
    /// Extract the file-drop list from a WPF drop payload. Non-file payloads (text, shell objects
    /// without a filesystem path) are reported as skipped so the count stays honest.
    /// </summary>
    /// <param name="data">The drag-drop data object of the panel.</param>
    public static ImportResult FromDrop(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            return FromFilePaths(files);
        }

        if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string text)
        {
            var trimmed = text.Trim();
            if (UrlSafety.IsHttpUrl(trimmed))
            {
                var item = NewItem(ItemKinds.Url, UrlSafety.HostName(trimmed) ?? trimmed, trimmed);
                return new ImportResult(new[] { new ImportDraft(item, null) }, Array.Empty<string>());
            }
        }

        return new ImportResult(Array.Empty<ImportDraft>(), new[] { "(不支持的拖放内容)" });
    }

    private static void ImportShellLink(string stored, string osPath, List<ImportDraft> drafts, List<string> skipped)
    {
        var info = LinkResolver.Resolve(osPath);
        if (!info.Ok)
        {
            skipped.Add(stored);
            return;
        }

        var item = NewItem(ItemKinds.App, Path.GetFileNameWithoutExtension(osPath), info.Target);
        item.Args = string.IsNullOrWhiteSpace(info.Arguments) ? null : info.Arguments;
        item.Workdir = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? null : info.WorkingDirectory;

        // design §3.4: IconLocation becomes the icon source; the link itself is never referenced again.
        if (!string.IsNullOrWhiteSpace(info.IconSource))
        {
            item.Icon = new IconReference
            {
                Mode = IconModes.Override,
                OverrideSource = info.IconIndex == 0 ? info.IconSource : $"{info.IconSource},{info.IconIndex}",
            };
        }

        drafts.Add(new ImportDraft(item, LauncherService.LooksValid(item.Kind, item.Target) ? null : "目标当前不可用"));
    }

    private static void ImportInternetShortcut(string stored, string osPath, List<ImportDraft> drafts, List<string> skipped)
    {
        var url = UrlFileParser.ReadUrl(osPath);
        if (url is null || !UrlSafety.IsHttpUrl(url, out var normalized))
        {
            skipped.Add(stored);
            return;
        }

        var name = UrlSafety.HostName(normalized) ?? Path.GetFileNameWithoutExtension(osPath);
        drafts.Add(new ImportDraft(NewItem(ItemKinds.Url, name, normalized), null));
    }

    private static CatalogItem NewItem(string kind, string name, string target) => new()
    {
        Id = Ids.NewItemId(),
        Kind = kind,
        Name = TrimName(name),
        Target = PathNorm.ToStored(target),
        Icon = new IconReference { Mode = IconModes.Auto },
    };

    private static string TrimName(string name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length > ItemValidator.MaxNameLength ? trimmed[..ItemValidator.MaxNameLength] : trimmed;
    }
}
