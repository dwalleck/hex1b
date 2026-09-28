using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The input ids one send was assigned, and what acceptance means for the target.
/// </summary>
public sealed class DiagnosticAcceptedInput
{
    /// <summary>The first id the send's events received.</summary>
    [JsonPropertyName("firstId")]
    public long FirstId { get; init; }

    /// <summary>The last id; milestones for the send name this id.</summary>
    [JsonPropertyName("lastId")]
    public long LastId { get; init; }

    /// <summary>What acceptance proves for this target.</summary>
    [JsonPropertyName("meaning")]
    public string Meaning { get; init; } = "";
}
