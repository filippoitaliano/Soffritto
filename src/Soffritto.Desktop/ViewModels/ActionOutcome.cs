namespace Soffritto.Desktop.ViewModels;

/// <summary>The outcome of an action to show the user, with the kind of FlashCard that shows it.</summary>
public sealed record ActionOutcome(string Text, FlashKind Kind)
{
    public static ActionOutcome Done(string text) => new(text, FlashKind.Success);

    public static ActionOutcome Partial(string text) => new(text, FlashKind.Warning);

    public static ActionOutcome Failed(string text) => new(text, FlashKind.Error);
}
