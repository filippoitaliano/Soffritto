using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

public partial class StoryPointsMeetingPanel : UserControl
{
    public StoryPointsMeetingPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The panel's X: the host decides how to hide it.</summary>
    public event Action? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is StoryPointsMeetingViewModel old)
        {
            old.PropertyChanged -= OnMeetingChanged;
        }

        if (e.NewValue is StoryPointsMeetingViewModel meeting)
        {
            meeting.PropertyChanged += OnMeetingChanged;
            ApplyTheme(meeting);
        }
    }

    private void OnMeetingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StoryPointsMeetingViewModel.IsLight) && sender is StoryPointsMeetingViewModel meeting)
        {
            ApplyTheme(meeting);
        }
    }

    // The panel's dynamic resources: neutral backgrounds and texts here, accents from the
    // ViewModel's palette, which also uses them for the phase color.
    private void ApplyTheme(StoryPointsMeetingViewModel meeting)
    {
        var light = meeting.IsLight;
        var palette = meeting.Palette;

        // WCAG contrast: buttons and the borders of boxes and pills at least 3:1 on card and
        // background, text at least 4.5:1 on its own background.
        Set("Bg", light ? "#EEF1F8" : "#111827");
        Set("Card", light ? "#FFFFFF" : "#1B2338");
        Set("CardLine", light ? "#DCE2EE" : "#1B2338");
        Set("Tile", light ? "#E2E7F2" : "#26304D");
        Set("ButtonFill", light ? "#5B6680" : "#66729A");
        Set("Field", light ? "#FFFFFF" : "#0B1120");
        Set("Line", light ? "#7F899E" : "#7C88A8");
        Set("Text", light ? "#141B2D" : "#F3F5FB");
        Set("Dim", light ? "#566076" : "#9AA3BC");
        Set("Body", light ? "#2B3245" : "#D6DBEA");
        Set("ScrollBarThumbBrush", light ? "#592B2F3A" : "#99F3F5FB");
        Set("ExpertBrush", palette.Expert);
        Set("DescriptionBrush", palette.Description);
        Set("ThinkingBrush", palette.Thinking);
        Set("VotesBrush", palette.Votes);
        Set("ExpiredBrush", palette.Expired);
        Set("OnAccent", palette.OnAccent);
    }

    private void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Resources[key] = brush;
    }
}

/// <summary>
/// Half-height radius for pill buttons, like ButtonRadius in App.xaml: WPF shrinks a larger
/// radius separately in width and height, and the pill turns into an oval.
/// </summary>
public sealed class HalfHeightRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new CornerRadius(value is double height ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
