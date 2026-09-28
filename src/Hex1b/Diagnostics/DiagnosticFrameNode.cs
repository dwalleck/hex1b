using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One node of a published application frame, as it was laid out and clipped in that frame.
/// </summary>
public sealed class DiagnosticFrameNode
{
    /// <summary>Node type name, for example <c>TextBlockNode</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>Arranged bounds.</summary>
    [JsonPropertyName("bounds")]
    public DiagnosticRect Bounds { get; init; } = new();

    /// <summary>Bounds used for mouse hit testing.</summary>
    [JsonPropertyName("hitTestBounds")]
    public DiagnosticRect HitTestBounds { get; init; } = new();

    /// <summary>Bounds of the node's content area.</summary>
    [JsonPropertyName("contentBounds")]
    public DiagnosticRect ContentBounds { get; init; } = new();

    /// <summary>Bounds intersected with the screen and every enclosing clip region.</summary>
    [JsonPropertyName("visibleBounds")]
    public DiagnosticRect VisibleBounds { get; init; } = new();

    /// <summary>How much of the node is visible.</summary>
    [JsonPropertyName("clipState")]
    public DiagnosticClipState ClipState { get; init; }

    /// <summary>The clip region this node imposes on its descendants, when it is a clip provider.</summary>
    [JsonPropertyName("clipRect")]
    public DiagnosticRect? ClipRect { get; init; }

    /// <summary>The clip mode of a clip provider: <c>clip</c> or <c>overflow</c>.</summary>
    [JsonPropertyName("clipMode")]
    public string? ClipMode { get; init; }

    /// <summary>Whether the node can take focus.</summary>
    [JsonPropertyName("isFocusable")]
    public bool IsFocusable { get; init; }

    /// <summary>Whether the node had focus in this frame.</summary>
    [JsonPropertyName("isFocused")]
    public bool IsFocused { get; init; }

    /// <summary>Text the node renders (non-editor nodes); rendered application content.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Type-specific values such as a list's selected index.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>Child nodes in render order.</summary>
    [JsonPropertyName("children")]
    public IReadOnlyList<DiagnosticFrameNode> Children { get; init; } = [];
}
