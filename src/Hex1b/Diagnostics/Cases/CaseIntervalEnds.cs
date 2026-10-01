namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Where a case's re-applicable model coverage ends, per segment. A segment begins at the case's origin and at each
/// complete recovery checkpoint; within a segment the first end (the lowest model sequence) wins, and a segment's end
/// is written before the first model event at or after it. Kept outside the event queue, so overload cannot drop it.
/// The recorder calls <see cref="End"/> and <see cref="BeginSegment"/> under the model lock and <see cref="TakeDue"/>
/// from its writer.
/// </summary>
internal sealed class CaseIntervalEnds
{
    private readonly object _sync = new();
    private long _end = long.MaxValue;
    private string? _reason;
    private long _origin = long.MinValue;
    // Ends of closed segments (and the open one, once taken) that the writer has not written yet, in order.
    private readonly Queue<(long Sequence, string Reason)> _due = new();

    /// <summary>The open segment's end, or <see cref="long.MaxValue"/> when it has none.</summary>
    internal long CurrentEnd
    {
        get { lock (_sync) return _end; }
    }

    /// <summary>
    /// Ends coverage at <paramref name="modelSequence"/> (the first event that cannot be reproduced). Within the open
    /// segment the first end wins; an end before the segment's origin belongs to the segment before it and is queued
    /// for writing as it is.
    /// </summary>
    internal void End(long modelSequence, string reason)
    {
        lock (_sync)
        {
            if (modelSequence < _origin)
            {
                _due.Enqueue((modelSequence, reason));
                return;
            }
            if (modelSequence >= _end)
                return;
            _end = modelSequence;
            _reason = reason;
        }
    }

    /// <summary>
    /// Starts a new segment at a complete recovery checkpoint's model sequence: the open segment's end, if any and
    /// not yet taken, stays due for writing; later ends apply to the new segment only.
    /// </summary>
    internal void BeginSegment(long modelSequence)
    {
        lock (_sync)
        {
            if (_end != long.MaxValue)
                _due.Enqueue((_end, _reason ?? "unsupported"));
            _end = long.MaxValue;
            _reason = null;
            _origin = modelSequence;
        }
    }

    /// <summary>
    /// The ends to write before the model event at <paramref name="nextModelSequence"/> (every end at or before it, in
    /// the order they became due); <see cref="long.MaxValue"/> takes every end. Each is returned once.
    /// </summary>
    internal List<(long Sequence, string Reason)> TakeDue(long nextModelSequence)
    {
        var taken = new List<(long, string)>();
        lock (_sync)
        {
            while (_due.TryPeek(out var due) && due.Sequence <= nextModelSequence)
                taken.Add(_due.Dequeue());
            if (_end != long.MaxValue && _end <= nextModelSequence)
            {
                taken.Add((_end, _reason ?? "unsupported"));
                _end = long.MaxValue;
                _reason = null;
                // The segment keeps its origin: a later, lower end cannot reopen a written one.
                _origin = Math.Max(_origin, taken[^1].Item1 + 1);
            }
        }
        return taken;
    }
}
