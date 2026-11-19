using System.Windows;
using System.Windows.Input;
using Galah.Services.CatalogSources;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// Add-shortcut picker (Sprint 1 / ROADMAP B1-B5): category rail + filter + candidate list.
/// Modeless like the settings window; the commit callback lives in the panel view model so the
/// catalog write path stays single-owner.
/// </summary>
public sealed partial class AddPickerWindow : Window
{
    private readonly AddPickerViewModel _vm;
    private readonly Action<CatalogSeed> _onAdd;

    /// <summary>Create the picker.</summary>
    /// <param name="viewModel">Picker model (sources + filter).</param>
    /// <param name="onAdd">Invoked with each chosen seed (window stays open for multi-add).</param>
    public AddPickerWindow(AddPickerViewModel viewModel, Action<CatalogSeed> onAdd)
    {
        InitializeComponent();
        _vm = viewModel;
        _onAdd = onAdd;
        DataContext = _vm;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void SeedList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add();

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter commits without needing the mouse; the list keeps focus after each add so the
        // keyboard flow is: type filter -> Enter -> arrow keys -> Enter.
        if (e.Key == Key.Enter && _vm.SelectedItem is not null)
        {
            Add();
            e.Handled = true;
        }
    }

    private void Add()
    {
        var seed = _vm.TakeSelection();
        if (seed is null) return;
        _onAdd(seed);
        SeedList.Focus();
        if (SeedList.SelectedItem is not null)
            SeedList.ScrollIntoView(SeedList.SelectedItem);
    }
}
