namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The re-applicable intervals of a case, one per origin: the initial checkpoint (a fresh model, or a complete
/// <c>text-state/1</c> start) and each recovery checkpoint, in checkpoint order. An origin's interval runs from its
/// model sequence to the earliest end at or after it: a loss range, an interval end the recorder wrote, a gap in the
/// model sequences, truncation or interruption, or the stop. An origin inside a loss range starts nothing; an origin
/// that is not complete is listed as invalid with its reason. A later origin never changes an earlier interval.
/// </summary>
internal static class CaseIntervals
{
    internal static IReadOnlyList<DiagnosticCaseInterval> Describe(DiagnosticCaseManifest manifest, DiagnosticCaseCompletion? completion,
        DiagnosticCaseCompletionState state, CaseArtifactReader.ScanResult scan)
    {
        var intervals = new List<DiagnosticCaseInterval>();
        var checkpoint = manifest.Checkpoint;
        var modelStart = checkpoint.ModelSequence ?? 0;
        if (checkpoint.Status != DiagnosticCaseCheckpointStatus.Complete)
        {
            intervals.Add(new DiagnosticCaseInterval { Valid = false, EndReason = $"checkpoint {DiagnosticContractNames.Of(checkpoint.Status)}: {checkpoint.Reason}" });
        }
        else if (checkpoint.Profile == DiagnosticCaseCheckpointProfiles.TextState && (scan.Start is not { } line || line.ModelSequence != modelStart))
        {
            // A text-state/1 start re-applies only from its verified start line with state, at the manifest's sequence.
            intervals.Add(new DiagnosticCaseInterval { Valid = false, EndReason = "start-missing: the start checkpoint line is not among the verified events at the manifest's model sequence, or holds no state" });
        }
        else
        {
            var start = scan.Start;
            var origin = new DiagnosticCaseOrigin
            {
                Profile = checkpoint.Profile,
                Trigger = "start",
                Label = start?.Label,
                CheckpointOrdinal = start?.Ordinal,
                CaseSequence = start?.CaseSequence,
                ModelSequence = modelStart,
            };
            intervals.Add(Interval(origin, modelStart, modelStart, completion, state, scan));
        }

        foreach (var recovery in scan.Recoveries.OrderBy(r => r.Ordinal))
        {
            var origin = new DiagnosticCaseOrigin
            {
                Profile = DiagnosticCaseCheckpointProfiles.TextState,
                Trigger = "recovery",
                Label = recovery.Label,
                CheckpointOrdinal = recovery.Ordinal,
                CaseSequence = recovery.CaseSequence,
                ModelSequence = recovery.ModelSequence,
            };
            intervals.Add(recovery.Status == "recorded" && recovery.HasState
                ? Interval(origin, recovery.ModelSequence, modelStart, completion, state, scan)
                : new DiagnosticCaseInterval { Valid = false, Origin = origin, EndReason = $"checkpoint {recovery.Status}: {recovery.Reason ?? "its state was not recorded"}" });
        }

        return intervals;
    }

    // The interval from one origin. Model events are recorded from the sequence after the initial checkpoint's
    // (modelStart), so a model-stream ordinal n is model sequence modelStart + n. Only ends at or after the origin
    // count; the minimum wins, and on a tie the first reason considered is reported (a recorded cause before an
    // inferred gap).
    private static DiagnosticCaseInterval Interval(DiagnosticCaseOrigin origin, long from, long modelStart, DiagnosticCaseCompletion? completion,
        DiagnosticCaseCompletionState state, CaseArtifactReader.ScanResult scan)
    {
        var last = Math.Max(scan.LastModelSequence ?? from, from);
        (long To, string Reason)? end = null;
        void Consider(long to, string reason)
        {
            if (end is null || to < end.Value.To)
                end = (to, reason);
        }

        foreach (var (sequence, reason) in scan.IntervalEnds)
        {
            if (sequence > from)
                Consider(sequence - 1, reason);
        }
        foreach (var missing in scan.Streams["model"].Missing)
        {
            var first = modelStart + (missing.FromOrdinal ?? 1);
            var lastMissing = missing.ToOrdinal is { } to ? modelStart + to : long.MaxValue;
            // The origin's state follows its event: a range ending at or before it is before it, and one that
            // covers it (lost events after it, before any retained one) leaves nothing to re-apply from it.
            if (lastMissing <= from)
                continue;
            if (first <= from)
                return new DiagnosticCaseInterval { Valid = false, Origin = origin, FromModelSequence = from, EndReason = $"inside-loss: the origin is within a range of lost events ({missing.Reason})" };
            Consider(first - 1, missing.Reason == "unknown" ? "model-events-missing" : missing.Reason);
        }
        foreach (var gap in scan.ModelGaps)
        {
            if (gap >= from)
                Consider(gap, "model-events-missing");
        }
        if (state == DiagnosticCaseCompletionState.Truncated)
            Consider(last, "truncated");
        else if (state == DiagnosticCaseCompletionState.Interrupted)
            Consider(last, "interrupted");

        var (end_, endReason) = end ?? (last, $"case-stopped: {(completion is null ? "unknown" : DiagnosticContractNames.Of(completion.StopReason))}");
        return new DiagnosticCaseInterval { Valid = true, Origin = origin, FromModelSequence = from, ToModelSequence = end_, EndReason = endReason };
    }
}
