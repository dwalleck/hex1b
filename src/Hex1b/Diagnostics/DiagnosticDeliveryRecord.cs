using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One presentation write and what the presentation did with it.
/// </summary>
public sealed record DiagnosticDeliveryRecord
{
    /// <summary>Per-session sequence, assigned when the write started, in start order.</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    /// <summary>The terminal path that made the write.</summary>
    [JsonPropertyName("source")]
    public DiagnosticDeliverySource Source { get; init; }

    /// <summary>What the presentation did with the write.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticDeliveryOutcome Outcome { get; init; }

    /// <summary>Relative to model application of the same bytes; absent for terminal-control writes.</summary>
    [JsonPropertyName("phase")]
    public DiagnosticDeliveryPhase? Phase { get; init; }

    /// <summary>Bytes the write carried.</summary>
    [JsonPropertyName("length")]
    public int Length { get; init; }

    /// <summary>
    /// Bytes the host took: the length when accepted, 0 when refused, and for a failure the bytes
    /// taken before the error; absent when the presentation cannot observe it.
    /// </summary>
    [JsonPropertyName("bytesAccepted")]
    public int? BytesAccepted { get; init; }

    /// <summary>Why a write was refused.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The error a failed write raised (type and message).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>When the write started, in the <c>process-monotonic</c> domain of capture acquisitions.</summary>
    [JsonPropertyName("startTimestamp")]
    public long StartTimestamp { get; init; }

    /// <summary>When the write returned or failed, in the <c>process-monotonic</c> domain.</summary>
    [JsonPropertyName("endTimestamp")]
    public long EndTimestamp { get; init; }

    /// <summary>When the write started (UTC).</summary>
    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The terminal model sequence when the write started.</summary>
    [JsonPropertyName("modelSequenceAtStart")]
    public long ModelSequenceAtStart { get; init; }

    /// <summary>
    /// The output item the write carried (input-milestone output sequence), which links it to the
    /// application frame whose output mark covers it; absent when the session does not track milestones
    /// or for terminal-control writes.
    /// </summary>
    [JsonPropertyName("outputSequence")]
    public long? OutputSequence { get; init; }

    /// <summary>The retained written bytes (base64); only with native-output authorization.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>Only the first 64 KiB of the write was retained.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>The retained bytes were dropped to keep the session within its 1 MiB budget.</summary>
    [JsonPropertyName("bytesEvicted")]
    public bool BytesEvicted { get; init; }
}
