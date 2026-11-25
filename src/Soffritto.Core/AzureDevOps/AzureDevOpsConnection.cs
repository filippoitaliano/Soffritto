using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Soffritto.Core.AzureDevOps;

/// <summary>
/// La connessione ad Azure DevOps, on-prem o servizio, presa dal server MCP Azure DevOps: URL
/// della collection, progetto e PAT. Il PAT viaggia in Basic auth con utente vuoto, cioe' quello
/// che manda la VssBasicCredential dell'MCP, senza i client TFS che ci stanno intorno. Senza PAT
/// vale l'utente Windows che esegue il processo. Nessun codice TLS: la fiducia nel certificato
/// interno e' della CA nel trust store della macchina, come nell'MCP. Il PAT non si logga mai.
/// </summary>
public sealed class AzureDevOpsConnection : IDisposable
{
    private readonly HttpClient http;

    /// <param name="pat">Null o vuoto: autenticazione Windows.</param>
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

    /// <summary>Descrizione del PAT sicura per log e messaggi: solo presenza e lunghezza, mai il valore.</summary>
    public static string DescribePat(string? pat)
        => string.IsNullOrEmpty(pat) ? "PAT assente" : $"PAT presente, lunghezza {pat.Length}";

    /// <summary>Chi risulta autenticato: per il bottone "Prova connessione" del setup.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await GetJsonAsync($"{CollectionUrl}/_apis/connectionData", cancellationToken).ConfigureAwait(false);
        var user = connection.RootElement.GetProperty("authenticatedUser");

        return user.TryGetProperty("providerDisplayName", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : user.GetProperty("id").GetString() ?? "utente sconosciuto";
    }

    public Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    /// <param name="mediaType">Le modifiche ai work item vogliono <c>application/json-patch+json</c>.</param>
    public Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string json, string mediaType, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(method, url) { Content = new StringContent(json, Encoding.UTF8, mediaType) }, cancellationToken);

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var _ = request;
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Con un PAT scaduto o senza permessi il server risponde 401/203 con una pagina di login:
        // meglio dirlo che lasciar fallire il parser JSON.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || response.StatusCode == System.Net.HttpStatusCode.NonAuthoritativeInformation)
        {
            throw new HttpRequestException("Azure DevOps ha rifiutato le credenziali (401): controlla il PAT o l'utente Windows.");
        }

        // Un PAT di sola lettura passa le GET e cade sulle scritture.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException("Azure DevOps ha negato l'operazione (403): al PAT serve il permesso di scrittura sui work item.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();
}
