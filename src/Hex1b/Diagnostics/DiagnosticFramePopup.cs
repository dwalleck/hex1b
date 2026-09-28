using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One entry of the popup stack in a published frame.
/// </summary>
public sealed class DiagnosticFramePopup
{
    /// <summary>Position in the popup stack, bottom first.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Type name of the popup's content node.</summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "";

    /// <summary>Content bounds, when the popup has content.</summary>
    [JsonPropertyName("contentBounds")]
    public DiagnosticRect? ContentBounds { get; init; }

    /// <summary>Whether the popup blocks input to what is beneath it.</summary>
    [JsonPropertyName("isBarrier")]
    public bool IsBarrier { get; init; }

    /// <summary>Whether the popup is anchored to a node.</summary>
    [JsonPropertyName("isAnchored")]
    public bool IsAnchored { get; init; }

    /// <summary>Type name of the node focus returns to when the popup closes.</summary>
    [JsonPropertyName("focusRestoreNodeType")]
    public string? FocusRestoreNodeType { get; init; }

    /// <summary>Type name of the anchor node, for anchored popups.</summary>
    [JsonPropertyName("anchorNodeType")]
    public string? AnchorNodeType { get; init; }

    /// <summary>Bounds of the anchor node, for anchored popups.</summary>
    [JsonPropertyName("anchorBounds")]
    public DiagnosticRect? AnchorBounds { get; init; }

    /// <summary>Whether the anchor no longer matches the node tree.</summary>
    [JsonPropertyName("anchorIsStale")]
    public bool? AnchorIsStale { get; init; }

    /// <summary>Requested position relative to the anchor.</summary>
    [JsonPropertyName("anchorPosition")]
    public string? AnchorPosition { get; init; }
}
