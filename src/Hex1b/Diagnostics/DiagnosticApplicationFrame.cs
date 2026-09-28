using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// An immutable projection of one completed application render pass, published on the
/// application loop at the end of that pass.
/// </summary>
public sealed record DiagnosticApplicationFrame
{
    /// <summary>The application's completed-pass count after this pass.</summary>
    [JsonPropertyName("frameId")]
    public long FrameId { get; init; }

    /// <summary>Frame width in columns.</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; init; }

    /// <summary>Frame height in rows.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>Whether this pass wrote terminal output; layout- or focus-only passes do not.</summary>
    [JsonPropertyName("wroteOutput")]
    public bool WroteOutput { get; init; }

    /// <summary>Root of the node tree, absent when the application had no nodes.</summary>
    [JsonPropertyName("root")]
    public DiagnosticFrameNode? Root { get; init; }

    /// <summary>The popup stack, bottom first.</summary>
    [JsonPropertyName("popups")]
    public IReadOnlyList<DiagnosticFramePopup> Popups { get; init; } = [];

    /// <summary>The focus ring.</summary>
    [JsonPropertyName("focus")]
    public DiagnosticFrameFocus Focus { get; init; } = new();

    /// <summary>The focused editor's interaction state, when the focused node is an editor.</summary>
    [JsonPropertyName("focusedEditor")]
    public DiagnosticEditorState? FocusedEditor { get; init; }

    /// <summary>Phase timings of this pass, when diagnostic timing is enabled.</summary>
    [JsonPropertyName("timings")]
    public DiagnosticFrameTimings? Timings { get; init; }
}
