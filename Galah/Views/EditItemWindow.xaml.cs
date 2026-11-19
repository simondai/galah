using System.Windows;
using System.Windows.Input;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// Separate add/edit window (spec §4.2: it outlives the panel and blocks a second editor for the
/// same id). Cancel and window close both mean "write nothing" (INV-3).
/// </summary>
public sealed partial class EditItemWindow : Window
{
    private readonly EditItemViewModel _vm;

    /// <summary>Create the dialog over a view model built from an item snapshot.</summary>
    /// <param name="viewModel">Edit model.</param>
    /// <param name="owner">Panel window that owns the dialog.</param>
    public EditItemWindow(EditItemViewModel viewModel, Window? owner)
    {
        _vm = viewModel;
        Owner = owner;
        Title = viewModel.Title;
        DataContext = _vm;
        InitializeComponent();
    }

    /// <summary>Outcome to hand back to the caller.</summary>
    public EditOutcome Outcome => _vm.Outcome;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        CloseIfCompleted();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelCommand.Execute(null);
        CloseIfCompleted();
    }

    private void DeleteAndReAdd_Click(object sender, RoutedEventArgs e)
    {
        _vm.DeleteAndReAddCommand.Execute(null);
        CloseIfCompleted();
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e) => _vm.BrowseTargetCommand.Execute(null);

    private void BrowseWorkdir_Click(object sender, RoutedEventArgs e) => _vm.BrowseWorkdirCommand.Execute(null);

    private void PickIcon_Click(object sender, RoutedEventArgs e) => _vm.PickIconCommand.Execute(null);

    private void ResetIcon_Click(object sender, RoutedEventArgs e) => _vm.ResetIconCommand.Execute(null);

    private void CloseIfCompleted()
    {
        if (!_vm.Completed) return;
        // A canceled dialog leaves every file untouched (INV-3): the caller sees a default outcome.
        DialogResult = _vm.Outcome.Canceled ? false : true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.CancelCommand.Execute(null);
            CloseIfCompleted();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                 e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            _vm.SaveCommand.Execute(null);
            CloseIfCompleted();
            e.Handled = true;
        }
    }
}
