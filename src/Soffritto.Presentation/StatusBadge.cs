namespace Soffritto.Presentation;

// Status dot colors, the same for every dot in the VSIX and the app: soft tints, like
// the header icons (Animal Crossing style), readable on both light and dark themes.
// Green all good (running, up to date, approved), orange needs attention, red error, blue in
// progress (it pulses in the app: StatusDot), grey off or waiting. Dots only: text on the
// theme background needs more contrast (see MigrationColors).
public static class StatusBadge
{
    public const string OkColor = "#6BB36B";
    public const string WarningColor = "#F4A261";
    public const string ErrorColor = "#E76F6F";
    public const string InfoColor = "#5AA9E6";
    public const string OffColor = "#B8B8B8";
}

/// <summary>Status labels of a migration, as they appear next to its name.</summary>
public static class MigrationLabels
{
    public const string Applied = "applied";
    public const string Missing = "missing";
    public const string OutOfOrder = "out of order";
    public const string Unknown = "unknown";
    public const string NotRead = "not read";
}

// Pending migrations no longer have a list of their own: the label flags them, so
// "missing" is red and everything else translucent grey (readable on light and dark).
// It is not a red for white text on a solid fill: here the text sits on the theme background and
// needs a tone that keeps its contrast on both.
public static class MigrationColors
{
    public const string Missing = "#D13438";
    public const string Neutral = "#99808080";

    public static string For(string label) => label == MigrationLabels.Missing ? Missing : Neutral;
}
