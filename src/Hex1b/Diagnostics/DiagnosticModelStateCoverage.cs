using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>What a re-application's comparison covers: the projection profile, its surfaces, and what it leaves out.</summary>
public sealed record DiagnosticModelStateCoverage
{
    /// <summary>The state projection profile compared.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = DiagnosticCaseCheckpointProfiles.TextState;

    /// <summary>The surfaces compared, in the order differences are listed.</summary>
    [JsonPropertyName("compared")]
    public IReadOnlyList<string> Compared { get; init; } = [];

    /// <summary>What the profile leaves out, and why.</summary>
    [JsonPropertyName("excluded")]
    public IReadOnlyList<string> Excluded { get; init; } = [];
}
