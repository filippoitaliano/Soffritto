using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Soffritto.Desktop;

/// <summary>Lane tag colours: red for what is broken, grey for other anomalies.</summary>
public static class LaneTags
{
    public const string Error = "#C42B1C";
    public const string Neutral = "#5F6368";

    /// <summary>Tag and button labels with the first letter capitalised and the rest as is.</summary>
    public static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>An action of a <see cref="Lane"/>: Segoe Fluent Icons glyph, command, tooltip and enabled state.</summary>
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
/// Row of a <see cref="LaneGrid"/> (style in App.xaml): only the text is required; status dot,
/// grey badge, coloured note and actions appear when present. Each action has its own button
/// on the right (LaneActionButton); the row itself is not clickable.
/// </summary>
/// <remarks>
/// The actions live in a FreezableCollection: this way they inherit the lane's DataContext and their
/// bindings are written in XAML like those of any other element.
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

    /// <summary>Text of the grey badge on the left (port number, migration position); empty hides it.</summary>
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Short label to the right of the text (status, feedback); empty hides it.</summary>
    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    public Brush NoteBrush { get => (Brush)GetValue(NoteBrushProperty); set => SetValue(NoteBrushProperty, value); }

    /// <summary>Colour of the status dot on the left (account tunnels); null hides it.</summary>
    public Brush? Dot { get => (Brush?)GetValue(DotProperty); set => SetValue(DotProperty, value); }

    public FreezableCollection<LaneAction> Actions => (FreezableCollection<LaneAction>)GetValue(ActionsProperty);

    public bool HasActions => (bool)GetValue(HasActionsProperty);
}
