using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <param name="SecretsCall">Per un .bat: il file dei segreti da richiamare con @CALL al posto dei loro valori.</param>
public sealed record EnvWriteOptions(string? SecretsCall = null);

/// <summary>Legge e scrive un file di env in un formato: .bat, dotenv, appsettings, o nessun file.</summary>
public interface IEnvAdapter
{
    EnvAdapterKind Kind { get; }

    /// <summary>Le variabili del file come costanti con il file come origine; vuoto se il file non c'e'.</summary>
    EnvSet Read(string path);

    /// <summary>Scrive le variabili del set: le righe e le chiavi di altre variabili restano come sono.</summary>
    void Write(string path, EnvSet set, EnvWriteOptions options);
}
