using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Recorded Sixel cell metrics.</summary>
public sealed record DiagnosticCaseSixelCellMetrics
{
    /// <summary>Cell width in pixels.</summary>
    [JsonPropertyName("width")]
    public double Width { get; init; }

    /// <summary>Cell height in pixels.</summary>
    [JsonPropertyName("height")]
    public double Height { get; init; }

    /// <summary>Where the metrics came from (the <c>SixelCellMetricsSource</c> name).</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary>How reliable the metrics are (the <c>SixelCellMetricsReliability</c> name).</summary>
    [JsonPropertyName("reliability")]
    public string Reliability { get; init; } = "";

    /// <summary>Recorded fields this build does not know.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
