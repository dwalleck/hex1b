using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Commands.App;
using Hex1b.Tool.Infrastructure;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Shared output for the case commands: the full contract result as JSON, or a short text summary.
/// Target-supplied text is escaped before it reaches the terminal.
/// </summary>
internal static class CaseCommandOutput
{
    public static int Write(OutputFormatter formatter, DiagnosticCaseResult result, bool json, string verb)
    {
        if (json)
            WriteJson(result);
        if (result.Outcome != DiagnosticOutcome.Captured)
            return Failure(formatter, result.Outcome, result.Problem);
        if (json)
            return 0;

        formatter.WriteLine($"Case {AppTreeCommand.Safe(result.CaseId)} {verb}: {DiagnosticContractNames.Of(result.State!.Value)}" +
            (result.StopReason is { } reason ? $" ({DiagnosticContractNames.Of(reason)})" : ""));
        formatter.WriteLine($"Path: {AppTreeCommand.Safe(result.Path)}");
        var checkpoint = result.Checkpoint!;
        // A start's unsupported surfaces are listed by Checkpoint(...); its reason repeats them, so it is left out.
        formatter.WriteLine($"Checkpoint: {Checkpoint(checkpoint)}" +
            (checkpoint.Reason is { } why && checkpoint.UnsupportedSurfaces is not { Count: > 0 } ? $" ({AppTreeCommand.Safe(why)})" : ""));
        formatter.WriteLine($"Bounds: {result.Bounds!.MaxBytes} bytes, {result.Bounds.MaxSeconds} s; " +
            $"written {result.BytesWritten ?? 0} bytes in {result.ElapsedSeconds ?? 0:0.###} s");
        foreach (var stream in result.Streams)
            formatter.WriteLine($"  {AppTreeCommand.Safe(stream.Stream)}: {stream.Offered} offered, {stream.Written} written, {stream.Dropped} dropped");
        return 0;
    }

    public static int Write(OutputFormatter formatter, DiagnosticCaseInspection inspection, bool json)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(inspection, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaseInspection))));
        if (inspection.Outcome != DiagnosticOutcome.Captured)
            return Failure(formatter, inspection.Outcome, inspection.Problem);
        if (json)
            return 0;

        var manifest = inspection.Manifest!;
        formatter.WriteLine($"Case {AppTreeCommand.Safe(manifest.CaseId)}: {DiagnosticContractNames.Of(inspection.CompletionState!.Value)}" +
            (inspection.Completion is { } completion ? $" ({DiagnosticContractNames.Of(completion.StopReason)})" : "") +
            (inspection.TruncatedAtLine is { } line ? $", verified through line {line - 1}" : ""));
        formatter.WriteLine($"Started: {manifest.StartedAt:O} ({DiagnosticContractNames.Of(manifest.StartPath)}); " +
            $"checkpoint {Checkpoint(manifest.Checkpoint)}");
        foreach (var stream in inspection.Streams)
        {
            formatter.WriteLine($"  {AppTreeCommand.Safe(stream.Stream)}: {AppTreeCommand.Safe(stream.State)}, {stream.Events} events" +
                (stream.FirstOrdinal is { } first ? $" ({first}..{stream.LastOrdinal})" : ""));
            foreach (var missing in stream.Missing)
                formatter.WriteLine($"    missing {missing.FromOrdinal}..{(missing.ToOrdinal?.ToString() ?? "end")}" +
                    (missing.Extent == "envelope" ? " (bounded by its envelope)" : "") + $": {AppTreeCommand.Safe(missing.Reason)}");
        }

        foreach (var interval in inspection.Intervals)
            formatter.WriteLine((interval.Valid
                ? $"Re-applicable: model {interval.FromModelSequence}..{interval.ToModelSequence} (ends: {AppTreeCommand.Safe(interval.EndReason)})"
                : $"Not re-applicable: {AppTreeCommand.Safe(interval.EndReason)}") + Origin(interval.Origin));
        formatter.WriteLine($"Events on this page: {inspection.Events.Count}");
        return 0;
    }

    public static int Write(OutputFormatter formatter, DiagnosticCaseMarkResult result, bool json)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaseMarkResult))));
        if (result.Outcome != DiagnosticOutcome.Captured)
            return Failure(formatter, result.Outcome, result.Problem);
        if (json)
            return 0;

        formatter.WriteLine($"Case {AppTreeCommand.Safe(result.CaseId)} marked '{AppTreeCommand.Safe(result.Label)}' " +
            $"(checkpoint {result.CheckpointOrdinal}) at model sequence {result.ModelSequence}" +
            (result.StateRecorded == true ? "" : $"; boundary only ({AppTreeCommand.Safe(result.StateReason)})"));
        return 0;
    }

    public static int Write(OutputFormatter formatter, DiagnosticCaseRecoverResult result, bool json)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaseRecoverResult))));
        if (result.Outcome != DiagnosticOutcome.Captured)
            return Failure(formatter, result.Outcome, result.Problem);
        if (json)
            return 0;

        formatter.WriteLine($"Case {AppTreeCommand.Safe(result.CaseId)} recovered '{AppTreeCommand.Safe(result.Label)}' " +
            $"(checkpoint {result.CheckpointOrdinal}) at model sequence {result.ModelSequence}: a new origin");
        return 0;
    }

    public static int Write(OutputFormatter formatter, DiagnosticCaseReapplyResult result, bool json)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaseReapplyResult))));
        if (result.Outcome != DiagnosticOutcome.Captured)
        {
            Failure(formatter, result.Outcome, result.Problem);
            if (!json && result.LastValidModelSequence is { } last)
                formatter.WriteError($"Re-applicable through model sequence {last} (ends: {AppTreeCommand.Safe(result.IntervalEndReason)})");
            if (!json && result.Compatibility.Checks.FirstOrDefault(c => c.Verdict == DiagnosticCaseCompatibility.Incompatible) is { } failed)
                formatter.WriteError($"Incompatible: {AppTreeCommand.Safe(failed.Check)} (the case declares {AppTreeCommand.Safe(failed.Producer)}; this build {AppTreeCommand.Safe(failed.Consumer)})");
            return 1;
        }
        if (json)
            return result.Comparison == "matched" ? 0 : 2;

        var target = result.Target!;
        formatter.WriteLine(Builds(result));
        if (result.Origin is { Trigger: "start", Profile: DiagnosticCaseCheckpointProfiles.TextState } start)
            formatter.WriteLine($"Restored from the {AppTreeCommand.Safe(start.Profile)} start at model sequence {start.ModelSequence}");
        else if (result.Origin is { } recovery && recovery.Trigger != "start")
            formatter.WriteLine($"Restored from recovery '{AppTreeCommand.Safe(recovery.Label)}' (checkpoint {recovery.CheckpointOrdinal}) at model sequence {recovery.ModelSequence}");
        formatter.WriteLine($"Re-applied to model sequence {target.ModelSequence}" +
            (target.Label is { } label ? $" ('{AppTreeCommand.Safe(label)}')" : "") + $": {AppTreeCommand.Safe(result.Comparison)}" +
            (result.ComparisonReason is { } reason ? $" ({AppTreeCommand.Safe(reason)})" : ""));
        if (result.FaultInjected)
            formatter.WriteLine($"Fault injected: {string.Join(", ", result.Faults.Select(f => $"{AppTreeCommand.Safe(f.Kind)} at {AppTreeCommand.Safe(f.Path)}"))}");
        if (result.Differences is { Total: > 0 } differences)
        {
            formatter.WriteLine($"Differences: {differences.Total}" + (differences.Truncated ? $" (first {differences.Differences.Count} listed)" : "") +
                $"; by surface: {string.Join(", ", differences.BySurface.Select(s => $"{AppTreeCommand.Safe(s.Key)} {s.Value}"))}");
            foreach (var difference in differences.Differences.Take(20))
                formatter.WriteLine($"  {AppTreeCommand.Safe(difference.Path)}: {AppTreeCommand.Safe(difference.Recorded ?? "(absent)")} recorded, " +
                    $"{AppTreeCommand.Safe(difference.Reapplied ?? "(absent)")} re-applied");
        }
        formatter.WriteLine($"Run: {AppTreeCommand.Safe(result.RunPath)} ({string.Join(", ", result.Files.Select(AppTreeCommand.Safe))})");
        return result.Comparison == "matched" ? 0 : 2;
    }

    // A case's initial checkpoint: its profile and status, and for a start on a model that had applied output, its
    // model sequence and any surfaces it could not restore.
    private static string Checkpoint(DiagnosticCaseCheckpoint checkpoint) =>
        $"{AppTreeCommand.Safe(checkpoint.Profile)} {DiagnosticContractNames.Of(checkpoint.Status)}" +
        (checkpoint.ModelSequence is { } start ? $" at model sequence {start}" : "") +
        (checkpoint.UnsupportedSurfaces is { Count: > 0 } surfaces ? $"; unsupported surfaces: {string.Join(", ", surfaces.Select(AppTreeCommand.Safe))}" : "");

    // The two builds a re-application met: the one that recorded the case and the one that re-applied it (ticket 14).
    private static string Builds(DiagnosticCaseReapplyResult result) =>
        $"Builds: recorded by {AppTreeCommand.Safe(result.Producer?.Hex1bVersion)} ({AppTreeCommand.Safe(result.Producer?.Hex1bBuild ?? "no build id")}); " +
        $"re-applied by {AppTreeCommand.Safe(result.Consumer?.Hex1bVersion)} ({AppTreeCommand.Safe(result.Consumer?.Hex1bBuild)}): " +
        (result.Compatibility.SameBuild switch { true => "same build", false => "different builds", null => "build ids not compared" });

    // An interval's origin: the case's start, or the recovery checkpoint it restores from.
    private static string Origin(DiagnosticCaseOrigin? origin) =>
        origin is null ? "" : origin.Trigger == "start" ? " from the start" : $" from recovery '{AppTreeCommand.Safe(origin.Label)}' (checkpoint {origin.CheckpointOrdinal})";

    private static int Failure(OutputFormatter formatter, DiagnosticOutcome outcome, DiagnosticProblem? problem)
    {
        formatter.WriteError($"{DiagnosticContractNames.Of(outcome)} ({AppTreeCommand.Safe(problem?.Code)}): {AppTreeCommand.Safe(problem?.Message)}");
        return 1;
    }

    private static void WriteJson(DiagnosticCaseResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaseResult))));
}
