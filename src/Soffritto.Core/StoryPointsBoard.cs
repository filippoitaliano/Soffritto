using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soffritto.Core.AzureDevOps;

namespace Soffritto.Core;

/// <summary>
/// Una storia utente o un bug da stimare nella riunione: la descrizione e' gia' testo semplice (per
/// un bug sono i passaggi per riprodurre). Un bug puo' non avere una funzionalita' padre.
/// </summary>
public sealed record StoryToEstimate(int Id, string Title, string Description, int? FeatureId, string FeatureTitle, bool IsBug = false);

public sealed record StoriesToEstimate(IReadOnlyList<StoryToEstimate> Stories, string? Error = null);

/// <summary>
/// Le storie utente da stimare nella riunione dei punti storia e il salvataggio della stima, sul
/// progetto configurato (vedi <see cref="AzureDevOpsConnection"/>).
/// </summary>
public sealed partial class StoryPointsBoard(AzureDevOpsConnection connection)
{
    public const string StoryPointsField = "Microsoft.VSTS.Scheduling.StoryPoints";

    // Nomi del processo in italiano, come li restituisce il server.
    private const string FeatureType = "Funzionalità";
    private const string StoryType = "Storia utente";
    private const string BugType = "Bug";
    private const string ReproStepsField = "Microsoft.VSTS.TCM.ReproSteps";

    // Una funzionalita' gia' attiva puo' avere storie nuove da stimare.
    private static readonly string[] FeatureStates = ["Accettato", "Attivo"];
    private static readonly string[] DoneStates = ["Chiuso", "Rimosso"];

    public string CollectionUrl => connection.CollectionUrl;

    public string Project => connection.Project;

    /// <summary>
    /// Le storie utente figlie di funzionalita' accettate o attive e tutti i bug, purche' ancora
    /// aperti e senza punti. In ordine di funzionalita' e di id; i bug senza funzionalita' in fondo.
    /// </summary>
    public async Task<StoriesToEstimate> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Stato chiuso e punti vuoti si filtrano dopo: in WIQL il confronto con un numerico
            // vuoto non e' affidabile tra versioni del server.
            var storyLinks = await ReadLinksAsync(
                "SELECT [System.Id] FROM WorkItemLinks"
                    + " WHERE ([Source].[System.TeamProject] = @project"
                    + $" AND [Source].[System.WorkItemType] = '{FeatureType}'"
                    + $" AND [Source].[System.State] IN ({string.Join(", ", FeatureStates.Select(s => $"'{s}'"))}))"
                    + " AND ([System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward')"
                    + $" AND ([Target].[System.WorkItemType] = '{StoryType}')"
                    + " MODE (MustContain)",
                cancellationToken).ConfigureAwait(false);

            // I bug contano qualunque sia lo stato della funzionalita', e anche senza: la
            // funzionalita' padre serve solo a mostrarla.
            var bugIds = await ReadIdsAsync(
                "SELECT [System.Id] FROM WorkItems"
                    + " WHERE [System.TeamProject] = @project"
                    + $" AND [System.WorkItemType] = '{BugType}'"
                    + string.Concat(DoneStates.Select(s => $" AND [System.State] <> '{s}'")),
                cancellationToken).ConfigureAwait(false);
            var bugFeatures = (await ReadLinksAsync(
                "SELECT [System.Id] FROM WorkItemLinks"
                    + " WHERE ([Source].[System.TeamProject] = @project"
                    + $" AND [Source].[System.WorkItemType] = '{FeatureType}')"
                    + " AND ([System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward')"
                    + $" AND ([Target].[System.WorkItemType] = '{BugType}')"
                    + " MODE (MustContain)",
                cancellationToken).ConfigureAwait(false))
                .Where(l => bugIds.Contains(l.Child))
                .GroupBy(l => l.Child)
                .ToDictionary(g => g.Key, g => g.First().Parent);

            var ids = storyLinks.SelectMany(l => new[] { l.Parent, l.Child })
                .Concat(bugIds)
                .Concat(bugFeatures.Values)
                .Distinct()
                .ToList();
            var fields = await ReadFieldsAsync(ids, cancellationToken).ConfigureAwait(false);

            bool ToEstimate(int id) => fields.TryGetValue(id, out var item) && !DoneStates.Contains(item.State) && item.StoryPoints is null;

            var stories = storyLinks
                .Where(l => fields.ContainsKey(l.Parent) && ToEstimate(l.Child))
                .Select(l => new StoryToEstimate(l.Child, fields[l.Child].Title, fields[l.Child].Description, l.Parent, fields[l.Parent].Title));

            var bugs = bugIds
                .Where(ToEstimate)
                .Select(id =>
                {
                    var bug = fields[id];
                    int? feature = bugFeatures.TryGetValue(id, out var parent) && fields.ContainsKey(parent) ? parent : null;
                    return new StoryToEstimate(
                        id,
                        bug.Title,
                        bug.ReproSteps.Length > 0 ? bug.ReproSteps : bug.Description,
                        feature,
                        feature is { } f ? fields[f].Title : string.Empty,
                        IsBug: true);
                });

            var all = stories.Concat(bugs)
                .DistinctBy(s => s.Id)
                .OrderBy(s => s.FeatureId is null)
                .ThenBy(s => s.FeatureId)
                .ThenBy(s => s.Id)
                .ToList();

            return new StoriesToEstimate(all);
        }
        catch (Exception ex)
        {
            return new StoriesToEstimate([], ex is TaskCanceledException ? "Azure DevOps non ha risposto entro 30 secondi." : ex.Message);
        }
    }

    /// <summary>Scrive i punti storia sul work item. Null se riuscito, altrimenti il motivo.</summary>
    public async Task<string?> SaveAsync(int storyId, int points, CancellationToken cancellationToken = default)
    {
        try
        {
            var patch = JsonSerializer.Serialize(new[] { new { op = "add", path = $"/fields/{StoryPointsField}", value = points } });
            using var _ = await connection.SendJsonAsync(HttpMethod.Patch, $"{CollectionUrl}/_apis/wit/workitems/{storyId}?api-version=5.0", patch, "application/json-patch+json", cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex is TaskCanceledException ? "Azure DevOps non ha risposto entro 30 secondi." : ex.Message;
        }
    }

    private async Task<List<(int Parent, int Child)>> ReadLinksAsync(string wiql, CancellationToken cancellationToken)
    {
        var links = new List<(int, int)>();
        using var doc = await QueryAsync(wiql, cancellationToken).ConfigureAwait(false);
        foreach (var relation in doc.RootElement.GetProperty("workItemRelations").EnumerateArray())
        {
            if (relation.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                && relation.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            {
                links.Add((source.GetProperty("id").GetInt32(), target.GetProperty("id").GetInt32()));
            }
        }

        return links;
    }

    private async Task<HashSet<int>> ReadIdsAsync(string wiql, CancellationToken cancellationToken)
    {
        using var doc = await QueryAsync(wiql, cancellationToken).ConfigureAwait(false);
        return doc.RootElement.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).ToHashSet();
    }

    private Task<JsonDocument> QueryAsync(string wiql, CancellationToken cancellationToken)
        => connection.SendJsonAsync(HttpMethod.Post, $"{CollectionUrl}/{Project}/_apis/wit/wiql?api-version=5.0", JsonSerializer.Serialize(new { query = wiql }), "application/json", cancellationToken);

    private async Task<Dictionary<int, (string Title, string State, string Description, string ReproSteps, double? StoryPoints)>> ReadFieldsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (string, string, string, string, double?)>();

        // L'API accetta al massimo 200 id per chiamata.
        foreach (var chunk in ids.Chunk(200))
        {
            var url = $"{CollectionUrl}/_apis/wit/workitems?ids={string.Join(',', chunk)}"
                + $"&fields=System.Title,System.State,System.Description,{ReproStepsField},{StoryPointsField}&api-version=5.0";
            using var doc = await connection.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var fields = item.GetProperty("fields");
                double? points = fields.TryGetProperty(StoryPointsField, out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : null;

                result[item.GetProperty("id").GetInt32()] = (
                    GetString(fields, "System.Title"),
                    GetString(fields, "System.State"),
                    PlainText(GetString(fields, "System.Description")),
                    PlainText(GetString(fields, ReproStepsField)),
                    points);
            }
        }

        return result;
    }

    // La descrizione e' HTML: per leggerla in riunione bastano i paragrafi.
    private static string PlainText(string html)
    {
        var text = BlockEnd().Replace(html, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, string.Empty));
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h\d)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();
}

/// <summary>
/// La scala dei punti storia: la media dei voti si arrotonda al valore piu' vicino. Il massimo e'
/// 13: una storia che pesa di piu' va spezzata, non stimata.
/// </summary>
public static class StoryPointsScale
{
    public static IReadOnlyList<int> Values { get; } = [1, 2, 3, 5, 8, 13];

    /// <summary>A pari distanza vince il valore piu' alto: nel dubbio una storia pesa di piu'.</summary>
    public static int Nearest(double average)
        => Values.OrderBy(v => Math.Abs(v - average)).ThenByDescending(v => v).First();
}
