using System.Windows.Media;
using Galah.Models;
using Galah.Services;
using Galah.Services.CatalogSources;

namespace Galah.ViewModels;

/// <summary>One row of the picker: a seed plus its lazily resolved icon (Sprint 1 / ROADMAP B1-B5).</summary>
public sealed class PickerItemViewModel : ObservableObject
{
    private ImageSource? _icon;

    /// <summary>Create a row for a seed.</summary>
    /// <param name="seed">Candidate produced by a catalog source.</param>
    public PickerItemViewModel(CatalogSeed seed)
    {
        Seed = seed;
        _ = LoadIconAsync();
    }

    /// <summary>The candidate to commit when the user picks this row.</summary>
    public CatalogSeed Seed { get; }

    /// <summary>Tile name.</summary>
    public string Name => Seed.Name;

    /// <summary>Secondary line (publisher / note).</summary>
    public string? Note => Seed.Note;

    /// <summary>Icon image; null until loaded (the template shows the placeholder meanwhile).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set => Set(ref _icon, value);
    }

    private async Task LoadIconAsync()
    {
        try
        {
            // A transient item is enough: IconService keys its cache by (source,index), so picker
            // icons and tile icons share one cache entry.
            var probe = new CatalogItem
            {
                Id = "picker",
                Kind = Seed.Kind,
                Name = Seed.Name,
                Target = Seed.Target,
                Args = Seed.Args,
                Icon = new IconReference { Mode = IconModes.Auto },
            };
            Icon = await IconService.GetAsync(probe, large: true);
        }
        catch (Exception)
        {
            // An icon failure must never hide a candidate; the placeholder stands in.
        }
    }
}

/// <summary>One picker category as shown in the left rail.</summary>
/// <param name="Category">Source category key.</param>
/// <param name="Label">Chinese label.</param>
public sealed record PickerCategoryViewModel(SeedCategory Category, string Label);

/// <summary>ViewModel of the add-shortcut picker window.</summary>
public sealed class AddPickerViewModel : ObservableObject
{
    private readonly CatalogSourceService _sources = new();
    private readonly Dictionary<SeedCategory, List<PickerItemViewModel>> _cache = new();
    private IReadOnlyList<PickerItemViewModel> _items = Array.Empty<PickerItemViewModel>();
    private PickerCategoryViewModel? _selectedCategory;
    private string _filter = "";
    private PickerItemViewModel? _selectedItem;

    /// <summary>Create the picker model.</summary>
    public AddPickerViewModel()
    {
        Categories = _sources.Categories
            .Select(c => new PickerCategoryViewModel(c, CatalogSourceService.LabelOf(c)))
            .ToList();
        SelectedCategory = Categories.FirstOrDefault();
    }

    /// <summary>Category tabs of the picker.</summary>
    public IReadOnlyList<PickerCategoryViewModel> Categories { get; }

    /// <summary>Rows shown for the current category + filter.</summary>
    public IReadOnlyList<PickerItemViewModel> Items
    {
        get => _items;
        private set => Set(ref _items, value);
    }

    /// <summary>Selected category; loading its rows is deferred to the first selection.</summary>
    public PickerCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Case-insensitive name filter typed by the user.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>Row highlighted in the list.</summary>
    public PickerItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            Raise(nameof(CanAdd));
        }
    }

    /// <summary>True when a row can be committed.</summary>
    public bool CanAdd => SelectedItem is not null;

    /// <summary>Commit the selected row (the caller closes the window).</summary>
    /// <returns>The seed, or null when nothing is selected.</returns>
    public CatalogSeed? TakeSelection()
    {
        var seed = SelectedItem?.Seed;
        Raise(nameof(CanAdd));
        return seed;
    }

    /// <summary>Force a re-scan of the installed-app feed (after an install/uninstall cycle).</summary>
    public void RefreshInstalled()
    {
        _cache.Remove(SeedCategory.InstalledApps);
        InstalledAppSource.ResetCache();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var category = _selectedCategory;
        if (category is null)
        {
            Items = Array.Empty<PickerItemViewModel>();
            return;
        }

        if (!_cache.TryGetValue(category.Category, out var rows))
        {
            rows = _sources.Enumerate(category.Category)
                .Select(s => new PickerItemViewModel(s))
                .ToList();
            _cache[category.Category] = rows;
        }

        var needle = (_filter ?? "").Trim();
        Items = needle.Length == 0
            ? rows
            : rows.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                           || (r.Note?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
                  .ToList();
        SelectedItem = Items.FirstOrDefault();
    }
}
