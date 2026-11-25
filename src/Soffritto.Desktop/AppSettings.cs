using System.IO;
using System.Text.Json;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop;

/// <summary>
/// Le impostazioni dell'exe (scheda Preferenze del setup, Ctrl+G, Ctrl+T, Ctrl+D), separate dalla
/// configurazione di config.json: si applicano subito, senza riavvio. Gattino, sfondo e sue scene,
/// tema e colonne nascoste si salvano a ogni cambio accanto alle note degli ambienti; la demo no: riaprire
/// l'exe sui dati inventati confonderebbe.
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

    // Calcolata, non inizializzata: Current nasce in un inizializzatore statico che gira prima.
    public static IReadOnlyList<string> DefaultInitials => ["SF", "ML", "LZ", "FI"];

    private static IReadOnlyList<string> AllSceneries => [.. PixelScenery.Scenes.Select(s => s.Key)];

    public static AppSettings Current { get; } = Load();

    public bool ShowCat { get => showCat; set => SetAndSave(ref showCat, value); }

    public bool ShowScenery { get => showScenery; set => SetAndSave(ref showScenery, value); }

    public bool IsDemo { get => isDemo; set => SetProperty(ref isDemo, value); }

    /// <summary>Tema chiaro o scuro dell'exe (ThemeManager): di default segue Windows.</summary>
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
        new(ThemeMode.System, "Sistema"),
        new(ThemeMode.Light, "Chiaro"),
        new(ThemeMode.Dark, "Scuro"),
        new(ThemeMode.Scenery, "In base allo sfondo"),
    ];

    /// <summary>Alla chiusura dell'exe toglie le immagini degli ambienti Docker che non esistono piu' e riduce la cache di build: attiva di default.</summary>
    public bool DockerCleanupOnExit { get => dockerCleanupOnExit; set => SetAndSave(ref dockerCleanupOnExit, value); }

    /// <summary>Le chiavi delle scene di PixelScenery.Scenes che entrano nella rotazione dello sfondo: di default tutte.</summary>
    public IReadOnlyList<string> Sceneries => sceneries;

    /// <summary>
    /// Mette o toglie una scena dalla rotazione. L'ultima non si toglie: per non avere sfondi
    /// c'e' l'interruttore «Sfondo» (ShowScenery). Restituisce false se la scelta e' rifiutata.
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

    /// <summary>Le sigle dei partecipanti della riunione punti storia, nell'ordine in cui compaiono.</summary>
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

    /// <summary>I numeri degli ambienti con la colonna nascosta.</summary>
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

    // Un file illeggibile vale come assente: si riparte dai default.
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

                    // Chiavi sconosciute (scene tolte) si scartano; se non ne resta nessuna valgono tutte.
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

    // Se non si riesce a scrivere la scelta vale fino alla chiusura.
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

    // I campi aggiunti nel tempo mancano nei file scritti prima: valgono i default.
    private sealed record Saved(bool ShowCat, bool ShowScenery, string[]? MeetingInitials = null, int[]? CollapsedEnvironments = null, bool? DockerCleanupOnExit = null, string? Theme = null, string[]? Sceneries = null);
}

public sealed record ThemeOption(ThemeMode Mode, string Label);
