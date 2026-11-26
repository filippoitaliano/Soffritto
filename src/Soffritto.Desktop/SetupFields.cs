using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Soffritto.Desktop;

/// <summary>
/// The “i” button next to a setup label: on hover it shows the description, on click it keeps it
/// open until you click elsewhere. Its look is in the SetupPanel style.
/// </summary>
public sealed class InfoTip : ToggleButton
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

/// <summary>
/// The label of a setup field: the name, the “i” button with the description (<see cref="Info"/>),
/// the padlock of locked fields (<see cref="LockText"/>) and a line below for what must always be
/// read (<see cref="Hint"/>). Parts without text are not drawn.
/// </summary>
public sealed class FieldLabel : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(FieldLabel));

    public static readonly DependencyProperty InfoProperty =
        DependencyProperty.Register(nameof(Info), typeof(string), typeof(FieldLabel));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(FieldLabel));

    public static readonly DependencyProperty LockTextProperty =
        DependencyProperty.Register(nameof(LockText), typeof(string), typeof(FieldLabel));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Info
    {
        get => (string?)GetValue(InfoProperty);
        set => SetValue(InfoProperty, value);
    }

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Why the field is read-only; null = the field is editable.</summary>
    public string? LockText
    {
        get => (string?)GetValue(LockTextProperty);
        set => SetValue(LockTextProperty, value);
    }
}
