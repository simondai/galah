using System.Collections.ObjectModel;
using System.Windows.Input;
using Galah.Models;
using Galah.Services;

namespace Galah.ViewModels;

/// <summary>A labelled dropdown entry.</summary>
public sealed record OptionItem(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Settings page model. Every change is applied and committed immediately (no "保存" button per
/// spec §1); a failed commit surfaces in the warning list rather than being dropped (INV-9).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly PanelViewModel? _panel;
    private OptionItem _theme;
    private OptionItem _iconSize;
    private OptionItem _tabPosition = null!;
    private OptionItem _panelPlacement = null!;
    private OptionItem _panelMaterial = null!;
    private bool _wheelSwitchTab;
    private bool _numberKeysSwitchTab;
    private OptionItem _clickMode;
    private bool _hideOnLaunch;
    private bool _hideOnBlur;
    private bool _autoStart;
    private string _hotkeyText = "";
    private string _hotkeyHint = "";
    private string? _pendingHotkey;

    /// <summary>Theme options (design §3.3).</summary>
    public IReadOnlyList<OptionItem> ThemeOptions { get; } =
        [new(ThemeModes.System, "跟随系统"), new(ThemeModes.Light, "浅色"), new(ThemeModes.Dark, "深色")];

    /// <summary>Icon size options.</summary>
    public IReadOnlyList<OptionItem> IconSizeOptions { get; } =
        [new(IconSizes.Small, "小"), new(IconSizes.Medium, "中"), new(IconSizes.Large, "大")];

    /// <summary>Options for the tab strip placement.</summary>
    public IReadOnlyList<OptionItem> TabPositionOptions { get; } =
        [new(TabPositions.Top, "顶部(左右排列)"), new(TabPositions.Left, "左侧(上下排列)")];

    /// <summary>Panel surface material options (Sprint 4).</summary>
    public IReadOnlyList<OptionItem> PanelMaterialOptions { get; } =
    [
        new(PanelMaterials.None, "不透明"),
        new(PanelMaterials.Translucent, "半透明(可透出桌面)"),
    ];

    /// <summary>Options for where a summon places the panel (Sprint 3 / E1).</summary>
    public IReadOnlyList<OptionItem> PanelPlacementOptions { get; } =
    [
        new(PanelPositions.Remember, "记住上次位置"),
        new(PanelPositions.Mouse, "跟随鼠标"),
        new(PanelPositions.Center, "屏幕居中"),
    ];



    /// <summary>Click mode options (spec §3.2).</summary>
    public IReadOnlyList<OptionItem> ClickModeOptions { get; } =
        [new(ClickModes.Single, "单击启动"), new(ClickModes.Double, "双击启动")];

    /// <summary>Selected theme.</summary>
    public OptionItem Theme
    {
        get => _theme;
        set
        {
            if (!Set(ref _theme, value)) return;
            Commit(settings => settings.Theme = value.Value);
            ThemeService.Apply(value.Value);
        }
    }

    /// <summary>Selected icon size.</summary>
    public OptionItem IconSize
    {
        get => _iconSize;
        set
        {
            if (!Set(ref _iconSize, value)) return;
            Commit(settings => settings.IconSize = value.Value);
            _panel?.RefreshSettings();
        }
    }

    /// <summary>Tab strip placement; applied live through the panel's layout refresh.</summary>
    public OptionItem TabPosition
    {
        get => _tabPosition;
        set
        {
            if (!Set(ref _tabPosition, value)) return;
            Commit(settings => settings.TabPosition = value.Value);
            _panel?.RefreshSettings();
        }
    }

    /// <summary>Selected click mode.</summary>
    public OptionItem ClickMode
    {
        get => _clickMode;
        set
        {
            if (!Set(ref _clickMode, value)) return;
            Commit(settings => settings.ClickMode = value.Value);
            _panel?.RefreshSettings();
        }
    }

    /// <summary>Hide the panel after a successful launch (default on, spec §9-Q1).</summary>
    public bool HideOnLaunch
    {
        get => _hideOnLaunch;
        set { if (Set(ref _hideOnLaunch, value)) Commit(settings => settings.HideOnLaunch = value); }
    }

    /// <summary>Panel surface material; applied live.</summary>
    public OptionItem PanelMaterial
    {
        get => _panelMaterial;
        set
        {
            if (!Set(ref _panelMaterial, value)) return;
            Commit(settings => settings.PanelMaterial = value.Value);
            _panel?.RefreshSettings();
        }
    }

    /// <summary>Where a summon puts the panel; applied on the next show.</summary>
    public OptionItem PanelPlacement
    {
        get => _panelPlacement;
        set
        {
            if (!Set(ref _panelPlacement, value)) return;
            Commit(settings => settings.PanelPlacement = value.Value);
        }
    }

    /// <summary>Wheel at the grid edge switches category (Sprint 3 / E4).</summary>
    public bool WheelSwitchTab
    {
        get => _wheelSwitchTab;
        set { if (Set(ref _wheelSwitchTab, value)) Commit(settings => settings.WheelSwitchTab = value); }
    }

    /// <summary>Ctrl+1..9 jumps to a category (Sprint 3 / E4).</summary>
    public bool NumberKeysSwitchTab
    {
        get => _numberKeysSwitchTab;
        set { if (Set(ref _numberKeysSwitchTab, value)) Commit(settings => settings.NumberKeysSwitchTab = value); }
    }

    /// <summary>Hide the panel when it loses focus (default off, design §2.1 P-3).</summary>
    public bool HideOnBlur
    {
        get => _hideOnBlur;
        set { if (Set(ref _hideOnBlur, value)) Commit(settings => settings.HideOnBlur = value); }
    }

    /// <summary>Autostart preference; writing it also writes the HKCU Run value.</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            if (!AutoStartService.SetEnabled(value, _env.ExecutablePath, out var error))
            {
                // Roll the checkbox back so UI and registry never disagree silently.
                _autoStart = !value;
                Raise(nameof(AutoStart));
                AddNote("开机自启设置失败：" + error);
                return;
            }
            Commit(settings => settings.AutoStart = value);
            AddNote(value ? "已开启开机自启（当前用户）" : "已关闭开机自启");
        }
    }

    /// <summary>Hotkey text being edited (canonical form suggested by the parser).</summary>
    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (Set(ref _hotkeyText, value)) UpdateHotkeyHint();
        }
    }

    /// <summary>Live hint under the hotkey box: current registration or the parse result.</summary>
    public string HotkeyHint
    {
        get => _hotkeyHint;
        private set { Set(ref _hotkeyHint, value); Raise(nameof(HasHotkeyHint)); }
    }

    /// <summary>True when the warning row under the hotkey box is populated.</summary>
    public bool HasHotkeyHint => !string.IsNullOrEmpty(_hotkeyHint);

    /// <summary>The combination that is currently registered globally.</summary>
    public string RegisteredHotkey => _env.Hotkey.RegisteredText ?? "（未注册）";

    /// <summary>Warning/notice rows: boot findings, hotkey problems, failed saves.</summary>
    public ObservableCollection<string> Notes { get; } = new();

    /// <summary>Apply the edited hotkey (live re-register) and store it when it works.</summary>
    public ICommand ApplyHotkeyCommand { get; }

    /// <summary>Restore the documented default combination.</summary>
    public ICommand RestoreHotkeyCommand { get; }

    /// <summary>Raised whenever a preference changed, so the shell can re-read settings.</summary>
    public event Action? SettingsChanged;

    /// <summary>Create the settings model.</summary>
    public SettingsViewModel(AppEnvironment environment, PanelViewModel? panel)
    {
        _env = environment;
        _panel = panel;

        _theme = Find(ThemeOptions, environment.Settings.Current.Theme, 0);
        _iconSize = Find(IconSizeOptions, environment.Settings.Current.IconSize, 1);
        _tabPosition = Find(TabPositionOptions, environment.Settings.Current.TabPosition, 0);
        _panelPlacement = Find(PanelPlacementOptions, environment.Settings.Current.PanelPlacement, 0);
        _panelMaterial = Find(PanelMaterialOptions, environment.Settings.Current.PanelMaterial, 0);
        _wheelSwitchTab = environment.Settings.Current.WheelSwitchTab;
        _numberKeysSwitchTab = environment.Settings.Current.NumberKeysSwitchTab;
        _clickMode = Find(ClickModeOptions, environment.Settings.Current.ClickMode, 0);
        _hideOnLaunch = environment.Settings.Current.HideOnLaunch;
        _hideOnBlur = environment.Settings.Current.HideOnBlur;
        _autoStart = environment.Settings.Current.AutoStart;
        _hotkeyText = environment.Settings.Current.Hotkey;

        ApplyHotkeyCommand = new RelayCommand(ApplyHotkey);
        RestoreHotkeyCommand = new RelayCommand(() =>
        {
            HotkeyText = SettingsDocument.DefaultHotkey;
            ApplyHotkey();
        });

        foreach (var note in environment.BootNotes) Notes.Add(note);
        if (environment.HotkeyWarning is { } warning) Notes.Add("热键：" + warning);
        _env.HotkeyWarningChanged += RefreshHotkeyState;
        UpdateHotkeyHint();
    }

    private void ApplyHotkey()
    {
        _pendingHotkey = HotkeyText;
        var registered = _env.TryRegisterHotkey(HotkeyText);
        if (registered && _env.Hotkey.RegisteredText is { } canonical)
        {
            // Only a working combination is persisted — a rejected one must not overwrite the old value.
            Commit(settings => settings.Hotkey = canonical);
            HotkeyText = canonical;
            _pendingHotkey = null;
        }
        else if (!registered)
        {
            // Restore the previously working registration so the user is never left without a hotkey.
            if (_env.Settings.Current.Hotkey is { } previous && !string.Equals(previous, HotkeyText, StringComparison.OrdinalIgnoreCase))
                _env.TryRegisterHotkey(previous);
            AddNote("热键未生效：" + (_env.HotkeyWarning ?? "未知原因") + "（已保留原热键 " + (_env.Hotkey.RegisteredText ?? "无") + "）");
        }
        RefreshHotkeyState();
    }

    private void UpdateHotkeyHint()
    {
        if (HotkeyParser.TryParse(HotkeyText, out var spec, out var error))
            HotkeyHint = error ?? $"将注册为 {spec!.Display}";
        else
            HotkeyHint = error ?? "无法识别";
    }

    private void RefreshHotkeyState()
    {
        Notes.Clear();
        foreach (var note in _env.BootNotes) Notes.Add(note);
        if (_env.HotkeyWarning is { } warning)
        {
            Notes.Add("热键：" + warning);
            HotkeyHint = warning;
        }
        else
        {
            HotkeyHint = $"当前生效：{_env.Hotkey.RegisteredText ?? "（未注册）"}";
        }
        if (_env.Catalog.LastSaveError is { } catalogError) Notes.Add("内容库：" + catalogError);
        if (_env.Settings.LastSaveError is { } settingsError) Notes.Add("设置：" + settingsError);
        if (_env.UiState.LastSaveError is { } uiError) Notes.Add("窗口状态：" + uiError);
        Raise(nameof(RegisteredHotkey));
    }

    private void AddNote(string note)
    {
        if (!_env.BootNotes.Contains(note)) _env.BootNotes.Add(note);
        if (!Notes.Contains(note)) Notes.Add(note);
    }

    private void Commit(Action<SettingsDocument> mutate)
    {
        _env.Settings.Update(mutate);
        SettingsChanged?.Invoke();
        RefreshHotkeyState();
    }

    private static OptionItem Find(IReadOnlyList<OptionItem> options, string value, int fallbackIndex) =>
        options.FirstOrDefault(o => o.Value == value) ?? options[fallbackIndex];
}
