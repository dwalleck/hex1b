using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// States whether one content class is present in an observation.
/// </summary>
public sealed class DiagnosticContentCoverage
{
    /// <summary>The content class.</summary>
    [JsonPropertyName("content")]
    public DiagnosticContentClass Content { get; init; }

    /// <summary>Whether the content is included, excluded, or unavailable.</summary>
    [JsonPropertyName("state")]
    public DiagnosticCoverageState State { get; init; }

    /// <summary>Why the content is excluded or unavailable; absent when included.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
