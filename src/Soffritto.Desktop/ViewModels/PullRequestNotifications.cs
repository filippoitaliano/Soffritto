using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Un lavoro nuovo su una PR, finche' l'utente non la segna letta col tick (aprirla non basta).
/// Se nel frattempo il lavoro sparisce da Azure DevOps la notifica resta, marcata risolta.
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

    /// <summary>0 nelle notifiche salvate dalle versioni precedenti, che non lo avevano.</summary>
    public int PullRequestId { get; }

    public DateTime Created { get; }

    public string When => Created.Date == DateTime.Today ? $"oggi {Created:HH:mm}"
        : Created.Date == DateTime.Today.AddDays(-1) ? $"ieri {Created:HH:mm}"
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
/// La fonte unica delle notifiche operative: i lavori nuovi sulle pull request. Le confronta con
/// l'ultima lettura (la base) e le tiene, salvate su file, finche' l'utente non le consuma dal
/// pannello delle notifiche. Gattino e notifiche di Windows sono consumer indipendenti: ognuno
/// prende la sua <see cref="Listen"/> e annuncia ogni notifica una volta.
/// </summary>
public sealed class PullRequestNotifications : ObservableObject
{
    private readonly string? filePath;

    // Conteggi dell'ultima lettura riuscita; null finche' non ce n'e' stata nessuna, nemmeno in
    // una sessione precedente: la prima lettura fa da base e non notifica il pregresso.
    private Dictionary<string, int>? baseline;

    /// <param name="filePath">Dove si salvano notifiche e base; null per la demo, che vive in memoria.</param>
    /// <param name="notifyFirstRead">Vero nella demo: senza una base, anche la prima lettura diventa notifiche.</param>
    public PullRequestNotifications(string? filePath, bool notifyFirstRead = false)
    {
        this.filePath = filePath;
        baseline = notifyFirstRead ? [] : null;
        DismissAllCommand = new RelayCommand(_ => DismissAll());
        Load();
        Items.CollectionChanged += (_, _) => OnCountChanged();
    }

    /// <summary>Le notifiche non consumate, dalla piu' recente.</summary>
    public ObservableCollection<PullRequestNotification> Items { get; } = [];

    public int UnreadCount => Items.Count;

    /// <summary>Il numero sul bottone delle notifiche: vuoto quando non ce ne sono.</summary>
    public string BadgeText => Items.Count == 0 ? string.Empty : Items.Count > 99 ? "99+" : Items.Count.ToString();

    public string Summary => Items.Count switch
    {
        0 => "niente da leggere",
        1 => "1 da leggere",
        _ => $"{Items.Count} da leggere",
    };

    public string EmptyVisibility => Items.Count == 0 ? "Visible" : "Collapsed";

    public string DismissAllVisibility => Items.Count > 0 ? "Visible" : "Collapsed";

    public ICommand DismissAllCommand { get; }

    /// <summary>Le notifiche appena create, nell'ordine in cui sono arrivate.</summary>
    public event Action<IReadOnlyList<PullRequestNotification>>? Added;

    /// <summary>
    /// Clic su una notifica: chi ospita il pannello porta l'utente alla PR. La notifica resta,
    /// cosi' il lavoro non si perde se poi non lo si fa: la consuma solo il tick.
    /// </summary>
    public event Action<PullRequestNotification>? Opened;

    /// <summary>
    /// Un consumer: ogni chiamata restituisce i testi delle notifiche non ancora consumate che
    /// questo consumer non ha ancora detto. Quelle gia' presenti alla sua creazione (salvate da
    /// una sessione precedente) contano come gia' dette.
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
    /// Una lettura riuscita delle PR: cio' che e' comparso o cresciuto rispetto alla base diventa
    /// notifica (al posto di quella vecchia sullo stesso lavoro); cio' che non c'e' piu' segna
    /// le notifiche come risolte.
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

    // Un file illeggibile vale come assente: si riparte senza notifiche e senza base.
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

    // Se non si riesce a scrivere, le notifiche valgono fino alla chiusura.
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
