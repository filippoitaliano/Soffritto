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
/// The meeting colours, designed for a TV screen: one accent per phase, so you can tell from across
/// the room where things stand without reading. In the light theme the accents are darker, because
/// they also serve as text on a white background; <see cref="OnAccent"/> is the text on top of an
/// accent. The description stopwatch turns <see cref="Warning"/> after one minute and
/// <see cref="Expired"/> after two. Neutral backgrounds and texts belong to the panel
/// (StoryPointsMeetingPanel.ApplyTheme).
/// </summary>
public sealed record MeetingPalette(string Expert, string Description, string Thinking, string Votes, string Warning, string Expired, string OnAccent)
{
    public static MeetingPalette Dark { get; } = new("#A78BFA", "#60A5FA", "#FBBF24", "#34D399", "#FBBF24", "#F87171", "#1C1918");

    public static MeetingPalette Light { get; } = new("#7C3AED", "#2563EB", "#B45309", "#047857", "#B45309", "#DC2626", "#FFFFFF");
}

/// <summary>
/// The colours of the meeting's AppButtons (the main action of each screen), one per phase:
/// mid tones with white text, like the Tag colours of the app's AppButtons, the same in both
/// themes.
/// </summary>
public static class MeetingButtonColors
{
    public const string Expert = "#A98BE3";

    public const string Description = "#55B7EA";

    public const string Thinking = "#E5A33B";

    public const string Votes = "#3FB8A0";
}

/// <summary>
/// The colours Azure DevOps gives the work item types, fixed: they never change.
/// </summary>
public static class WorkItemColors
{
    public const string UserStory = "#009CCC";

    public const string Bug = "#CC293D";
}

/// <summary>
/// A meeting participant: has editable initials; the facilitator picks them as the expert and
/// types in their vote.
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

    /// <summary>The initials as shown: without initials, the position stands in (P5).</summary>
    public string Label => initials.Trim() is { Length: > 0 } trimmed ? trimmed.ToUpperInvariant() : $"P{number}";

    public ICommand ChooseExpertCommand { get; }

    public bool IsExpert { get => isExpert; set => SetProperty(ref isExpert, value); }

    /// <summary>Empty counts as abstaining; text that is not a number is not a vote.</summary>
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

/// <summary>A scale value in the voting phase: the one closest to the average is preselected.</summary>
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

/// <summary>A step in the phase or story strip: done, current or still to do.</summary>
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

/// <summary>How a story went, for the end-of-meeting summary.</summary>
public sealed record MeetingOutcome(string WorkItem, string Title, string Result, int? Points)
{
    public bool IsSkipped => Points is null;
}

/// <summary>A story in the recap list shown before starting; Color is its type's Azure DevOps colour.</summary>
public sealed record RecapStory(string WorkItem, string Title, string Feature, string Color);

/// <summary>
/// The story points meeting, a timed turn-based game: one turn per user story to estimate. Before
/// starting, a recap says how many stories there are and roughly how long it will take. The phases
/// are picking the expert, the expert's description, thinking time and the votes, whose average is
/// rounded to the nearest scale value. You always move on by hand with “Next”: only the description
/// has a stopwatch, which turns yellow after one minute and red after two but keeps counting. Points
/// are written to the work item only with “Save”.
/// </summary>
public sealed class StoryPointsMeetingViewModel : ObservableObject
{
    private const int MinParticipants = 2;
    private const int MaxParticipants = 20;

    // Realistic phase durations: they are no longer timers, but still drive the estimated duration.
    // DescriptionTime is also the stopwatch's yellow threshold; twice that is the red one.
    private static readonly TimeSpan ExpertTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DescriptionTime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DescriptionLate = DescriptionTime * 2;
    private static readonly TimeSpan ThinkingTime = TimeSpan.FromSeconds(10);

    // Votes have no timer: the duration estimate covers the round of votes, possibly picking
    // another value, and saving.
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

    // The description stopwatch: the start time while running, the frozen elapsed time while paused.
    private DateTime phaseStart;
    private TimeSpan pausedElapsed;
    private bool isPaused;
    private int signals;

    /// <param name="read">Where the stories come from: Azure DevOps, or made-up data in the demo.</param>
    /// <param name="save">Writes the points; null on success, otherwise the reason.</param>
    /// <param name="initials">The initials the meeting starts with.</param>
    /// <param name="remember">Remembers initials and theme for next time (not in the demo).</param>
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
            new MeetingStep("1", "Expert"),
            new MeetingStep("2", "Description"),
            new MeetingStep("3", "Think"),
            new MeetingStep("4", "Votes"),
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
        SkipCommand = new RelayCommand(_ => Finish("skipped", null));
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
        nameof(BeforeVotesVisibility), nameof(StopwatchVisibility), nameof(FinishedVisibility), nameof(PhaseTitle), nameof(PhaseHint), nameof(PhaseColor), nameof(PhaseButtonColor),
    ];

    public string StartVisibility => Visible(phase == MeetingPhase.Start);

    public string LoadingVisibility => Visible(phase == MeetingPhase.Loading);

    public string RecapVisibility => Visible(phase == MeetingPhase.Recap);

    public string TurnVisibility => Visible(IsTurn);

    public string ExpertVisibility => Visible(phase == MeetingPhase.Expert);


    public string ThinkingVisibility => Visible(phase == MeetingPhase.Thinking);

    public string VotesVisibility => Visible(phase == MeetingPhase.Votes);

    /// <summary>The three phases before the votes: title, phase commands and “Next”; during the votes, the result.</summary>
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

    public string PhaseButtonColor => phase switch
    {
        MeetingPhase.Expert => MeetingButtonColors.Expert,
        MeetingPhase.Description => MeetingButtonColors.Description,
        MeetingPhase.Thinking => MeetingButtonColors.Thinking,
        _ => MeetingButtonColors.Votes,
    };

    public string PhaseTitle => phase switch
    {
        MeetingPhase.Expert => "Who's the expert?",
        MeetingPhase.Description => $"{ExpertLabel} has the floor",
        MeetingPhase.Thinking => "Quiet, thinking time",
        MeetingPhase.Votes => "Call out your points",
        _ => string.Empty,
    };

    public string PhaseHint => phase switch
    {
        MeetingPhase.Expert => "Pick whoever knows this area best",
        MeetingPhase.Description => "About a minute to explain the story",
        MeetingPhase.Thinking => "Everyone picks a number without saying it",
        MeetingPhase.Votes => "All together, then type in the numbers",
        _ => string.Empty,
    };

    private string ExpertLabel => Participants.FirstOrDefault(p => p.IsExpert)?.Label ?? "The expert";

    public StoryToEstimate? Story => index < stories.Count ? stories[index] : null;

    public string StoryCounter => $"Story {Math.Min(index + 1, stories.Count)} of {stories.Count}";

    public string StoryId => Story is { } story ? $"#{story.Id}" : string.Empty;

    public string StoryTitle => Story?.Title ?? string.Empty;

    public string FeatureId => Story is { FeatureId: { } featureId } ? $"#{featureId}" : string.Empty;

    public string FeatureTitle => Story?.FeatureTitle ?? string.Empty;

    /// <summary>A bug without a parent feature has no feature section.</summary>
    public string FeatureSectionVisibility => Visible(Story?.FeatureId is not null);

    public string StoryKindLabel => Story?.IsBug == true ? "BUG" : "USER STORY";

    /// <summary>The Azure DevOps colour of the story's type, for the border of the story card.</summary>
    public string StoryKindColor => Story?.IsBug == true ? WorkItemColors.Bug : WorkItemColors.UserStory;

    /// <summary>The description as it is on the work item, even when it repeats the title.</summary>
    public string StoryDescription => Story?.Description ?? string.Empty;

    public string DescriptionSectionVisibility => Visible(StoryDescription.Length > 0);

    public string StoryCount => stories.Count.ToString(CultureInfo.CurrentCulture);

    public string StoryCountLabel => stories.Count == 1 ? "story to estimate" : "stories to estimate";

    public string EstimatedDuration => $"~{Minutes(PerStory * stories.Count)}";

    public string EstimatedDurationHint => $"{Minutes(PerStory)} per story: 10 s + 1 min + 10 s + ~{VotesEstimate.TotalSeconds:0} s of voting";

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
    /// The ring fills up in one minute, blue and then again in yellow; from the second minute on it
    /// stays full and red.
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

    public string PauseLabel => isPaused ? "Resume" : "Pause";

    private TimeSpan Elapsed => isPaused ? pausedElapsed : DateTime.UtcNow - phaseStart;

    public string VotesSummary
    {
        get
        {
            var invalid = Participants.Where(p => p.HasInvalidVote).Select(p => p.Label).ToList();
            if (invalid.Count > 0)
            {
                return $"{string.Join(", ", invalid)}: not a number";
            }

            var votes = Votes();
            return votes.Count == 0
                ? "No votes yet"
                : $"{votes.Count} of {Participants.Count} voted · average {votes.Average().ToString("0.#", CultureInfo.CurrentCulture)}";
        }
    }

    /// <summary>The value in the circle; empty until the votes give one.</summary>
    public string SelectedPoints => Selected?.Value.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>Whether the circle holds a value: without one it stays a faint shape.</summary>
    public bool HasPoints => Selected is not null;

    private PointsChoice? Selected => Choices.FirstOrDefault(c => c.IsSelected);

    private int? Nearest => Votes() is { Count: > 0 } votes ? StoryPointsScale.Nearest(votes.Average()) : null;

    private bool IsManual => Selected is { } selected && selected.Value != Nearest;

    /// <summary>The scale shows only after “Edit”: normally the average applies.</summary>
    public string ChoicesVisibility => Visible(isEditing);

    public string EditVisibility => Visible(!isEditing && Selected is not null);

    public string UseAverageVisibility => Visible(IsManual);

    public bool CanSave => !isSaving && Votes().Count > 0 && Participants.All(p => !p.HasInvalidVote) && Choices.Any(c => c.IsSelected);

    public string SaveLabel => isSaving ? "Saving…" : "Save and next";

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
            return $"{saved} {(saved == 1 ? "story estimated" : "stories estimated")}"
                + (skipped > 0 ? $", {skipped} skipped" : string.Empty)
                + $" · {Outcomes.Sum(o => o.Points ?? 0)} points in total";
        }
    }

    public string FinishedDuration
        => $"From {startedAt.ToString("HH:mm", CultureInfo.CurrentCulture)} to {finishedAt.ToString("HH:mm", CultureInfo.CurrentCulture)}"
            + $" · took {Minutes(finishedAt - startedAt)}, estimated {Minutes(PerStory * stories.Count)}";

    /// <summary>Stops the timer: the meeting stays where it is, but no longer beeps.</summary>
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
            RecapStories.Add(new RecapStory($"#{story.Id}", story.Title, feature, story.IsBug ? WorkItemColors.Bug : WorkItemColors.UserStory));
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
        foreach (var name in new[] { nameof(Story), nameof(StoryCounter), nameof(StoryId), nameof(StoryTitle), nameof(FeatureId), nameof(FeatureTitle), nameof(FeatureSectionVisibility), nameof(StoryKindLabel), nameof(StoryKindColor), nameof(StoryDescription), nameof(DescriptionSectionVisibility) })
        {
            OnPropertyChanged(name);
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

    // The stopwatch never ends the phase: it only beeps when it turns yellow and when it turns red.
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

    // Every changed vote (and “Use the average”) reselects the scale value closest to the average
    // and closes the scale again: an earlier manual pick no longer applies to the new votes.
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

    // A click on the scale picks a value and closes it: the circle says the value was changed by hand.
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
        OnPropertyChanged(nameof(HasPoints));
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
            ErrorLine = $"Points not saved on #{story.Id}: {error}";
            return;
        }

        Finish($"{choice.Value} points", choice.Value);
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

    // Participants keep their initials: only the votes and the expert are cleared.
    private void Reset()
    {
        timer.Stop();
        stories = [];
        index = 0;
        Outcomes.Clear();
        RecapStories.Clear();
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
        return minutes < 1 ? "less than a minute" : minutes == 1 ? "1 min" : $"{minutes} min";
    }

    private static string Visible(bool visible) => visible ? "Visible" : "Collapsed";
}
