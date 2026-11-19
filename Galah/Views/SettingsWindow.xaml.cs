using System.Windows;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// Modeless settings window: every control writes through immediately (spec §1: 产品无"保存"按钮),
/// and problems such as a hotkey conflict appear as warning rows instead of dialogs (AC-03).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    /// <summary>Create the window.</summary>
    /// <param name="viewModel">Settings model built over the environment.</param>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = _vm;
        InitializeComponent();
    }

    private void ApplyHotkey_Click(object sender, RoutedEventArgs e) => _vm.ApplyHotkeyCommand.Execute(null);

    private void RestoreHotkey_Click(object sender, RoutedEventArgs e) => _vm.RestoreHotkeyCommand.Execute(null);

    private void Maintenance_Click(object sender, RoutedEventArgs e) =>
        Galah.Services.AppEnvironment.Current?.ShowMaintenance();

    private void CreateShortcut_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var env = Galah.Services.AppEnvironment.Current;
        if (env is null) return;
        var icon = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(env.ExecutablePath) ?? "", "app.ico");
        var result = Galah.Services.ShortcutMaker.Create(env.ExecutablePath,
            System.IO.File.Exists(icon) ? icon : null);
        ShortcutResultLine.Text = result.Message;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
