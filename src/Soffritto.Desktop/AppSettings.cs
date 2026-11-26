using System.IO;
using System.Text.Json;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop;

/// <summary>
/// The app's settings (Preferences tab of the setup, Ctrl+G, Ctrl+T, Ctrl+D), kept apart from the
/// config.json configuration: they apply immediately, without a restart. Kitten, background and its scenes,
/// theme and hidden columns are saved on every change next to the environment notes; the demo is not:
/// reopening the app on made-up data would be confusing.
/// </summary>
public sealed class AppSettings : ObservableObject
{
    private static readonly string FilePath = Path.Combine(Soffritto.Core.Configuration.ConfigurationFiles.DataFolder, "impostazioni.json");

    private bool showCat;
    private bool showScenery;
    private bool isDemo;
    private ThemeMode theme = ThemeMode.System;
    private bool dockerCleanupOnExit = true;
    private IReadOnlyList<string> meetingInitials = DefaultInitials;
    private IReadOnlyList<string> sceneries = AllSceneries;
    private readonly List<int> collapsedEnvironments = [];

    // Computed, not initialised: Current is created in a static initialiser that runs earlier.
    public static IReadOnlyList<string> DefaultInitials => ["SF", "ML", "LZ", "FI"];

    private static IReadOnlyList<string> AllSceneries => [.. PixelScenery.Scenes.Select(s => s.Key)];

    public static AppSettings Current { get; } = Load();

    public bool ShowCat { get => showCat; set => SetAndSave(ref showCat, value); }

    public bool ShowScenery { get => showScenery; set => SetAndSave(ref showScenery, value); }

    public bool IsDemo { get => isDemo; set => SetProperty(ref isDemo, value); }

    /// <summary>Light or dark theme of the app (ThemeManager): follows Windows by default.</summary>
    public ThemeMode Theme
    {
        get => theme;
        set
        {
            if (SetProperty(ref theme, value))
            {
                Save();
            }
        }
    }

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemeMode.System, "System"),
        new(ThemeMode.Light, "Light"),
        new(ThemeMode.Dark, "Dark"),
        new(ThemeMode.Scenery, "Match the background"),
    ];

    /// <summary>When the app closes, removes the images of Docker environments that no longer exist and trims the build cache: on by default.</summary>
    public bool DockerCleanupOnExit { get => dockerCleanupOnExit; set => SetAndSave(ref dockerCleanupOnExit, value); }

    /// <summary>The keys of the PixelScenery.Scenes scenes included in the background rotation: all of them by default.</summary>
    public IReadOnlyList<string> Sceneries => sceneries;

    /// <summary>
    /// Adds a scene to the rotation or removes it. The last one cannot be removed: to have no background
    /// there is the “Background” switch (ShowScenery). Returns false if the choice is rejected.
    /// </summary>
    public bool SetSceneryIncluded(string key, bool included)
    {
        if (included == sceneries.Contains(key))
        {
            return true;
        }

        if (!included && sceneries.Count == 1)
        {
            return false;
        }

        sceneries = included
            ? [.. AllSceneries.Where(k => k == key || sceneries.Contains(k))]
            : [.. sceneries.Where(k => k != key)];
        OnPropertyChanged(nameof(Sceneries));
        Save();
        return true;
    }

    /// <summary>The initials of the story points meeting participants, in the order they appear.</summary>
    public IReadOnlyList<string> MeetingInitials
    {
        get => meetingInitials;
        set
        {
            meetingInitials = value;
            OnPropertyChanged();
            Save();
        }
    }

    /// <summary>The numbers of the environments whose column is hidden.</summary>
    public IReadOnlyList<int> CollapsedEnvironments => collapsedEnvironments;

    public void SetCollapsed(int number, bool collapsed)
    {
        if (collapsed == collapsedEnvironments.Contains(number))
        {
            return;
        }

        if (collapsed)
        {
            collapsedEnvironments.Add(number);
        }
        else
        {
            collapsedEnvironments.Remove(number);
        }

        Save();
    }

    private void SetAndSave(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            Save();
        }
    }

    // An unreadable file counts as missing: start again from the defaults.
    private static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
                if (saved is not null)
                {
                    settings.showCat = saved.ShowCat;
                    settings.showScenery = saved.ShowScenery;
                    settings.theme = Enum.TryParse<ThemeMode>(saved.Theme, out var mode) ? mode : ThemeMode.System;
                    settings.dockerCleanupOnExit = saved.DockerCleanupOnExit ?? true;
                    if (saved.MeetingInitials is { Length: >= 2 } initials)
                    {
                        settings.meetingInitials = initials;
                    }

                    settings.collapsedEnvironments.AddRange(saved.CollapsedEnvironments ?? []);

                    // Unknown keys (removed scenes) are dropped; if none are left, all scenes apply.
                    var known = AllSceneries.Where(k => saved.Sceneries?.Contains(k) == true).ToArray();
                    if (known.Length > 0)
                    {
                        settings.sceneries = known;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return settings;
    }

    // If the file cannot be written, the choice lasts until the app closes.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(showCat, showScenery, [.. meetingInitials], [.. collapsedEnvironments], dockerCleanupOnExit, theme.ToString(), [.. sceneries])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Fields added over time are missing from files written earlier: the defaults apply.
    private sealed record Saved(bool ShowCat, bool ShowScenery, string[]? MeetingInitials = null, int[]? CollapsedEnvironments = null, bool? DockerCleanupOnExit = null, string? Theme = null, string[]? Sceneries = null);
}

public sealed record ThemeOption(ThemeMode Mode, string Label);
