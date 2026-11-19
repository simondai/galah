using System.Windows;
using Microsoft.Win32;
using Galah.Models;

namespace Galah.Services;

/// <summary>
/// Swaps one of two self-drawn resource dictionaries at application scope (design §1.1 S10).
/// "system" reads the well-known AppsUseLightTheme preference — a read-only registry access.
/// While a "system" preference is active, Microsoft.Win32.SystemEvents.UserPreferenceChanged
/// re-applies the resolved theme live (dark mode toggles take effect without a restart).
/// </summary>
public static class ThemeService
{
    private const string LightSource = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/Themes/Dark.xaml";

    /// <summary>Currently applied concrete theme ("light" or "dark").</summary>
    public static string Applied { get; private set; } = ThemeModes.Light;

    private static string _preference = ThemeModes.System;
    private static bool _listening;

    /// <summary>
    /// Apply a theme preference ("light" | "dark" | "system") immediately — every view binds theme
    /// colors through DynamicResource so no window has to be recreated (AC-14).
    /// </summary>
    /// <param name="preference">User preference value.</param>
    public static void Apply(string preference)
    {
        _preference = ThemeModes.All.Contains(preference) ? preference : ThemeModes.System;
        var concrete = Resolve(_preference);
        if (concrete == Applied && Application.Current?.Resources.MergedDictionaries.Count > 0)
        {
            EnsureSystemListener();
            return;
        }
        Applied = concrete;

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null) return;

        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString.StartsWith("pack://application:,,,/Themes/", StringComparison.OrdinalIgnoreCase) == true)
                dictionaries.RemoveAt(i);
        }
        var uri = new Uri(concrete == ThemeModes.Dark ? DarkSource : LightSource, UriKind.Absolute);
        dictionaries.Insert(0, new ResourceDictionary { Source = uri });

        EnsureSystemListener();
    }

    /// <summary>Resolve "system" to a concrete theme.</summary>
    /// <param name="preference">User preference value.</param>
    public static string Resolve(string preference) => preference switch
    {
        ThemeModes.Dark => ThemeModes.Dark,
        ThemeModes.Light => ThemeModes.Light,
        _ => SystemPrefersDark() ? ThemeModes.Dark : ThemeModes.Light,
    };

    /// <summary>
    /// One live listener for the whole app lifetime while the preference is "system": Windows'
    /// dark-mode flip (AppsUseLightTheme changes) re-resolves and re-applies on the UI thread.
    /// Removed for explicit light/dark preferences — the user pinned a concrete theme.
    /// </summary>
    private static void EnsureSystemListener()
    {
        if (Application.Current is null) return;

        var shouldListen = _preference == ThemeModes.System;
        if (shouldListen && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        else if (!shouldListen && _listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        // The event fires on a Win32 thread; dictionary swaps must run on the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var concrete = SystemPrefersDark() ? ThemeModes.Dark : ThemeModes.Light;
            if (concrete == Applied) return;
            Applied = concrete;
            var dictionaries = Application.Current?.Resources.MergedDictionaries;
            if (dictionaries is null) return;
            for (var i = dictionaries.Count - 1; i >= 0; i--)
            {
                if (dictionaries[i].Source?.OriginalString.StartsWith("pack://application:,,,/Themes/", StringComparison.OrdinalIgnoreCase) == true)
                    dictionaries.RemoveAt(i);
            }
            var uri = new Uri(concrete == ThemeModes.Dark ? DarkSource : LightSource, UriKind.Absolute);
            dictionaries.Insert(0, new ResourceDictionary { Source = uri });
        });
    }

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
