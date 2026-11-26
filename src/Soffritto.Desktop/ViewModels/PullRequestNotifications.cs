using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// A new piece of work on a PR, until the user marks it read with the tick (opening it is not enough).
/// If the work disappears from Azure DevOps in the meantime, the notification stays, marked resolved.
/// </summary>
public sealed class PullRequestNotification : ObservableObject
{
    private bool isResolved;

    public PullRequestNotification(string id, string key, int count, string text, string title, Uri url, int pullRequestId, DateTime created, bool isResolved)
    {
        Id = id;
        Key = key;
        Count = count;
        Text = text;
        Title = title;
        Url = url;
        PullRequestId = pullRequestId;
        Created = created;
        this.isResolved = isResolved;
    }

    public string Id { get; }

    public string Key { get; }

    public int Count { get; }

    public string Text { get; }

    public string Title { get; }

    public Uri Url { get; }

    /// <summary>0 in notifications saved by earlier versions, which did not have it.</summary>
    public int PullRequestId { get; }

    public DateTime Created { get; }

    public string When => Created.Date == DateTime.Today ? $"today {Created:HH:mm}"
        : Created.Date == DateTime.Today.AddDays(-1) ? $"yesterday {Created:HH:mm}"
        : $"{Created:d MMM HH:mm}";

    public bool IsResolved
    {
        get => isResolved;
        set
        {
            if (SetProperty(ref isResolved, value))
            {
                OnPropertyChanged(nameof(ResolvedVisibility));
            }
        }
    }

    public string ResolvedVisibility => isResolved ? "Visible" : "Collapsed";

    public ICommand? OpenCommand { get; init; }

    public ICommand? DismissCommand { get; init; }
}

/// <summary>
/// The single source of operational notifications: new work on pull requests. It compares them
/// with the last read (the baseline) and keeps them, saved to file, until the user consumes them
/// from the notifications panel. The kitten and Windows notifications are independent consumers:
/// each one takes its own <see cref="Listen"/> and announces every notification once.
/// </summary>
public sealed class PullRequestNotifications : ObservableObject
{
    private readonly string? filePath;

    // Counts from the last successful read; null until there has been one, not even in a previous
    // session: the first read becomes the baseline and does not notify what came before.
    private Dictionary<string, int>? baseline;

    /// <param name="filePath">Where notifications and baseline are saved; null for the demo, which lives in memory.</param>
    /// <param name="notifyFirstRead">True in the demo: without a baseline, even the first read turns into notifications.</param>
    public PullRequestNotifications(string? filePath, bool notifyFirstRead = false)
    {
        this.filePath = filePath;
        baseline = notifyFirstRead ? [] : null;
        DismissAllCommand = new RelayCommand(_ => DismissAll());
        Load();
        Items.CollectionChanged += (_, _) => OnCountChanged();
    }

    /// <summary>The unconsumed notifications, newest first.</summary>
    public ObservableCollection<PullRequestNotification> Items { get; } = [];

    public int UnreadCount => Items.Count;

    /// <summary>The number on the notifications button: empty when there are none.</summary>
    public string BadgeText => Items.Count == 0 ? string.Empty : Items.Count > 99 ? "99+" : Items.Count.ToString();

    public string Summary => Items.Count switch
    {
        0 => "nothing to read",
        1 => "1 to read",
        _ => $"{Items.Count} to read",
    };

    public string EmptyVisibility => Items.Count == 0 ? "Visible" : "Collapsed";

    public string DismissAllVisibility => Items.Count > 0 ? "Visible" : "Collapsed";

    public ICommand DismissAllCommand { get; }

    /// <summary>The newly created notifications, in the order they arrived.</summary>
    public event Action<IReadOnlyList<PullRequestNotification>>? Added;

    /// <summary>
    /// Click on a notification: the panel's host takes the user to the PR. The notification stays,
    /// so the work is not lost if it is not done right away: only the tick consumes it.
    /// </summary>
    public event Action<PullRequestNotification>? Opened;

    /// <summary>
    /// A consumer: each call returns the texts of the unconsumed notifications this consumer has
    /// not announced yet. Those already present when it was created (saved by a previous session)
    /// count as already announced.
    /// </summary>
    public Func<IReadOnlyList<string>> Listen()
    {
        var announced = Items.Select(n => n.Id).ToHashSet();
        return () =>
        {
            announced.IntersectWith(Items.Select(n => n.Id));
            return Items.Reverse().Where(n => announced.Add(n.Id)).Select(n => n.Text).ToList();
        };
    }

    /// <summary>
    /// A successful read of the PRs: whatever appeared or grew compared with the baseline becomes a
    /// notification (replacing the old one on the same work); whatever is gone marks the
    /// notifications as resolved.
    /// </summary>
    public void Update(IReadOnlyList<PullRequestAlert> alerts)
    {
        var current = new Dictionary<string, PullRequestAlert>();
        foreach (var alert in alerts)
        {
            current[alert.Key] = alert;
        }

        foreach (var item in Items)
        {
            item.IsResolved = !current.ContainsKey(item.Key);
        }

        var added = new List<PullRequestNotification>();
        if (baseline is not null)
        {
            foreach (var alert in current.Values)
            {
                if (baseline.TryGetValue(alert.Key, out var count) && alert.Count <= count)
                {
                    continue;
                }

                foreach (var old in Items.Where(n => n.Key == alert.Key).ToList())
                {
                    Items.Remove(old);
                }

                var notification = Create(Guid.NewGuid().ToString("N"), alert.Key, alert.Count, alert.Text, alert.Title, alert.Url, alert.PullRequestId, DateTime.Now, isResolved: false);
                Items.Insert(0, notification);
                added.Add(notification);
            }
        }

        baseline = current.ToDictionary(a => a.Key, a => a.Value.Count);
        Save();

        if (added.Count > 0)
        {
            Added?.Invoke(added);
        }
    }

    private PullRequestNotification Create(string id, string key, int count, string text, string title, Uri url, int pullRequestId, DateTime created, bool isResolved)
    {
        PullRequestNotification? notification = null;
        notification = new PullRequestNotification(id, key, count, text, title, url, pullRequestId, created, isResolved)
        {
            OpenCommand = new RelayCommand(_ => Opened?.Invoke(notification!)),
            DismissCommand = new RelayCommand(_ => Consume(notification!)),
        };
        return notification;
    }

    private void Consume(PullRequestNotification notification)
    {
        if (Items.Remove(notification))
        {
            Save();
        }
    }

    private void DismissAll()
    {
        Items.Clear();
        Save();
    }

    private void OnCountChanged()
    {
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(DismissAllVisibility));
    }

    // An unreadable file counts as missing: start over with no notifications and no baseline.
    private void Load()
    {
        if (filePath is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(filePath));
            if (saved is null)
            {
                return;
            }

            baseline = saved.Baseline;
            foreach (var item in saved.Items.OrderByDescending(i => i.Created))
            {
                Items.Add(Create(item.Id, item.Key, item.Count, item.Text, item.Title, item.Url, item.PullRequestId, item.Created, item.IsResolved));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    // If the file cannot be written, the notifications last until the app closes.
    private void Save()
    {
        if (filePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var items = Items.Select(n => new SavedItem(n.Id, n.Key, n.Count, n.Text, n.Title, n.Url, n.Created, n.IsResolved, n.PullRequestId)).ToList();
            File.WriteAllText(filePath, JsonSerializer.Serialize(new Saved(baseline, items)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Saved(Dictionary<string, int>? Baseline, List<SavedItem> Items);

    private sealed record SavedItem(string Id, string Key, int Count, string Text, string Title, Uri Url, DateTime Created, bool IsResolved, int PullRequestId = 0);
}
