using System.Windows.Threading;
using Galah.Persistence;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>
/// The surface the controller drives. Implemented by the panel window; keeps the controller free of
/// view types (design §4: PanelController is the only writer of window state).
/// </summary>
public interface IPanelSurface
{
    /// <summary>HWND of the surface (zero before first show).</summary>
    IntPtr Handle { get; }

    /// <summary>Make the surface visible (no-op activation details handled by the controller).</summary>
    void ShowSurface();

    /// <summary>Hide the surface without destroying it.</summary>
    void HideSurface();

    /// <summary>Position and size the surface in DIPs.</summary>
    void SetGeometry(double x, double y, double width, double height);

    /// <summary>Current geometry in DIPs.</summary>
    (double X, double Y, double Width, double Height) GetGeometry();

    /// <summary>True while the surface is maximised (its restore rectangle must not be overwritten).</summary>
    bool IsMaximized { get; }

    /// <summary>Bring the surface to the foreground (used by the tray activation).</summary>
    void ActivateSurface();

    /// <summary>Apply the persisted maximised state to the surface.</summary>
    /// <param name="maximized">True to maximise, false to restore.</param>
    void SetMaximized(bool maximized);

    /// <summary>
    /// Let the surface really close (the ✕ button and Alt+F4 normally mean "hide", design §2.1 P-6);
    /// called only on the explicit quit path.
    /// </summary>
    void ReleaseForShutdown();
}

/// <summary>
/// Owns panel visibility and ui-state persistence: the single writer of window state.
/// Visibility changes commit immediately (spec §1), geometry commits are debounced by 500 ms.
/// </summary>
public sealed class PanelController
{
    /// <summary>Geometry write debounce (task B).</summary>
    public const int GeometryDebounceMs = 500;

    private readonly SettingsStore _settings;
    private readonly UiStateStore _uiState;
    private readonly Func<IPanelSurface> _surfaceFactory;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _geometryTimer;
    private IPanelSurface? _surface;
    private bool _applyingGeometry;
    private bool _isVisible;

    /// <summary>True while the panel surface is shown.</summary>
    public bool IsVisible => _isVisible;

    /// <summary>Raised after visibility changed, so views can react without owning the state.</summary>
    public event Action<bool>? VisibilityChanged;

    /// <summary>Create the controller.</summary>
    /// <param name="settings">Preference store (read for debounce sizing only).</param>
    /// <param name="uiState">ui-state store — the only persistence this class touches.</param>
    /// <param name="surfaceFactory">Lazy factory for the panel surface.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    public PanelController(SettingsStore settings, UiStateStore uiState, Func<IPanelSurface> surfaceFactory, Dispatcher dispatcher)
    {
        _settings = settings;
        _uiState = uiState;
        _surfaceFactory = surfaceFactory;
        _dispatcher = dispatcher;
        _geometryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GeometryDebounceMs),
        };
        _geometryTimer.Tick += (_, _) =>
        {
            _geometryTimer.Stop();
            CommitGeometryCore();
        };
    }

    /// <summary>Current surface, null while the app is resident without a window.</summary>
    public IPanelSurface? Surface => _surface;

    /// <summary>Show the panel, restoring/clamping geometry, and take foreground (spec §5.1-6, P-2).</summary>
    public void Show()
    {
        _surface ??= _surfaceFactory();
        ApplyPlacement();
        _surface.ShowSurface();
        _isVisible = true;
        VisibilityChanged?.Invoke(true);
        ActivateForeground();
        _uiState.SetVisibility(true);
    }

    /// <summary>Hide the panel (✕, Esc, hotkey, hideOnLaunch). Never quits the process (P-6).</summary>
    public void Hide()
    {
        if (_surface is null || !_isVisible) return;
        CommitGeometryCore();
        _surface.HideSurface();
        _isVisible = false;
        VisibilityChanged?.Invoke(false);
        _uiState.SetVisibility(false);
    }

    /// <summary>Toggle visibility — the hotkey and the remote TOGGLE command both land here (P-3/P-5).</summary>
    public void Toggle()
    {
        if (_isVisible) Hide();
        else Show();
    }

    /// <summary>
    /// Show the panel if it is hidden and bring it to the foreground. This is what the tray icon
    /// does on a left click: an activation, not a blind toggle, so the panel never disappears
    /// because it happened to be open.
    /// </summary>
    public void Activate()
    {
        if (!_isVisible) Show();
        _surface?.ActivateSurface();
    }

    /// <summary>
    /// Called by the surface when the user maximises or restores the window. The flag is a user
    /// action, so it is committed right away; an unchanged value costs no write.
    /// </summary>
    /// <param name="maximized">True when the window is maximised.</param>
    public void NotifyMaximizedChanged(bool maximized) => _uiState.SetMaximized(maximized);

    /// <summary>Called by the surface when the user moved/resized it; debounces the write.</summary>
    public void NotifyGeometryChanged()
    {
        if (_applyingGeometry || _surface is null) return;
        _geometryTimer.Stop();
        _geometryTimer.Start();
    }

    /// <summary>Immediate geometry commit (used on hide and on application exit, design §3.5).</summary>
    public void Flush()
    {
        _geometryTimer.Stop();
        if (_isVisible) CommitGeometryNow();
    }

    /// <summary>
    /// Explicit quit path: commit ui-state, permit the window to close, then let the caller shut the
    /// application down. Visibility is left as it is so the next start restores the same branch
    /// (spec §5.2: 面板开着退出 → 重启弹面板).
    /// </summary>
    public void PrepareForShutdown()
    {
        _geometryTimer.Stop();
        CommitGeometryCore();
        _surface?.ReleaseForShutdown();
    }

    /// <summary>
    /// Persist the surface's current geometry immediately (resize gesture end, shutdown).
    /// </summary>
    public void CommitGeometryNow() => CommitGeometryCore();

    private void CommitGeometryCore()
    {
        if (_surface is null) return;
        // A maximised window reports the screen bounds; writing those would destroy the restore
        // rectangle, so the geometry stays untouched until the window is restored.
        if (_surface.IsMaximized) return;
        var (x, y, w, h) = _surface.GetGeometry();
        if (!double.IsFinite(x) || !double.IsFinite(y) || w <= 0 || h <= 0) return;
        _uiState.UpdateGeometry(x, y, w, h);
        _uiState.CommitGeometry();
    }

    /// <summary>
    /// Place the panel for this summon according to the 面板位置 preference (Sprint 3 / E1), then
    /// keep the remembered size. Remember mode is the shipped default and is byte-identical to
    /// the previous behaviour; mouse/centre recompute only the position.
    /// </summary>
    private void ApplyPlacement()
    {
        var mode = _settings.Current.PanelPlacement;
        if (mode == Models.PanelPositions.Remember)
        {
            ApplyGeometry();
            return;
        }

        var window = _uiState.Current.Window;
        var scale = ResolveScale();
        var cursor = TryCursor(scale);
        var area = PanelPlacement.AreaFor(cursor);
        var width = window.Width;
        var height = window.Height;
        var (x, y) = PanelPlacement.Resolve(mode, null, cursor, area, width, height);

        // the resolved point is device pixels; the surface speaks DIPs of its own monitor
        var (cx, cy) = (x / scale, y / scale);
        var (dw, dh) = (width / scale, height / scale);

        _applyingGeometry = true;
        try
        {
            _surface!.SetGeometry(cx, cy, dw, dh);
            _uiState.UpdateGeometry(cx, cy, dw, dh);
            _surface.SetMaximized(window.Maximized);
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    /// <summary>Cursor in device pixels, or null when the session refuses to report one (RDP).</summary>
    private static (double X, double Y)? TryCursor(double scale)
    {
        if (Interop.Win32.GetCursorPos(out var point))
            return (point.X, point.Y);
        return null;
    }

    private void ApplyGeometry()
    {
        var window = _uiState.Current.Window;
        var scale = ResolveScale();
        var (x, y, w, h) = MonitorGeometry.ClampToScreens(
            window.HasPosition ? window.X : null, window.HasPosition ? window.Y : null,
            window.Width, window.Height, scale);
        _applyingGeometry = true;
        try
        {
            _surface!.SetGeometry(x, y, w, h);
            // Record the reachable position we ended up with (e.g. after a monitor was unplugged).
            _uiState.UpdateGeometry(x, y, w, h);
            // The maximised flag is applied last so the restore rectangle stays the stored one.
            _surface.SetMaximized(window.Maximized);
        }
        finally
        {
            _applyingGeometry = false;
        }
    }

    private static double ResolveScale()
    {
        // Before the first show WPF uses the system DPI; after that the surface's own scale is used
        // when we capture geometry, so mixed-DPI sessions stay within one DIP space per window.
        return MonitorGeometry.SystemScale();
    }

    private void ActivateForeground()
    {
        var handle = _surface?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        if (Win32.GetForegroundWindow() == handle) return;
        // SetForegroundWindow is the documented way to take the foreground from a hotkey-driven app;
        // WPF's Activate() alone is often refused when another process owns the foreground.
        Win32.SetForegroundWindow(handle);
        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { });
    }
}
