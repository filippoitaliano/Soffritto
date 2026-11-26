namespace Soffritto.Presentation;

/// <summary>Algorithm for the items of the branch drop-down, independent of the host's list type.</summary>
public static class BranchList
{
    /// <summary>
    /// The current branch always comes first, even if it does not match the filter; then the names that
    /// contain the filter, case-insensitively. Null current branch (git unavailable): no items.
    /// </summary>
    public static IReadOnlyList<string> Desired(string? current, IReadOnlyList<string> all, string filter)
    {
        var desired = new List<string>();

        if (current is not null)
        {
            desired.Add(current);
            desired.AddRange(all.Where(name =>
                name != current && name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        return desired;
    }

    // The list is realigned item by item instead of being cleared and refilled: with an editable
    // ComboBox, removing the selected item also wipes the text the user is typing.
    public static void Sync(IList<string> target, IReadOnlyList<string> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            while (i < target.Count && target[i] != desired[i])
            {
                if (desired.Contains(target[i]))
                {
                    target.Insert(i, desired[i]);
                }
                else
                {
                    target.RemoveAt(i);
                }
            }

            if (i >= target.Count)
            {
                target.Add(desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
