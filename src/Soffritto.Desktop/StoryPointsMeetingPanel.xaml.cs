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

    /// <summary>La X del pannello: chi lo ospita decide come nasconderlo.</summary>
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

    // Le risorse dinamiche del pannello: fondi e testi neutri qui, accenti dalla palette del
    // ViewModel, che li usa anche per il colore della fase.
    private void ApplyTheme(StoryPointsMeetingViewModel meeting)
    {
        var light = meeting.IsLight;
        var palette = meeting.Palette;

        // Contrasti WCAG: bottoni e bordi di caselle e pillole almeno 3:1 su card e sfondo, testo
        // almeno 4.5:1 sul suo fondo.
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
/// Raggio di meta' altezza per i bottoni a pillola, come ButtonRadius in App.xaml: un raggio piu'
/// grande WPF lo riduce separatamente in larghezza e altezza e la pillola diventa un ovale.
/// </summary>
public sealed class HalfHeightRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new CornerRadius(value is double height ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
