namespace Soffritto.Core.Docker;

/// <summary>I comandi docker che servono agli ambienti Docker, tutti con un timeout.</summary>
public static class DockerCli
{
    /// <summary>docker con gli argomenti dati; le variabili valgono solo per quel processo.</summary>
    public static Task<ProcessOutcome> RunAsync(
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 60,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
        => ProcessRunner.RunAsync("docker", arguments, workingDirectory ?? Path.GetTempPath(), timeoutSeconds, environment, onOutputLine, ct);

    /// <summary>La versione del server Docker; null se Docker non risponde.</summary>
    public static async Task<string?> ServerVersionAsync(CancellationToken ct = default)
    {
        var version = await RunAsync("version --format \"{{.Server.Version}}\"", timeoutSeconds: 10, ct: ct).ConfigureAwait(false);
        return version.Success && version.StandardOutput.Trim() is { Length: > 0 } text ? text : null;
    }

    /// <summary>L'id del container con quel nome esatto, acceso o no; null se non c'e'.</summary>
    public static async Task<string?> ContainerIdAsync(string name, CancellationToken ct = default)
    {
        var ps = await RunAsync($"ps -aq --no-trunc --filter \"name=^/{name}$\"", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        return ps.Success && ps.StandardOutput.Trim() is { Length: > 0 } id ? id.Split('\n')[0].Trim() : null;
    }

    public static Task<ProcessOutcome> CopyToAsync(string container, string hostPath, string containerPath, CancellationToken ct = default)
        => RunAsync($"cp \"{hostPath}\" \"{container}:{containerPath}\"", timeoutSeconds: 600, ct: ct);

    public static Task<ProcessOutcome> CopyFromAsync(string container, string containerPath, string hostPath, CancellationToken ct = default)
        => RunAsync($"cp \"{container}:{containerPath}\" \"{hostPath}\"", timeoutSeconds: 600, ct: ct);

    /// <summary>docker exec, con l'utente dato (es. "0" per root) o quello dell'immagine.</summary>
    public static Task<ProcessOutcome> ExecAsync(string container, string command, string? user = null, int timeoutSeconds = 120, CancellationToken ct = default)
        => RunAsync($"exec {(user is null ? string.Empty : $"-u {user} ")}{container} {command}", timeoutSeconds: timeoutSeconds, ct: ct);

    /// <summary>Il valore di un'etichetta del container; stringa vuota se manca, null se il container non c'e'.</summary>
    public static async Task<string?> ContainerLabelAsync(string container, string label, CancellationToken ct = default)
    {
        var inspect = await RunAsync($"inspect --format \"{{{{ index .Config.Labels \\\"{label}\\\" }}}}\" {container}", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        if (!inspect.Success)
        {
            return null;
        }

        var value = inspect.StandardOutput.Trim();
        return value == "<no value>" ? string.Empty : value;
    }
}
