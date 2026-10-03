using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The producer's effective DCS introducer limits, recorded field by field (manifest format 2).</summary>
public sealed record DiagnosticCaseDcsFramingConfiguration
{
    /// <summary>Maximum parameters accepted in a DCS introducer.</summary>
    [JsonPropertyName("maximumHeaderParameters")]
    public int MaximumHeaderParameters { get; init; }

    /// <summary>Maximum numeric value accepted in DCS parameters.</summary>
    [JsonPropertyName("maximumNumericValue")]
    public int MaximumNumericValue { get; init; }

    /// <summary>Recorded fields this build does not know; re-application refuses a configuration with any.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
