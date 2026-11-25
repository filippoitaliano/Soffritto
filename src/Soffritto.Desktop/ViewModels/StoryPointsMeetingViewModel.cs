using System.Collections.ObjectModel;
using System.Globalization;
using System.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Soffritto.Core;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

public enum MeetingPhase
{
    Start,
    Loading,
    Recap,
    Expert,
    Description,
    Thinking,
    Votes,
    Finished,
}

/// <summary>
/// I colori della riunione, pensata per un televisore: un accento per fase, cosi' da lontano si
/// capisce a che punto si e' anche senza leggere. Nel tema chiaro gli accenti sono piu' scuri,
/// perche' stanno anche come testo su fondo bianco; <see cref="OnAccent"/> e' il testo sopra un
/// accento. Il cronometro della descrizione passa a <see cref="Warning"/> dopo un minuto e a
/// <see cref="Expired"/> dopo due. Fondi e testi neutri li tiene il pannello
/// (StoryPointsMeetingPanel.ApplyTheme).
/// </summary>
public sealed record MeetingPalette(string Expert, string Description, string Thinking, string Votes, string Warning, string Expired, string OnAccent)
{
    public static MeetingPalette Dark { get; } = new("#A78BFA", "#60A5FA", "#FBBF24", "#34D399", "#FBBF24", "#F87171", "#0B1220");

    public static MeetingPalette Light { get; } = new("#7C3AED", "#2563EB", "#B45309", "#047857", "#B45309", "#DC2626", "#FFFFFF");
}

/// <summary>
/// Un partecipante della riunione: ha una sigla modificabile, chi conduce lo sceglie come esperto
/// e ne scrive il voto.
/// </summary>
public sealed class MeetingParticipant : ObservableObject
{
    private readonly int number;
    private readonly Action votesChanged;
    private readonly Action initialsChanged;
    private string initials;
    private string voteText = string.Empty;
    private bool isExpert;

    public MeetingParticipant(int number, string initials, Action<MeetingParticipant> chooseExpert, Action votesChanged, Action initialsChanged)
    {
        this.number = number;
        this.initials = initials;
        this.votesChanged = votesChanged;
        this.initialsChanged = initialsChanged;
        ChooseExpertCommand = new RelayCommand(_ => chooseExpert(this));
    }

    public string Initials
    {
        get => initials;
        set
        {
            if (SetProperty(ref initials, value))
            {
                OnPropertyChanged(nameof(Label));
                initialsChanged();
            }
        }
    }

    /// <summary>La sigla come si mostra: senza sigla vale la posizione (P5).</summary>
    public string Label => initials.Trim() is { Length: > 0 } trimmed ? trimmed.ToUpperInvariant() : $"P{number}";

    public ICommand ChooseExpertCommand { get; }

    public bool IsExpert { get => isExpert; set => SetProperty(ref isExpert, value); }

    /// <summary>Vuoto vale come astensione; un testo che non e' un numero non e' un voto.</summary>
    public string VoteText
    {
        get => voteText;
        set
        {
            if (SetProperty(ref voteText, value))
            {
                OnPropertyChanged(nameof(HasInvalidVote));
                votesChanged();
            }
        }
    }

    public double? Vote => TryParse(voteText);

    public bool HasInvalidVote => voteText.Trim().Length > 0 && Vote is null;

    public void Clear()
    {
        IsExpert = false;
        VoteText = string.Empty;
    }

    private static double? TryParse(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0
            && (double.TryParse(trimmed, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
                || double.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            && value >= 0
                ? value
                : null;
    }
}

/// <summary>Un valore della scala nella fase dei voti: quello piu' vicino alla media e' gia' scelto.</summary>
public sealed class PointsChoice : ObservableObject
{
    private bool isSelected;

    public PointsChoice(int value, Action<PointsChoice> select)
    {
        Value = value;
        SelectCommand = new RelayCommand(_ => select(this));
    }

    public int Value { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
}

/// <summary>Una tappa nella striscia delle fasi o delle storie: fatta, in corso o da fare.</summary>
public sealed class MeetingStep : ObservableObject
{
    private readonly string number;
    private bool isCurrent;
    private bool isDone;

    public MeetingStep(string number, string label)
    {
        this.number = number;
        Label = label;
    }

    public string Label { get; }

    public string Mark => isDone ? "✓" : number;

    public bool IsCurrent { get => isCurrent; private set => SetProperty(ref isCurrent, value); }

    public bool IsDone
    {
        get => isDone;
        private set
        {
            if (SetProperty(ref isDone, value))
            {
                OnPropertyChanged(nameof(Mark));
            }
        }
    }

    public void Show(bool done, bool current)
    {
        IsDone = done;
        IsCurrent = current;
    }
}

/// <summary>Come e' andata una storia, per il riepilogo di fine riunione.</summary>
public sealed record MeetingOutcome(string WorkItem, string Title, string Result, int? Points)
{
    public bool IsSkipped => Points is null;
}

/// <summary>Una storia nell'elenco del recap prima di partire.</summary>
public sealed record RecapStory(string WorkItem, string Title, string Feature);

/// <summary>
/// La riunione dei punti storia, un gioco a turni e a tempo: un turno per storia utente da
/// stimare. Prima di partire un recap dice quante storie ci sono e quanto durera' circa. Le fasi
/// sono la scelta dell'esperto, la sua descrizione, il tempo per pensare e i voti, di cui la media
/// si arrotonda al valore della scala piu' vicino. Si avanza sempre a mano con «Avanti»: solo la
/// descrizione ha un cronometro, che diventa giallo dopo un minuto e rosso dopo due ma continua a
/// contare. I punti si scrivono sul work item solo con «Salva».
/// </summary>
public sealed class StoryPointsMeetingViewModel : ObservableObject
{
    private const int MinParticipants = 2;
    private const int MaxParticipants = 20;

    // I tempi realistici delle fasi: non sono piu' timer, ma restano la base della durata stimata.
    // DescriptionTime e' anche la soglia del giallo del cronometro; il doppio e' quella del rosso.
    private static readonly TimeSpan ExpertTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DescriptionTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DescriptionLate = DescriptionTime * 2;
    private static readonly TimeSpan ThinkingTime = TimeSpan.FromSeconds(10);

    // I voti non hanno timer: per la stima della durata contano giro di voti, eventuale scelta
    // di un altro valore e salvataggio.
    private static readonly TimeSpan VotesEstimate = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan PerStory = ExpertTime + DescriptionTime + ThinkingTime + VotesEstimate;

    private readonly Func<Task<StoriesToEstimate>> read;
    private readonly Func<int, int, Task<string?>> save;
    private readonly Action<IReadOnlyList<string>, bool> remember;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private IReadOnlyList<StoryToEstimate> stories = [];
    private int index;
    private MeetingPhase phase = MeetingPhase.Start;
    private string errorLine = string.Empty;
    private bool isSaving;
    private bool isEditing;
    private bool isLight;
    private DateTime startedAt;
    private DateTime finishedAt;

    // Il cronometro della descrizione: l'inizio mentre corre, il tempo fermo mentre e' in pausa.
    private DateTime phaseStart;
    private TimeSpan pausedElapsed;
    private bool isPaused;
    private int signals;

    /// <param name="read">Da dove arrivano le storie: Azure DevOps, o dati inventati nella demo.</param>
    /// <param name="save">Scrive i punti; null se riuscito, altrimenti il motivo.</param>
    /// <param name="initials">Le sigle con cui parte la riunione.</param>
    /// <param name="remember">Ricorda sigle e tema per la volta dopo (non in demo).</param>
    public StoryPointsMeetingViewModel(
        Func<Task<StoriesToEstimate>> read,
        Func<int, int, Task<string?>> save,
        Func<Uri, ICommand> open,
        Func<int, Uri?> workItemUrl,
        IReadOnlyList<string> initials,
        bool isLight,
        Action<IReadOnlyList<string>, bool> remember)
    {
        this.read = read;
        this.save = save;
        this.remember = remember;
        this.isLight = isLight;

        foreach (var sigla in initials.Take(MaxParticipants))
        {
            AddParticipant(sigla);
        }

        Choices = new ObservableCollection<PointsChoice>(StoryPointsScale.Values.Select(v => new PointsChoice(v, Select)));
        PhaseSteps =
        [
            new MeetingStep("1", "Esperto"),
            new MeetingStep("2", "Descrizione"),
            new MeetingStep("3", "Pensare"),
            new MeetingStep("4", "Voti"),
        ];

        LessCommand = new RelayCommand(_ => RemoveParticipant());
        MoreCommand = new RelayCommand(_ => AddParticipant(string.Empty, rememberNow: true));
        DarkCommand = new RelayCommand(_ => IsLight = false);
        LightCommand = new RelayCommand(_ => IsLight = true);
        PrepareCommand = new AsyncCommand(PrepareAsync);
        BackCommand = new RelayCommand(_ => Reset());
        BeginCommand = new RelayCommand(_ => Begin());
        NextCommand = new RelayCommand(_ => Next());
        PauseCommand = new RelayCommand(_ => TogglePause());
        EditCommand = new RelayCommand(_ => SetEditing(true));
        UseAverageCommand = new RelayCommand(_ => VotesChanged());
        SaveCommand = new AsyncCommand(SaveAsync);
        SkipCommand = new RelayCommand(_ => Finish("saltata", null));
        NewMeetingCommand = new RelayCommand(_ => Reset());
        OpenStoryCommand = new RelayCommand(_ =>
        {
            if (Story is { } story && workItemUrl(story.Id) is { } url)
            {
                open(url).Execute(null);
            }
        });
        OpenFeatureCommand = new RelayCommand(_ =>
        {
            if (Story is { FeatureId: { } featureId } && workItemUrl(featureId) is { } url)
            {
                open(url).Execute(null);
            }
        });

        timer.Tick += (_, _) => Tick();
    }

    public ObservableCollection<MeetingParticipant> Participants { get; } = [];

    public ObservableCollection<PointsChoice> Choices { get; }

    public ObservableCollection<MeetingOutcome> Outcomes { get; } = [];

    public ObservableCollection<RecapStory> RecapStories { get; } = [];

    public IReadOnlyList<MeetingStep> PhaseSteps { get; }

    /// <summary>Un pallino per storia, in cima durante i turni.</summary>
    public ObservableCollection<MeetingStep> StoryDots { get; } = [];

    public ICommand LessCommand { get; }

    public ICommand MoreCommand { get; }

    public ICommand DarkCommand { get; }

    public ICommand LightCommand { get; }

    public ICommand PrepareCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand BeginCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand UseAverageCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand SkipCommand { get; }

    public ICommand NewMeetingCommand { get; }

    public ICommand OpenStoryCommand { get; }

    public ICommand OpenFeatureCommand { get; }

    public bool IsLight
    {
        get => isLight;
        set
        {
            if (SetProperty(ref isLight, value))
            {
                OnPropertyChanged(nameof(IsDark));
                OnPropertyChanged(nameof(Palette));
                OnPropertyChanged(nameof(PhaseColor));
                TimeChanged();
                Remember();
            }
        }
    }

    public bool IsDark => !isLight;

    public MeetingPalette Palette => isLight ? MeetingPalette.Light : MeetingPalette.Dark;

    public bool CanPrepare => Participants.Count is >= MinParticipants and <= MaxParticipants;

    public bool CanRemove => Participants.Count > MinParticipants;

    public bool CanAdd => Participants.Count < MaxParticipants;

    public string ScaleText => string.Join(" · ", StoryPointsScale.Values);

    public MeetingPhase Phase
    {
        get => phase;
        private set
        {
            if (SetProperty(ref phase, value))
            {
                foreach (var name in PhaseBound)
                {
                    OnPropertyChanged(name);
                }

                ShowSteps();
            }
        }
    }

    private static readonly string[] PhaseBound =
    [
        nameof(StartVisibility), nameof(LoadingVisibility), nameof(RecapVisibility), nameof(TurnVisibility),
        nameof(ExpertVisibility), nameof(ThinkingVisibility), nameof(VotesVisibility),
        nameof(BeforeVotesVisibility), nameof(StopwatchVisibility), nameof(FinishedVisibility), nameof(PhaseTitle), nameof(PhaseHint), nameof(PhaseColor),
    ];

    public string StartVisibility => Visible(phase == MeetingPhase.Start);

    public string LoadingVisibility => Visible(phase == MeetingPhase.Loading);

    public string RecapVisibility => Visible(phase == MeetingPhase.Recap);

    public string TurnVisibility => Visible(IsTurn);

    public string ExpertVisibility => Visible(phase == MeetingPhase.Expert);


    public string ThinkingVisibility => Visible(phase == MeetingPhase.Thinking);

    public string VotesVisibility => Visible(phase == MeetingPhase.Votes);

    /// <summary>Le tre fasi prima dei voti: titolo, comandi della fase e «Avanti»; nei voti il risultato.</summary>
    public string BeforeVotesVisibility => Visible(phase is MeetingPhase.Expert or MeetingPhase.Description or MeetingPhase.Thinking);

    public string StopwatchVisibility => Visible(phase == MeetingPhase.Description);

    public string FinishedVisibility => Visible(phase == MeetingPhase.Finished);

    private bool IsTurn => phase is MeetingPhase.Expert or MeetingPhase.Description or MeetingPhase.Thinking or MeetingPhase.Votes;

    public string PhaseColor => phase switch
    {
        MeetingPhase.Expert => Palette.Expert,
        MeetingPhase.Description => Palette.Description,
        MeetingPhase.Thinking => Palette.Thinking,
        _ => Palette.Votes,
    };

    public string PhaseTitle => phase switch
    {
        MeetingPhase.Expert => "Chi è l'esperto?",
        MeetingPhase.Description => $"Parla {ExpertLabel}",
        MeetingPhase.Thinking => "Silenzio, si pensa",
        MeetingPhase.Votes => "Dite i vostri punti",
        _ => string.Empty,
    };

    public string PhaseHint => phase switch
    {
        MeetingPhase.Expert => "Scegliete chi conosce meglio quest'area",
        MeetingPhase.Description => "Circa un minuto per spiegare la storia",
        MeetingPhase.Thinking => "Ognuno decide il suo numero, senza dirlo",
        MeetingPhase.Votes => "Tutti insieme, poi si scrivono i numeri",
        _ => string.Empty,
    };

    private string ExpertLabel => Participants.FirstOrDefault(p => p.IsExpert)?.Label ?? "l'esperto";

    public StoryToEstimate? Story => index < stories.Count ? stories[index] : null;

    public string StoryCounter => $"Storia {Math.Min(index + 1, stories.Count)} di {stories.Count}";

    public string StoryId => Story is { } story ? $"#{story.Id}" : string.Empty;

    public string StoryTitle => Story?.Title ?? string.Empty;

    public string FeatureId => Story is { FeatureId: { } featureId } ? $"#{featureId}" : string.Empty;

    public string FeatureTitle => Story?.FeatureTitle ?? string.Empty;

    /// <summary>Un bug senza funzionalita' padre non ha la sezione della funzionalita'.</summary>
    public string FeatureSectionVisibility => Visible(Story?.FeatureId is not null);

    public string StoryKindLabel => Story?.IsBug == true ? "BUG" : "STORIA UTENTE";

    /// <summary>La descrizione com'e' sul work item, anche quando ripete il titolo.</summary>
    public string StoryDescription => Story?.Description ?? string.Empty;

    public string DescriptionSectionVisibility => Visible(StoryDescription.Length > 0);

    public string StoryCount => stories.Count.ToString(CultureInfo.CurrentCulture);

    public string StoryCountLabel => stories.Count == 1 ? "storia da stimare" : "storie da stimare";

    public string EstimatedDuration => $"~{Minutes(PerStory * stories.Count)}";

    public string EstimatedDurationHint => $"{Minutes(PerStory)} a storia: 10 s + 1 min + 10 s + ~{VotesEstimate.TotalSeconds:0} s di voti";

    public string EstimatedEnd => (DateTime.Now + PerStory * stories.Count).ToString("HH:mm", CultureInfo.CurrentCulture);

    public bool CanBegin => stories.Count > 0;

    public string NoStoriesVisibility => Visible(stories.Count == 0);

    public string TimeText
    {
        get
        {
            var elapsed = Elapsed;
            return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        }
    }

    /// <summary>
    /// L'anello si riempie in un minuto, blu e poi di nuovo giallo; dal secondo minuto in poi
    /// resta pieno e rosso.
    /// </summary>
    public double TimeRatio
    {
        get
        {
            var elapsed = Elapsed;
            return elapsed >= DescriptionLate ? 1 : (double)(elapsed.Ticks % DescriptionTime.Ticks) / DescriptionTime.Ticks;
        }
    }

    public string TimeColor
    {
        get
        {
            var elapsed = Elapsed;
            return elapsed >= DescriptionLate ? Palette.Expired : elapsed >= DescriptionTime ? Palette.Warning : Palette.Description;
        }
    }

    public string PauseLabel => isPaused ? "Riprendi" : "Pausa";

    private TimeSpan Elapsed => isPaused ? pausedElapsed : DateTime.UtcNow - phaseStart;

    public string VotesSummary
    {
        get
        {
            var invalid = Participants.Where(p => p.HasInvalidVote).Select(p => p.Label).ToList();
            if (invalid.Count > 0)
            {
                return $"{string.Join(", ", invalid)}: non è un numero";
            }

            var votes = Votes();
            return votes.Count == 0
                ? "Nessun voto ancora"
                : $"{votes.Count} voti su {Participants.Count} · media {votes.Average().ToString("0.#", CultureInfo.CurrentCulture)}";
        }
    }

    public string SelectedPoints => Selected?.Value.ToString(CultureInfo.CurrentCulture) ?? "–";

    private PointsChoice? Selected => Choices.FirstOrDefault(c => c.IsSelected);

    private int? Nearest => Votes() is { Count: > 0 } votes ? StoryPointsScale.Nearest(votes.Average()) : null;

    private bool IsManual => Selected is { } selected && selected.Value != Nearest;

    /// <summary>Da dove viene il valore nel cerchio: la media, o una scelta a mano che la cambia.</summary>
    public string PointsNote => Selected is null
        ? "scrivete i voti per avere la media"
        : IsManual ? $"cambiato a mano · la media dava {Nearest}" : "il valore più vicino alla media";

    /// <summary>La scala si vede solo dopo «Modifica»: di norma vale la media.</summary>
    public string ChoicesVisibility => Visible(isEditing);

    public string EditVisibility => Visible(!isEditing && Selected is not null);

    public string UseAverageVisibility => Visible(IsManual);

    public bool CanSave => !isSaving && Votes().Count > 0 && Participants.All(p => !p.HasInvalidVote) && Choices.Any(c => c.IsSelected);

    public string SaveLabel => isSaving ? "Salvataggio…" : "Salva e avanti";

    public string ErrorLine
    {
        get => errorLine;
        private set
        {
            if (SetProperty(ref errorLine, value))
            {
                OnPropertyChanged(nameof(ErrorVisibility));
            }
        }
    }

    public string ErrorVisibility => Visible(errorLine.Length > 0);

    public string FinishedSummary
    {
        get
        {
            var saved = Outcomes.Count(o => o.Points is not null);
            var skipped = Outcomes.Count - saved;
            return $"{saved} {(saved == 1 ? "storia stimata" : "storie stimate")}"
                + (skipped > 0 ? $", {skipped} {(skipped == 1 ? "saltata" : "saltate")}" : string.Empty)
                + $" · {Outcomes.Sum(o => o.Points ?? 0)} punti in tutto";
        }
    }

    public string FinishedDuration
        => $"Dalle {startedAt.ToString("HH:mm", CultureInfo.CurrentCulture)} alle {finishedAt.ToString("HH:mm", CultureInfo.CurrentCulture)}"
            + $" · durata {Minutes(finishedAt - startedAt)}, stimata {Minutes(PerStory * stories.Count)}";

    /// <summary>Ferma il timer: la riunione resta dov'e', ma non suona piu'.</summary>
    public void Stop() => timer.Stop();

    private void AddParticipant(string initials, bool rememberNow = false)
    {
        if (Participants.Count >= MaxParticipants)
        {
            return;
        }

        Participants.Add(new MeetingParticipant(Participants.Count + 1, initials, ChooseExpert, VotesChanged, Remember));
        RosterChanged(rememberNow);
    }

    private void RemoveParticipant()
    {
        if (Participants.Count <= MinParticipants)
        {
            return;
        }

        Participants.RemoveAt(Participants.Count - 1);
        RosterChanged(rememberNow: true);
    }

    private void RosterChanged(bool rememberNow)
    {
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanAdd));
        if (rememberNow)
        {
            Remember();
        }
    }

    private void Remember() => remember(Participants.Select(p => p.Initials.Trim().ToUpperInvariant()).ToList(), isLight);

    private async Task PrepareAsync()
    {
        if (!CanPrepare || phase != MeetingPhase.Start)
        {
            return;
        }

        ErrorLine = string.Empty;
        Phase = MeetingPhase.Loading;

        var result = await read();
        if (result.Error is not null)
        {
            ErrorLine = result.Error;
            Phase = MeetingPhase.Start;
            return;
        }

        stories = result.Stories;
        RecapStories.Clear();
        foreach (var story in stories)
        {
            var feature = story.IsBug ? string.Join(" · ", new[] { "Bug", story.FeatureTitle }.Where(t => t.Length > 0)) : story.FeatureTitle;
            RecapStories.Add(new RecapStory($"#{story.Id}", story.Title, feature));
        }

        foreach (var name in new[] { nameof(StoryCount), nameof(StoryCountLabel), nameof(EstimatedDuration), nameof(EstimatedDurationHint), nameof(EstimatedEnd), nameof(CanBegin), nameof(NoStoriesVisibility) })
        {
            OnPropertyChanged(name);
        }

        Phase = MeetingPhase.Recap;
    }

    private void Begin()
    {
        if (phase != MeetingPhase.Recap || stories.Count == 0)
        {
            return;
        }

        Outcomes.Clear();
        StoryDots.Clear();
        for (var n = 1; n <= stories.Count; n++)
        {
            StoryDots.Add(new MeetingStep(n.ToString(CultureInfo.CurrentCulture), string.Empty));
        }

        startedAt = DateTime.Now;
        index = 0;
        BeginTurn();
    }

    private void BeginTurn()
    {
        if (Story is null)
        {
            timer.Stop();
            finishedAt = DateTime.Now;
            Phase = MeetingPhase.Finished;
            OnPropertyChanged(nameof(FinishedSummary));
            OnPropertyChanged(nameof(FinishedDuration));
            return;
        }

        foreach (var participant in Participants)
        {
            participant.Clear();
        }

        ErrorLine = string.Empty;
        foreach (var name in new[] { nameof(Story), nameof(StoryCounter), nameof(StoryId), nameof(StoryTitle), nameof(FeatureId), nameof(FeatureTitle), nameof(FeatureSectionVisibility), nameof(StoryKindLabel), nameof(StoryDescription), nameof(DescriptionSectionVisibility) })
        {
            OnPropertyChanged(name);
        }

        for (var n = 0; n < StoryDots.Count; n++)
        {
            StoryDots[n].Show(done: n < index, current: n == index);
        }

        Enter(MeetingPhase.Expert);
    }

    private void Enter(MeetingPhase next)
    {
        Phase = next;

        phaseStart = DateTime.UtcNow;
        pausedElapsed = TimeSpan.Zero;
        isPaused = false;
        signals = 0;

        if (next == MeetingPhase.Description)
        {
            timer.Start();
        }
        else
        {
            timer.Stop();
        }

        if (next == MeetingPhase.Votes)
        {
            VotesChanged();
        }

        TimeChanged();
        OnPropertyChanged(nameof(PauseLabel));
    }

    private void ShowSteps()
    {
        var current = phase switch
        {
            MeetingPhase.Expert => 0,
            MeetingPhase.Description => 1,
            MeetingPhase.Thinking => 2,
            MeetingPhase.Votes => 3,
            _ => -1,
        };

        for (var n = 0; n < PhaseSteps.Count; n++)
        {
            PhaseSteps[n].Show(done: n < current, current: n == current);
        }
    }

    private void Next()
    {
        switch (phase)
        {
            case MeetingPhase.Expert:
                Enter(MeetingPhase.Description);
                break;
            case MeetingPhase.Description:
                Enter(MeetingPhase.Thinking);
                break;
            case MeetingPhase.Thinking:
                Enter(MeetingPhase.Votes);
                break;
        }
    }

    private void TogglePause()
    {
        if (phase != MeetingPhase.Description)
        {
            return;
        }

        if (isPaused)
        {
            phaseStart = DateTime.UtcNow - pausedElapsed;
            isPaused = false;
            timer.Start();
        }
        else
        {
            pausedElapsed = Elapsed;
            isPaused = true;
            timer.Stop();
        }

        OnPropertyChanged(nameof(PauseLabel));
        TimeChanged();
    }

    // Il cronometro non ferma la fase: suona solo quando diventa giallo e quando diventa rosso.
    private void Tick()
    {
        var elapsed = Elapsed;
        if ((signals == 0 && elapsed >= DescriptionTime) || (signals == 1 && elapsed >= DescriptionLate))
        {
            signals++;
            SystemSounds.Exclamation.Play();
        }

        TimeChanged();
    }

    private void TimeChanged()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(TimeRatio));
        OnPropertyChanged(nameof(TimeColor));
    }

    private void ChooseExpert(MeetingParticipant expert)
    {
        foreach (var participant in Participants)
        {
            participant.IsExpert = participant == expert;
        }

        OnPropertyChanged(nameof(PhaseTitle));
    }

    // Ogni voto cambiato (e «Usa la media») ripropone il valore della scala piu' vicino alla media
    // e richiude la scala: una scelta a mano fatta prima non vale piu' per i voti nuovi.
    private void VotesChanged()
    {
        var nearest = Nearest;
        foreach (var choice in Choices)
        {
            choice.IsSelected = choice.Value == nearest;
        }

        isEditing = false;
        SaveStateChanged();
        OnPropertyChanged(nameof(VotesSummary));
    }

    // Un clic sulla scala sceglie e la richiude: il cerchio dice che il valore e' cambiato a mano.
    private void Select(PointsChoice selected)
    {
        foreach (var choice in Choices)
        {
            choice.IsSelected = choice == selected;
        }

        isEditing = false;
        SaveStateChanged();
    }

    private void SetEditing(bool editing)
    {
        isEditing = editing;
        SaveStateChanged();
    }

    private void SaveStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(SelectedPoints));
        OnPropertyChanged(nameof(PointsNote));
        OnPropertyChanged(nameof(ChoicesVisibility));
        OnPropertyChanged(nameof(EditVisibility));
        OnPropertyChanged(nameof(UseAverageVisibility));
    }

    private List<double> Votes() => Participants.Select(p => p.Vote).OfType<double>().ToList();

    private async Task SaveAsync()
    {
        if (!CanSave || Story is not { } story || Choices.FirstOrDefault(c => c.IsSelected) is not { } choice)
        {
            return;
        }

        isSaving = true;
        SaveStateChanged();
        ErrorLine = string.Empty;

        var error = await save(story.Id, choice.Value);

        isSaving = false;
        SaveStateChanged();

        if (error is not null)
        {
            ErrorLine = $"Punti non salvati su #{story.Id}: {error}";
            return;
        }

        Finish($"{choice.Value} punti", choice.Value);
    }

    private void Finish(string result, int? points)
    {
        if (Story is not { } story || isSaving)
        {
            return;
        }

        Outcomes.Add(new MeetingOutcome($"#{story.Id}", story.Title, result, points));
        index++;
        BeginTurn();
    }

    // I partecipanti restano con le loro sigle: si perdono solo voti ed esperto.
    private void Reset()
    {
        timer.Stop();
        stories = [];
        index = 0;
        Outcomes.Clear();
        RecapStories.Clear();
        StoryDots.Clear();
        foreach (var participant in Participants)
        {
            participant.Clear();
        }

        ErrorLine = string.Empty;
        Phase = MeetingPhase.Start;
    }

    private static string Minutes(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes < 1 ? "meno di un minuto" : minutes == 1 ? "1 min" : $"{minutes} min";
    }

    private static string Visible(bool visible) => visible ? "Visible" : "Collapsed";
}
