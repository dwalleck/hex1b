using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A range of model events that can be re-applied from one origin (the case's initial checkpoint, or a complete recovery
/// checkpoint), or why none can from it. A case lists one interval per origin, in checkpoint order; no interval extends
/// past a loss or an unsupported event, and a later origin never changes an earlier interval.
/// </summary>
public sealed record DiagnosticCaseInterval
{
    /// <summary>Whether the range is re-applicable.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>The checkpoint the range starts from; absent when the case's initial checkpoint itself is not complete.</summary>
    [JsonPropertyName("origin")]
    public DiagnosticCaseOrigin? Origin { get; init; }

    /// <summary>The origin's model sequence (0 for a fresh model).</summary>
    [JsonPropertyName("fromModelSequence")]
    public long? FromModelSequence { get; init; }

    /// <summary>The last model event the range covers.</summary>
    [JsonPropertyName("toModelSequence")]
    public long? ToModelSequence { get; init; }

    /// <summary>Why the range ends (or why there is none).</summary>
    [JsonPropertyName("endReason")]
    public string EndReason { get; init; } = "";
}
