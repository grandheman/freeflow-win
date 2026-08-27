using System;
using System.Windows;
using Microsoft.Win32;

namespace FreeFlow.App.UI;

/// <summary>
/// Follows the Windows app theme.
/// </summary>
/// <remarks>
/// The overlay floats over other applications, so a theme that disagrees with the
/// desktop reads as a foreign object rather than part of the system. Following the
/// OS setting is the whole point; there is deliberately no in-app theme override.
/// </remarks>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Uri LightThemeUri =
        new("pack://application:,,,/UI/Theme.Light.xaml", UriKind.Absolute);

    public static bool IsLightTheme
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                // AppsUseLightTheme is 1 for light, 0 for dark. A missing value means
                // light, which is the historical Windows default.
                return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public static void ApplySystemTheme(Application application)
    {
        Apply(application, IsLightTheme);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category != UserPreferenceCategory.General) return;
            application.Dispatcher.Invoke(() => Apply(application, IsLightTheme));
        };
    }

    private static void Apply(Application application, bool isLight)
    {
        var dictionaries = application.Resources.MergedDictionaries;

        // Theme.xaml carries the dark values as the base, so light mode is applied by
        // layering an override dictionary rather than swapping the whole system out.
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (dictionaries[index].Source == LightThemeUri) dictionaries.RemoveAt(index);
        }

        if (isLight)
        {
            dictionaries.Add(new ResourceDictionary { Source = LightThemeUri });
        }
    }
}
