using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Asks a capture to wait, bounded, until a named milestone of one input is met.
/// </summary>
public sealed class DiagnosticMilestoneRequest
{
    /// <summary>The stage to wait for.</summary>
    [JsonPropertyName("milestone")]
    public DiagnosticMilestone Milestone { get; init; }

    /// <summary>The input id, as returned by a send (its <c>lastId</c>).</summary>
    [JsonPropertyName("inputId")]
    public long? InputId { get; init; }

    /// <summary>Maximum wait in milliseconds: 1 to 60,000; default 5,000.</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}
