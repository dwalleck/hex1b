using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Result of starting, stopping or checking a diagnostic case. Every CLI, MCP and socket client
/// returns this shape.
/// </summary>
public sealed record DiagnosticCaseResult
{
    /// <summary>Version of the diagnostic contract that produced this result.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome; <see cref="DiagnosticOutcome.Captured"/> when the operation took effect.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>
    /// Why the operation did not take effect: <c>case-active</c>, <c>no-active-case</c>,
    /// <c>storage-refused</c>, or an invalid-request code. Absent on success.
    /// </summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The case identifier.</summary>
    [JsonPropertyName("caseId")]
    public string? CaseId { get; init; }

    /// <summary>The case's artifact directory.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Where the case is in its lifecycle.</summary>
    [JsonPropertyName("state")]
    public DiagnosticCaseState? State { get; init; }

    /// <summary>How the case was started.</summary>
    [JsonPropertyName("startPath")]
    public DiagnosticCaseStartPath? StartPath { get; init; }

    /// <summary>The effective bounds.</summary>
    [JsonPropertyName("bounds")]
    public DiagnosticCaseBounds? Bounds { get; init; }

    /// <summary>The authorizations the case records under.</summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization> Authorizations { get; init; } = [];

    /// <summary>The initial checkpoint.</summary>
    [JsonPropertyName("checkpoint")]
    public DiagnosticCaseCheckpoint? Checkpoint { get; init; }

    /// <summary>When recording started (UTC).</summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Seconds since recording started, or the case's duration once stopped.</summary>
    [JsonPropertyName("elapsedSeconds")]
    public double? ElapsedSeconds { get; init; }

    /// <summary>Artifact bytes written so far.</summary>
    [JsonPropertyName("bytesWritten")]
    public long? BytesWritten { get; init; }

    /// <summary>Per-stream counts.</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<DiagnosticCaseStreamStatus> Streams { get; init; } = [];

    /// <summary>Why the case stopped; absent while it records.</summary>
    [JsonPropertyName("stopReason")]
    public DiagnosticCaseStopReason? StopReason { get; init; }
}
