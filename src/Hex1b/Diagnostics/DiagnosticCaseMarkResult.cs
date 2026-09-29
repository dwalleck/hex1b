using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The result of marking a boundary in the active case.</summary>
public sealed record DiagnosticCaseMarkResult
{
    /// <summary>The diagnostics contract version.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Whether the mark was taken.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why the mark was not taken.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The case the mark belongs to.</summary>
    [JsonPropertyName("caseId")]
    public string? CaseId { get; init; }

    /// <summary>The checkpoint's label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The checkpoint's ordinal among the case's checkpoints.</summary>
    [JsonPropertyName("checkpointOrdinal")]
    public long? CheckpointOrdinal { get; init; }

    /// <summary>The model sequence the mark was taken at.</summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>Whether the model's state was taken (only with <c>reapplication-data</c>).</summary>
    [JsonPropertyName("stateRecorded")]
    public bool? StateRecorded { get; init; }

    /// <summary>
    /// Why no state was recorded: <c>requires reapplication-data</c>, <c>pending-state budget: …</c>, or
    /// <c>capture-failed: …</c>; absent when state was recorded.
    /// </summary>
    [JsonPropertyName("stateReason")]
    public string? StateReason { get; init; }
}
