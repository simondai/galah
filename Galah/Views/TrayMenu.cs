using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Linq;
using Galah.Services;

namespace Galah.Views;

/// <summary>
/// The tray context menu. It is a WPF popup placed at the cursor instead of a native Win32 menu:
/// a native popup owned by the hidden tray message window cannot take foreground, so it closes on
/// the first click. A WPF popup keeps the application's own look and behaves like the rest of the UI.
/// </summary>
public static class TrayMenu
{
    /// <summary>
    /// Show the tray menu at the current cursor position.
    /// </summary>
    /// <param name="environment">Wired environment (panel, settings, exit path).</param>
    private static ContextMenu? _open;

    public static void Show(AppEnvironment environment)
    {
        // One menu at a time: a menu opened by a synthetic/UIA invoke is not dismissed
        // automatically, and stacking popups hijacks every later global UIA search.
        if (_open is { IsOpen: true })
        {
            _open.IsOpen = false;
        }
        // AbsolutePoint ignores the offsets without a PlacementTarget and drops the menu
        // at (0,0); Absolute is measured from the screen origin, which with a DPI-converted
        // cursor position puts the menu exactly under the user's mouse.
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Absolute,
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, menu)) _open = null;
        };

        var show = new MenuItem { Header = "显示面板" };
        show.Click += (_, _) => environment.Panel.Activate();

        var hide = new MenuItem { Header = "隐藏面板" };
        hide.Click += (_, _) => environment.Panel.Hide();

        var add = new MenuItem { Header = "添加快捷方式…" };
        add.Click += (_, _) => environment.ShowAddPicker();

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => environment.ShowSettings();

        var exit = new MenuItem { Header = "退出程序" };
        // Defer past the popup's own teardown: opening a modal directly inside a ContextMenu
        // click races the popup's capture release and leaves the dialog stranded in the background.
        exit.Click += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(
            environment.RequestExit, System.Windows.Threading.DispatcherPriority.Background);

        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(add);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(exit);

        _open = menu;   // remember it so the next Show closes THIS menu, not nothing

        // Popup host: with the tray-only startup there may be no shown window at all, and a
        // ContextMenu without an HWND host never renders (see TrayAnchorWindow).
        if (System.Windows.Application.Current?.Windows.OfType<Window>().Any(w => w.IsVisible) != true)
        {
            menu.PlacementTarget = TrayAnchorWindow.Ensure();
        }

        var cursor = CursorPosition();
        var scale = Win32Cursor.Scale;
        menu.HorizontalOffset = cursor.X / scale;
        menu.VerticalOffset = cursor.Y / scale;
        menu.IsOpen = true;
        Galah.Services.DiagnosticsLog.Line("tray", "菜单弹出 items=" + menu.Items.Count + " open=" + menu.IsOpen);
    }

    /// <summary>
    /// Position of the last input message, in physical screen pixels. GetCursorPos fails
    /// outright on some RDP sessions (returns false), so the menu anchor comes from
    /// GetMessagePos instead: the shell delivered the tray click microseconds earlier,
    /// so this IS the user's click point.
    /// </summary>
    private static Point CursorPosition()
    {
        var pos = Services.Interop.Win32.GetMessagePos();
        return new Point((int)(pos & 0xFFFF), (int)((pos >> 16) & 0xFFFF));
    }
}

/// <summary>Cursor position helper (kept separate so the view layer owns no P/Invoke).</summary>
internal static class Win32Cursor
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>System DPI scale (1.0 at 96 DPI) for physical-pixel to DIP conversion.</summary>
    public static double Scale => GetDpiForSystem() / 96.0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>Read the cursor position.</summary>
    /// <param name="x">X coordinate.</param>
    /// <param name="y">Y coordinate.</param>
    /// <returns>True when the position could be read.</returns>
    public static bool TryGetPosition(out double x, out double y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }
}
