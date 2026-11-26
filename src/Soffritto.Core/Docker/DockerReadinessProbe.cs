using Microsoft.Data.SqlClient;

namespace Soffritto.Core.Docker;

/// <summary>
/// Whether a slot's service actually responds, beyond its container running: Docker's port proxy
/// accepts the connection even when the process inside is not listening yet.
/// </summary>
public static class DockerReadinessProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>The server accepts a login on the slot's database (which exists only once the restore is done).</summary>
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

    /// <summary>Any HTTP response on the host port (even a Kestrel 404) means the service is listening.</summary>
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
