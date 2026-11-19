using System.Windows;
using System.Windows.Input;

namespace Galah.Views;

/// <summary>Small self-drawn prompt/confirm window used by the tab commands.</summary>
public sealed partial class PromptWindow : Window
{
    protected override void OnClosed(System.EventArgs e)
    {
        Services.DiagnosticsLog.Line("prompt", "closed, result=" + (DialogResult.HasValue ? DialogResult.Value.ToString() : "<null>"));
        base.OnClosed(e);
    }

    private PromptWindow(string title, string message, string? initial)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (initial is not null)
        {
            InputBox.Visibility = Visibility.Visible;
            InputBox.Text = initial;
            InputBox.SelectAll();
            InputBox.Focus();
        }
        else
        {
            OkButton.Focus();
        }
    }

    /// <summary>Ask for a single line of text.</summary>
    /// <param name="owner">Owner window (may be null).</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="label">Question text.</param>
    /// <param name="initial">Pre-filled value.</param>
    /// <returns>The entered text, or null when canceled.</returns>
    public static string? AskText(Window? owner, string title, string label, string? initial)
    {
        var window = new PromptWindow(title, label, initial);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ForceForeground(window);
        return window.ShowDialog() == true ? window.InputBox.Text : null;
    }

    /// <summary>Ask a yes/no question.</summary>
    /// <param name="owner">Owner window (may be null).</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Question text.</param>
    /// <returns>True when the user confirmed.</returns>
    public static bool Confirm(Window? owner, string title, string message)
    {
        var window = new PromptWindow(title, message, null);
        // An invisible owner would make ShowDialog throw; a modal without an owner is always safe.
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ForceForeground(window);
        return window.ShowDialog() == true;
    }

    /// <summary>
    /// A tray-menu interaction does not make us the foreground process, and Windows refuses to put
    /// our windows in front for a background app. Without this the modal opens BEHIND everything:
    /// the app blocks waiting for input on a dialog the user cannot see. Topmost + an explicit
    /// SetForegroundWindow on load is the documented escape hatch.
    /// </summary>
    private static void ForceForeground(Window window)
    {
        window.Topmost = true;
        window.ShowActivated = true;
        window.Loaded += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                Services.Interop.Win32.SetForegroundWindow(handle);
                window.Activate();
            }
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Services.DiagnosticsLog.Line("prompt", "ok clicked");
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Services.DiagnosticsLog.Line("prompt", "cancel clicked");
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Services.DiagnosticsLog.Line("prompt", "esc pressed");
        DialogResult = false;
        Close();
        e.Handled = true;
    }
}
