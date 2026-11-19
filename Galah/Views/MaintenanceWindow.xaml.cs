using System.Windows;
using Galah.Services;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// Maintenance and migration window (Sprint 2). Thin: every decision lives in the view model so
/// the same behaviour is unit-testable without a UI thread.
/// </summary>
public sealed partial class MaintenanceWindow : Window
{
    private readonly MaintenanceViewModel _vm;

    /// <summary>Create the window.</summary>
    /// <param name="viewModel">Maintenance model.</param>
    public MaintenanceWindow(MaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceViewModel.LastResult))
                ResultLine.Text = _vm.LastResult;
        };
        ResultLine.Text = _vm.LastResult;
    }

    private void CleanInvalid_Click(object sender, RoutedEventArgs e) => _vm.CleanInvalid();

    private void CleanDuplicates_Click(object sender, RoutedEventArgs e) => _vm.CleanDuplicates();

    private void SortAll_Click(object sender, RoutedEventArgs e) => _vm.SortAll();

    private void RebuildIcons_Click(object sender, RoutedEventArgs e) => _vm.RebuildIcons();

    private void Export_Click(object sender, RoutedEventArgs e) => _vm.Export();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // the stores raise Changed after import, which the panel already listens to
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
