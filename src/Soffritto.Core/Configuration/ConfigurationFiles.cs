using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>
/// Dove vivono i dati del prodotto e come si legge la configurazione. La copia di lavoro sta in
/// %LOCALAPPDATA%\Soffritto\config.json; l'exe la alimenta dal setup o da un file di
/// provisioning accanto a se' (vedi <c>ConfigurationStore</c> nell'exe).
/// </summary>
public static class ConfigurationFiles
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new RepositoryConfigurationConverter() },
    };

    /// <summary>Cartella dati alternativa, per provare una build senza toccare quella di lavoro.</summary>
    public const string DataFolderVariable = "SOFFRITTO_DATA";

    private static readonly string? dataFolder;

    static ConfigurationFiles()
    {
        var value = Environment.GetEnvironmentVariable(DataFolderVariable);

        // PowerShell cancella la variabile quando la si imposta a vuoto: vuota e assente sono lo
        // stesso caso. Un valore di soli spazi invece non e' vuoto e cade nel ramo dell'errore.
        if (string.IsNullOrEmpty(value))
        {
            dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soffritto");
        }
        else if (Path.IsPathFullyQualified(value.Trim()))
        {
            dataFolder = Path.GetFullPath(value.Trim());
            IsDataFolderOverridden = true;
        }
        else
        {
            // Una variabile sbagliata non deve mai ricadere sui dati di lavoro.
            DataFolderProblem = $"{DataFolderVariable} vale '{value}': serve un percorso completo (es. C:\\Dati\\Soffritto-prova). Nessun dato viene letto o scritto.";
        }
    }

    /// <summary>Vero quando la cartella dati viene da <see cref="DataFolderVariable"/>.</summary>
    public static bool IsDataFolderOverridden { get; }

    /// <summary>Il motivo per cui <see cref="DataFolderVariable"/> non e' usabile; null se va bene o non c'e'.</summary>
    public static string? DataFolderProblem { get; }

    public static string DataFolder => dataFolder ?? throw new InvalidOperationException(DataFolderProblem);

    public static string ConfigPath => Path.Combine(DataFolder, "config.json");

    /// <summary>La configurazione salvata; null se non c'e' ancora (primo avvio) o non e' leggibile.</summary>
    public static AppConfiguration? TryLoad(out string? error) => TryRead(ConfigPath, out error);

    public static AppConfiguration? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"{path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>La configurazione di un testo json; un formato precedente si rifiuta con un messaggio.</summary>
    public static AppConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, Json) ?? throw new JsonException("Il file e' vuoto.");

        // Un formato precedente si leggerebbe come un catalogo vuoto, e il setup lo riscriverebbe
        // perdendo repository e ambienti.
        if (configuration.SchemaVersion < AppConfiguration.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"il file e' nel formato schemaVersion {configuration.SchemaVersion}, non supportato: "
                + $"questa versione legge solo schemaVersion {AppConfiguration.CurrentSchemaVersion}.");
        }

        return configuration;
    }

    public static string Serialize(AppConfiguration configuration)
    {
        configuration.SchemaVersion = AppConfiguration.CurrentSchemaVersion;
        return JsonSerializer.Serialize(configuration, Json);
    }

    private static readonly SemaphoreSlim updateLock = new(1, 1);

    public static string UnreadableCopyPath => Path.Combine(DataFolder, "config.illeggibile.json");

    /// <summary>Il lock fra processi di config.json: lo prende <see cref="Update"/>, in exe, VSIX, CLI e MCP.</summary>
    public static string LockPath => Path.Combine(DataFolder, "config.lock");

    /// <summary>
    /// L'unico modo di scrivere config.json mentre l'exe gira: serializza setup, ambienti Docker e
    /// seed. Rilegge il file, applica <paramref name="mutate"/> (che restituisce un errore o null),
    /// valida e scrive. Replace solo per il Salva del setup.
    /// </summary>
    /// <param name="notice">Riceve cosa e' successo a un file illeggibile con Replace (dove sta la copia).</param>
    /// <returns>L'errore, o null se il file e' stato scritto.</returns>
    public static string? Update(Func<AppConfiguration, string?> mutate, ConfigurationFileFallback fallback, Action<string>? notice = null)
    {
        updateLock.Wait();
        CrossProcessLock? fileLock = null;
        try
        {
            fileLock = CrossProcessLock.Acquire(LockPath, TimeSpan.FromSeconds(30));
            var current = TryLoad(out var readError);
            if (current is null)
            {
                var reason = readError ?? "il file non esiste";
                if (fallback == ConfigurationFileFallback.Refuse)
                {
                    return $"config.json manca o non si legge ({reason}): salva prima dal setup";
                }

                if (readError is not null)
                {
                    File.Copy(ConfigPath, UnreadableCopyPath, overwrite: true);
                    notice?.Invoke($"config.json non si leggeva ({readError}): la copia e' in {UnreadableCopyPath}.");
                }

                current = new AppConfiguration();
            }

            if (mutate(current) is { } error)
            {
                return error;
            }

            if (current.Validate() is { Count: > 0 } errors)
            {
                return string.Join(" ", errors);
            }

            Write(current);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return $"config.json non scritto: {ex.Message}";
        }
        finally
        {
            fileLock?.Dispose();
            updateLock.Release();
        }
    }

    /// <summary>Scrive la copia di lavoro (tmp + move).</summary>
    public static void Write(AppConfiguration configuration)
    {
        Directory.CreateDirectory(DataFolder);

        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, Serialize(configuration));
        File.Move(temporary, ConfigPath, overwrite: true);
    }
}

/// <summary>Cosa fa <see cref="ConfigurationFiles.Update"/> quando config.json manca o non si legge.</summary>
public enum ConfigurationFileFallback
{
    /// <summary>Nessuna scrittura, con un errore: per chi modifica una voce di un file che deve esistere.</summary>
    Refuse,

    /// <summary>Si parte da una configurazione nuova (con la copia del file illeggibile): solo per il Salva del setup.</summary>
    Replace,
}
