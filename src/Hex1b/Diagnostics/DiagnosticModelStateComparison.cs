using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A typed comparison of two model states. Every difference is counted; at most the cap is listed,
/// in the projection's order (geometry, screens, history, then the other surfaces).
/// </summary>
public sealed record DiagnosticModelStateComparison
{
    /// <summary>Every difference found.</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }

    /// <summary>Differences per surface (the path's first segment), in surface order.</summary>
    [JsonPropertyName("bySurface")]
    public IReadOnlyDictionary<string, long> BySurface { get; init; } = new Dictionary<string, long>();

    /// <summary>Whether more differences exist than are listed.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>The first differences, up to the cap.</summary>
    [JsonPropertyName("differences")]
    public IReadOnlyList<DiagnosticModelStateDifference> Differences { get; init; } = [];
}
