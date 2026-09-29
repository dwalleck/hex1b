using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A range of model events that can be re-applied from the case's checkpoint, or why none can.</summary>
public sealed record DiagnosticCaseInterval
{
    /// <summary>Whether the range is re-applicable.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>The checkpoint's model sequence (0 for a fresh model).</summary>
    [JsonPropertyName("fromModelSequence")]
    public long? FromModelSequence { get; init; }

    /// <summary>The last model event the range covers.</summary>
    [JsonPropertyName("toModelSequence")]
    public long? ToModelSequence { get; init; }

    /// <summary>Why the range ends (or why there is none).</summary>
    [JsonPropertyName("endReason")]
    public string EndReason { get; init; } = "";
}
