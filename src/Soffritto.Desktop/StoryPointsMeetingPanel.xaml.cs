using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

        // The app's warm neutrals (ThemeLight/ThemeDark), a step stronger where the TV needs it:
        // the borders of boxes and pills at least 3:1 on the card, text at least 4.5:1.
        Set("Bg", light ? "#F3F1EA" : "#1A1717");
        Set("Card", light ? "#FFFFFB" : "#262021");
        Set("CardLine", light ? "#E6DAC0" : "#3D3330");
        Set("Tile", light ? "#EFE6D2" : "#3B332E");
        Set("Theme.Control", light ? "#F2E6CB" : "#3B332E");
        Set("Theme.ChipOutline", light ? "#26000000" : "#24FFFFFF");
        Set("Theme.TextSoft", light ? "#4A4033" : "#DACDBA");
        Set("Field", light ? "#FFFEFA" : "#1F1B1B");
        Set("Line", light ? "#A8946F" : "#8A7A68");
        Set("Text", light ? "#2E2820" : "#F6EEE2");
        Set("Dim", light ? "#6B5B45" : "#A8987F");
        Set("Body", light ? "#3B3226" : "#DACDBA");
        Set("ScrollBarThumbBrush", light ? "#A65A4C38" : "#80CDBDA6");
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
