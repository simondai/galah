using System.IO;
using System.Windows;
using System.Windows.Threading;
using Galah.Persistence;
using Galah.Services;
using Galah.Views;

namespace Galah;

/// <summary>
/// Application entry: the boot sequence of spec §5.1 in order — single instance → settings →
/// catalog → ui-state → geometry clamp → window/visibility → render. Reading never rewrites the
/// content library (INV-1/INV-4): a clean start touches no file at all.
/// </summary>
public partial class App : Application
{
    private AppEnvironment? _environment;
    private SingleInstanceService? _instance;
    private ViewDialogHost? _dialogHost;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 1 ─ single instance: a second process forwards TOGGLE and leaves (design §2.1 P-5).
        var instance = new SingleInstanceService();
        _instance = instance;
        if (!instance.TryBecomePrimary())
        {
            instance.Dispose();
            _instance = null;
            SingleInstanceService.NotifyPrimary();
            Shutdown(0);
            return;
        }
        instance.RemoteToggle += () => Dispatcher.Invoke(() => _environment?.Panel.Toggle());

        var paths = AppDataPaths.ForExecutable().EnsureCreated();
        // Captured before the stores load: loading bootstraps the files, so afterwards a first run
        // is no longer distinguishable from a normal start.
        var firstRun = !File.Exists(paths.SettingsFile) && !File.Exists(paths.UiStateFile);
        DiagnosticsLog.Initialize(paths);
        IconService.Initialize(paths);

        // 2/3/4 ─ three independent domains, each with its own fallback chain.
        var settingsStore = new SettingsStore(paths);
        var settingsOutcome = settingsStore.Load();

        var catalogStore = new CatalogStore(paths);
        var catalogOutcome = catalogStore.Load();

        var uiStateStore = new UiStateStore(paths);
        var uiOutcome = uiStateStore.Load();

        var notes = new List<string>();
        Describe(settingsOutcome, notes, "settings");
        Describe(catalogOutcome, notes, "catalog");
        Describe(uiOutcome, notes, "ui-state");

        ThemeService.Apply(settingsStore.Current.Theme);
        try { Resources["Galah.WindowIcon"] = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(IconArt.EmbeddedIconUri, UriKind.Absolute)); }
        catch (Exception ex) { DiagnosticsLog.Line("icon", "窗口图标加载失败:" + ex.Message); }

        // 5/6 ─ geometry is clamped inside the controller; the window itself is created lazily so a
        //       hidden start really runs with no window at all.
        var hotkey = new HotkeyService();
        var panel = new PanelController(settingsStore, uiStateStore, CreateSurface, Dispatcher);

        _environment = new AppEnvironment(
            paths, catalogStore, settingsStore, uiStateStore, instance, hotkey, panel,
            Environment.ProcessPath ?? AppContext.BaseDirectory);

        // The dialog host lives in the UI layer and is shared by the panel, the tray menu and the
        // exit confirmation, so every dialog has one owner and one wording.
        _dialogHost = new ViewDialogHost(_environment, FindVisiblePanel);
        _environment.ConfirmExit = () => _dialogHost!.Confirm("退出程序", "确定退出？面板将不再驻留后台。");
        _environment.ShowSettingsRequested = ShowSettings;
        _environment.ShowAddPickerRequested = ShowAddPicker;
        _environment.ShowMaintenanceRequested = ShowMaintenance;

        // The tray is the always-reachable door: the panel may start hidden, and a hidden panel
        // would otherwise leave the user with no way back in.
        var tray = new TrayIconService(Path.Combine(paths.Root, "icons", "galah.ico"));
        _environment.Tray = tray;
        // BeginInvoke, not Invoke: the close request arrives inside the tray sink's own WndProc,
        // and disposing that HwndSource mid-message is undefined behaviour. Let the message
        // return first, then shut down on the next dispatcher turn.
        tray.CloseRequested += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            DiagnosticsLog.Line("tray", "优雅退出");
            _environment.Panel.PrepareForShutdown();
            _environment.Dispose();
            Shutdown(0);
        }), System.Windows.Threading.DispatcherPriority.Send);
        if (!tray.IsAvailable)
            _environment.BootNotes.Add("通知区域图标不可用，请使用全局热键唤出面板");
        else
        {
            // Left click activates the panel (show + focus, never a blind toggle).
            tray.ActivateRequested += () => Dispatcher.Invoke(() => panel.Activate());
            // Right click asks the UI layer for the menu; a WPF popup keeps focus and looks native.
            tray.MenuRequested += () => Dispatcher.Invoke(() => TrayMenu.Show(_environment));
        }

        foreach (var note in notes) _environment.BootNotes.Add(note);

        hotkey.Triggered += () => panel.Toggle();
        if (!_environment.TryRegisterHotkey(settingsStore.Current.Hotkey))
            _environment.BootNotes.Add("热键不可用：" + _environment.HotkeyWarning);   // app keeps running (AC-03)

        if (!_environment.SyncAutoStartWithRegistry())
            _environment.BootNotes.Add("开机自启状态与注册表不一致，已尝试同步");

        if (catalogStore.IsSealed)
            _environment.BootNotes.Add("内容库为只读保护状态，本次会话的改动不会写入 catalog.json");

        DiagnosticsLog.Write(_environment.BootNotes, "boot");
        instance.StartListening(Dispatcher);

        // Startup policy (product decision 2026-11-18): launching Galah.exe ALWAYS lands in the
        // tray only - the panel never opens by itself. The one exception is reachability: if the
        // tray entry or the global hotkey could not be created, the panel is shown so the app is
        // never undiscoverable. First run gets a tray balloon instead of a window.
        var reachable = _environment.Tray?.IsAvailable == true && hotkey.RegisteredText is not null;
        var showPanel = !reachable;
        DiagnosticsLog.Line("boot", $"firstRun={firstRun} reachable={reachable} showPanel={showPanel}");
        if (showPanel)
        {
            panel.Show();
            _environment.Toast.Show("全局热键与托盘图标均不可用,已直接显示面板", 8000);
        }
        else if (firstRun)
        {
            _environment.Tray?.Notify("Galah", "已在后台运行 · 按 " + (hotkey.RegisteredText ?? "Alt+Space") + " 或点击托盘图标唤出面板");
        }
        if (_environment.BootNotes.Count > 0 && showPanel) _environment.Toast.Show(string.Join("；", _environment.BootNotes), 6000);
    }

    /// <summary>Build the panel surface plus its dialog host (lazy: only when the panel is shown).</summary>
    private IPanelSurface CreateSurface()
    {
        var environment = _environment ?? throw new InvalidOperationException("环境尚未初始化");
        var host = _dialogHost ?? new ViewDialogHost(environment, FindVisiblePanel);
        return new PanelWindow(environment, host);
    }

    /// <summary>Open the add-shortcut picker (tray menu route); commits flow through the panel VM.</summary>
    private void ShowAddPicker()
    {
        // The panel may never have been created (startup is tray-only by design). Showing it is
        // also the right behaviour here: a user asking to add a shortcut wants to see the result.
        _environment.Panel.Show();
        var viewModel = Windows.OfType<Galah.Views.PanelWindow>().FirstOrDefault()?.ViewModel;
        if (viewModel is null)
        {
            Galah.Services.DiagnosticsLog.Line("picker", "no panel view model - picker refused");
            return;
        }
        _dialogHost?.ShowAddPicker(viewModel.AddFromSeed);
    }

    /// <summary>Open the maintenance/migration window (chrome menu route).</summary>
    private void ShowMaintenance()
    {
        if (_dialogHost is null) return;
        _dialogHost.ShowMaintenance();
    }

    /// <summary>Open the settings window (tray menu route).</summary>
    private void ShowSettings()
    {
        _dialogHost?.ShowSettings();
    }

    private Window? FindVisiblePanel()
    {
        // Only a VISIBLE panel may own a modal dialog: ShowDialog against an invisible owner
        // throws InvalidOperationException, which used to abort the tray exit path silently.
        // Application.Windows is a non-generic list, so the panels are filtered with LINQ.
        return Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is PanelWindow or SettingsWindow);
    }

    /// <summary>Translate a load outcome into a user-facing note (spec §5.1 steps 2-4).</summary>
    private static void Describe<T>(LoadOutcome<T> outcome, List<string> notes, string domain) where T : class
    {
        switch (outcome.Status)
        {
            case LoadStatus.RecoveredFromBackup:
                notes.Add($"{domain} 主文件损坏，已从备份恢复");
                break;
            case LoadStatus.StartedEmpty:
                notes.Add($"{domain} 无法恢复，已从默认值开始");
                break;
            case LoadStatus.RefusedNewerSchema:
                notes.Add($"{domain} 版本较新（schema 高于当前程序），本程序拒绝写入以保护数据");
                break;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI fault must not lose the library: report it, keep the process, leave the data as it is.
        DiagnosticsLog.Line("error", e.Exception.GetType().Name + ": " + e.Exception.Message);
        _environment?.Toast.Show("界面出现异常，已忽略：" + e.Exception.Message, 5000);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _environment?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
