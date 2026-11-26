using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Soffritto.Core.AzureDevOps;

/// <summary>
/// The connection to Azure DevOps, on-prem or service, modeled on the Azure DevOps MCP server: collection
/// URL, project and PAT. The PAT travels as Basic auth with an empty user, which is what the
/// MCP's VssBasicCredential sends, without the TFS clients wrapped around it. Without a PAT
/// the Windows user running the process is used. No TLS code: trust in the internal certificate
/// comes from the CA in the machine's trust store, as in the MCP. The PAT is never logged.
/// </summary>
public sealed class AzureDevOpsConnection : IDisposable
{
    private readonly HttpClient http;

    /// <param name="pat">Null or empty: Windows authentication.</param>
    public AzureDevOpsConnection(string collectionUrl, string project, string? pat)
    {
        CollectionUrl = collectionUrl.Trim().TrimEnd('/');
        Project = project.Trim();

        var usesPat = !string.IsNullOrEmpty(pat);
        http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = !usesPat })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        if (usesPat)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        }
    }

    public string CollectionUrl { get; }

    public string Project { get; }

    /// <summary>A description of the PAT safe for logs and messages: only presence and length, never the value.</summary>
    public static string DescribePat(string? pat)
        => string.IsNullOrEmpty(pat) ? "no PAT" : $"PAT present, length {pat.Length}";

    /// <summary>Who is authenticated: for the setup's "Test connection" button.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await GetJsonAsync($"{CollectionUrl}/_apis/connectionData", cancellationToken).ConfigureAwait(false);
        var user = connection.RootElement.GetProperty("authenticatedUser");

        return user.TryGetProperty("providerDisplayName", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : user.GetProperty("id").GetString() ?? "unknown user";
    }

    public Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    /// <param name="mediaType">Work item updates require <c>application/json-patch+json</c>.</param>
    public Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string json, string mediaType, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, mediaType) }, cancellationToken);

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var _ = request;
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // With an expired PAT or one lacking permissions the server answers 401/203 with a login page:
        // better to say so than to let the JSON parser fail.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || response.StatusCode == System.Net.HttpStatusCode.NonAuthoritativeInformation)
        {
            throw new HttpRequestException("Azure DevOps rejected the credentials (401): check the PAT or the Windows user.");
        }

        // A read-only PAT gets through the GETs and fails on writes.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException("Azure DevOps denied the operation (403): the PAT needs write permission on work items.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();
}
