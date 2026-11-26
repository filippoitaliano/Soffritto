using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>A service's tools, next to its name on its card: open, status, start, stop.</summary>
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

    // Segoe Fluent Icons glyphs for start (Play) and stop (Stop).
    private const string PlayGlyph = "";
    private const string StopGlyph = "";

    public ToolGroupViewModel(string openText, string openToolTip)
    {
        OpenText = openText;
        OpenToolTip = openToolTip;

        // A single button: stops whatever is running, otherwise starts.
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

    /// <summary>False for repositories that start nothing (generic ones): there is no start/stop button.</summary>
    public bool HasToggle { get; set; } = true;

    public string ToggleVisibility => HasToggle ? "Visible" : "Collapsed";

    /// <summary>An extra command of the repository (genapi in the frontend): the button is there only when it is set.</summary>
    public ICommand? ExtraCommand { get; set; }

    public string ExtraText { get; set; } = string.Empty;

    public string ExtraToolTip { get; set; } = string.Empty;

    public string ExtraVisibility => ExtraCommand is null ? "Collapsed" : "Visible";

    /// <summary>
    /// An extra action (localhost in the frontend, the Metro bundler in the app): the button is there only
    /// when it is set, as an icon with <see cref="SideGlyph"/> or as text with <see cref="SideText"/>.
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

    /// <summary>The button now stops: it is red, so whatever is running stands out at a glance.</summary>
    public bool IsStop { get => canStop; private set => SetProperty(ref canStop, value); }

    /// <summary>The start/stop button: stops if something is running, otherwise starts if allowed.</summary>
    public void SetActions(bool canStart, bool canStop, string startToolTip, string? stopToolTip = null)
    {
        IsStop = canStop;
        ToggleEnabled = canStop || canStart;
        ToggleGlyph = canStop ? StopGlyph : PlayGlyph;
        ToggleToolTip = canStop ? stopToolTip ?? "Stop (closes the console and all child processes)" : startToolTip;
    }

    public void SetState(ServiceState state, string name)
    {
        StateColor = ConsoleServices.ColorOf(state);
        StateToolTip = $"{name}: {ConsoleServices.Describe(state)}";
    }

    /// <summary>Status with a ready-made tooltip (the tunnel host also says where it got it from).</summary>
    public void SetDescribedState(ServiceState state, string toolTip)
    {
        StateColor = ConsoleServices.ColorOf(state);
        StateToolTip = toolTip;
    }
}
