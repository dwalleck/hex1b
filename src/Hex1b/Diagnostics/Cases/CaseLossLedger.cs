namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Which events each stream lost to overload, kept outside the event queue so that losing the queue's
/// capacity cannot lose the record of the loss. Consecutive drops extend one range; past
/// <see cref="MaxRangesPerStream"/> ranges a stream's further loss is recorded as one range of unknown
/// extent from its first uncounted drop.
/// </summary>
internal sealed class CaseLossLedger
{
    internal const int MaxRangesPerStream = 1024;

    private readonly object _sync = new();
    private readonly Dictionary<CaseStream, StreamLoss> _streams = [];

    private sealed class StreamLoss
    {
        public readonly List<(long From, long To)> Closed = [];
        public int Written;
        public (long From, long To)? Open;
        public long? UnknownFrom;
        public bool UnknownWritten;
        public int Ranges;
    }

    /// <summary>Records that <paramref name="ordinal"/> of <paramref name="stream"/> was dropped.</summary>
    internal void Record(CaseStream stream, long ordinal)
    {
        lock (_sync)
        {
            if (!_streams.TryGetValue(stream, out var loss))
                _streams[stream] = loss = new StreamLoss();
            if (loss.UnknownFrom is not null)
                return;
            if (loss.Open is { } open && open.To == ordinal - 1)
            {
                loss.Open = (open.From, ordinal);
                return;
            }

            if (loss.Open is { } previous)
                loss.Closed.Add(previous);
            if (++loss.Ranges > MaxRangesPerStream)
            {
                loss.Open = null;
                loss.UnknownFrom = ordinal;
                return;
            }

            loss.Open = (ordinal, ordinal);
        }
    }

    /// <summary>Ranges that can no longer grow and were not taken yet; at the end, every range, open ones included.</summary>
    internal List<DiagnosticCaseRecord> Take(bool final)
    {
        var records = new List<DiagnosticCaseRecord>();
        lock (_sync)
        {
            foreach (var (stream, loss) in _streams)
            {
                if (final && loss.Open is { } open)
                {
                    loss.Closed.Add(open);
                    loss.Open = null;
                }

                for (; loss.Written < loss.Closed.Count; loss.Written++)
                {
                    var (from, to) = loss.Closed[loss.Written];
                    records.Add(new DiagnosticCaseRecord { Stream = CaseArtifactWriter.StreamName(stream), FromOrdinal = from, ToOrdinal = to, Reason = "overload" });
                }

                if (loss.UnknownFrom is { } unknown && !loss.UnknownWritten)
                {
                    loss.UnknownWritten = true;
                    records.Add(new DiagnosticCaseRecord { Stream = CaseArtifactWriter.StreamName(stream), FromOrdinal = unknown, ToOrdinal = null, Reason = "overload-unknown-extent" });
                }
            }
        }

        return records;
    }
}
