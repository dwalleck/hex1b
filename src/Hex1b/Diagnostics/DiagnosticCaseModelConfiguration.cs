using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The configuration that determines a fresh terminal model's state: a fresh model built with these
/// values holds the same state (checkpoint profile <c>fresh-model/1</c>). Format 2 records every value
/// structurally, so a model can be rebuilt from it.
/// </summary>
public sealed record DiagnosticCaseModelConfiguration
{
    /// <summary>Columns at construction.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Rows at construction.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>Retained model-history rows, or absent when the model keeps none.</summary>
    [JsonPropertyName("scrollbackCapacity")]
    public int? ScrollbackCapacity { get; init; }

    /// <summary>Retained command marks.</summary>
    [JsonPropertyName("commandMarkHistoryCapacity")]
    public int CommandMarkHistoryCapacity { get; init; }

    /// <summary>Most custom markers.</summary>
    [JsonPropertyName("customMarkerLimit")]
    public int? CustomMarkerLimit { get; init; }

    /// <summary>How long an incomplete escape sequence is held before it is flushed, in milliseconds.</summary>
    [JsonPropertyName("escapeSequenceTimeoutMs")]
    public double EscapeSequenceTimeoutMs { get; init; }

    /// <summary>Whether the presentation reflows the model on resize.</summary>
    [JsonPropertyName("reflowEnabled")]
    public bool ReflowEnabled { get; init; }

    /// <summary>
    /// The presentation's reflow strategy (<c>none</c>, <c>kitty</c>, <c>xterm</c>, ...), whether or not reflow
    /// is enabled: the strategy also decides whether absolute positioning clears soft wraps. A strategy
    /// this build cannot name is <c>custom:</c> and its type.
    /// </summary>
    [JsonPropertyName("reflowStrategy")]
    public string ReflowStrategy { get; init; } = "none";

    /// <summary>The presentation adapter type.</summary>
    [JsonPropertyName("presentation")]
    public string Presentation { get; init; } = "";

    /// <summary>The workload adapter type.</summary>
    [JsonPropertyName("workload")]
    public string Workload { get; init; } = "";

    /// <summary>The presentation's terminal capabilities, which may come from the environment.</summary>
    [JsonPropertyName("capabilities")]
    public DiagnosticCaseCapabilities? Capabilities { get; init; }

    /// <summary>Graphics policy and retained-resource limits.</summary>
    [JsonPropertyName("graphics")]
    public DiagnosticCaseGraphicsLimits? Graphics { get; init; }

    /// <summary>Effective DCS introducer limits; required to rebuild both fresh and continued parsing.</summary>
    [JsonPropertyName("dcsFraming")]
    public DiagnosticCaseDcsFramingConfiguration? DcsFraming { get; init; }

    /// <summary>Recorded fields this build does not know; re-application refuses a configuration with any.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
