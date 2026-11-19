using System.Linq;
using System.Windows;

namespace Galah.Views;

/// <summary>
/// A zero-footprint host window for the tray context menu.
///
/// WPF popups need an HWND to attach to. When Galah starts tray-only (the shipped startup policy)
/// no window has ever been shown, so a tray right-click silently produces nothing: the menu
/// reports IsOpen=true, its window is never created, and the user sees no menu at all. This
/// anchor is created on the first tray interaction - fully transparent, 1x1, parked far off
/// screen, never activated, never in the taskbar or Alt+Tab - and it gives the popup a host.
/// </summary>
public sealed class TrayAnchorWindow : Window
{
    private TrayAnchorWindow()
    {
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Opacity = 0;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "GalahAnchor";
    }

    /// <summary>Existing anchor, or a newly shown one.</summary>
    public static Window Ensure()
    {
        var app = Application.Current;
        if (app is null) return null!;

        var existing = app.Windows.OfType<TrayAnchorWindow>().FirstOrDefault();
        if (existing is not null) return existing;

        var anchor = new TrayAnchorWindow();
        anchor.Show();
        return anchor;
    }
}
