using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A case artifact's completion record, written when the case stops. Its absence means the recording
/// was interrupted: nothing after the last verified event is claimed.
/// </summary>
public sealed record DiagnosticCaseCompletion
{
    /// <summary>The case identifier.</summary>
    [JsonPropertyName("caseId")]
    public string CaseId { get; init; } = "";

    /// <summary>Why the case stopped.</summary>
    [JsonPropertyName("stopReason")]
    public DiagnosticCaseStopReason StopReason { get; init; }

    /// <summary>When the case stopped (UTC).</summary>
    [JsonPropertyName("stoppedAt")]
    public DateTimeOffset StoppedAt { get; init; }

    /// <summary>The case sequence of the last event written, or absent when none was.</summary>
    [JsonPropertyName("lastCaseSequence")]
    public long? LastCaseSequence { get; init; }

    /// <summary>Artifact bytes written, including the manifest.</summary>
    [JsonPropertyName("bytesWritten")]
    public long BytesWritten { get; init; }

    /// <summary>Per-stream counts at stop.</summary>
    [JsonPropertyName("streams")]
    public IReadOnlyList<DiagnosticCaseStreamStatus> Streams { get; init; } = [];

    /// <summary>
    /// Checkpoints taken (<c>offered</c>), written as a checkpoint record (<c>written</c>, with or without
    /// state), and declared missing by a <c>missing</c> range of stream <c>checkpoint</c> (<c>dropped</c>).
    /// </summary>
    [JsonPropertyName("checkpoints")]
    public DiagnosticCaseStreamStatus? Checkpoints { get; init; }
}
