using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A case artifact's manifest, written when the case starts: what the case is, how it was started and
/// bounded, what it records, and the checkpoint its events start from.
/// </summary>
public sealed record DiagnosticCaseManifest
{
    /// <summary>The artifact format version this manifest belongs to.</summary>
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    /// <summary>Version of the diagnostic contract that wrote the case.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; }

    /// <summary>The case identifier.</summary>
    [JsonPropertyName("caseId")]
    public string CaseId { get; init; } = "";

    /// <summary>How the case was started.</summary>
    [JsonPropertyName("startPath")]
    public DiagnosticCaseStartPath StartPath { get; init; }

    /// <summary>When recording started (UTC).</summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When recording started, in the <c>process-monotonic</c> domain of every event timestamp.</summary>
    [JsonPropertyName("startTimestamp")]
    public long StartTimestamp { get; init; }

    /// <summary>Ticks per second of the <c>process-monotonic</c> domain.</summary>
    [JsonPropertyName("timestampFrequency")]
    public long TimestampFrequency { get; init; }

    /// <summary>Whether the model was fresh (nothing applied or read since construction) when recording started.</summary>
    [JsonPropertyName("fresh")]
    public bool Fresh { get; init; }

    /// <summary>The initial checkpoint.</summary>
    [JsonPropertyName("checkpoint")]
    public DiagnosticCaseCheckpoint Checkpoint { get; init; } = new();

    /// <summary>The effective bounds.</summary>
    [JsonPropertyName("bounds")]
    public DiagnosticCaseBounds Bounds { get; init; } = new();

    /// <summary>The authorizations the case records under.</summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization> Authorizations { get; init; } = [];

    /// <summary>Process, session, build and configuration identity of the recording target.</summary>
    [JsonPropertyName("identity")]
    public DiagnosticObservationIdentity? Identity { get; init; }

    /// <summary>What the case records for each stream.</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<DiagnosticCaseStreamDeclaration> Streams { get; init; } = [];
}
