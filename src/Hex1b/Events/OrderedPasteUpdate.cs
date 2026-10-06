namespace Hex1b.Events;

/// <summary>An update delivered synchronously to the captured paste receiver.</summary>
public sealed class OrderedPasteUpdate
{
    internal OrderedPasteUpdate(OrderedPastePhase phase, string text = "", Exception? error = null)
    { Phase = phase; Text = text; Error = error; }
    /// <summary>Gets the chunk or terminal phase.</summary>
    public OrderedPastePhase Phase { get; }
    /// <summary>Gets original unnormalized text, nonempty only for chunks.</summary>
    public string Text { get; }
    /// <summary>Gets the cause of a failed operation.</summary>
    public Exception? Error { get; }
}
