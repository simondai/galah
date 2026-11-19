using Galah.Persistence;
using Galah.Services;

namespace Galah.ViewModels;

/// <summary>
/// ViewModel of the maintenance window (Sprint 2). Every action is preview-then-confirm: the
/// numbers shown are computed from the live catalog, and the destructive ones ask before writing
/// (product rule inherited from spec INV-3: nothing is written without an explicit yes).
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly IDialogHost _dialogs;
    private readonly IFilePickerService _picker;
    private MaintenancePreview _preview = new(Array.Empty<MaintenanceFinding>(), Array.Empty<DuplicateGroup>(), 0, Array.Empty<string>());
    private string _lastResult = "";

    /// <summary>Create the model.</summary>
    /// <param name="environment">Wired environment.</param>
    /// <param name="dialogs">Confirm host (preview-then-write).</param>
    /// <param name="picker">File chooser used by import.</param>
    public MaintenanceViewModel(AppEnvironment environment, IDialogHost dialogs, IFilePickerService picker)
    {
        _env = environment;
        _dialogs = dialogs;
        _picker = picker;
        Refresh();
    }

    /// <summary>Total items in the library.</summary>
    public int TotalItems => _preview.TotalItems;

    /// <summary>Entries whose target no longer resolves.</summary>
    public int InvalidCount => _preview.Invalid.Count;

    /// <summary>Removable copies of duplicated launchables.</summary>
    public int DuplicateCount => _preview.Groups.Sum(g => g.Extras.Count);

    /// <summary>Categories not currently sorted by name.</summary>
    public int UnsortedTabCount => _preview.UnsortedTabs.Count;

    /// <summary>True when the invalid cleanup has something to do.</summary>
    public bool CanCleanInvalid => InvalidCount > 0;

    /// <summary>True when the duplicate cleanup has something to do.</summary>
    public bool CanCleanDuplicates => DuplicateCount > 0;

    /// <summary>True when at least one category needs sorting.</summary>
    public bool CanSort => UnsortedTabCount > 0;

    /// <summary>Human line describing the last executed action (shown under the cards).</summary>
    public string LastResult
    {
        get => _lastResult;
        private set => Set(ref _lastResult, value);
    }

    /// <summary>Recompute the preview from the live catalog (also called after each action).</summary>
    public void Refresh()
    {
        _preview = MaintenanceService.Preview(_env.Catalog.Current);
        foreach (var name in new[]
        {
            nameof(TotalItems), nameof(InvalidCount), nameof(DuplicateCount), nameof(UnsortedTabCount),
            nameof(CanCleanInvalid), nameof(CanCleanDuplicates), nameof(CanSort),
        })
        {
            Raise(name);
        }
    }

    /// <summary>Ask, then delete the flagged invalid entries.</summary>
    /// <returns>Count removed (0 when canceled).</returns>
    public int CleanInvalid()
    {
        if (!CanCleanInvalid) return 0;
        var list = string.Join("、", _preview.Invalid.Take(6).Select(i => i.Name));
        var more = InvalidCount > 6 ? $" 等 {InvalidCount} 项" : "";
        if (!Confirm("清理失效项",
            $"将删除 {InvalidCount} 个目标已不存在的条目（{list}{more}）。\n备份文件仍保留，可回滚。"))
            return 0;
        return Apply(store => MaintenanceService.RemoveInvalid(store, _preview), "已清理失效项");
    }

    /// <summary>Ask, then delete duplicate copies (the first of each group stays).</summary>
    public int CleanDuplicates()
    {
        if (!CanCleanDuplicates) return 0;
        if (!Confirm("清理重复项",
            $"将删除 {DuplicateCount} 个重复条目（同一目标只保留第一个）。\n备份文件仍保留，可回滚。"))
            return 0;
        return Apply(store => MaintenanceService.RemoveDuplicates(store, _preview), "已清理重复项");
    }

    /// <summary>Sort every category by name (no confirm: purely a reorder, ids untouched).</summary>
    public int SortAll()
    {
        if (!CanSort) return 0;
        return Apply(store => MaintenanceService.SortAllTabs(store, _preview), "已按名称排序");
    }

    /// <summary>Drop derived icon bitmaps so they are re-extracted on next render.</summary>
    public int RebuildIcons()
    {
        var cleared = MaintenanceService.ClearIconCache(_env.Paths);
        LastResult = cleared == 0 ? "图标缓存本来就是空的" : $"已清除 {cleared} 个图标缓存，重新生成中";
        _env.Toast.Show(LastResult);
        return cleared;
    }

    /// <summary>Write the library to GalahData/exports and report the path.</summary>
    public string Export()
    {
        var file = MigrationService.Export(_env.Paths, _env.Catalog.Current, _env.Settings.Current);
        LastResult = "已导出：" + file.Replace('\\', '/');
        _env.Toast.Show("已导出库备份");
        return file;
    }

    /// <summary>
    /// Import a bundle chosen by the caller. Merge/replace is asked; a rejected bundle surfaces
    /// its reason instead of half-applying.
    /// </summary>
    /// <param name="file">Path of the bundle.</param>
    /// <param name="ownerHint">Unused; kept for call-site symmetry with other dialogs.</param>
    /// <summary>Ask for a bundle file and import it. Returns null when canceled.</summary>
    public ImportOutcome? ImportInteractively()
    {
        var file = _picker.PickFile("导入 Galah 备份", "Galah 备份 (*.json)|*.json|所有文件 (*.*)|*.*", null);
        return file is null ? null : Import(file);
    }

    public ImportOutcome? Import(string file)
    {
        try
        {
            var bundle = MigrationService.ReadBundle(file);
            var replace = Confirm("导入方式",
                "「替换」= 用备份覆盖当前库\n「合并」= 保留现有条目，只补入缺失的\n\n选择替换？");
            var outcome = MigrationService.Apply(_env.Catalog, _env.Settings, bundle, replace);
            _env.Catalog.Load();
            _env.Settings.Load();
            Refresh();
            LastResult = (replace ? "已替换：" : "已合并：") +
                $"{outcome.ItemsAdded} 项入库，跳过 {outcome.ItemsDropped} 项";
            _env.Toast.Show(LastResult);
            return outcome;
        }
        catch (MigrationException ex)
        {
            LastResult = ex.Message;
            _env.Toast.Show(ex.Message, 6000);
            return null;
        }
    }

    private int Apply(Func<CatalogStore, int> action, string label)
    {
        var changed = action(_env.Catalog);
        Refresh();
        LastResult = changed == 0 ? "无需改动" : $"{label}：{changed} 项";
        if (changed > 0) _env.Toast.Show(LastResult);
        return changed;
    }

    private bool Confirm(string title, string message) => _dialogs.Confirm(title, message);
}
