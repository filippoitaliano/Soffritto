using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Gli strumenti di un servizio, accanto al nome nella sua card: apri, stato, avvia, ferma.</summary>
public sealed class ToolGroupViewModel : ObservableObject
{
    private string stateColor = ConsoleServices.ColorOf(ServiceState.Stopped);
    private string stateToolTip = string.Empty;
    private bool canStop;
    private bool toggleEnabled;
    private string toggleGlyph = PlayGlyph;
    private string toggleToolTip = string.Empty;
    private bool sideEnabled;
    private string sideToolTip = string.Empty;

    // Glifi Segoe Fluent Icons di avvia (Play) e ferma (Stop).
    private const string PlayGlyph = "";
    private const string StopGlyph = "";

    public ToolGroupViewModel(string openText, string openToolTip)
    {
        OpenText = openText;
        OpenToolTip = openToolTip;

        // Un solo bottone: ferma cio' che c'e' di acceso, altrimenti avvia.
        ToggleCommand = new AsyncCommand(() =>
        {
            (canStop ? StopCommand : StartCommand)?.Execute(null);
            return Task.CompletedTask;
        });
    }

    public string OpenText { get; }

    public string OpenToolTip { get; }

    public ICommand? OpenCommand { get; set; }

    public ICommand? StartCommand { get; set; }

    public ICommand? StopCommand { get; set; }

    /// <summary>Falso per i repository che non avviano niente (generici): il bottone avvia/ferma non c'e'.</summary>
    public bool HasToggle { get; set; } = true;

    public string ToggleVisibility => HasToggle ? "Visible" : "Collapsed";

    /// <summary>Un comando in piu' del repository (genapi nel frontend): il bottone c'e' solo se e' impostato.</summary>
    public ICommand? ExtraCommand { get; set; }

    public string ExtraText { get; set; } = string.Empty;

    public string ExtraToolTip { get; set; } = string.Empty;

    public string ExtraVisibility => ExtraCommand is null ? "Collapsed" : "Visible";

    /// <summary>
    /// Un'azione in piu' (localhost nel frontend, il bundler Metro nell'app): il bottone c'e' solo
    /// se e' impostata, a icona con <see cref="SideGlyph"/> o a testo con <see cref="SideText"/>.
    /// </summary>
    public ICommand? SideCommand { get; set; }

    public string SideGlyph { get; set; } = string.Empty;

    public string SideText { get; set; } = string.Empty;

    public string SideIconVisibility => SideCommand is not null && SideText.Length == 0 ? "Visible" : "Collapsed";

    public string SideTextVisibility => SideCommand is not null && SideText.Length > 0 ? "Visible" : "Collapsed";

    public bool SideEnabled { get => sideEnabled; private set => SetProperty(ref sideEnabled, value); }

    public string SideToolTip { get => sideToolTip; private set => SetProperty(ref sideToolTip, value); }

    public void SetSide(bool enabled, string toolTip)
    {
        SideEnabled = enabled;
        SideToolTip = toolTip;
    }

    public string StateColor { get => stateColor; private set => SetProperty(ref stateColor, value); }

    public string StateToolTip { get => stateToolTip; private set => SetProperty(ref stateToolTip, value); }

    public ICommand ToggleCommand { get; }

    public bool ToggleEnabled { get => toggleEnabled; private set => SetProperty(ref toggleEnabled, value); }

    public string ToggleGlyph { get => toggleGlyph; private set => SetProperty(ref toggleGlyph, value); }

    public string ToggleToolTip { get => toggleToolTip; private set => SetProperty(ref toggleToolTip, value); }

    /// <summary>Il bottone ora ferma: e' rosso, cosi' cio' che e' acceso si vede a colpo d'occhio.</summary>
    public bool IsStop { get => canStop; private set => SetProperty(ref canStop, value); }

    /// <summary>Il bottone avvia/ferma: ferma se c'e' qualcosa di acceso, altrimenti avvia se e' ammesso.</summary>
    public void SetActions(bool canStart, bool canStop, string startToolTip, string? stopToolTip = null)
    {
        IsStop = canStop;
        ToggleEnabled = canStop || canStart;
        ToggleGlyph = canStop ? StopGlyph : PlayGlyph;
        ToggleToolTip = canStop ? stopToolTip ?? "Ferma (chiude la console e tutti i processi figli)" : startToolTip;
    }

    public void SetState(ServiceState state, string name)
    {
        StateColor = ConsoleServices.ColorOf(state);
        StateToolTip = $"{name}: {ConsoleServices.Describe(state)}";
    }

    /// <summary>Stato con un tooltip gia' composto (l'host del tunnel dice anche da dove lo sa).</summary>
    public void SetDescribedState(ServiceState state, string toolTip)
    {
        StateColor = ConsoleServices.ColorOf(state);
        StateToolTip = toolTip;
    }
}
