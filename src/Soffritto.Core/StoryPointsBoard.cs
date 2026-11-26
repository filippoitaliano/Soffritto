using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soffritto.Core.AzureDevOps;

namespace Soffritto.Core;

/// <summary>
/// A user story or a bug to estimate in the meeting: the description is already plain text (for
/// a bug, the repro steps). A bug may have no parent feature.
/// </summary>
public sealed record StoryToEstimate(int Id, string Title, string Description, int? FeatureId, string FeatureTitle, bool IsBug = false);

public sealed record StoriesToEstimate(IReadOnlyList<StoryToEstimate> Stories, string? Error = null);

/// <summary>
/// The user stories to estimate in the story points meeting and saving the estimate, on the
/// configured project (see <see cref="AzureDevOpsConnection"/>).
/// </summary>
public sealed partial class StoryPointsBoard(AzureDevOpsConnection connection)
{
    public const string StoryPointsField = "Microsoft.VSTS.Scheduling.StoryPoints";

    // Process names in Italian, exactly as the server returns them.
    private const string FeatureType = "Funzionalità";
    private const string StoryType = "Storia utente";
    private const string BugType = "Bug";
    private const string ReproStepsField = "Microsoft.VSTS.TCM.ReproSteps";

    // A feature that is already active can have new stories to estimate.
    private static readonly string[] FeatureStates = ["Accettato", "Attivo"];
    private static readonly string[] DoneStates = ["Chiuso", "Rimosso"];

    public string CollectionUrl => connection.CollectionUrl;

    public string Project => connection.Project;

    /// <summary>
    /// The user stories under accepted or active features, and all bugs, as long as they are still
    /// open and have no points. Ordered by feature and id; bugs without a feature come last.
    /// </summary>
    public async Task<StoriesToEstimate> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Closed state and empty points are filtered afterwards: in WIQL comparing against an empty
            // numeric field is not reliable across server versions.
            var storyLinks = await ReadLinksAsync(
                "SELECT [System.Id] FROM WorkItemLinks"
                    + " WHERE ([Source].[System.TeamProject] = @project"
                    + $" AND [Source].[System.WorkItemType] = '{FeatureType}'"
                    + $" AND [Source].[System.State] IN ({string.Join(", ", FeatureStates.Select(s => $"'{s}'"))}))"
                    + " AND ([System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward')"
                    + $" AND ([Target].[System.WorkItemType] = '{StoryType}')"
                    + " MODE (MustContain)",
                cancellationToken).ConfigureAwait(false);

            // Bugs count whatever the feature's state, and even without one: the
            // parent feature is only there to be shown.
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
            return new StoriesToEstimate([], ex is TaskCanceledException ? "Azure DevOps did not respond within 30 seconds." : ex.Message);
        }
    }

    /// <summary>Writes the story points on the work item. Null on success, otherwise the reason.</summary>
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
            return ex is TaskCanceledException ? "Azure DevOps did not respond within 30 seconds." : ex.Message;
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

        // The API accepts at most 200 ids per call.
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

    // The description is HTML: paragraphs are enough to read it in the meeting.
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
/// The story points scale: the average of the votes is rounded to the nearest value. The maximum is
/// 13: a story that weighs more should be split, not estimated.
/// </summary>
public static class StoryPointsScale
{
    public static IReadOnlyList<int> Values { get; } = [1, 2, 3, 5, 8, 13];

    /// <summary>On a tie the higher value wins: when in doubt, a story weighs more.</summary>
    public static int Nearest(double average)
        => Values.OrderBy(v => Math.Abs(v - average)).ThenByDescending(v => v).First();
}
