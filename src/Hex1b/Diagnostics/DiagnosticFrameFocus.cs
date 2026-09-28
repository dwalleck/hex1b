using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The focus ring of a published frame.
/// </summary>
public sealed class DiagnosticFrameFocus
{
    /// <summary>Index of the focused entry, or -1 when nothing had focus.</summary>
    [JsonPropertyName("currentIndex")]
    public int CurrentIndex { get; init; } = -1;

    /// <summary>Type name of the focused node, when one had focus.</summary>
    [JsonPropertyName("focusedNodeType")]
    public string? FocusedNodeType { get; init; }

    /// <summary>The last mouse hit test the ring performed, when one has been performed.</summary>
    [JsonPropertyName("lastHitTest")]
    public string? LastHitTest { get; init; }

    /// <summary>Focusable nodes in focus order.</summary>
    [JsonPropertyName("focusables")]
    public IReadOnlyList<DiagnosticFocusable> Focusables { get; init; } = [];
}
