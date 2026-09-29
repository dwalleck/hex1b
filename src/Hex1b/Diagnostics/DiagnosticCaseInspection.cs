using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A case artifact as read from disk: what it is, how it ended, what each stream holds, which model
/// ranges can be re-applied, and optionally a page of events. Every CLI and MCP client returns this shape.
/// </summary>
public sealed record DiagnosticCaseInspection
{
    /// <summary>Version of the diagnostic contract that read the artifact.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome; <see cref="DiagnosticOutcome.Captured"/> when the artifact was read.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why the artifact could not be read.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The artifact directory.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The manifest.</summary>
    [JsonPropertyName("manifest")]
    public DiagnosticCaseManifest? Manifest { get; init; }

    /// <summary>The completion record, absent when interrupted.</summary>
    [JsonPropertyName("completion")]
    public DiagnosticCaseCompletion? Completion { get; init; }

    /// <summary>Whether the artifact was finished.</summary>
    [JsonPropertyName("completionState")]
    public DiagnosticCaseCompletionState? CompletionState { get; init; }

    /// <summary>The 1-based line of <c>events.jsonl</c> where verification stopped, when truncated.</summary>
    [JsonPropertyName("truncatedAtLine")]
    public long? TruncatedAtLine { get; init; }

    /// <summary>The last verified event's case sequence.</summary>
    [JsonPropertyName("lastCaseSequence")]
    public long? LastCaseSequence { get; init; }

    /// <summary>What each stream holds.</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<DiagnosticCaseStreamCoverage> Streams { get; init; } = [];

    /// <summary>Re-applicable model ranges.</summary>
    [JsonPropertyName("intervals")]
    public IReadOnlyList<DiagnosticCaseInterval> Intervals { get; init; } = [];

    /// <summary>Events after the requested case sequence, oldest first, at most the requested limit.</summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<DiagnosticCaseEvent> Events { get; init; } = [];
}
