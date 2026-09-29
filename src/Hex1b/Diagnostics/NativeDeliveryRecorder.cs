using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Hex1b.Diagnostics;

/// <summary>
/// Records every write a terminal makes to an observable presentation, one record per write, in a
/// bounded ring with totals that are never evicted. It exists only when diagnostics are armed for a
/// terminal whose presentation can report its writes.
/// </summary>
internal sealed class NativeDeliveryRecorder
{
    /// <summary>Most records retained.</summary>
    internal const int MaxRecords = 4096;

    /// <summary>Most bytes retained for one record.</summary>
    internal const int MaxRecordBytes = 64 * 1024;

    /// <summary>Most bytes retained across all records.</summary>
    internal const int MaxRetainedBytes = 1024 * 1024;

    /// <summary>Counts recorders constructed in the current async flow while a test has set a counter.</summary>
    internal static readonly AsyncLocal<StrongBox<int>?> ConstructionsForTesting = new();

    private readonly object _sync = new();
    private readonly Entry?[] _ring = new Entry?[MaxRecords];
    private long _started;
    private long _retainedBytes;
    // The oldest sequence that may still hold retained bytes; everything older has none.
    private long _byteCursor = 1;
    private long _accepted;
    private long _refused;
    private long _failed;
    private long _bytesAccepted;
    private long? _firstCompleted;
    private long? _lastCompleted;

    internal NativeDeliveryRecorder(string deliveryLayer)
    {
        DeliveryLayer = deliveryLayer;
        CoverageStartedAt = DateTimeOffset.UtcNow;
        CoverageStartTimestamp = Stopwatch.GetTimestamp();
        if (ConstructionsForTesting.Value is { } counter)
            Interlocked.Increment(ref counter.Value);
    }

    internal string DeliveryLayer { get; }

    internal DateTimeOffset CoverageStartedAt { get; }

    internal long CoverageStartTimestamp { get; }

    /// <summary>Starts a record for a write about to be made; its sequence follows start order.</summary>
    internal Entry Begin(DiagnosticDeliverySource source, DiagnosticDeliveryPhase? phase, ReadOnlySpan<byte> data,
        long? outputSequence, long modelSequence)
    {
        var retained = data.Length > MaxRecordBytes ? data[..MaxRecordBytes].ToArray() : data.ToArray();
        var entry = new Entry(source, phase, data.Length, outputSequence, modelSequence, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), retained, data.Length > MaxRecordBytes);
        lock (_sync)
        {
            entry.Sequence = ++_started;
            var slot = entry.Sequence % MaxRecords;
            if (_ring[slot] is { Bytes: { } overwritten })
                _retainedBytes -= overwritten.Length;
            _ring[slot] = entry;
            _retainedBytes += retained.Length;
            EvictBytesUnsafe(entry.Sequence);
        }

        return entry;
    }

    /// <summary>Completes a record exactly once with what the presentation did.</summary>
    internal void Complete(Entry entry, DiagnosticDeliveryOutcome outcome, int? bytesAccepted, string? reason, string? error)
    {
        var end = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (entry.Outcome is not null)
                return;
            entry.Outcome = outcome;
            entry.BytesAccepted = bytesAccepted;
            entry.Reason = reason;
            entry.Error = error;
            entry.EndTimestamp = end;
            switch (outcome)
            {
                case DiagnosticDeliveryOutcome.Accepted: _accepted++; break;
                case DiagnosticDeliveryOutcome.Refused: _refused++; break;
                default: _failed++; break;
            }

            _bytesAccepted += bytesAccepted ?? 0;
            _firstCompleted = _firstCompleted is { } first ? Math.Min(first, entry.Sequence) : entry.Sequence;
            _lastCompleted = _lastCompleted is { } last ? Math.Max(last, entry.Sequence) : entry.Sequence;
        }
    }

    /// <summary>
    /// Reads completed records after <paramref name="since"/>, oldest first, stopping before the oldest
    /// write still in progress so that a continuation from the last returned sequence never skips one.
    /// </summary>
    internal Snapshot Read(long since, int limit, bool includeBytes)
    {
        // Copied under the lock, encoded after it, so a capture never holds the pump's writes back
        // while it base64-encodes up to 1 MiB.
        var copies = new List<(DiagnosticDeliveryRecord Record, byte[]? Bytes)>();
        int inProgress;
        DiagnosticDeliveryTotals totals;
        long evicted;
        lock (_sync)
        {
            var oldestRetained = Math.Max(1, _started - MaxRecords + 1);
            inProgress = 0;
            var blocked = false;
            // since >= _started returns nothing; comparing first keeps since + 1 from overflowing.
            var first = since >= _started ? _started + 1 : Math.Max(since + 1, oldestRetained);
            for (var sequence = first; sequence <= _started; sequence++)
            {
                var entry = _ring[sequence % MaxRecords]!;
                if (entry.Outcome is null)
                {
                    inProgress++;
                    blocked = true;
                    continue;
                }

                if (blocked || copies.Count >= limit)
                    continue;
                copies.Add((entry.ToRecord(), includeBytes ? entry.Bytes : null));
            }

            totals = new DiagnosticDeliveryTotals
            {
                Accepted = _accepted,
                Refused = _refused,
                Failed = _failed,
                BytesAccepted = _bytesAccepted,
                FirstSequence = _firstCompleted,
                LastSequence = _lastCompleted,
            };
            evicted = Math.Max(0, _started - MaxRecords);
        }

        var records = copies
            .Select(c => c.Bytes is { } bytes ? c.Record with { Content = Convert.ToBase64String(bytes) } : c.Record)
            .ToList();
        return new Snapshot(records, totals, evicted, inProgress);
    }

    // Must hold _sync. Drops retained bytes from the oldest records until the budget holds, never
    // from the record just added. The cursor only moves forward, so this is amortized O(1) per write.
    private void EvictBytesUnsafe(long newest)
    {
        _byteCursor = Math.Max(_byteCursor, newest - MaxRecords + 1);
        while (_retainedBytes > MaxRetainedBytes && _byteCursor < newest)
        {
            // A write that carried no bytes has nothing to evict.
            if (_ring[_byteCursor % MaxRecords] is { Bytes: { Length: > 0 } bytes } entry && entry.Sequence == _byteCursor)
            {
                _retainedBytes -= bytes.Length;
                entry.Bytes = null;
                entry.BytesEvicted = true;
            }

            _byteCursor++;
        }
    }

    internal readonly record struct Snapshot(IReadOnlyList<DiagnosticDeliveryRecord> Records, DiagnosticDeliveryTotals Totals,
        long EvictedRecords, int WritesInProgress);

    internal sealed class Entry(DiagnosticDeliverySource source, DiagnosticDeliveryPhase? phase, int length, long? outputSequence,
        long modelSequence, DateTimeOffset startedAt, long startTimestamp, byte[] bytes, bool truncated)
    {
        public long Sequence { get; set; }

        public DiagnosticDeliveryOutcome? Outcome { get; set; }

        public int? BytesAccepted { get; set; }

        public string? Reason { get; set; }

        public string? Error { get; set; }

        public long EndTimestamp { get; set; }

        public byte[]? Bytes { get; set; } = bytes;

        public bool BytesEvicted { get; set; }

        public DiagnosticDeliveryRecord ToRecord() => new()
        {
            Sequence = Sequence,
            Source = source,
            Outcome = Outcome!.Value,
            Phase = phase,
            Length = length,
            BytesAccepted = BytesAccepted,
            Reason = Reason,
            Error = Error,
            StartTimestamp = startTimestamp,
            EndTimestamp = EndTimestamp,
            StartedAt = startedAt,
            ModelSequenceAtStart = modelSequence,
            OutputSequence = outputSequence,
            Truncated = truncated,
            BytesEvicted = BytesEvicted,
        };
    }
}
