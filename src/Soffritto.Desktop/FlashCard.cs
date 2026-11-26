using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

public enum FlashKind
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Something is running: info colours, with the badge spinning.</summary>
    Progress,
}

/// <summary>
/// A message box in the app's single style: tinted background, round badge with the kind's glyph,
/// text on the right. The look lives in the FlashCard style in App.xaml; text content wraps, and
/// null or empty text hides the card.
/// </summary>
public sealed class FlashCard : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(FlashKind),
        typeof(FlashCard),
        new PropertyMetadata(FlashKind.Info));

    static FlashCard()
    {
        FocusableProperty.OverrideMetadata(typeof(FlashCard), new FrameworkPropertyMetadata(false));
    }

    public FlashKind Kind
    {
        get => (FlashKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
