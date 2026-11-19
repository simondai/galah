using System.Windows;
using Galah.Models;
using Galah.Services;
using Galah.Services.CatalogSources;
using Galah.ViewModels;

namespace Galah.Views;

/// <summary>
/// View-layer implementation of the dialog and picker contracts. It owns the lifetime of the
/// secondary windows and keeps one guard lease per open dialog so auto-hide rules pause while the
/// user is typing (spec §4.2).
/// </summary>
public sealed class ViewDialogHost : IDialogHost, IFilePickerService
{
    private readonly AppEnvironment _env;
    private readonly Func<Window?> _ownerProvider;
    private readonly Dictionary<string, EditItemWindow> _openEditors = new(StringComparer.Ordinal);
    private SettingsWindow? _settings;

    /// <summary>Create the host.</summary>
    /// <param name="environment">Wired environment.</param>
    /// <param name="ownerProvider">Currently visible panel window, used as dialog owner.</param>
    public ViewDialogHost(AppEnvironment environment, Func<Window?> ownerProvider)
    {
        _env = environment;
        _ownerProvider = ownerProvider;
    }

    /// <inheritdoc />
    public EditOutcome EditNewItem(CatalogItem draft) => ShowEditor(draft, isNew: true);

    /// <inheritdoc />
    public EditOutcome EditExistingItem(CatalogItem item)
    {
        // One editor per id (spec §4.2, MVP simplification): focus the window that is already open.
        if (_openEditors.TryGetValue(item.Id, out var existing))
        {
            existing.Activate();
            return default;
        }
        return ShowEditor(item, isNew: false);
    }

    /// <inheritdoc />
    public string? PromptText(string title, string label, string? initial) =>
        Guarded(() => PromptWindow.AskText(_ownerProvider(), title, label, initial));

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        Guarded(() => PromptWindow.Confirm(_ownerProvider(), title, message));

    private AddPickerWindow? _picker;
    private MaintenanceWindow? _maintenance;

    /// <inheritdoc />
    public void ShowAddPicker(Action<CatalogSeed> onAdd)
    {
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }

        var viewModel = new Galah.ViewModels.AddPickerViewModel();
        var window = new AddPickerWindow(viewModel, onAdd);
        _picker = window;
        var lease = _env.Dialogs.Open();
        window.Closed += (_, _) =>
        {
            _picker = null;
            lease.Dispose();
        };
        window.Show();
    }

    /// <inheritdoc />
    public void ShowMaintenance()
    {
        if (_maintenance is not null)
        {
            _maintenance.Activate();
            return;
        }

        var panelViewModel = FindPanelViewModel();
        var viewModel = new MaintenanceViewModel(_env, this, this);
        var window = new MaintenanceWindow(viewModel) { Owner = _ownerProvider() };
        _maintenance = window;
        var lease = _env.Dialogs.Open();
        window.Closed += (_, _) =>
        {
            _maintenance = null;
            lease.Dispose();
            panelViewModel?.Reload();
        };
        window.Show();
    }

    /// <inheritdoc />
    public void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var panelViewModel = FindPanelViewModel();
        var viewModel = new SettingsViewModel(_env, panelViewModel);
        if (panelViewModel is not null) viewModel.SettingsChanged += panelViewModel.RefreshSettings;

        var window = new SettingsWindow(viewModel);
        _settings = window;
        // Keep the guard lease for the whole lifetime of the modeless window (spec §4.2).
        var lease = _env.Dialogs.Open();
        window.Closed += (_, _) =>
        {
            _settings = null;
            lease.Dispose();
            panelViewModel?.RefreshSettings();
        };
        window.Show();
    }

    private PanelViewModel? FindPanelViewModel()
    {
        if (Application.Current is null) return null;
        foreach (var window in Application.Current.Windows)
            if (window is PanelWindow panel) return panel.ViewModel;
        return null;
    }

    // ---- pickers ------------------------------------------------------------------

    /// <inheritdoc />
    public string? PickFile(string title, string filter, string? initialPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            var os = Persistence.PathNorm.ToOs(initialPath);
            if (Directory.Exists(os)) dialog.InitialDirectory = os;
            else if (File.Exists(os)) dialog.FileName = System.IO.Path.GetFileName(os);
        }

        return Guarded(() => dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null);
    }

    /// <inheritdoc />
    public string? PickFolder(string title, string? initialPath)
    {
        // .NET 8 WPF ships OpenFolderDialog (IFileOpenDialog with FOS_PICKFOLDERS under the hood).
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        var os = Persistence.PathNorm.ToOs(initialPath);
        if (!string.IsNullOrWhiteSpace(os) && Directory.Exists(os))
        {
            try { dialog.InitialDirectory = os; } catch (InvalidOperationException) { /* dialog ignores it */ }
        }
        return Guarded(() => dialog.ShowDialog(_ownerProvider()) == true ? dialog.FolderName : null);
    }

    // ---- internals ----------------------------------------------------------------

    private EditOutcome ShowEditor(CatalogItem item, bool isNew)
    {
        var viewModel = new EditItemViewModel(item, isNew, _env, this);
        var window = new EditItemWindow(viewModel, _ownerProvider());
        if (!isNew)
        {
            _openEditors[item.Id] = window;
            window.Closed += (_, _) => _openEditors.Remove(item.Id);
        }

        var result = Guarded(() => window.ShowDialog() == true ? window.Outcome : default);
        return result;
    }

    /// <summary>Hold a dialog-guard lease for the duration of a modal call.</summary>
    private T Guarded<T>(Func<T> action)
    {
        using var lease = _env.Dialogs.Open();
        return action();
    }
}
