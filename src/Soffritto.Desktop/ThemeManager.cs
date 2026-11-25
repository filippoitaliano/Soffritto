using System.Windows;
using Microsoft.Win32;

namespace Soffritto.Desktop;

/// <summary>Come si sceglie il tema chiaro o scuro dell'exe (Preferenze del setup).</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
    Scenery,
}

/// <summary>
/// Tema chiaro o scuro di tutta l'exe: sostituisce ThemeLight.xaml con ThemeDark.xaml tra i
/// dizionari dell'applicazione, e ogni colore Theme.* preso con DynamicResource si aggiorna.
/// La scelta viene da AppSettings.Theme; per «Sistema» si legge il tema delle app di Windows,
/// per «In base allo sfondo» la scena di Ctrl+T (PixelScenery.IsDark).
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

        // Il cambio di tema delle app di Windows arriva come preferenza «General».
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

    // AppsUseLightTheme = 0 vuol dire app scure; senza chiave (Windows vecchi) vale chiaro.
    private static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
