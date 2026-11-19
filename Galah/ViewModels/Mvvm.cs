using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Galah.ViewModels;

/// <summary>Tiny INotifyPropertyChanged base (no framework, no NuGet).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise a change notification.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Assign and notify when the value differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Minimal command implementation with an optional CanExecute gate.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Create a command.</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>Create a parameterless command.</summary>
    public RelayCommand(Action execute) : this(_ => execute()) { }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);
}
