namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The origin a re-application restores from: the earliest valid interval covering the target, or the one `from`
/// names (`start`, or a recovery by label, `case:N` or `checkpoint:N`). A named checkpoint that is not an origin, or
/// an unknown one, is refused with the code the target's resolution uses for the same form.
/// </summary>
internal static class CaseOrigins
{
    internal sealed record Selection(DiagnosticCaseInterval? Interval, DiagnosticProblem? Problem);

    internal static Selection Select(string? from, IReadOnlyList<DiagnosticCaseInterval> intervals,
        IReadOnlyList<CaseReapplier.CheckpointLine> checkpoints, long target)
    {
        if (from is null)
            return new(intervals.Where(i => i.Valid && i.FromModelSequence <= target && target <= i.ToModelSequence).OrderBy(i => i.FromModelSequence).FirstOrDefault(), null);
        // The bare word names the initial origin; a recovery labelled "start" is named label:start.
        if (from == "start")
            return new(intervals.FirstOrDefault(), null);
        Func<DiagnosticCaseOrigin, bool> matches;
        Func<CaseReapplier.CheckpointLine, bool> exists;
        string what, unknown;
        if (from.StartsWith("case:", StringComparison.Ordinal) && long.TryParse(from.AsSpan(5), out var caseSequence))
        {
            matches = o => o.CaseSequence == caseSequence;
            exists = c => c.CaseSequence == caseSequence;
            (what, unknown) = ($"case sequence {caseSequence}", "unknown-case-sequence");
        }
        else if (from.StartsWith("checkpoint:", StringComparison.Ordinal) && long.TryParse(from.AsSpan(11), out var ordinal))
        {
            matches = o => o.CheckpointOrdinal == ordinal;
            exists = c => c.Ordinal == ordinal;
            (what, unknown) = ($"checkpoint {ordinal}", "unknown-checkpoint");
        }
        else
        {
            var label = from.StartsWith("label:", StringComparison.Ordinal) ? from[6..] : from;
            matches = o => o.Label == label;
            exists = c => c.Label == label;
            (what, unknown) = ($"label '{label}'", "unknown-label");
        }
        var named = intervals.Where(i => i.Origin is { } o && matches(o)).ToList();
        if (named.Count > 1)
            return new(null, Problem("ambiguous-label", $"{named.Count} origins have {what}; name one by case sequence."));
        if (named.Count == 1)
            return new(named[0], null);
        return new(null, checkpoints.Any(exists)
            ? Problem("not-an-origin", $"The checkpoint with {what} is not an origin: only the case's start and its complete recovery checkpoints are.")
            : Problem(unknown, $"No checkpoint has {what}."));
    }

    private static DiagnosticProblem Problem(string code, string message) => new() { Code = code, Message = message };
}
