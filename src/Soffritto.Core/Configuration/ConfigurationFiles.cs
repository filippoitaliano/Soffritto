using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>
/// Where the product's data lives and how the configuration is read. The working copy is in
/// %LOCALAPPDATA%\Soffritto\config.json; the app feeds it from setup or from a
/// provisioning file next to itself (see <c>ConfigurationStore</c> in the app).
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

    /// <summary>An alternative data folder, to try a build without touching the working one.</summary>
    public const string DataFolderVariable = "SOFFRITTO_DATA";

    private static readonly string? dataFolder;

    static ConfigurationFiles()
    {
        var value = Environment.GetEnvironmentVariable(DataFolderVariable);

        // PowerShell deletes the variable when it is set to empty: empty and missing are the
        // same case. A whitespace-only value, however, is not empty and falls into the error branch.
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
            // A wrong variable must never fall back to the working data.
            DataFolderProblem = $"{DataFolderVariable} is '{value}': a full path is required (e.g. C:\\Data\\Soffritto-test). No data is read or written.";
        }
    }

    /// <summary>True when the data folder comes from <see cref="DataFolderVariable"/>.</summary>
    public static bool IsDataFolderOverridden { get; }

    /// <summary>Why <see cref="DataFolderVariable"/> is unusable; null if it is fine or not set.</summary>
    public static string? DataFolderProblem { get; }

    public static string DataFolder => dataFolder ?? throw new InvalidOperationException(DataFolderProblem);

    public static string ConfigPath => Path.Combine(DataFolder, "config.json");

    /// <summary>The saved configuration; null if there is none yet (first run) or it is unreadable.</summary>
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

    /// <summary>The configuration from a JSON text; an older format is refused with a message.</summary>
    public static AppConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, Json) ?? throw new JsonException("The file is empty.");

        // An older format would be read as an empty catalog, and setup would rewrite it,
        // losing repositories and environments.
        if (configuration.SchemaVersion < AppConfiguration.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"the file uses format schemaVersion {configuration.SchemaVersion}, which is not supported: "
                + $"this version only reads schemaVersion {AppConfiguration.CurrentSchemaVersion}.");
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

    /// <summary>The cross-process lock of config.json: taken by <see cref="Update"/>, in the app, VSIX, CLI and MCP.</summary>
    public static string LockPath => Path.Combine(DataFolder, "config.lock");

    /// <summary>
    /// The only way to write config.json while the app is running: serializes setup, Docker environments and
    /// seed. Rereads the file, applies <paramref name="mutate"/> (which returns an error or null),
    /// validates and writes. Replace only for the setup's Save.
    /// </summary>
    /// <param name="notice">Receives what happened to an unreadable file with Replace (where the copy is).</param>
    /// <returns>The error, or null if the file was written.</returns>
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
                var reason = readError ?? "the file does not exist";
                if (fallback == ConfigurationFileFallback.Refuse)
                {
                    return $"config.json is missing or unreadable ({reason}): save from setup first";
                }

                if (readError is not null)
                {
                    File.Copy(ConfigPath, UnreadableCopyPath, overwrite: true);
                    notice?.Invoke($"config.json was unreadable ({readError}): a copy is in {UnreadableCopyPath}.");
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
            return $"config.json not written: {ex.Message}";
        }
        finally
        {
            fileLock?.Dispose();
            updateLock.Release();
        }
    }

    /// <summary>Writes the working copy (tmp + move).</summary>
    public static void Write(AppConfiguration configuration)
    {
        Directory.CreateDirectory(DataFolder);

        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, Serialize(configuration));
        File.Move(temporary, ConfigPath, overwrite: true);
    }
}

/// <summary>What <see cref="ConfigurationFiles.Update"/> does when config.json is missing or unreadable.</summary>
public enum ConfigurationFileFallback
{
    /// <summary>No write, with an error: for callers changing one entry of a file that must exist.</summary>
    Refuse,

    /// <summary>Start from a new configuration (keeping a copy of the unreadable file): only for the setup's Save.</summary>
    Replace,
}
