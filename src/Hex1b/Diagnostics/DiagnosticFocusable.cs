using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One entry of the focus ring in a published frame.
/// </summary>
public sealed class DiagnosticFocusable
{
    /// <summary>Position in focus order.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Node type name.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>Arranged bounds.</summary>
    [JsonPropertyName("bounds")]
    public DiagnosticRect Bounds { get; init; } = new();

    /// <summary>Bounds used for mouse hit testing.</summary>
    [JsonPropertyName("hitTestBounds")]
    public DiagnosticRect HitTestBounds { get; init; } = new();

    /// <summary>Whether this entry had focus.</summary>
    [JsonPropertyName("isFocused")]
    public bool IsFocused { get; init; }
}
