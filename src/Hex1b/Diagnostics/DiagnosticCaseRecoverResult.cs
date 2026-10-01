using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The result of requesting a recovery checkpoint in the active case: a complete <c>text-state/1</c> checkpoint of the
/// terminal model, taken between two model events and classified as a start is, from which re-application can begin
/// after recording loss. A refused request records the attempt as a checkpoint without state.
/// </summary>
public sealed record DiagnosticCaseRecoverResult
{
    /// <summary>The diagnostics contract version.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Captured when the checkpoint is complete; otherwise why not.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>
    /// Why no complete checkpoint was taken: <c>no-active-case</c>, <c>requires-reapplication-data</c>,
    /// <c>hmp1-workload</c>, <c>busy</c>, or the recorded refusal's kind (<c>mid-application</c>, <c>unapplied-output</c>,
    /// <c>unsupported-surfaces</c>, <c>configuration</c>, <c>pending-state-budget</c>, <c>size-limit</c>,
    /// <c>capture-failed</c>).
    /// </summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The case the checkpoint belongs to.</summary>
    [JsonPropertyName("caseId")]
    public string? CaseId { get; init; }

    /// <summary>The checkpoint's label (default <c>recovery-</c> and its ordinal).</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The checkpoint's ordinal among the case's checkpoints; present for a recorded refusal too.</summary>
    [JsonPropertyName("checkpointOrdinal")]
    public long? CheckpointOrdinal { get; init; }

    /// <summary>The model sequence the checkpoint was taken at.</summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary><c>complete</c>, or <c>unsupported</c> for a recorded refusal.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>The recorded refusal's reason; absent when complete.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The state surfaces the checkpoint cannot restore, when that is the refusal.</summary>
    [JsonPropertyName("unsupportedSurfaces")]
    public IReadOnlyList<string>? UnsupportedSurfaces { get; init; }
}
