using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// Tendina dei branch (stile in App.xaml): con il menu aperto le frecce spostano solo
/// l'evidenziato e Invio lo sceglie. Nella ComboBox standard le frecce cambiano subito
/// SelectedItem, e qui una scelta e' un git switch.
/// </summary>
public sealed class BranchComboBox : ComboBox
{
    public static readonly DependencyProperty HighlightedItemProperty = DependencyProperty.Register(
        nameof(HighlightedItem), typeof(object), typeof(BranchComboBox),
        new PropertyMetadata(null, (d, _) => ((BranchComboBox)d).MarkContainers()));

    /// <summary>Vero sul contenitore della voce evidenziata, da tastiera o dal mouse: lo legge lo stile delle voci.</summary>
    public static readonly DependencyProperty IsHighlightedItemProperty = DependencyProperty.RegisterAttached(
        "IsHighlightedItem", typeof(bool), typeof(BranchComboBox), new PropertyMetadata(false));

    private TextBox? editableTextBox;

    /// <summary>La voce su cui si trova il cursore del menu; null a menu chiuso.</summary>
    public object? HighlightedItem
    {
        get => GetValue(HighlightedItemProperty);
        set => SetValue(HighlightedItemProperty, value);
    }

    public static bool GetIsHighlightedItem(DependencyObject element) => (bool)element.GetValue(IsHighlightedItemProperty);

    public static void SetIsHighlightedItem(DependencyObject element, bool value) => element.SetValue(IsHighlightedItemProperty, value);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var plain = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;

        if (plain && e.Key is Key.Down or Key.Up)
        {
            // A menu chiuso la freccia lo apre, invece di passare al branch successivo.
            if (IsDropDownOpen)
            {
                MoveHighlight(e.Key == Key.Down ? 1 : -1);
            }
            else
            {
                IsDropDownOpen = true;
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && IsDropDownOpen && HighlightedItem is { } item)
        {
            // La voce gia' selezionata non cambia SelectedItem e il ViewModel non chiuderebbe il menu.
            if (Equals(item, SelectedItem))
            {
                IsDropDownOpen = false;
            }
            else
            {
                SelectedItem = item;
            }

            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        editableTextBox = GetTemplateChild("PART_EditableTextBox") as TextBox;
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        HighlightedItem = SelectedItem is { } selected && Items.Contains(selected) ? selected : null;
        BringHighlightIntoView();

        // La ComboBox editabile seleziona tutto il testo quando il menu si apre: aperto dal
        // ViewModel alla prima lettera della ricerca, la lettera successiva la sostituirebbe.
        // Il cursore torna in fondo dopo la selezione di WPF, che arriva in coda al dispatcher.
        if (editableTextBox is { IsKeyboardFocused: true } box)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Select(box.Text.Length, 0));
        }
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        HighlightedItem = null;
    }

    // Il filtro della ricerca cambia le voci: un evidenziato sparito si perde e la prossima
    // freccia riparte dalla prima voce.
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (HighlightedItem is { } item && !Items.Contains(item))
        {
            HighlightedItem = null;
        }
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        SetIsHighlightedItem(element, Equals(item, HighlightedItem));

        if (element is ComboBoxItem container)
        {
            container.MouseEnter -= OnItemMouseEnter;
            container.MouseEnter += OnItemMouseEnter;
        }
    }

    // Mouse e tastiera muovono lo stesso evidenziato: una freccia dopo il mouse riparte da li'.
    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ComboBoxItem container && ItemContainerGenerator.ItemFromContainer(container) is { } item
            && item != DependencyProperty.UnsetValue)
        {
            HighlightedItem = item;
        }
    }

    private void MoveHighlight(int step)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = HighlightedItem is null ? -1 : Items.IndexOf(HighlightedItem);
        index = index < 0
            ? (step > 0 ? 0 : Items.Count - 1)
            : Math.Clamp(index + step, 0, Items.Count - 1);

        HighlightedItem = Items[index];
        BringHighlightIntoView();
    }

    private void BringHighlightIntoView()
    {
        if (HighlightedItem is not null && ItemContainerGenerator.ContainerFromItem(HighlightedItem) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    private void MarkContainers()
    {
        foreach (var item in Items)
        {
            if (ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container)
            {
                SetIsHighlightedItem(container, Equals(item, HighlightedItem));
            }
        }
    }
}
