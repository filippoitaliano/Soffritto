using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// Il browser di playwright dal vivo, utilizzabile: mostra i fotogrammi della dashboard e le
/// inoltra mouse, rotella e tastiera, riportati sulle coordinate della pagina. Il browser e'
/// quello che pilota Claude: chi lo usa mentre Claude lavora gli cambia la pagina sotto i piedi.
/// </summary>
public sealed class PlaywrightLiveView : Image
{
    private static readonly Dictionary<Key, string> SpecialKeys = new()
    {
        [Key.Enter] = "Enter",
        [Key.Tab] = "Tab",
        [Key.Back] = "Backspace",
        [Key.Delete] = "Delete",
        [Key.Escape] = "Escape",
        [Key.Left] = "ArrowLeft",
        [Key.Right] = "ArrowRight",
        [Key.Up] = "ArrowUp",
        [Key.Down] = "ArrowDown",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "PageUp",
        [Key.PageDown] = "PageDown",
        [Key.Insert] = "Insert",
        [Key.F1] = "F1", [Key.F2] = "F2", [Key.F3] = "F3", [Key.F4] = "F4",
        [Key.F5] = "F5", [Key.F6] = "F6", [Key.F7] = "F7", [Key.F8] = "F8",
        [Key.F9] = "F9", [Key.F10] = "F10", [Key.F11] = "F11", [Key.F12] = "F12",
    };

    private long lastMoveTicks;

    public PlaywrightLiveView()
    {
        Stretch = Stretch.Uniform;
        Focusable = true;
        FocusVisualStyle = null;
        SetBinding(SourceProperty, new Binding(nameof(FrontendPreviewViewModel.Frame)));
    }

    private FrontendPreviewViewModel? Preview => DataContext as FrontendPreviewViewModel;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Una trentina di movimenti al secondo bastano, e non intasano la connessione.
        var now = Environment.TickCount64;
        if (now - lastMoveTicks < 33 || !TryMap(e, out var x, out var y))
        {
            return;
        }

        lastMoveTicks = now;
        Send("mousemove", new { x, y });
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (TryMap(e, out var x, out var y))
        {
            CaptureMouse();
            Send("mousedown", new { x, y, button = ButtonName(e.ChangedButton) });
        }

        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (TryMap(e, out var x, out var y))
        {
            Send("mouseup", new { x, y, button = ButtonName(e.ChangedButton) });
        }

        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Send("wheel", new { deltaX = 0, deltaY = -e.Delta });
        e.Handled = true;
    }

    // I caratteri stampabili arrivano da OnTextInput; qui solo tasti speciali e scorciatoie.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (SpecialKeys.TryGetValue(key, out var name))
        {
            Press(name, modifiers);
            e.Handled = true;
        }
        else if (modifiers.HasFlag(ModifierKeys.Control) && ShortcutKey(key) is { } letter)
        {
            Press(letter, modifiers);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        foreach (var ch in e.Text)
        {
            if (!char.IsControl(ch))
            {
                Send("keydown", new { key = ch.ToString() });
                Send("keyup", new { key = ch.ToString() });
            }
        }

        e.Handled = true;
    }

    private void Press(string key, ModifierKeys modifiers)
    {
        var held = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) held.Add("Control");
        if (modifiers.HasFlag(ModifierKeys.Shift)) held.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) held.Add("Alt");

        foreach (var modifier in held)
        {
            Send("keydown", new { key = modifier });
        }

        Send("keydown", new { key });
        Send("keyup", new { key });

        foreach (var modifier in Enumerable.Reverse(held))
        {
            Send("keyup", new { key = modifier });
        }
    }

    private static string? ShortcutKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => null,
    };

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        _ => "left",
    };

    // Con Stretch Uniform l'Image misura esattamente l'area del fotogramma: la proporzione sulla
    // sua dimensione e' la stessa sulla pagina.
    private bool TryMap(MouseEventArgs e, out double x, out double y)
    {
        x = y = 0;
        var preview = Preview;
        if (preview is null || preview.ViewportWidth <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return false;
        }

        var point = e.GetPosition(this);
        x = Math.Clamp(point.X / ActualWidth, 0, 1) * preview.ViewportWidth;
        y = Math.Clamp(point.Y / ActualHeight, 0, 1) * preview.ViewportHeight;
        return true;
    }

    private void Send(string method, object parameters) => Preview?.Input?.Send(method, parameters);
}
