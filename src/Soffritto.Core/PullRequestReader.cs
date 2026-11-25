using System.Net.Http;
using Soffritto.Core.AzureDevOps;
using System.Text.Json;

namespace Soffritto.Core;

/// <summary>Voto di un reviewer: 10 approvata, 5 con suggerimenti, 0 nessuno, -5 in attesa dell'autore, -10 rifiutata.</summary>
public sealed record PullRequestVote(string Id, string Name, int Vote, bool IsRequired, bool IsGroup);

/// <summary>Discussione ancora aperta: chi ha scritto l'ultimo commento e chi vi ha partecipato.</summary>
public sealed record PullRequestThread(string LastAuthorId, IReadOnlyList<string> AuthorIds);

/// <summary>Il work item a cui la PR appartiene: la storia o il bug, non l'attivita' figlia.</summary>
public sealed record PullRequestWorkItem(int Id, string Type, string Title);

public sealed record PullRequestSummary(
    int Id,
    string Title,
    string Repository,
    string RepositoryId,
    string Author,
    string SourceBranch,
    bool IsDraft,
    DateTime Created,
    Uri WebUrl,
    IReadOnlyList<PullRequestVote> Reviewers)
{
    public IReadOnlyList<PullRequestThread> ActiveThreads { get; init; } = [];

    /// <summary>Id dei work item collegati alla PR, prima della risoluzione in <see cref="WorkItem"/>.</summary>
    public IReadOnlyList<int> LinkedWorkItemIds { get; init; } = [];

    public PullRequestWorkItem? WorkItem { get; init; }
}

/// <summary>Le PR attive che riguardano l'utente: quelle che ha aperto e quelle in cui e' reviewer.</summary>
public sealed record MyPullRequests(
    IReadOnlyList<PullRequestSummary> Created,
    IReadOnlyList<PullRequestSummary> ToReview,
    string? MyId = null,
    string? Error = null);

/// <summary>
/// Legge le pull request del progetto configurato su Azure DevOps (vedi <see cref="AzureDevOpsConnection"/>).
/// </summary>
public sealed class PullRequestReader(AzureDevOpsConnection connection)
{
    public string CollectionUrl => connection.CollectionUrl;

    public string Project => connection.Project;

    public async Task<MyPullRequests> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string myId;
            using (var data = await connection.GetJsonAsync($"{CollectionUrl}/_apis/connectionData", cancellationToken).ConfigureAwait(false))
            {
                myId = data.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString() ?? string.Empty;
            }

            var created = await QueryAsync("creatorId", myId, cancellationToken).ConfigureAwait(false);
            var toReview = await QueryAsync("reviewerId", myId, cancellationToken).ConfigureAwait(false);

            // Chi apre una PR puo' comparire anche tra i suoi reviewer: resta solo tra le sue.
            var mine = created.Select(pr => pr.Id).ToHashSet();
            toReview = toReview.Where(pr => !mine.Contains(pr.Id)).ToList();

            var workItems = await ResolveWorkItemsAsync(created.Concat(toReview), cancellationToken).ConfigureAwait(false);
            return new MyPullRequests(WithWorkItem(created, workItems), WithWorkItem(toReview, workItems), myId);
        }
        catch (Exception ex)
        {
            return new MyPullRequests([], [], Error: ex is TaskCanceledException ? "Azure DevOps non ha risposto entro 30 secondi." : ex.Message);
        }
    }

    // Solo le attive: completate e abbandonate non chiedono piu' lavoro. Le bozze restano.
    private async Task<List<PullRequestSummary>> QueryAsync(string criterion, string myId, CancellationToken cancellationToken)
    {
        var url = $"{CollectionUrl}/{Project}/_apis/git/pullrequests"
            + $"?searchCriteria.{criterion}={myId}&searchCriteria.status=active&$top=200&api-version=5.0";

        List<PullRequestSummary> list;
        using (var doc = await connection.GetJsonAsync(url, cancellationToken).ConfigureAwait(false))
        {
            list = doc.RootElement.GetProperty("value").EnumerateArray().Select(Map).ToList();
        }

        var detailed = await Task.WhenAll(list.Select(async pr =>
        {
            var threads = ReadActiveThreadsAsync(pr, cancellationToken);
            var linked = ReadLinkedWorkItemsAsync(pr, cancellationToken);
            return pr with
            {
                ActiveThreads = await threads.ConfigureAwait(false),
                LinkedWorkItemIds = await linked.ConfigureAwait(false),
            };
        })).ConfigureAwait(false);

        return detailed.OrderByDescending(pr => pr.Created).ToList();
    }

    private async Task<IReadOnlyList<int>> ReadLinkedWorkItemsAsync(PullRequestSummary pr, CancellationToken cancellationToken)
    {
        var url = $"{CollectionUrl}/{Project}/_apis/git/repositories/{pr.RepositoryId}/pullRequests/{pr.Id}/workitems?api-version=5.0";
        using var doc = await connection.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(w => w.GetProperty("id") is var id && id.ValueKind == JsonValueKind.Number ? id.GetInt32()
                : int.TryParse(id.GetString(), out var parsed) ? parsed : 0)
            .Where(id => id > 0)
            .OrderBy(id => id)
            .ToList();
    }

    // Se la PR e' collegata a un'attivita', il gruppo e' la storia o il bug padre: le PR di
    // backend, frontend e app dello stesso lavoro finiscono insieme anche se linkano attivita' diverse.
    private async Task<Dictionary<int, PullRequestWorkItem>> ResolveWorkItemsAsync(IEnumerable<PullRequestSummary> prs, CancellationToken cancellationToken)
    {
        var linked = prs.SelectMany(pr => pr.LinkedWorkItemIds).Distinct().ToList();
        var items = await ReadWorkItemsAsync(linked, cancellationToken).ConfigureAwait(false);

        var parents = items.Values
            .Where(w => w.Item.Type is "Attività" or "Task" && w.ParentId is not null)
            .Select(w => w.ParentId!.Value)
            .Where(id => !items.ContainsKey(id))
            .Distinct()
            .ToList();

        foreach (var (id, parent) in await ReadWorkItemsAsync(parents, cancellationToken).ConfigureAwait(false))
        {
            items[id] = parent;
        }

        return items.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Item.Type is "Attività" or "Task" && pair.Value.ParentId is { } parentId && items.TryGetValue(parentId, out var parent)
                ? parent.Item
                : pair.Value.Item);
    }

    private async Task<Dictionary<int, (PullRequestWorkItem Item, int? ParentId)>> ReadWorkItemsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (PullRequestWorkItem, int?)>();

        // L'API accetta al massimo 200 id per chiamata.
        foreach (var chunk in ids.Chunk(200))
        {
            var url = $"{CollectionUrl}/_apis/wit/workitems?ids={string.Join(',', chunk)}&$expand=relations&api-version=5.0";
            using var doc = await connection.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                var fields = item.GetProperty("fields");
                int? parentId = null;

                if (item.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var relation in relations.EnumerateArray())
                    {
                        if (GetString(relation, "rel") == "System.LinkTypes.Hierarchy-Reverse"
                            && int.TryParse(GetString(relation, "url").Split('/')[^1], out var parent))
                        {
                            parentId = parent;
                        }
                    }
                }

                result[id] = (new PullRequestWorkItem(id, GetString(fields, "System.WorkItemType"), GetString(fields, "System.Title")), parentId);
            }
        }

        return result;
    }

    private static List<PullRequestSummary> WithWorkItem(IEnumerable<PullRequestSummary> prs, Dictionary<int, PullRequestWorkItem> workItems)
        => prs.Select(pr => pr with
            {
                WorkItem = pr.LinkedWorkItemIds.Select(id => workItems.GetValueOrDefault(id)).FirstOrDefault(w => w is not null),
            })
            .ToList();

    // Attive o in sospeso, senza i thread di sistema (push, voti, cambi di reviewer) e senza i
    // commenti cancellati, che non chiedono risposta.
    private async Task<IReadOnlyList<PullRequestThread>> ReadActiveThreadsAsync(PullRequestSummary pr, CancellationToken cancellationToken)
    {
        var url = $"{CollectionUrl}/{Project}/_apis/git/repositories/{pr.RepositoryId}/pullRequests/{pr.Id}/threads?api-version=5.0";
        using var doc = await connection.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        var threads = new List<PullRequestThread>();
        foreach (var thread in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var status = GetString(thread, "status");
            if (status is not ("active" or "pending")
                || (thread.TryGetProperty("isDeleted", out var deleted) && deleted.GetBoolean())
                || !thread.TryGetProperty("comments", out var comments))
            {
                continue;
            }

            var authors = comments.EnumerateArray()
                .Where(c => GetString(c, "commentType") != "system"
                    && !(c.TryGetProperty("isDeleted", out var gone) && gone.GetBoolean()))
                .Select(c => c.TryGetProperty("author", out var author) ? GetString(author, "id") : string.Empty)
                .ToList();

            if (authors.Count > 0)
            {
                threads.Add(new PullRequestThread(authors[^1], authors.Distinct().ToList()));
            }
        }

        return threads;
    }

    private PullRequestSummary Map(JsonElement pr)
    {
        var id = pr.GetProperty("pullRequestId").GetInt32();
        var repository = pr.GetProperty("repository").GetProperty("name").GetString() ?? string.Empty;
        var repositoryId = pr.GetProperty("repository").GetProperty("id").GetString() ?? string.Empty;

        var reviewers = pr.TryGetProperty("reviewers", out var list)
            ? list.EnumerateArray().Select(r => new PullRequestVote(
                    GetString(r, "id"),
                    GetString(r, "displayName"),
                    r.TryGetProperty("vote", out var vote) ? vote.GetInt32() : 0,
                    r.TryGetProperty("isRequired", out var required) && required.GetBoolean(),
                    r.TryGetProperty("isContainer", out var container) && container.GetBoolean()))
                .ToList()
            : [];

        return new PullRequestSummary(
            id,
            GetString(pr, "title"),
            repository,
            repositoryId,
            pr.TryGetProperty("createdBy", out var author) ? GetString(author, "displayName") : string.Empty,
            GetString(pr, "sourceRefName").Replace("refs/heads/", string.Empty),
            pr.TryGetProperty("isDraft", out var draft) && draft.GetBoolean(),
            pr.TryGetProperty("creationDate", out var date) ? date.GetDateTime() : DateTime.MinValue,
            new Uri($"{CollectionUrl}/{Project}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{id}"),
            reviewers);
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}
