using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

// Same state machine as the VSIX drop-down (UI/StatusViewModel.cs): a behaviour change must be
// mirrored in both files. The item algorithm is shared (BranchList).
public sealed class BranchPickerViewModel : ObservableObject
{
    private readonly object branchesGate = new();
    private List<string> allBranches = [];
    private string? selectedBranch;
    private string branchFilter = string.Empty;
    private string branchText = string.Empty;
    private bool branchDropDownOpen;

    /// <summary>The drop-down items: the current branch first, then the ones that pass the filter, in order.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>
    /// Text in the editable ComboBox's box. While typing it filters the items and opens the
    /// menu; when WPF rewrites it with the chosen item (or the current one) it is not a search.
    /// </summary>
    public string BranchText
    {
        get => branchText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref branchText, text))
            {
                return;
            }

            if (text == selectedBranch || text == CurrentBranch)
            {
                ClearBranchFilter();
                return;
            }

            branchFilter = text;

            // If the selection was lost (text cleared by the control), an empty filter goes back
            // to the current branch; while typing the selection must be left alone, otherwise
            // WPF would rewrite the box with the selected branch name.
            SyncBranches(reselect: text.Length == 0 && selectedBranch is null);

            if (text.Length > 0)
            {
                BranchDropDownOpen = true;
            }
        }
    }

    /// <summary>
    /// Opened by the ViewModel as soon as the user types, closed by the user or by a choice. If it
    /// closes (Esc, click outside, Tab) while the box still holds search text, the search is
    /// abandoned: the box goes back to showing the current branch.
    /// </summary>
    public bool BranchDropDownOpen
    {
        get => branchDropDownOpen;
        set
        {
            if (!SetProperty(ref branchDropDownOpen, value))
            {
                return;
            }

            if (!value && CurrentBranch is not null && branchText != CurrentBranch && branchText != selectedBranch)
            {
                RestoreBranchText();
            }
        }
    }

    /// <summary>Current branch as of the last read: a different selection is a switch request.</summary>
    public string? CurrentBranch { get; private set; }

    /// <summary>Called by the <see cref="SelectedBranch"/> setter when the user picks a branch other than the current one.</summary>
    public Action<string>? BranchSelected { get; set; }

    // TwoWay binding from the ComboBox: the setter is hit both by the user's choices and by the
    // sink's reassignments (and with null if the selected item disappears). Only a choice other
    // than the current branch is an intent to switch. A choice always closes the search: the
    // text echo WPF sends right after finds the filter already empty.
    public string? SelectedBranch
    {
        get => selectedBranch;
        set
        {
            if (!SetProperty(ref selectedBranch, value))
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            ClearBranchFilter();
            SetProperty(ref branchText, value, nameof(BranchText));
            BranchDropDownOpen = false;

            if (value != CurrentBranch)
            {
                BranchSelected?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// A fresh git read: sets the current branch and the full list, then realigns the drop-down
    /// on the current one. A null <paramref name="current"/> means git is unavailable: empty drop-down.
    /// </summary>
    public void SetBranches(string? current, IEnumerable<string> names)
    {
        lock (branchesGate)
        {
            CurrentBranch = current;
            allBranches = names.ToList();
        }

        SyncBranches(reselect: true);
    }

    private void RestoreBranchText()
    {
        branchFilter = string.Empty;
        SyncBranches(reselect: true);
        SetProperty(ref branchText, CurrentBranch ?? string.Empty, nameof(BranchText));
    }

    private void ClearBranchFilter()
    {
        if (branchFilter.Length == 0)
        {
            return;
        }

        branchFilter = string.Empty;
        SyncBranches(reselect: false);
    }

    private void SyncBranches(bool reselect)
    {
        lock (branchesGate)
        {
            var current = CurrentBranch;

            BranchList.Sync(Branches, BranchList.Desired(current, allBranches, branchFilter));

            if (current is null)
            {
                SelectedBranch = null;
            }
            else if (reselect)
            {
                SelectedBranch = current;
            }
        }
    }
}
