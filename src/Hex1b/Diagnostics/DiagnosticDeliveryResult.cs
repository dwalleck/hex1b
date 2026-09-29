using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Result of a native delivery capture: the session's recent presentation writes and what the
/// presentation did with each. Every CLI, MCP, and socket client returns this shape.
/// </summary>
public sealed record DiagnosticDeliveryResult
{
    /// <summary>Version of the diagnostic contract that produced this result.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome; only <see cref="DiagnosticOutcome.Captured"/> carries records.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why no record was returned; absent when captured.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The delivery layer written to: <c>console</c> (a native terminal) or <c>websocket</c> (a transport).</summary>
    [JsonPropertyName("deliveryLayer")]
    public string? DeliveryLayer { get; init; }

    /// <summary>When recording started (UTC); writes before it are not covered.</summary>
    [JsonPropertyName("coverageStartedAt")]
    public DateTimeOffset? CoverageStartedAt { get; init; }

    /// <summary>When recording started, in the <c>process-monotonic</c> domain.</summary>
    [JsonPropertyName("coverageStartTimestamp")]
    public long? CoverageStartTimestamp { get; init; }

    /// <summary>Completed records after the requested sequence, oldest first, at most the requested limit.</summary>
    [JsonPropertyName("records")]
    public IReadOnlyList<DiagnosticDeliveryRecord> Records { get; init; } = [];

    /// <summary>Counts since coverage started.</summary>
    [JsonPropertyName("totals")]
    public DiagnosticDeliveryTotals? Totals { get; init; }

    /// <summary>Records no longer retained (the session keeps the most recent 4,096).</summary>
    [JsonPropertyName("evictedRecords")]
    public long EvictedRecords { get; init; }

    /// <summary>
    /// Writes still in progress. They, and any later write, are reported once the oldest of them
    /// completes, so a <c>since</c> continuation never skips a record.
    /// </summary>
    [JsonPropertyName("writesInProgress")]
    public int WritesInProgress { get; init; }

    /// <summary>Where and when the record was read.</summary>
    [JsonPropertyName("identity")]
    public DiagnosticObservationIdentity? Identity { get; init; }

    /// <summary>Included, excluded, and unavailable content classes.</summary>
    [JsonPropertyName("contentCoverage")]
    public IReadOnlyList<DiagnosticContentCoverage> ContentCoverage { get; init; } = [];

    /// <summary>Fields that are absent and why.</summary>
    [JsonPropertyName("unavailableFields")]
    public IReadOnlyList<DiagnosticUnavailableField> UnavailableFields { get; init; } = [];

    /// <summary>Limits on what this observation can support.</summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
