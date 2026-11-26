using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <param name="SecretsCall">For a .bat: the secrets file to invoke with @CALL instead of writing their values.</param>
public sealed record EnvWriteOptions(string? SecretsCall = null);

/// <summary>Reads and writes an env file in one format: .bat, dotenv, appsettings, or no file.</summary>
public interface IEnvAdapter
{
    EnvAdapterKind Kind { get; }

    /// <summary>The file's variables as constants with the file as their source; empty if the file does not exist.</summary>
    EnvSet Read(string path);

    /// <summary>Writes the set's variables: lines and keys of other variables stay as they are.</summary>
    void Write(string path, EnvSet set, EnvWriteOptions options);
}
