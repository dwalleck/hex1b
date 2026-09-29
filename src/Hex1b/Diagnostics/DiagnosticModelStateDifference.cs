using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>One difference between a recorded and a reconstructed model state.</summary>
public sealed record DiagnosticModelStateDifference
{
    /// <summary>
    /// Where the states differ, in the projection's terms: <c>screen[3][5].text</c>,
    /// <c>history.rows[12][0].style.foreground</c>, <c>modes.wraparound</c>, <c>history.rows.count</c>, ...
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>The recorded value, as JSON; absent when the recorded state has no such value.</summary>
    [JsonPropertyName("recorded")]
    public string? Recorded { get; init; }

    /// <summary>The reconstructed value, as JSON; absent when the reconstructed state has no such value.</summary>
    [JsonPropertyName("reapplied")]
    public string? Reapplied { get; init; }
}
