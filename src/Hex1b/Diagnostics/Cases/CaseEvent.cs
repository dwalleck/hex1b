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
    object? Detail = null,
    int DetailBytes = 0)
{
    /// <summary>
    /// What the event costs the queue's byte bound: a fixed record overhead, its payload, and the retained
    /// size of its detail (a frame projection's estimate, <see cref="CaseFrameSize"/>).
    /// </summary>
    public int QueuedBytes => 128 + (Payload?.Length ?? 0) + DetailBytes;
}
