using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The result of re-applying a recorded case.</summary>
public sealed record DiagnosticCaseReapplyResult
{
    /// <summary>The diagnostics contract version.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary><c>captured</c> when the case was re-applied (whatever the comparison); otherwise why not.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why nothing was compared: <c>invalid-request</c> codes, <c>incompatible</c>, <c>beyond-interval</c>, <c>storage-refused</c>, <c>reapplication-failed</c>, ...</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The case's starting checkpoint (<c>fresh-model/1</c>), with the recorded model configuration the replica was built from.</summary>
    [JsonPropertyName("checkpoint")]
    public DiagnosticCaseCheckpoint? Checkpoint { get; init; }

    /// <summary>What the comparison covers and leaves out.</summary>
    [JsonPropertyName("coverage")]
    public DiagnosticModelStateCoverage? Coverage { get; init; }

    /// <summary>The build and process that recorded the case (the manifest's identity).</summary>
    [JsonPropertyName("producer")]
    public DiagnosticObservationIdentity? Producer { get; init; }

    /// <summary>The Hex1b build that re-applied it (its version; <see cref="Consumer"/> adds its build id).</summary>
    [JsonPropertyName("consumerHex1bVersion")]
    public string? ConsumerHex1bVersion { get; init; }

    /// <summary>The build that re-applied it: version and build id (ticket 14).</summary>
    [JsonPropertyName("consumer")]
    public DiagnosticBuildIdentity? Consumer { get; init; }

    /// <summary>
    /// What was compared between the case's declarations and this build (ticket 14): the seven checks in order with
    /// both sides' values and verdicts, and whether the two builds are the same. Every result carries it; a result
    /// refused before any check ran carries every check <c>not-checked</c>.
    /// </summary>
    [JsonPropertyName("compatibility")]
    public DiagnosticCaseCompatibility Compatibility { get; init; } = DiagnosticCaseCompatibility.Unchecked;

    /// <summary>The case directory.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>This run's own directory, <c>reapplications/&lt;n&gt;</c> in the case.</summary>
    [JsonPropertyName("runPath")]
    public string? RunPath { get; init; }

    /// <summary>The target boundary.</summary>
    [JsonPropertyName("target")]
    public DiagnosticCaseReapplyTarget? Target { get; init; }

    /// <summary>The checkpoint the replica was restored from (or, for a fresh model, started from).</summary>
    [JsonPropertyName("origin")]
    public DiagnosticCaseOrigin? Origin { get; init; }

    /// <summary>The last model sequence the reconstructed model applied.</summary>
    [JsonPropertyName("appliedThrough")]
    public long? AppliedThrough { get; init; }

    /// <summary>The last boundary the case can be re-applied to, when the target is beyond it.</summary>
    [JsonPropertyName("lastValidModelSequence")]
    public long? LastValidModelSequence { get; init; }

    /// <summary>Why re-applicable coverage ends there.</summary>
    [JsonPropertyName("intervalEndReason")]
    public string? IntervalEndReason { get; init; }

    /// <summary><c>matched</c>, <c>different</c>, or <c>unavailable</c> (see <see cref="ComparisonReason"/>).</summary>
    [JsonPropertyName("comparison")]
    public string? Comparison { get; init; }

    /// <summary>Why the comparison is unavailable, or where the models diverged.</summary>
    [JsonPropertyName("comparisonReason")]
    public string? ComparisonReason { get; init; }

    /// <summary>The typed differences, when compared.</summary>
    [JsonPropertyName("differences")]
    public DiagnosticModelStateComparison? Differences { get; init; }

    /// <summary>True when faults were injected: the comparison is then not the recorded path's outcome.</summary>
    [JsonPropertyName("faultInjected")]
    public bool FaultInjected { get; init; }

    /// <summary>The injected faults.</summary>
    [JsonPropertyName("faults")]
    public IReadOnlyList<DiagnosticCaseInjectedFault> Faults { get; init; } = [];

    /// <summary>Files written in the run directory.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];
}
