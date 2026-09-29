using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected margins, zero-based and inclusive.</summary>
public sealed record DiagnosticModelMargins
{
    /// <summary>Top scrolling margin.</summary>
    [JsonPropertyName("top")]
    public int Top { get; init; }

    /// <summary>Bottom scrolling margin.</summary>
    [JsonPropertyName("bottom")]
    public int Bottom { get; init; }

    /// <summary>Left margin.</summary>
    [JsonPropertyName("left")]
    public int Left { get; init; }

    /// <summary>Right margin.</summary>
    [JsonPropertyName("right")]
    public int Right { get; init; }
}
