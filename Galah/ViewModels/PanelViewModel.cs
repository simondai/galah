using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Galah.Models;
using Galah.Services;

namespace Galah.ViewModels;

/// <summary>Result of the edit dialog: nothing on cancel, a committed item, or a replacement draft.</summary>
public readonly record struct EditOutcome(CatalogItem? Item, CatalogItem? ReplacementDraft)
{
    /// <summary>True when the user canceled (no writes allowed).</summary>
    public bool Canceled => Item is null && ReplacementDraft is null;
}

/// <summary>
/// Dialog services the panel needs. Implemented by the view layer, injected here so the view model
/// never references a window type (design §4 dependency direction).
/// </summary>
public interface IDialogHost
{
    /// <summary>Open the editor for a draft; returns the committed draft or null when canceled.</summary>
    EditOutcome EditNewItem(CatalogItem draft);

    /// <summary>Open the editor for a stored item; returns the saved copy or null when canceled.</summary>
    EditOutcome EditExistingItem(CatalogItem item);

    /// <summary>Single-line text prompt; null on cancel.</summary>
    string? PromptText(string title, string label, string? initial);

    /// <summary>Yes/no confirmation.</summary>
    bool Confirm(string title, string message);

    /// <summary>Show the settings window (modeless).</summary>
    void ShowSettings();

    /// <summary>Show the add-shortcut picker (modeless); invoked once per chosen seed.</summary>
    /// <param name="onAdd">Commit callback receiving each picked candidate.</param>
    void ShowAddPicker(Action<Galah.Services.CatalogSources.CatalogSeed> onAdd);

    /// <summary>Show the maintenance/migration window (modeless).</summary>
    void ShowMaintenance();
}

/// <summary>ViewModel of the panel: tab strip, icon grid, chrome menu, drop target.</summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private TabViewModel? _selectedTab;
    private ItemTileViewModel? _selectedItem;
    private DateTime _lastLaunchUtc = DateTime.MinValue;
    private string? _saveProblem;

    /// <summary>Re-click guard for single-click mode (spec §3.2, task E: 250 ms).</summary>
    public const int RelaunchGuardMs = 250;

    /// <summary>Title text of the custom chrome.</summary>
    public string AppTitle => "Galah";

    /// <summary>Tabs in stored order.</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>Items of the selected tab in stored order.</summary>
    public ObservableCollection<ItemTileViewModel> Items { get; } = new();

    /// <summary>Toast strip state.</summary>
    public ToastService Toast => _env.Toast;

    /// <summary>Dialog host assigned by the view layer at construction.</summary>
    public IDialogHost? DialogHost { get; set; }

    /// <summary>
    /// Selected tab. Rendering never writes; only an explicit user selection is committed to
    /// ui-state (schema 2) through <see cref="SelectTab"/> — the content library is never touched
    /// by a selection (INV-1).
    /// </summary>
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            foreach (var tab in Tabs) tab.IsSelected = ReferenceEquals(tab, value);
            RebuildItems();
        }
    }

    /// <summary>
    /// Select a category because the user asked for it and remember the choice.
    /// </summary>
    /// <param name="tab">The category to show.</param>
    /// <summary>
    /// Jump to a category by visible index (Sprint 3 / E4). Returns false when the index is out
    /// of range so the caller can leave the keystroke unhandled instead of swallowing it.
    /// </summary>
    /// <param name="index">Zero-based position in the tab list.</param>
    public bool SelectTabByIndex(int index)
    {
        if (index < 0 || index >= Tabs.Count) return false;
        SelectTab(Tabs[index]);
        return true;
    }

    /// <summary>
    /// Move to the next/previous category, wrapping at the ends (wheel and Ctrl+Tab).
    /// </summary>
    /// <param name="delta">+1 or -1.</param>
    public void SwitchTabRelative(int delta)
    {
        var current = SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab);
        var next = Galah.Services.TabNavigation.StepIndex(Tabs.Count, current, delta);
        if (next < 0 || next >= Tabs.Count) return;
        SelectTab(Tabs[next]);
    }

    public void SelectTab(TabViewModel tab)
    {
        SelectedTab = tab;
        _env.UiState.SetActiveTab(tab.Id);
    }

    /// <summary>Highlighted tile (single-click selection, spec §3.2).</summary>
    public ItemTileViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            foreach (var item in Items) item.IsSelected = ReferenceEquals(item, value);
            Set(ref _selectedItem, value);
        }
    }

    /// <summary>Tile width in DIPs (iconSize step).</summary>
    public double TileWidth => IconSizes.TileWidth(_env.Settings.Current.IconSize);

    /// <summary>True when the panel surface should alpha-blend with the desktop.</summary>
    public bool IsTranslucentMaterial => Galah.Models.PanelMaterials.Translucent == _env.Settings.Current.PanelMaterial;

    /// <summary>True when the tab strip renders as a vertical sidebar (settings: 标签位置=左侧).</summary>
    public bool IsTabVertical => Galah.Models.TabPositions.Left == _env.Settings.Current.TabPosition;

    /// <summary>Icon edge in DIPs.</summary>
    public double IconEdge => IconSizes.IconEdge(_env.Settings.Current.IconSize);

    /// <summary>True when tiles launch on a single click.</summary>
    public bool SingleClickMode => _env.Settings.Current.ClickMode == ClickModes.Single;

    /// <summary>Global banner text when a commit failed (INV-9); null when everything is saved.</summary>
    public string? SaveProblem
    {
        get => _saveProblem;
        private set { Set(ref _saveProblem, value); Raise(nameof(HasSaveProblem)); }
    }

    /// <summary>True while the retry banner is up.</summary>
    public bool HasSaveProblem => !string.IsNullOrEmpty(_saveProblem);

    // ---- commands -----------------------------------------------------------------

    public ICommand AddAppCommand { get; }

    /// <summary>Open the Sprint 1 add-shortcut picker (system tools / settings pages / folders / installed apps).</summary>
    public ICommand AddPickerCommand { get; }
    public ICommand AddFileCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddUrlCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand RenameTabCommand { get; }
    public ICommand DeleteTabCommand { get; }
    public ICommand MoveTabLeftCommand { get; }
    public ICommand MoveTabRightCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand DeleteItemCommand { get; }
    public ICommand MoveItemLeftCommand { get; }
    public ICommand MoveItemRightCommand { get; }
    public ICommand RetrySaveCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand HideCommand { get; }

    /// <summary>Create the panel view model over the environment.</summary>
    public PanelViewModel(AppEnvironment environment)
    {
        _env = environment;

        AddAppCommand = new RelayCommand(_ => AddItem(ItemKinds.App));
        AddPickerCommand = new RelayCommand(_ => DialogHost?.ShowAddPicker(AddFromSeed));
        AddFileCommand = new RelayCommand(_ => AddItem(ItemKinds.File));
        AddFolderCommand = new RelayCommand(_ => AddItem(ItemKinds.Folder));
        AddUrlCommand = new RelayCommand(_ => AddItem(ItemKinds.Url));

        NewTabCommand = new RelayCommand(AddTab);
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is TabViewModel tab) SelectTab(tab);
        });
        RenameTabCommand = new RelayCommand(parameter => { if (parameter is TabViewModel tab) RenameTab(tab); });
        DeleteTabCommand = new RelayCommand(parameter => { if (parameter is TabViewModel tab) DeleteTab(tab); });
        MoveTabLeftCommand = new RelayCommand(parameter => { if (parameter is TabViewModel tab) MoveTab(tab, -1); });
        MoveTabRightCommand = new RelayCommand(parameter => { if (parameter is TabViewModel tab) MoveTab(tab, +1); });

        EditItemCommand = new RelayCommand(parameter => { if (parameter is ItemTileViewModel tile) EditItem(tile); });
        DeleteItemCommand = new RelayCommand(parameter => { if (parameter is ItemTileViewModel tile) DeleteItem(tile); });
        MoveItemLeftCommand = new RelayCommand(parameter => { if (parameter is ItemTileViewModel tile) MoveItem(tile, -1); });
        MoveItemRightCommand = new RelayCommand(parameter => { if (parameter is ItemTileViewModel tile) MoveItem(tile, +1); });

        RetrySaveCommand = new RelayCommand(RetrySave);
        ShowSettingsCommand = new RelayCommand(() => DialogHost?.ShowSettings());
        ExitCommand = new RelayCommand(ExitApplication);
        HideCommand = new RelayCommand(() => _env.Panel.Hide());

        _env.Catalog.Changed += Reload;
        _env.Catalog.SaveFailed += message => SaveProblem = "未能保存：" + message;
        _env.Settings.SaveFailed += message => SaveProblem = "未能保存设置：" + message;
        _env.UiState.SaveFailed += message => SaveProblem = "未能保存窗口状态：" + message;
        _env.Panel.VisibilityChanged += OnPanelVisibilityChanged;

        Reload();
    }

    /// <summary>Rebuild the visible snapshot from the store (rendering only — zero writes).</summary>
    public void Reload()
    {
        var keepTabId = _selectedTab?.Id;
        var keepItemId = _selectedItem?.Id;

        Tabs.Clear();
        foreach (var tab in _env.Catalog.Current.TabsInOrder())
            Tabs.Add(new TabViewModel(tab.Id, tab.Name, tab.Items.Count));

        // A reload is a render, not a user action: pick the category to show without writing.
        var restoreId = keepTabId ?? _env.UiState.Current.ActiveTabId;
        var restored = Tabs.FirstOrDefault(t => t.Id == restoreId);
        SelectedTab = restored ?? Tabs.FirstOrDefault();
        if (restored is null && !string.IsNullOrEmpty(_env.UiState.Current.ActiveTabId))
            _env.UiState.SetActiveTab(SelectedTab?.Id);   // the remembered category is gone: record the fallback
        if (keepItemId is not null) SelectedItem = Items.FirstOrDefault(i => i.Id == keepItemId);
        Raise(nameof(TileWidth));
        Raise(nameof(IconEdge));
        Raise(nameof(SingleClickMode));
    }

    /// <summary>Re-apply settings-derived layout after a settings change.</summary>
    public void RefreshSettings()
    {
        Raise(nameof(TileWidth));
        Raise(nameof(IconEdge));
        Raise(nameof(SingleClickMode));
        Raise(nameof(IsTabVertical));
        Raise(nameof(IsTranslucentMaterial));
        foreach (var item in Items) item.Refresh(IconEdge);
    }

    /// <summary>Handle a raw WPF drop payload (file list or link text) and commit what it yields.</summary>
    /// <param name="data">The drag payload.</param>
    public void HandleDrop(System.Windows.IDataObject data)
    {
        var result = ImportService.FromDrop(data);
        if (result.AddedCount == 0 && result.SkippedCount == 0)
        {
            Toast.Show("没有可添加的项目");
            return;
        }
        CommitImport(result);
    }

    /// <summary>Handle a drop of Explorer paths: one committed batch, skipped entries counted (spec §2.4).</summary>
    public void HandleFileDrop(string[] paths) => CommitImport(ImportService.FromFilePaths(paths));

    /// <summary>
    /// Commit an import batch as one write. A batch with no accepted entry is not a commit at all, so
    /// the three authoritative files stay byte-identical (INV-1).
    /// </summary>
    private void CommitImport(ImportResult result)
    {
        if (result.AddedCount == 0)
        {
            Toast.Show(result.SkippedCount > 0 ? $"已跳过 {result.SkippedCount} 项（格式不支持）" : "没有可添加的项目");
            return;
        }

        var tabId = EnsureActiveTabId();
        if (tabId is null)
        {
            Toast.Show("无法创建分类，请先重试保存");
            return;
        }

        // Batch = a single commit → a single atomic write.
        _env.Catalog.AddItems(tabId, result.Drafts.Select(d => d.Item));
        var message = $"已添加 {result.AddedCount} 项";
        if (result.SkippedCount > 0) message += $"，已跳过 {result.SkippedCount} 项";
        Toast.Show(message);
    }

    /// <summary>Launch one tile (spec §3.1 dispatch + §3.2 click modes + hideOnLaunch).</summary>
    /// <param name="tile">Tile whose snapshot is launched.</param>
    /// <param name="clickCount">WPF click count, used to honour the double-click mode.</param>
    public void Launch(ItemTileViewModel tile, int clickCount)
    {
        if (SingleClickMode)
        {
            var elapsed = (DateTime.UtcNow - _lastLaunchUtc).TotalMilliseconds;
            if (elapsed < RelaunchGuardMs) return;   // debounce: no second process for a double click
        }
        else if (clickCount < 2)
        {
            SelectedItem = tile;                     // single click only selects in double-click mode
            tile.IsValid = LauncherService.LooksValid(tile.Source);
            return;
        }

        _lastLaunchUtc = DateTime.UtcNow;
        SelectedItem = tile;

        var result = LauncherService.Launch(tile.Snapshot());
        if (!result.Ok)
        {
            Toast.ShowLaunchFailure(tile.Source, result);
            tile.IsValid = LauncherService.LooksValid(tile.Source);   // derived badge, nothing stored
            return;
        }

        if (_env.Settings.Current.HideOnLaunch) _env.Panel.Hide();    // only ui-state is written here
    }

    /// <summary>Escape key from the panel (design §2.1 P-4).</summary>
    public void HandleEscape() => _env.Panel.Hide();

    // ---- item / tab commands -------------------------------------------------------

    private string? ActiveTabId =>
        Tabs.FirstOrDefault(t => t.Id == _selectedTab?.Id)?.Id ?? Tabs.FirstOrDefault()?.Id;

    /// <summary>
    /// Tab that receives new items; when the library has no tab at all, the first one is created as a
    /// user-commit action (never from a render path — INV-1).
    /// </summary>
    private string? EnsureActiveTabId()
    {
        var existing = ActiveTabId;
        if (existing is not null) return existing;
        var created = _env.Catalog.AddTab("默认");
        Reload();
        var createdTab = Tabs.FirstOrDefault(t => t.Id == created.Id);
        if (createdTab is not null) SelectTab(createdTab);
        return created.Id;
    }

    /// <summary>
    /// Commit one picker seed into the active tab (Sprint 1). Idempotent per tab: the same
    /// launchable (kind+target+args) is never offered twice side by side; a duplicate reports
    /// where it already lives instead of silently piling up tiles (spec INV-2: one write per add).
    /// </summary>
    /// <param name="seed">Candidate chosen in the picker.</param>
    public void AddFromSeed(Galah.Services.CatalogSources.CatalogSeed seed)
    {
        var tabId = EnsureActiveTabId();
        if (tabId is null)
        {
            Toast.Show("无法创建分类，请先重试保存");
            return;
        }

        var liveTab = _env.Catalog.Current.FindTab(tabId);
        var same = (liveTab?.Items ?? new List<CatalogItem>()).Where(i => i.Kind == seed.Kind
                   && string.Equals(i.Target, seed.Target, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(i.Args ?? "", seed.Args ?? "", StringComparison.OrdinalIgnoreCase))
                   .ToList();
        if (same.Count > 0)
        {
            Toast.Show($"「{seed.Name}」已在当前分类中");
            return;
        }

        var item = new CatalogItem
        {
            Id = Ids.NewItemId(),
            Kind = seed.Kind,
            Name = seed.Name,
            Target = seed.Target,
            Args = seed.Args,
            Icon = new IconReference { Mode = IconModes.Auto },
        };

        var errors = Galah.Services.ItemValidator.Validate(item, isNew: true);
        if (errors.Count > 0)
        {
            Toast.Show("无法添加：" + errors[0].Message);
            return;
        }

        _env.Catalog.AddItems(tabId, new[] { item });
        Toast.Show($"已添加「{seed.Name}」");
    }

    private void AddItem(string kind)
    {
        var outcome = DialogHost?.EditNewItem(NewDraft(kind)) ?? default;
        var committed = outcome.Item;
        if (committed is null)
        {
            // Cancel path: nothing was written (INV-3, TL-A2) — not even a fallback category.
            return;
        }

        var tabId = EnsureActiveTabId();
        if (tabId is null)
        {
            Toast.Show("无法创建分类，请先重试保存");
            return;
        }

        _env.Catalog.AddItems(tabId, new[] { committed });
        Toast.Show($"已添加「{committed.Name}」");
    }

    private static CatalogItem NewDraft(string kind) => new()
    {
        Id = Ids.NewItemId(),
        Kind = kind,
        Name = "",
        Target = "",
        Icon = new IconReference { Mode = IconModes.Auto },
    };

    private void EditItem(ItemTileViewModel tile)
    {
        var original = tile.Source.Clone();
        var outcome = DialogHost?.EditExistingItem(original) ?? default;
        if (outcome.Canceled) return;                     // cancel = zero writes (INV-3, TL-C2)

        if (outcome.Item is { } saved)
        {
            _env.Catalog.ReplaceItem(saved);              // id + order + kind preserved (INV-7)
            Toast.Show($"已保存「{saved.Name}」");
            return;
        }
        if (outcome.ReplacementDraft is { } replacement) DeleteAndReAdd(original, replacement);
    }

    /// <summary>
    /// Delete + re-add flow for a kind change (spec §4.1: kind is immutable, switching type is two
    /// explicit commits offered as one click).
    /// </summary>
    /// <param name="item">Item to delete.</param>
    /// <param name="newKindDraft">Draft pre-filled for the replacement dialog.</param>
    public void DeleteAndReAdd(CatalogItem item, CatalogItem newKindDraft)
    {
        _env.Catalog.DeleteItem(item.Id);
        var outcome = DialogHost?.EditNewItem(newKindDraft) ?? default;
        var tabId = EnsureActiveTabId();
        if (outcome.Item is { } replacement && tabId is not null)
        {
            _env.Catalog.AddItems(tabId, new[] { replacement });
            Toast.Show($"已改为新条目「{replacement.Name}」");
        }
        else
        {
            Toast.Show("原条目已删除，未创建新条目（可用备份找回）");
        }
    }

    private void DeleteItem(ItemTileViewModel tile)
    {
        if (!DialogHost!.Confirm("删除条目", $"确定删除「{tile.Name}」？（不可撤销，可由备份文件兜底）")) return;
        _env.Catalog.DeleteItem(tile.Id);
        Toast.Show($"已删除「{tile.Name}」");
    }

    private void MoveItem(ItemTileViewModel tile, int delta)
    {
        _env.Catalog.MoveItem(tile.Id, delta);
    }

    /// <summary>Move the selected tile into another tab (cross-category move without dragging).</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="targetTabId">Destination tab id.</param>
    public void MoveItemToTab(string itemId, string targetTabId) => _env.Catalog.MoveItemToTab(itemId, targetTabId);

    private void AddTab()
    {
        var name = DialogHost?.PromptText("新建分类", "分类名称", "新分类");
        if (string.IsNullOrWhiteSpace(name)) return;
        var tab = _env.Catalog.AddTab(name.Trim());
        Reload();
        var renamed = Tabs.FirstOrDefault(t => t.Id == tab.Id);
        if (renamed is not null) SelectTab(renamed);
    }

    private void RenameTab(TabViewModel tab)
    {
        var name = DialogHost?.PromptText("重命名分类", "分类名称", tab.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == tab.Name) return;
        _env.Catalog.RenameTab(tab.Id, name.Trim());
    }

    private void DeleteTab(TabViewModel tab)
    {
        var count = _env.Catalog.CountItems(tab.Id);
        if (_env.Catalog.Current.Tabs.Count <= 1 && count == 0)
        {
            Toast.Show("至少需要保留一个分类");
            return;
        }
        if (!DialogHost!.Confirm("删除分类", $"将删除 {count} 个条目，确定删除分类「{tab.Name}」？")) return;
        _env.Catalog.DeleteTab(tab.Id);
    }

    private void MoveTab(TabViewModel tab, int delta) => _env.Catalog.MoveTab(tab.Id, delta);

    private void RebuildItems()
    {
        var keepId = _selectedItem?.Id;
        Items.Clear();
        var tab = _env.Catalog.Current.FindTab(SelectedTab?.Id ?? "");
        if (tab is not null)
        {
            foreach (var item in _env.Catalog.Current.ItemsInOrder(tab))
            {
                var tile = new ItemTileViewModel(item);
                Items.Add(tile);
            }
        }
        foreach (var tile in Items) tile.Refresh(IconEdge);
        SelectedItem = Items.FirstOrDefault(i => i.Id == keepId);
    }

    private void RetrySave()
    {
        var ok = _env.Catalog.RetryPendingSave() & _env.Settings.RetryPendingSave() & _env.UiState.RetryPendingSave();
        if (ok)
        {
            SaveProblem = null;
            Toast.Show("已重新保存");
        }
        else
        {
            Toast.Show("仍未保存成功，请稍后再试");
        }
    }

    private void OnPanelVisibilityChanged(bool visible)
    {
        if (visible) Reload();
        else _env.Toast.Close();
    }

    private void ExitApplication()
    {
        // One exit path for every route (panel menu, tray menu): the environment owns the
        // confirmation wording and the shutdown sequence. Deferred so the menu popup has fully
        // closed before the confirmation modal opens.
        System.Windows.Application.Current.Dispatcher.InvokeAsync(
            _env.RequestExit, System.Windows.Threading.DispatcherPriority.Background);
    }
}
