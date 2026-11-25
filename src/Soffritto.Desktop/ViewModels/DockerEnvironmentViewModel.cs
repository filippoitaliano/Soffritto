using System.Windows.Input;
using Soffritto.Core.Docker;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// La parte Docker di una colonna: lo slot (porte, progetto, database), la fase dei container,
/// gli avvisi di docker e degli env, e i comandi che agiscono sull'ambiente intero.
/// </summary>
public sealed class DockerEnvironmentViewModel : ObservableObject
{
    private string unreachableText = string.Empty;
    private string envWarningText = string.Empty;
    private string phaseText = string.Empty;
    private bool isWarmingUp;
    private bool hasFailure;
    private bool isOff = true;

    /// <param name="slot">Null se la configurazione non permette di calcolarlo: <paramref name="slotError"/> dice perche'.</param>
    public DockerEnvironmentViewModel(DockerSlot? slot, string? slotError)
    {
        Slot = slot;
        SlotError = slotError;
    }

    public DockerSlot? Slot { get; }

    public string? SlotError { get; }

    /// <summary>«Docker non raggiungibile» con il motivo; vuoto quando docker risponde.</summary>
    public string UnreachableText
    {
        get => unreachableText;
        private set
        {
            if (SetProperty(ref unreachableText, value))
            {
                OnPropertyChanged(nameof(UnreachableVisibility));
            }
        }
    }

    public string UnreachableVisibility => unreachableText.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Gli avvisi degli env dello slot (per esempio una variabile {poolUrl:&lt;id&gt;} senza porta del pool): solo nomi, mai valori.</summary>
    public string EnvWarningText
    {
        get => envWarningText;
        set
        {
            if (SetProperty(ref envWarningText, value))
            {
                OnPropertyChanged(nameof(EnvWarningVisibility));
            }
        }
    }

    public string EnvWarningVisibility => envWarningText.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Fase dei container a parole: «pronto», «spento: ▷ per avviare», «avvio: db pronto · api in avvio · web in avvio».</summary>
    public string PhaseText { get => phaseText; private set => SetProperty(ref phaseText, value); }

    /// <summary>Container in avvio: il pallino dell'ambiente pulsa, la card dell'ambiente scorre a righe.</summary>
    public bool IsWarmingUp { get => isWarmingUp; private set => SetProperty(ref isWarmingUp, value); }

    /// <summary>Un container in errore (uscito da solo, o acceso senza rispondere): pallino dell'ambiente rosso.</summary>
    public bool HasFailure { get => hasFailure; private set => SetProperty(ref hasFailure, value); }

    /// <summary>Tutti i container spenti: il pallino dell'ambiente resta grigio.</summary>
    public bool IsOff { get => isOff; private set => SetProperty(ref isOff, value); }

    internal void ApplyPhase(DockerReadiness.Snapshot snapshot)
    {
        PhaseText = snapshot.Text;
        IsWarmingUp = snapshot.IsWarmingUp;
        HasFailure = snapshot.Services.Any(s => s.State == DockerReadiness.ServiceState.Failed);
        IsOff = snapshot.Phase == DockerReadiness.Phase.Off;
    }

    public ICommand? DestroyCommand { get; set; }

    public ICommand? RepairEnvCommand { get; set; }

    /// <summary>L'esito dell'ultima lettura di docker ps: <paramref name="dockerError"/> se docker non risponde.</summary>
    public void ApplyDocker(string? dockerError) => UnreachableText = dockerError ?? string.Empty;
}
