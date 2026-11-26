using System.Windows.Input;
using Soffritto.Core.Docker;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// The Docker part of a column: the slot (ports, project, database), the container phase,
/// the docker and env warnings, and the commands that act on the whole environment.
/// </summary>
public sealed class DockerEnvironmentViewModel : ObservableObject
{
    private string unreachableText = string.Empty;
    private string envWarningText = string.Empty;
    private string phaseText = string.Empty;
    private bool isWarmingUp;
    private bool hasFailure;
    private bool isOff = true;

    /// <param name="slot">Null if the configuration does not allow computing it: <paramref name="slotError"/> says why.</param>
    public DockerEnvironmentViewModel(DockerSlot? slot, string? slotError)
    {
        Slot = slot;
        SlotError = slotError;
    }

    public DockerSlot? Slot { get; }

    public string? SlotError { get; }

    /// <summary>“Docker unreachable” with the reason; empty when docker answers.</summary>
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

    /// <summary>Warnings about the slot's env files (for example a {poolUrl:&lt;id&gt;} variable without a pool port): names only, never values.</summary>
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

    /// <summary>Container phase in words: “ready”, “off: ▷ to start”, “starting: db ready · api starting · web starting”.</summary>
    public string PhaseText { get => phaseText; private set => SetProperty(ref phaseText, value); }

    /// <summary>Containers starting: the environment dot pulses and the environment card shows moving stripes.</summary>
    public bool IsWarmingUp { get => isWarmingUp; private set => SetProperty(ref isWarmingUp, value); }

    /// <summary>A failed container (exited by itself, or running without answering): red environment dot.</summary>
    public bool HasFailure { get => hasFailure; private set => SetProperty(ref hasFailure, value); }

    /// <summary>All containers off: the environment dot stays grey.</summary>
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

    /// <summary>The result of the last docker ps read: <paramref name="dockerError"/> if docker does not answer.</summary>
    public void ApplyDocker(string? dockerError) => UnreachableText = dockerError ?? string.Empty;
}
