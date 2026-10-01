namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// A case's size bound: the tiers lines are written under (events and running loss below <c>MaxBytes − 16 KiB</c>,
/// ranges written as the case closes below <c>MaxBytes − 8 KiB</c>, each closing line below <c>MaxBytes − 2 KiB</c>, the
/// completion record in the rest), net of the room reserved for the lines of complete recovery checkpoints awaiting
/// the writer. A recovery reserves its room in the same step as its room check, before its state is projected, so every
/// line the writer appends from then on respects the reduced bound. The line the writer is appending at that moment is
/// announced before the writer reads its limit, and the reservation is published before the check reads the
/// announcement, so one of the two always sees the other. A line takes its reservation back once, as it is written.
/// The writer calls <see cref="BeginLine"/>, <see cref="Limit"/> and <see cref="EndLine"/> from its own thread, and
/// <see cref="ReleaseLine"/> as it writes a checkpoint; recoveries call <see cref="TryReserveRecovery"/>,
/// <see cref="TryKeep"/> and <see cref="Release"/> under the model lock.
/// </summary>
internal sealed class CaseRoom
{
    internal enum Tier { Events, Ranges, Closing }

    private readonly long _maxBytes;
    private readonly Func<long> _bytesWritten;
    private long _reserved;
    private long _inFlight;
    // Checkpoint ordinals whose reservation was taken back: the writer retries a line it could not write at the close.
    private readonly HashSet<long> _released = [];

    internal CaseRoom(long maxBytes, Func<long> bytesWritten)
    {
        _maxBytes = maxBytes;
        _bytesWritten = bytesWritten;
    }

    internal long EventLimit => _maxBytes - DiagnosticCaseRecorder.EventReserve - Volatile.Read(ref _reserved);

    internal long RangeLimit => _maxBytes - DiagnosticCaseRecorder.RangeReserve - Volatile.Read(ref _reserved);

    internal long ClosingLimit => _maxBytes - DiagnosticCaseRecorder.ClosingReserve - Volatile.Read(ref _reserved);

    internal long Limit(Tier tier) => tier switch { Tier.Events => EventLimit, Tier.Ranges => RangeLimit, _ => ClosingLimit };

    /// <summary>The bytes reserved for pending recovery lines.</summary>
    internal long Reserved => Volatile.Read(ref _reserved);

    /// <summary>
    /// What the events tier leaves for a recovery's state taken now: after the bytes written and in flight, the queued
    /// events at their written size, the checkpoint states awaiting the writer, and the reserve a reservation adds.
    /// </summary>
    internal long RecoveryRoom(long queuedBytes, long pendingStateBytes) =>
        EventLimit - _bytesWritten() - Volatile.Read(ref _inFlight) - queuedBytes - pendingStateBytes - DiagnosticCaseRecorder.EventReserve;

    /// <summary>
    /// Reserves the room a recovery line may take: its state's estimate with a line's allowance and the events
    /// reserve (so the line fits whichever tier writes it), or, when what is ahead of it leaves less, what is left
    /// (the projected size then decides, in <see cref="TryKeep"/>); false, with nothing reserved, when not even a
    /// line's overhead is left. The reservation is published before the check reads the writer's bytes and its line
    /// in flight, so from then on the writer appends under the reduced bound.
    /// </summary>
    internal bool TryReserveRecovery(long estimate, long queuedBytes, long pendingStateBytes, out long reserved)
    {
        const long overhead = DiagnosticCaseRecorder.StartLineAllowance + DiagnosticCaseRecorder.EventReserve;
        reserved = estimate + overhead;
        Interlocked.Add(ref _reserved, reserved);
        var slack = EventLimit - _bytesWritten() - Volatile.Read(ref _inFlight) - queuedBytes - pendingStateBytes;
        if (slack >= 0)
            return true;
        if (reserved + slack <= overhead)
        {
            Interlocked.Add(ref _reserved, -reserved);
            reserved = 0;
            return false;
        }
        Interlocked.Add(ref _reserved, slack);
        reserved += slack;
        return true;
    }

    /// <summary>
    /// Sets a reservation to the line's measured size once the state is projected: down always; up only when what is
    /// ahead still fits, else the reservation stays as it was and the recovery is refused.
    /// </summary>
    internal bool TryKeep(ref long reserved, long stateJsonBytes, long queuedBytes, long pendingStateBytes)
    {
        var kept = stateJsonBytes + DiagnosticCaseRecorder.StartLineAllowance + DiagnosticCaseRecorder.EventReserve;
        if (kept <= reserved)
        {
            Interlocked.Add(ref _reserved, kept - reserved);
            reserved = kept;
            return true;
        }
        if (!Fits(kept - reserved, queuedBytes, pendingStateBytes))
            return false;
        reserved = kept;
        return true;
    }

    /// <summary>Returns a reservation a refused recovery holds.</summary>
    internal void Release(long reserved) => Interlocked.Add(ref _reserved, -reserved);

    /// <summary>Takes a checkpoint line's reservation back, once, however many times its write is attempted.</summary>
    internal void ReleaseLine(long checkpointOrdinal, long reserved)
    {
        if (reserved > 0 && _released.Add(checkpointOrdinal))
            Interlocked.Add(ref _reserved, -reserved);
    }

    /// <summary>The writer's announcement of the line it is about to append, before it reads its limit.</summary>
    internal void BeginLine(long bytes) => Interlocked.Exchange(ref _inFlight, bytes);

    /// <summary>The line is written (its bytes counted) or abandoned.</summary>
    internal void EndLine() => Interlocked.Exchange(ref _inFlight, 0);

    // Adds `bytes` to the reservations, then checks that what is ahead fits the events tier net of them; a reservation
    // that does not fit is taken back. The add is a full fence, so a line announced before it is read here, and one
    // announced after it reads the reduced limit.
    private bool Fits(long bytes, long queuedBytes, long pendingStateBytes)
    {
        Interlocked.Add(ref _reserved, bytes);
        if (EventLimit - _bytesWritten() - Volatile.Read(ref _inFlight) - queuedBytes - pendingStateBytes >= 0)
            return true;
        Interlocked.Add(ref _reserved, -bytes);
        return false;
    }
}
