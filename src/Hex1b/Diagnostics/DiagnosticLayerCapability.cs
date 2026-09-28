using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Whether a target can provide observations from one evidence layer.
/// </summary>
public sealed class DiagnosticLayerCapability
{
    /// <summary>The evidence layer.</summary>
    [JsonPropertyName("layer")]
    public DiagnosticLayer Layer { get; init; }

    /// <summary>Whether observations of this layer are available through the contract.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    /// <summary>Why the layer is unavailable; absent when available.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
