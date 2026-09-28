using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A milestone an operation can wait for on this target, with its exact guarantee.
/// </summary>
public sealed class DiagnosticMilestoneCapability
{
    /// <summary>The milestone.</summary>
    [JsonPropertyName("milestone")]
    public DiagnosticMilestone Milestone { get; init; }

    /// <summary>Whether this target can report it.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    /// <summary>What a met milestone proves, and what it does not.</summary>
    [JsonPropertyName("guarantee")]
    public string Guarantee { get; init; } = "";

    /// <summary>Why the milestone is unavailable, when it is.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
