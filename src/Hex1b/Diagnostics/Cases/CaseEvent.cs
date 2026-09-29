namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// One recorded observation, as queued for the artifact writer. <see cref="Ordinal"/> is dense within
/// its stream, so a reader can detect any gap; the writer assigns the case sequence in file order.
/// </summary>
internal readonly record struct CaseEvent(
    CaseStream Stream,
    long Ordinal,
    long Timestamp,
    string Kind,
    long? ModelSequence,
    int? Width,
    int? Height,
    int? Length,
    byte[]? Payload,
    object? Detail = null)
{
    // A frame projection's size is unknown until serialized; it is charged a fixed estimate.
    private const int FrameEstimate = 4096;

    /// <summary>What the event costs the queue's byte bound: a fixed record overhead plus its payload.</summary>
    public int QueuedBytes => 128 + (Payload?.Length ?? 0) + (Detail is DiagnosticCaseFrameEvent ? FrameEstimate : 0);
}
