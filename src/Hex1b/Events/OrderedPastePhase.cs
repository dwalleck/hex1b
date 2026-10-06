namespace Hex1b.Events;

/// <summary>One ordered paste chunk or its final outcome.</summary>
public enum OrderedPastePhase
{
    /// <summary>Literal text in arrival order.</summary>
    Chunk,
    /// <summary>All payload was delivered.</summary>
    Completed,
    /// <summary>The operation was cancelled; applied text is retained.</summary>
    Cancelled,
    /// <summary>Input or the receiver failed.</summary>
    Failed,
    /// <summary>The owning application stopped.</summary>
    Shutdown
}
