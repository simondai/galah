using System.Windows;

namespace Galah.Views;

/// <summary>
/// Freezable that carries a data context into visual trees that are not connected to the window
/// (context menus live in their own popup root, so ordinary {Binding} cannot reach the panel view
/// model). Used as a StaticResource source for menu commands.
/// </summary>
public sealed class BindingProxy : Freezable
{
    /// <summary>The object to expose (normally the panel view model).</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    /// <inheritdoc />
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
