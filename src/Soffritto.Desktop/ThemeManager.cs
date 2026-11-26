using System.Windows;
using Microsoft.Win32;

namespace Soffritto.Desktop;

/// <summary>How the app's light or dark theme is chosen (Preferences in the setup).</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
    Scenery,
}

/// <summary>
/// Light or dark theme for the whole app: swaps ThemeLight.xaml for ThemeDark.xaml among the
/// application dictionaries, and every Theme.* colour taken with DynamicResource updates.
/// The choice comes from AppSettings.Theme; for “System” the Windows app theme is read,
/// for “Match the background” the Ctrl+T scene (PixelScenery.IsDark).
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly ResourceDictionary Light = new() { Source = new Uri("/ThemeLight.xaml", UriKind.Relative) };
    private static readonly ResourceDictionary Dark = new() { Source = new Uri("/ThemeDark.xaml", UriKind.Relative) };

    private static bool sceneryDark;
    private static bool started;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme))
            {
                Apply();
            }
        };

        // A change of the Windows app theme arrives as the “General” preference.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
            {
                Application.Current?.Dispatcher.BeginInvoke(Apply);
            }
        };

        Apply();
    }

    public static void SetSceneryDark(bool dark)
    {
        sceneryDark = dark;
        Apply();
    }

    private static void Apply()
    {
        var dark = AppSettings.Current.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            ThemeMode.Scenery => sceneryDark,
            _ => SystemIsDark(),
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        var wanted = dark ? Dark : Light;
        if (merged.Count > 0 && ReferenceEquals(merged[0], wanted))
        {
            return;
        }

        if (merged.Count > 0)
        {
            merged[0] = wanted;
        }
        else
        {
            merged.Add(wanted);
        }

        IsDark = dark;
        Changed?.Invoke();
    }

    // AppsUseLightTheme = 0 means dark apps; without the key (older Windows) it is light.
    private static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
