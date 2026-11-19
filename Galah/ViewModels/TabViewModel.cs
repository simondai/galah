using System.Windows.Media;
using Galah.Models;
using Galah.Services;

namespace Galah.ViewModels;

/// <summary>One tile in the grid: icon + two-line label + derived invalid badge.</summary>
public sealed class TileText
{
    /// <summary>Localized kind label shown in the context menu and the edit dialog.</summary>
    public static string OfKind(string kind) => kind switch
    {
        ItemKinds.App => "应用",
        ItemKinds.File => "文件",
        ItemKinds.Folder => "文件夹",
        ItemKinds.Url => "链接",
        ItemKinds.Protocol => "协议链接",
        _ => "未知",
    };
}

/// <summary>ViewModel of a single launcher entry rendered as an icon tile.</summary>
public sealed class ItemTileViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isSelected;
    private bool _isValid;
    private int _iconGeneration;

    /// <summary>The stored item (never mutated by the view).</summary>
    public CatalogItem Source { get; }

    /// <summary>Stable id (INV-7).</summary>
    public string Id => Source.Id;

    /// <summary>Item kind constant.</summary>
    public string Kind => Source.Kind;

    /// <summary>Display name; the two-line trim is done by the TextBlock.</summary>
    public string Name => Source.Name;

    /// <summary>Target path/URL shown in the context menu and toasts.</summary>
    public string Target => Source.Target;

    /// <summary>Localized kind label.</summary>
    public string KindLabel => TileText.OfKind(Source.Kind);

    /// <summary>Tile icon (placeholder until extraction lands).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    /// <summary>True when the target currently looks reachable (derived, never stored — INV-8).</summary>
    public bool IsValid
    {
        get => _isValid;
        internal set { if (Set(ref _isValid, value)) Raise(nameof(IsInvalid)); }
    }

    /// <summary>Inverse helper for the badge visibility.</summary>
    public bool IsInvalid => !_isValid;

    /// <summary>Selection highlight (single-click mode still selects, spec §3.2).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Icon edge in DIPs, from the iconSize setting.</summary>
    public double IconEdge { get; set; }

    /// <summary>Create a tile over a stored item.</summary>
    public ItemTileViewModel(CatalogItem item)
    {
        Source = item;
        _isValid = LauncherService.LooksValid(item);
    }

    /// <summary>Launch-time snapshot: the values as they are right now (spec §4.2).</summary>
    public LaunchRequest Snapshot() => LaunchRequest.From(Source.Clone());

    /// <summary>Re-probe validity (on render and on click) and refresh the icon lazily.</summary>
    public void Refresh(double iconEdge)
    {
        IconEdge = iconEdge;
        IsValid = LauncherService.LooksValid(Source);
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        var generation = ++_iconGeneration;
        try
        {
            var large = IconEdge >= 36;
            var image = await IconService.GetAsync(Source, large);
            if (generation != _iconGeneration) return;
            Icon = image;
        }
        catch
        {
            if (generation == _iconGeneration)
                Icon = IconService.CreatePlaceholder(Source.Name);
        }
    }
}

/// <summary>ViewModel of one tab in the strip.</summary>
public sealed class TabViewModel : ObservableObject
{
    private bool _isSelected;

    /// <summary>Stable tab id.</summary>
    public string Id { get; }

    /// <summary>Tab name.</summary>
    public string Name { get; }

    /// <summary>Number of items (context menus, delete confirmation).</summary>
    public int ItemCount { get; }

    /// <summary>Highlight state.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Create a tab view model.</summary>
    public TabViewModel(string id, string name, int itemCount)
    {
        Id = id;
        Name = name;
        ItemCount = itemCount;
    }
}
