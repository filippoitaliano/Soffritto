using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Soffritto.Desktop;

/// <summary>Colori dei tag delle lane: rosso per cio' che e' rotto, grigio per le altre anomalie.</summary>
public static class LaneTags
{
    public const string Error = "#C42B1C";
    public const string Neutral = "#5F6368";

    /// <summary>Etichette di tag e bottoni con l'iniziale maiuscola e il resto com'e'.</summary>
    public static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>Un'azione di una <see cref="Lane"/>: glifo Segoe Fluent Icons, comando, tooltip e abilitazione.</summary>
public sealed class LaneAction : Freezable
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(LaneAction), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(LaneAction));

    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.Register(
        nameof(ToolTip), typeof(object), typeof(LaneAction));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.Register(
        nameof(IsEnabled), typeof(bool), typeof(LaneAction), new PropertyMetadata(true));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public object? ToolTip { get => GetValue(ToolTipProperty); set => SetValue(ToolTipProperty, value); }

    public bool IsEnabled { get => (bool)GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }

    protected override Freezable CreateInstanceCore() => new LaneAction();
}

/// <summary>
/// Riga di una <see cref="LaneGrid"/> (stile in App.xaml): solo il testo e' obbligatorio; pallino
/// di stato, badge grigio, nota colorata e azioni compaiono quando ci sono. Ogni azione ha il suo
/// bottone a destra (LaneActionButton); la riga in se' non si clicca.
/// </summary>
/// <remarks>
/// Le azioni stanno in una FreezableCollection: cosi' ereditano il DataContext della lane e i loro
/// binding si scrivono in XAML come quelli di qualunque altro elemento.
/// </remarks>
[ContentProperty(nameof(Actions))]
public sealed class Lane : Button
{
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(Lane), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Lane), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note), typeof(string), typeof(Lane), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty NoteBrushProperty = DependencyProperty.Register(
        nameof(NoteBrush), typeof(Brush), typeof(Lane), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty DotProperty = DependencyProperty.Register(
        nameof(Dot), typeof(Brush), typeof(Lane), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(FreezableCollection<LaneAction>), typeof(Lane));

    private static readonly DependencyPropertyKey HasActionsKey = DependencyProperty.RegisterReadOnly(
        nameof(HasActions), typeof(bool), typeof(Lane), new PropertyMetadata(false));

    public static readonly DependencyProperty HasActionsProperty = HasActionsKey.DependencyProperty;

    public Lane()
    {
        var actions = new FreezableCollection<LaneAction>();
        ((INotifyCollectionChanged)actions).CollectionChanged += (_, _) => SetValue(HasActionsKey, Actions.Count > 0);
        SetValue(ActionsProperty, actions);
    }

    /// <summary>Testo del badge grigio a sinistra (numero di porta, posizione della migrazione); vuoto lo nasconde.</summary>
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Etichetta breve a destra del testo (stato, feedback); vuota la nasconde.</summary>
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    public Brush NoteBrush { get => (Brush)GetValue(NoteBrushProperty); set => SetValue(NoteBrushProperty, value); }

    /// <summary>Colore del pallino di stato a sinistra (tunnel dell'account); null lo nasconde.</summary>
    public Brush? Dot { get => (Brush?)GetValue(DotProperty); set => SetValue(DotProperty, value); }

    public FreezableCollection<LaneAction> Actions => (FreezableCollection<LaneAction>)GetValue(ActionsProperty);

    public bool HasActions => (bool)GetValue(HasActionsProperty);
}
