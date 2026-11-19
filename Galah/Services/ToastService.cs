using System.ComponentModel;
using System.Windows.Threading;
using Galah.Models;

namespace Galah.Services;

/// <summary>
/// Self-drawn notification strip (red line: no shipped assets, no third-party toast library).
/// It is presentation-only — nothing here touches persistence.
/// </summary>
public sealed class ToastService : INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer;
    private string _message = "";

    /// <summary>True while the strip is visible.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Current message.</summary>
    public string Message
    {
        get => _message;
        private set { _message = value; OnPropertyChanged(nameof(Message)); }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Create the service (must be constructed on the UI thread).</summary>
    public ToastService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _timer.Tick += (_, _) => Close();
    }

    /// <summary>Show a message for a while (last call wins).</summary>
    /// <param name="text">Message text.</param>
    /// <param name="durationMs">How long to keep it visible.</param>
    public void Show(string text, int durationMs = 3000)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(800, durationMs));
        Message = text;
        IsOpen = true;
        OnPropertyChanged(nameof(IsOpen));
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Hide the strip now.</summary>
    public void Close()
    {
        _timer.Stop();
        if (!IsOpen) return;
        IsOpen = false;
        OnPropertyChanged(nameof(IsOpen));
    }

    /// <summary>Render a launch failure the way spec §3.4 asks: item name + target path.</summary>
    /// <param name="item">The item that failed to start.</param>
    /// <param name="result">Launcher outcome.</param>
    public void ShowLaunchFailure(CatalogItem item, LaunchResult result)
    {
        var message = result.Message ?? "启动失败";
        Show($"「{item.Name}」启动失败：{message}（目标：{item.Target}）", 4200);
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
