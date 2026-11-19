using Galah.Persistence;

namespace Galah.Services;

/// <summary>
/// Composition root: the single place where stores and services are wired together. Views read it
/// through <see cref="Current"/>; services never look at each other sideways (design §4 dependency
/// direction UI → services → persistence/win32).
/// </summary>
public sealed class AppEnvironment : IDisposable
{
    /// <summary>Active environment (null before boot and in headless tests).</summary>
    public static AppEnvironment? Current { get; private set; }

    /// <summary>Data locations (GalahData next to the executable).</summary>
    public AppDataPaths Paths { get; }

    public CatalogStore Catalog { get; }
    public SettingsStore Settings { get; }
    public UiStateStore UiState { get; }
    public ToastService Toast { get; }
    public DialogGuard Dialogs { get; }
    public SingleInstanceService Instance { get; }
    public HotkeyService Hotkey { get; }
    public PanelController Panel { get; }
    public TrayIconService? Tray { get; internal set; }

    /// <summary>Path of the running executable (used by the autostart value and by the window title).</summary>
    public string ExecutablePath { get; }

    /// <summary>Non-fatal findings from the boot sequence, surfaced on the settings page (INV-9 spirit).</summary>
    public List<string> BootNotes { get; } = new();

    /// <summary>Current hotkey registration problem, null when the hotkey is live.</summary>
    public string? HotkeyWarning { get; private set; }

    /// <summary>Raised when the hotkey problem appears/clears so the settings page can refresh.</summary>
    public event Action? HotkeyWarningChanged;

    /// <summary>Build the environment over already-loaded stores.</summary>
    public AppEnvironment(
        AppDataPaths paths,
        CatalogStore catalog,
        SettingsStore settings,
        UiStateStore uiState,
        SingleInstanceService instance,
        HotkeyService hotkey,
        PanelController panel,
        string executablePath)
    {
        Paths = paths;
        Catalog = catalog;
        Settings = settings;
        UiState = uiState;
        Instance = instance;
        Hotkey = hotkey;
        Panel = panel;
        ExecutablePath = executablePath;
        Toast = new ToastService();
        Dialogs = new DialogGuard();
        Current = this;
    }

    /// <summary>
    /// Register (or re-register) the global hotkey and remember the outcome as a warning row instead
    /// of failing (AC-03: the app must keep running when the combination is taken).
    /// </summary>
    /// <param name="keyString">Combination in canonical or user form.</param>
    /// <returns>True when live.</returns>
    public bool TryRegisterHotkey(string keyString)
    {
        var ok = Hotkey.TryRegister(keyString, out var error);
        SetHotkeyWarning(ok ? null : error);
        return ok;
    }

    /// <summary>Update the warning state and notify listeners.</summary>
    /// <param name="warning">Message or null.</param>
    public void SetHotkeyWarning(string? warning)
    {
        if (string.Equals(HotkeyWarning, warning, StringComparison.Ordinal)) return;
        HotkeyWarning = warning;
        HotkeyWarningChanged?.Invoke();
    }

    /// <summary>
    /// Re-assert the HKCU Run value so the registry matches the stored preference. Registry-only:
    /// it never rewrites settings.json (INV-1).
    /// </summary>
    /// <returns>True when registry and preference agree.</returns>
    public bool SyncAutoStartWithRegistry()
    {
        var wanted = Settings.Current.AutoStart;
        var actual = AutoStartService.IsEnabled(ExecutablePath);
        if (wanted == actual) return true;
        if (AutoStartService.SetEnabled(wanted, ExecutablePath, out var error)) return true;
        BootNotes.Add("开机自启同步失败：" + error);
        return false;
    }

    /// <summary>
    /// Confirmation hook for exit requests. The UI layer installs it so that every exit route
    /// (panel menu, tray menu) shares one wording and one code path.
    /// </summary>
    public Func<bool>? ConfirmExit { get; set; }

    /// <summary>Opens the settings window; installed by the UI layer.</summary>
    public Action? ShowSettingsRequested { get; set; }

    /// <summary>Request the settings window (tray menu route).</summary>
    public void ShowSettings() => ShowSettingsRequested?.Invoke();

    /// <summary>Set by App: opens the add-shortcut picker through the panel view model.</summary>
    public Action? ShowAddPickerRequested { get; set; }

    /// <summary>Ask the UI layer to open the add-shortcut picker (tray-menu route).</summary>
    public void ShowAddPicker() => ShowAddPickerRequested?.Invoke();

    /// <summary>Set by App: opens the maintenance/migration window.</summary>
    public Action? ShowMaintenanceRequested { get; set; }

    /// <summary>Ask the UI layer to open maintenance (Sprint 2).</summary>
    public void ShowMaintenance() => ShowMaintenanceRequested?.Invoke();

    /// <summary>
    /// Exit the launcher: ask for confirmation, flush state, release native resources, shut down
    /// (design §2.1 P-6). Safe to call more than once.
    /// </summary>
    public void RequestExit()
    {
        DiagnosticsLog.Line("exit", "RequestExit begin");
        if (ConfirmExit is not null && !ConfirmExit()) { DiagnosticsLog.Line("exit", "confirm returned false, staying"); return; }
        DiagnosticsLog.Line("exit", "confirmed");
        Panel.PrepareForShutdown();
        DiagnosticsLog.Line("exit", "prepared");
        Dispose();
        DiagnosticsLog.Line("exit", "disposed");
        System.Windows.Application.Current.Shutdown();
        DiagnosticsLog.Line("exit", "shutdown called");
    }

    /// <summary>Release native resources on shutdown (idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Panel.Flush();
        Tray?.Dispose();
        Hotkey.Dispose();
        Instance.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    private bool _disposed;
}
