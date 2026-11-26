using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// Branch dropdown (style in App.xaml): with the menu open the arrow keys only move the
/// highlight and Enter picks it. In the standard ComboBox the arrows change SelectedItem
/// right away, and here a choice is a git switch.
/// </summary>
public sealed class BranchComboBox : ComboBox
{
    public static readonly DependencyProperty HighlightedItemProperty = DependencyProperty.Register(
        nameof(HighlightedItem), typeof(object), typeof(BranchComboBox),
        new PropertyMetadata(null, (d, _) => ((BranchComboBox)d).MarkContainers()));

    /// <summary>True on the container of the highlighted item, by keyboard or mouse: read by the item style.</summary>
    public static readonly DependencyProperty IsHighlightedItemProperty = DependencyProperty.RegisterAttached(
        "IsHighlightedItem", typeof(bool), typeof(BranchComboBox), new PropertyMetadata(false));

    private TextBox? editableTextBox;

    /// <summary>The item under the menu cursor; null while the menu is closed.</summary>
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
            // With the menu closed, the arrow opens it instead of moving to the next branch.
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
            // The already selected item does not change SelectedItem, and the ViewModel would not close the menu.
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

        // The editable ComboBox selects all the text when the menu opens: since the ViewModel opens it
        // on the first letter of the search, the next letter would replace that one.
        // The caret goes back to the end after WPF's selection, which is queued on the dispatcher.
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

    // The search filter changes the items: a highlight that disappears is lost, and the next
    // arrow starts again from the first item.
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

    // Mouse and keyboard move the same highlight: an arrow after the mouse continues from there.
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
