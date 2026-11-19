using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>What a tray click means, after decoding the shell callback.</summary>
public enum TrayClick
{
    /// <summary>Not a click we act on.</summary>
    None,

    /// <summary>Left click: show and focus the panel.</summary>
    LeftClick,

    /// <summary>Right click: ask the UI layer for the context menu.</summary>
    RightClick,
}

/// <summary>
/// Notification-area icon. The panel can start hidden, so the tray is the only always-reachable
/// way back into the app: the left button shows and focuses the panel, the right button asks the
/// UI layer for a context menu (the menu itself is presented by WPF — a native popup menu owned by
/// a hidden message window loses focus and vanishes on the first click).
/// The glyph is drawn at runtime (no asset files, no third-party packages) and the shell entry is
/// removed again on dispose so no ghost icon survives the process.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int IconId = 0x4C02;
    private const uint WmApp = 0x8000;
    private const uint TrayCallback = WmApp + 21;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const int WsPopup = unchecked((int)0x80000000);

    private readonly HwndSource _source;
    private IntPtr _icon;
    private Win32.NOTIFYICONDATAW _data;
    private bool _added;
    private bool _disposed;
    private DateTime _lastMenuShown = DateTime.MinValue;

    /// <summary>Raised on the UI thread when the user clicks the tray icon.</summary>
    public event Action? ActivateRequested;

    /// <summary>Raised on the UI thread when the user right-clicks the tray icon.</summary>
    public event Action? MenuRequested;

    /// <summary>True when the shell accepted the tray entry.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True when the shell still knows about the icon (self-check via NIM_GET).</summary>
    public bool IsRegistered => _added && QueryRegistered();

    /// <summary>
    /// Create the hidden message window and register the tray entry. Call on the UI thread.
    /// A shell that refuses the entry (no explorer.exe, policy) is not fatal — the app keeps running
    /// and the hotkey stays the primary way in.
    /// </summary>
    public TrayIconService(string iconPath)
    {
        var parameters = new HwndSourceParameters("GalahTraySink", 0, 0)
        {
            WindowStyle = WsPopup,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        try
        {
            _icon = IconArt.LoadIconHandle(iconPath);
            _data = new Win32.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
                hWnd = _source.Handle,
                uID = IconId,
                // NIF_MESSAGE is what wires uCallbackMessage to the shell: without it the icon
                // shows, clicks look fine to the user, and the app never hears a thing.
                uFlags = Win32.NifMessage | Win32.NifIcon | Win32.NifTip,
                uCallbackMessage = (int)TrayCallback,
                hIcon = _icon,
                szTip = "Galah",
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };

            _added = Win32.Shell_NotifyIconW(Win32.NimAdd, ref _data);
            var versioned = false;
            if (_added)
            {
                _data.uVersion = Win32.NotifyIconVersion4;
                versioned = Win32.Shell_NotifyIconW(Win32.NimSetVersion, ref _data);
            }
            IsAvailable = _added;
            DiagnosticsLog.Line("tray", $"图标注册={( _added ? "成功" : "失败") }，v4回调={( versioned ? "已启用" : "未启用(legacy)") }");
        }
        catch (Exception ex)
        {
            // A missing shell must never take the launcher down.
            DiagnosticsLog.Line("tray", "托盘图标不可用：" + ex.Message);
            IsAvailable = false;
        }
    }

    /// <summary>
    /// Decode a shell callback into a click kind.
    ///
    /// Three layouts are real-world traffic, captured from this very shell (galah.log):
    ///  - legacy:          lParam = the mouse message itself (0x0202), wParam = icon id
    ///  - documented v4:   notification code in the low word of wParam
    ///  - this shell (v4): lParam = (icon id &lt;&lt; 16) | (event byte &lt;&lt; 8) | flags,
    ///                    e.g. 0x4C020202 = left-button-up for icon id 0x4C02.
    /// Reading only one of them makes every click dead, so all three are decoded.
    /// </summary>
    /// <param name="wParam">wParam of the callback message.</param>
    /// <param name="lParam">lParam of the callback message.</param>
    /// <returns>The click kind.</returns>
    public static TrayClick Classify(long wParam, long lParam)
    {
        var candidates = new[]
        {
            (uint)(lParam & 0xFFFF),               // legacy: the mouse message itself
            (uint)((lParam >> 8) & 0xFF),          // this shell: event byte above the flags
            (uint)(wParam & 0xFFFF),               // documented v4: code in the low word of wParam
        };
        foreach (var code in candidates)
        {
            if (IsMouseCode(code)) return KindOf(code);
        }
        return TrayClick.None;
    }

    /// <summary>True for the mouse messages the shell sends for a tray click.</summary>
    private static bool IsMouseCode(uint code) =>
        code is WmLButtonUp or WmLButtonDblClk or WmRButtonUp or WmContextMenu;

    /// <summary>Map a mouse message to the intent.</summary>
    private static TrayClick KindOf(uint code) =>
        code is WmLButtonUp or WmLButtonDblClk ? TrayClick.LeftClick : TrayClick.RightClick;

    /// <summary>Ask the shell whether it still knows our icon.</summary>
    private bool QueryRegistered()
    {
        try
        {
            var probe = _data;
            return Win32.Shell_NotifyIconW(Win32.NimGet, ref probe);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Show a short balloon (used for "已启动" style confirmations).</summary>
    /// <param name="title">Balloon title.</param>
    /// <param name="message">Balloon body.</param>
    public void Notify(string title, string message)
    {
        if (!_added) return;
        try
        {
            var data = _data;
            data.uFlags = Win32.NifInfo;
            data.szInfoTitle = title;
            data.szInfo = message;
            data.dwInfoFlags = 0x00000001;   // NIIF_INFO
            Win32.Shell_NotifyIconW(Win32.NimModify, ref data);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Line("tray", "气泡通知失败：" + ex.Message);
        }
    }

    /// <summary>Raised when the session ends or a graceful taskkill asks the window to close.</summary>
    public event Action? CloseRequested;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_CLOSE (0x0010): a plain "taskkill" without /F sends this — honour it so the tray
        // entry is removed instead of leaving a ghost icon the user then clicks in vain.
        if (msg == 0x0010 /*WM_CLOSE*/ || msg == 0x0011 /*WM_QUERYENDSESSION*/)
        {
            handled = true;
            DiagnosticsLog.Line("tray", "收到关闭窗口消息");
            CloseRequested?.Invoke();
            // WM_QUERYENDSESSION must answer TRUE (1) or Windows blocks the logoff.
            return (IntPtr)(msg == 0x0011 ? 1 : 0);
        }

        if (msg != (int)TrayCallback)
        {
            handled = false;
            return IntPtr.Zero;
        }

        DiagnosticsLog.Line("tray", $"回调 wParam=0x{wParam.ToInt64():X} lParam=0x{lParam.ToInt64():X}");
        switch (Classify(wParam.ToInt64(), lParam.ToInt64()))
        {
            case TrayClick.LeftClick:
                handled = true;
                ActivateRequested?.Invoke();
                break;
            case TrayClick.RightClick:
                handled = true;
                // The shell can deliver both WM_RBUTTONUP and WM_CONTEXTMENU for one click.
                var now = DateTime.UtcNow;
                if ((now - _lastMenuShown).TotalMilliseconds < 500) break;
                _lastMenuShown = now;
                MenuRequested?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Remove the tray entry and release native resources (idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_added)
            {
                var data = _data;
                Win32.Shell_NotifyIconW(Win32.NimDelete, ref data);
                _added = false;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Line("tray", "移除托盘图标失败：" + ex.Message);
        }

        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
