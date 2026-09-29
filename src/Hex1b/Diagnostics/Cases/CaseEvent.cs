namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// One recorded observation, as queued for the artifact writer. <see cref="Ordinal"/> is dense within
/// its stream, so a reader can detect any gap; <see cref="CaseSequence"/> orders events across streams.
/// </summary>
internal readonly record struct CaseEvent(
    CaseStream Stream,
    long Ordinal,
    long CaseSequence,
    long Timestamp,
    string Kind,
    long? ModelSequence,
    int Width,
    int Height,
    int Length,
    byte[]? Payload)
{
    /// <summary>What the event costs the queue's byte bound: a fixed record overhead plus its payload.</summary>
    public int QueuedBytes => 128 + (Payload?.Length ?? 0);
}
