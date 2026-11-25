using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Soffritto.Desktop;

/// <summary>
/// Il bottone «i» accanto a un'etichetta del setup: al passaggio del mouse mostra la descrizione,
/// al clic la tiene aperta finche' non si clicca altrove. Il disegno e' nello stile di SetupPanel.
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
/// L'etichetta di un campo del setup: il nome, il bottone «i» con la descrizione (<see cref="Info"/>),
/// il lucchetto dei campi bloccati (<see cref="LockText"/>) e una riga sotto per cio' che va letto
/// sempre (<see cref="Hint"/>). Le parti senza testo non si disegnano.
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

    /// <summary>Il motivo per cui il campo e' in sola lettura; null = il campo si modifica.</summary>
    public string? LockText
    {
        get => (string?)GetValue(LockTextProperty);
        set => SetValue(LockTextProperty, value);
    }
}
