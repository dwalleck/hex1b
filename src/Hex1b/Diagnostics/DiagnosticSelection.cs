using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A selected range of editor text, from its start to its end (start never after end).
/// </summary>
public sealed class DiagnosticSelection
{
    /// <summary>Start of the selection.</summary>
    [JsonPropertyName("start")]
    public DiagnosticCaret Start { get; init; } = new();

    /// <summary>End of the selection (exclusive).</summary>
    [JsonPropertyName("end")]
    public DiagnosticCaret End { get; init; } = new();
}
