using System.Text;
using System.Windows.Media;
using Galah.Models;
using Galah.Persistence;
using Galah.Services;

namespace Galah.ViewModels;

/// <summary>File/folder picking, implemented by the view layer (keeps view models window-free).</summary>
public interface IFilePickerService
{
    /// <summary>Pick one file; null when canceled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">Win32 filter, e.g. "程序|*.exe|全部文件|*.*".</param>
    /// <param name="initialPath">Path pre-selected when possible.</param>
    string? PickFile(string title, string filter, string? initialPath);

    /// <summary>Pick one folder; null when canceled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialPath">Folder pre-selected when possible.</param>
    string? PickFolder(string title, string? initialPath);
}

/// <summary>ViewModel of the add/edit dialog: field state, validation, browse actions, commit result.</summary>
public sealed class EditItemViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly IFilePickerService _picker;
    private readonly CatalogItem _original;
    private int _iconGeneration;

    private string _name = "";
    private string _target = "";
    private string? _args;
    private string? _workdir;
    private bool _runAsAdmin;
    private string? _pendingImageOverride;
    private ImageSource? _iconPreview;
    private string _errors = "";
    private bool _showSuggestedKindButton;
    private string _suggestedKind = "";

    /// <summary>Dialog title.</summary>
    public string Title { get; }

    /// <summary>True for a brand-new item.</summary>
    public bool IsNew { get; }

    /// <summary>Kind constant (immutable after creation — spec §4.1).</summary>
    public string Kind => _original.Kind;

    /// <summary>Localized kind, shown read-only next to the title.</summary>
    public string KindLabel => TileText.OfKind(_original.Kind);

    /// <summary>Links have no filesystem source, so their 浏览 button is disabled (spec §2.1).</summary>
    public bool CanBrowseTarget => Kind != ItemKinds.Url;

    /// <summary>Caption of the commit button.</summary>
    public string AcceptLabel => IsNew ? "添加" : "保存";

    /// <summary>Visibility helper: the dialog is in "add" mode.</summary>
    /// <summary>True for URL items: only they may fetch a site icon.</summary>
    public System.Windows.Visibility IsUrlVisibility => Kind == ItemKinds.Url
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility IsNewVisibility => IsNew
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Visibility helper: the dialog is in "edit" mode.</summary>
    public System.Windows.Visibility IsEditVisibility => IsNew
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    /// <summary>Name field.</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { MaybeAutoName(); Revalidate(); } }
    }

    /// <summary>Target field (stored form: forward slashes).</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (Set(ref _target, value))
            {
                MaybeAutoName();
                Revalidate();
                _ = RefreshPreviewAsync();
            }
        }
    }

    /// <summary>Arguments field, stored and replayed verbatim (spec §2.3, TL-C3).</summary>
    public string? Args
    {
        get => _args;
        set { if (Set(ref _args, value)) Revalidate(); }
    }

    /// <summary>Working directory field.</summary>
    public string? Workdir
    {
        get => _workdir;
        set
        {
            if (Set(ref _workdir, value))
            {
                Revalidate();
                _ = RefreshPreviewAsync();
            }
        }
    }

    /// <summary>Elevation flag.</summary>
    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => Set(ref _runAsAdmin, value);
    }

    /// <summary>Icon preview (auto extraction or picked override).</summary>
    public ImageSource? IconPreview
    {
        get => _iconPreview;
        private set => Set(ref _iconPreview, value);
    }

    /// <summary>Caption describing the current icon mode.</summary>
    public string IconModeText => _overrideActive
        ? "手动图标（预览来自所选文件或快捷方式）"
        : "自动提取目标图标";

    /// <summary>Inline validation message block (spec §2.3: 内联报错).</summary>
    public string Errors
    {
        get => _errors;
        private set { Set(ref _errors, value); Raise(nameof(HasErrors)); }
    }

    /// <summary>True while the draft is rejected.</summary>
    public bool HasErrors => !string.IsNullOrEmpty(_errors);

    /// <summary>True when the typed target no longer matches the immutable kind.</summary>
    public bool ShowSuggestedKindButton
    {
        get => _showSuggestedKindButton;
        private set => Set(ref _showSuggestedKindButton, value);
    }

    /// <summary>Localized name of the suggested kind for the button caption.</summary>
    public string SuggestedKindLabel => TileText.OfKind(_suggestedKind);

    /// <summary>Outcome handed back to the caller.</summary>
    public EditOutcome Outcome { get; private set; }

    /// <summary>True when the dialog may close (set by 保存 / 删除并另加).</summary>
    public bool Completed { get; private set; }

    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand BrowseWorkdirCommand { get; }
    public RelayCommand PickIconCommand { get; }

    /// <summary>Manual site-icon fetch (URL items only; nothing is ever fetched automatically).</summary>
    public RelayCommand FetchIconCommand { get; }
    public RelayCommand ResetIconCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand DeleteAndReAddCommand { get; }

    /// <summary>Create the dialog model over a snapshot of the item (the store is never touched here).</summary>
    public EditItemViewModel(CatalogItem item, bool isNew, AppEnvironment environment, IFilePickerService picker)
    {
        _original = item.Clone();
        _env = environment;
        _picker = picker;
        IsNew = isNew;
        Title = isNew ? $"添加{TileText.OfKind(item.Kind)}" : $"编辑{TileText.OfKind(item.Kind)}";

        _name = item.Name;
        _target = item.Target;
        _args = item.Args;
        _workdir = item.Workdir;
        _runAsAdmin = item.RunAsAdmin;
        _pendingImageOverride = item.Icon.Mode == IconModes.Override ? item.Icon.OverrideSource : null;

        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        BrowseWorkdirCommand = new RelayCommand(BrowseWorkdir);
        PickIconCommand = new RelayCommand(PickIcon);
        FetchIconCommand = new RelayCommand(_ => { _ = FetchIcon(); }, _ => Kind == ItemKinds.Url && !IsFetchingIcon);
        ResetIconCommand = new RelayCommand(ResetIcon);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => { Completed = true; Outcome = default; });
        DeleteAndReAddCommand = new RelayCommand(DeleteAndReAdd);

        Revalidate();
        _ = RefreshPreviewAsync();
    }

    /// <summary>True when a manual/derived icon override is in play.</summary>
    private bool _overrideActive => !string.IsNullOrWhiteSpace(_pendingImageOverride);

    // ---- field helpers ------------------------------------------------------------

    private CatalogItem BuildDraft()
    {
        var draft = _original.Clone();
        draft.Name = (_name ?? "").Trim();
        draft.Target = PathNorm.ToStored(_target);
        draft.Args = string.IsNullOrWhiteSpace(_args) ? null : _args;
        draft.Workdir = string.IsNullOrWhiteSpace(_workdir) ? null : PathNorm.ToStored(_workdir!);
        draft.RunAsAdmin = _runAsAdmin;
        draft.Icon = string.IsNullOrWhiteSpace(_pendingImageOverride)
            ? new IconReference { Mode = IconModes.Auto, OverrideSource = null }
            : new IconReference { Mode = IconModes.Override, OverrideSource = PathNorm.ToStored(_pendingImageOverride!) };
        return draft;
    }

    private void MaybeAutoName()
    {
        if (!string.IsNullOrWhiteSpace(_name)) return;   // never overwrite a user-typed name
        var draft = BuildDraft();
        if (string.IsNullOrWhiteSpace(draft.Target)) return;
        _name = KindInference.DeriveName(draft.Kind, draft.Target);
        Raise(nameof(Name));
    }

    private void Revalidate()
    {
        var errors = ItemValidator.Validate(BuildDraft(), IsNew);
        Errors = errors.Count == 0 ? "" : string.Join("；", errors.Select(e => e.Message));
        var kindMismatch = errors.Any(e => e.Field == nameof(CatalogItem.Target)) && !IsNew;
        _suggestedKind = KindInference.Suggest(_target, Kind);
        ShowSuggestedKindButton = kindMismatch && _suggestedKind != Kind;
        Raise(nameof(ShowSuggestedKindButton));
    }

    private Task RefreshPreviewAsync()
    {
        var generation = ++_iconGeneration;
        var draft = BuildDraft();
        return _env is null
            ? Task.CompletedTask
            : IconService.GetAsync(draft, large: true).ContinueWith(task =>
            {
                if (generation != _iconGeneration) return;
                IconPreview = task.IsCompletedSuccessfully ? task.Result : IconService.CreatePlaceholder(draft.Name);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- commands -----------------------------------------------------------------

    private void BrowseTarget()
    {
        switch (Kind)
        {
            case ItemKinds.App:
                var program = _picker.PickFile("选择程序", "程序|*.exe;*.lnk;*.msi;*.bat;*.cmd;*.com|所有文件|*.*", PathNorm.ToOs(_target));
                if (program is null) return;
                ApplyPickedProgram(program);
                break;

            case ItemKinds.File:
                var file = _picker.PickFile("选择文件", "所有文件|*.*", PathNorm.ToOs(_target));
                if (file is not null) Target = PathNorm.ToStored(file);
                break;

            case ItemKinds.Folder:
                var folder = _picker.PickFolder("选择文件夹", PathNorm.ToOs(_target));
                if (folder is not null) Target = PathNorm.ToStored(folder);
                break;

            case ItemKinds.Url:
                // A link has no filesystem source; the field is typed by hand (spec §2.1).
                break;
        }
    }

    /// <summary>
    /// A .lnk picked from the 添加应用 dialog is resolved into the triple + icon source right away, the
    /// same way a dropped one is (spec §2.1/§2.3: 已解析).
    /// </summary>
    private void ApplyPickedProgram(string pickedPath)
    {
        var stored = PathNorm.ToStored(pickedPath);
        if (!pickedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            Target = stored;
            return;
        }

        var info = LinkResolver.Resolve(stored);
        if (!info.Ok)
        {
            Target = stored;   // keep the raw path; validation will flag it if unusable
            return;
        }

        _target = info.Target;
        _args = string.IsNullOrWhiteSpace(info.Arguments) ? _args : info.Arguments;
        _workdir = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? _workdir : info.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(info.IconSource))
            _pendingImageOverride = info.IconIndex == 0 ? info.IconSource : $"{info.IconSource},{info.IconIndex}";

        if (string.IsNullOrWhiteSpace(_name)) _name = Path.GetFileNameWithoutExtension(stored);

        Raise(nameof(Name));
        Raise(nameof(Target));
        Raise(nameof(Args));
        Raise(nameof(Workdir));
        Raise(nameof(IconModeText));
        Revalidate();
        _ = RefreshPreviewAsync();
    }

    private void BrowseWorkdir()
    {
        var folder = _picker.PickFolder("选择工作目录", PathNorm.ToOs(_workdir));
        if (folder is not null) Workdir = PathNorm.ToStored(folder);
    }

    private bool _isFetchingIcon;

    /// <summary>True while a site icon request is in flight (drives the button label).</summary>
    public bool IsFetchingIcon
    {
        get => _isFetchingIcon;
        private set { if (Set(ref _isFetchingIcon, value)) { Raise(nameof(FetchIconLabel)); Raise(nameof(CanSave)); } }
    }

    /// <summary>Button text for the fetch action.</summary>
    public string FetchIconLabel => IsFetchingIcon ? "获取中…" : "获取网站图标";

    /// <summary>Result line of the last fetch attempt (empty until tried).</summary>
    public string IconHint
    {
        get => _iconHint;
        private set => Set(ref _iconHint, value);
    }

    private string _iconHint = "";

    /// <summary>The dialog stays open on a failed fetch so the reason can be read.</summary>
    public bool CanSave => !IsFetchingIcon;

    private async Task FetchIcon()
    {
        if (Kind != ItemKinds.Url || IsFetchingIcon) return;
        IsFetchingIcon = true;
        IconHint = "";
        try
        {
            var result = await FaviconService.FetchAsync(Target, _original.Id, _env.Paths);
            if (result.Ok && result.StoredPath is not null)
            {
                _pendingImageOverride = result.StoredPath;
                Raise(nameof(IconModeText));
                await RefreshPreviewAsync();
                IconHint = "已获取网站图标";
            }
            else
            {
                IconHint = result.Message;
            }
        }
        finally
        {
            IsFetchingIcon = false;
        }
    }

    private void PickIcon()
    {
        var image = _picker.PickFile("选择图标文件", "图标/图片|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*", null);
        if (image is null) return;
        _pendingImageOverride = PathNorm.ToStored(image);
        Raise(nameof(IconModeText));
        _ = RefreshPreviewAsync();
    }

    private void ResetIcon()
    {
        _pendingImageOverride = null;
        Raise(nameof(IconModeText));
        _ = RefreshPreviewAsync();
    }

    private void Save()
    {
        var draft = BuildDraft();
        var errors = ItemValidator.Validate(draft, IsNew);
        if (errors.Count != 0)
        {
            // A rejected commit writes nothing (INV-3).
            Errors = string.Join("；", errors.Select(e => e.Message));
            return;
        }

        // A picked image is copied into the override store at commit time only, so cancel leaves no
        // trace in GalahData (spec §4.2 断缘). Cache-directory writes are derived data, never the library.
        if (draft.Icon.Mode == IconModes.Override && IsImageFile(draft.Icon.OverrideSource))
        {
            var copied = IconService.StoreOverrideImage(draft.Id, draft.Icon.OverrideSource!);
            if (copied is not null) draft.Icon.OverrideSource = copied;
        }

        Outcome = new EditOutcome(draft, null);
        Completed = true;
    }

    private void DeleteAndReAdd()
    {
        var draft = BuildDraft();
        draft.Kind = _suggestedKind;
        // The replacement is a new item: ids are never reused (INV-7).
        draft.Id = Ids.NewItemId();
        draft.Order = 0;
        draft.Icon = new IconReference { Mode = IconModes.Auto, OverrideSource = null };
        Outcome = new EditOutcome(null, draft);
        Completed = true;
    }

    private static bool IsImageFile(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return false;
        var extension = Path.GetExtension(PathNorm.ToOs(storedPath)).ToLowerInvariant();
        return extension is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }
}
