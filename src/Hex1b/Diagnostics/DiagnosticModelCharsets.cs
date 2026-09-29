using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected character sets.</summary>
public sealed record DiagnosticModelCharsets
{
    /// <summary>G0 designation.</summary>
    [JsonPropertyName("g0")]
    public string G0 { get; init; } = "B";

    /// <summary>G1 designation.</summary>
    [JsonPropertyName("g1")]
    public string G1 { get; init; } = "B";

    /// <summary>G2 designation.</summary>
    [JsonPropertyName("g2")]
    public string G2 { get; init; } = "B";

    /// <summary>G3 designation.</summary>
    [JsonPropertyName("g3")]
    public string G3 { get; init; } = "B";

    /// <summary>The invoked slot (0 to 3).</summary>
    [JsonPropertyName("active")]
    public int Active { get; init; }
}
