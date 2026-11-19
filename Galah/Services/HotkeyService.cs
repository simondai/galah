using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Galah.Services.Interop;

namespace Galah.Services;

/// <summary>
/// Global hotkey owner. Runs on a hidden 0×0 message window so it survives panel hide/show and never
/// depends on a visible window (design §1.1 S1, §4: "注册失败只告警不崩").
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4C01;
    private const int WsPopup = unchecked((int)0x80000000);

    private readonly HwndSource _source;
    private string? _registeredText;

    /// <summary>Raised on the UI thread when the registered combination is pressed.</summary>
    public event Action? Triggered;

    /// <summary>The combination currently registered (canonical string), null when none.</summary>
    public string? RegisteredText => _registeredText;

    /// <summary>Creates the hidden message window; call on the UI thread.</summary>
    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("GalahHotkeySink", 0, 0)
        {
            WindowStyle = WsPopup,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Register (or re-register) a hotkey from its string form. Failure keeps the app running and
    /// reports the reason so the settings page can show a warning row (spec §5.1 step 2, AC-03).
    /// </summary>
    /// <param name="keyString">Combination such as "alt+space".</param>
    /// <param name="error">Chinese reason text when the call returns false.</param>
    /// <returns>True when the hotkey is live.</returns>
    public bool TryRegister(string keyString, out string? error)
    {
        Unregister();
        if (!HotkeyParser.TryParse(keyString, out var spec, out error))
            return false;

        try
        {
            if (Win32.RegisterHotKey(_source.Handle, HotkeyId, spec!.Modifiers, spec.VirtualKey))
            {
                _registeredText = spec.Text;
                error = null;
                return true;
            }
            var win32 = Marshal.GetLastWin32Error();
            error = win32 == Win32Error.HotkeyAlreadyRegistered
                ? $"热键 {spec.Display} 已被其他程序占用"
                : $"热键 {spec.Display} 注册失败（Win32 错误 {win32}）";
            return false;
        }
        catch (Exception ex)
        {
            error = "热键注册异常：" + ex.Message;
            return false;
        }
    }

    /// <summary>Release the current registration (safe to call when nothing is registered).</summary>
    public void Unregister()
    {
        if (_registeredText is null) return;
        try { Win32.UnregisterHotKey(_source.Handle, HotkeyId); }
        catch (Exception) { /* best effort */ }
        _registeredText = null;
    }

    /// <summary>Destroy the hidden window.</summary>
    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Triggered?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
