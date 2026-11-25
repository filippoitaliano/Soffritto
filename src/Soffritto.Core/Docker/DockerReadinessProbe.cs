using Microsoft.Data.SqlClient;

namespace Soffritto.Core.Docker;

/// <summary>
/// Se un servizio di uno slot risponde davvero, oltre al container acceso: il proxy delle porte di
/// Docker accetta la connessione anche quando dentro il processo non ascolta ancora.
/// </summary>
public static class DockerReadinessProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Il database accetta un login sul database dello slot (che c'e' solo a restore finito).</summary>
    public static async Task<bool> DatabaseAsync(DockerSlot slot, CancellationToken ct)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(slot.HostConnectionString)
            {
                ConnectTimeout = 2,
                Pooling = false,
                ApplicationName = "Soffritto",
            };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Qualunque risposta HTTP sulla porta host (anche un 404 di Kestrel) vuol dire che il servizio ascolta.</summary>
    public static async Task<bool> HttpAsync(int hostPort, CancellationToken ct)
    {
        try
        {
            using var response = await Http
                .GetAsync($"http://127.0.0.1:{hostPort}/", HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
