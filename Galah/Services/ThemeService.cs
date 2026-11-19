using System.Windows;
using Microsoft.Win32;
using Galah.Models;

namespace Galah.Services;

/// <summary>
/// Swaps one of two self-drawn resource dictionaries at application scope (design §1.1 S10).
/// "system" reads the well-known AppsUseLightTheme preference — a read-only registry access.
/// </summary>
public static class ThemeService
{
    private const string LightSource = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/Themes/Dark.xaml";

    /// <summary>Currently applied concrete theme ("light" or "dark").</summary>
    public static string Applied { get; private set; } = ThemeModes.Light;

    /// <summary>
    /// Apply a theme preference ("light" | "dark" | "system") immediately — every view binds theme
    /// colors through DynamicResource so no window has to be recreated (AC-14).
    /// </summary>
    /// <param name="preference">User preference value.</param>
    public static void Apply(string preference)
    {
        var concrete = Resolve(preference);
        if (concrete == Applied && Application.Current?.Resources.MergedDictionaries.Count > 0) return;
        Applied = concrete;

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null) return;

        var uri = new Uri(concrete == ThemeModes.Dark ? DarkSource : LightSource, UriKind.Absolute);
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source is not null &&
                dictionaries[i].Source!.OriginalString.StartsWith("pack://application:,,,/Themes/", StringComparison.OrdinalIgnoreCase))
                dictionaries.RemoveAt(i);
        }
        dictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>Resolve "system" to a concrete theme.</summary>
    /// <param name="preference">User preference value.</param>
    public static string Resolve(string preference) => preference switch
    {
        ThemeModes.Dark => ThemeModes.Dark,
        ThemeModes.Light => ThemeModes.Light,
        _ => SystemPrefersDark() ? ThemeModes.Dark : ThemeModes.Light,
    };

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int number && number == 0;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }
}
