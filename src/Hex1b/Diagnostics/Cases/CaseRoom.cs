namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// A case's size bound: the bytes written so far, the tiers lines are written under (events and running loss below
/// <c>MaxBytes − 16 KiB</c>, ranges written as the case closes below <c>MaxBytes − 8 KiB</c>, each closing line below
/// <c>MaxBytes − 2 KiB</c>, the completion record in the rest), and the room reserved for the lines of complete recovery
/// checkpoints awaiting the writer, which every tier is net of. One lock covers the arithmetic alone, never a write:
/// the writer claims a line's bytes under it before it writes the line (<see cref="TryClaim"/>), and a recovery
/// reserves its room under it in the same step as its check (<see cref="TryReserveRecovery"/>), so a line is either
/// claimed before the reservation and counted by it, or claimed after it under the reduced bound. A recovery holds the
/// lock for a few comparisons under the model lock, never for the writer's I/O. A line takes its reservation back once,
/// as it is written (<see cref="ReleaseLine"/>).
/// </summary>
internal sealed class CaseRoom
{
    internal enum Tier { Events, Ranges, Closing }

    private readonly object _sync = new();
    private readonly long _maxBytes;
    // Counted as a line is claimed, before it is written, so the bound never admits two lines into the same room.
    private long _bytesWritten;
    private long _reserved;
    // Checkpoint ordinals whose reservation was taken back: the writer retries a line it could not write at the close.
    private readonly HashSet<long> _released = [];

    internal CaseRoom(long maxBytes) => _maxBytes = maxBytes;

    /// <summary>Artifact bytes written (claimed) so far.</summary>
    internal long BytesWritten
    {
        get { lock (_sync) return _bytesWritten; }
    }

    /// <summary>The bytes reserved for pending recovery lines.</summary>
    internal long Reserved
    {
        get { lock (_sync) return _reserved; }
    }

    internal long EventLimit
    {
        get { lock (_sync) return LimitUnsafe(Tier.Events); }
    }

    internal long RangeLimit
    {
        get { lock (_sync) return LimitUnsafe(Tier.Ranges); }
    }

    internal long ClosingLimit
    {
        get { lock (_sync) return LimitUnsafe(Tier.Closing); }
    }

    /// <summary>Claims a line's bytes under the tier's limit, before the line is written; false, nothing claimed, when it would cross.</summary>
    internal bool TryClaim(Tier tier, long bytes)
    {
        lock (_sync)
        {
            if (_bytesWritten + bytes > LimitUnsafe(tier))
                return false;
            _bytesWritten += bytes;
            return true;
        }
    }

    /// <summary>Counts bytes written outside the tiers: the manifest first, the completion last.</summary>
    internal void Count(long bytes)
    {
        lock (_sync)
            _bytesWritten += bytes;
    }

    /// <summary>What the tier leaves now.</summary>
    internal long RoomIn(Tier tier)
    {
        lock (_sync)
            return LimitUnsafe(tier) - _bytesWritten;
    }

    /// <summary>
    /// What the events tier leaves for a recovery's state taken now: after the bytes written, the queued events at
    /// their written size, the checkpoint states awaiting the writer, and the reserve a reservation adds.
    /// </summary>
    internal long RecoveryRoom(long queuedBytes, long pendingStateBytes)
    {
        lock (_sync)
            return LimitUnsafe(Tier.Events) - _bytesWritten - queuedBytes - pendingStateBytes - DiagnosticCaseRecorder.EventReserve;
    }

    /// <summary>
    /// Reserves the room a recovery line may take: its state's estimate with a line's allowance and the events
    /// reserve (so the line fits whichever tier writes it), or what is left when that is less (the projected size then
    /// decides, in <see cref="TryKeep"/>); false, with nothing reserved, when not even a line's overhead is left.
    /// </summary>
    internal bool TryReserveRecovery(long estimate, long queuedBytes, long pendingStateBytes, out long reserved)
    {
        lock (_sync)
        {
            var left = LimitUnsafe(Tier.Events) - _bytesWritten - queuedBytes - pendingStateBytes;
            if (left <= Overhead)
            {
                reserved = 0;
                return false;
            }
            reserved = Math.Min(estimate + Overhead, left);
            _reserved += reserved;
            return true;
        }
    }

    /// <summary>
    /// Sets a reservation to the line's measured size once the state is projected: down always; up only when what is
    /// ahead still fits, else the reservation stays as it was and the recovery is refused.
    /// </summary>
    internal bool TryKeep(ref long reserved, long stateJsonBytes, long queuedBytes, long pendingStateBytes)
    {
        var kept = stateJsonBytes + Overhead;
        lock (_sync)
        {
            var growth = kept - reserved;
            if (growth > 0 && LimitUnsafe(Tier.Events) - _bytesWritten - queuedBytes - pendingStateBytes - growth < 0)
                return false;
            _reserved += growth;
            reserved = kept;
            return true;
        }
    }

    /// <summary>Returns a reservation a refused recovery holds.</summary>
    internal void Release(long reserved)
    {
        lock (_sync)
            _reserved -= reserved;
    }

    /// <summary>Takes a checkpoint line's reservation back, once, however many times its write is attempted.</summary>
    internal void ReleaseLine(long checkpointOrdinal, long reserved)
    {
        if (reserved <= 0)
            return;
        lock (_sync)
        {
            if (_released.Add(checkpointOrdinal))
                _reserved -= reserved;
        }
    }

    private const long Overhead = DiagnosticCaseRecorder.StartLineAllowance + DiagnosticCaseRecorder.EventReserve;

    private long LimitUnsafe(Tier tier) =>
        _maxBytes - tier switch
        {
            Tier.Events => DiagnosticCaseRecorder.EventReserve,
            Tier.Ranges => DiagnosticCaseRecorder.RangeReserve,
            _ => DiagnosticCaseRecorder.ClosingReserve,
        } - _reserved;
}
