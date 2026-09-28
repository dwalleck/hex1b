using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// An immutable projection of one completed application render pass, published on the
/// application loop at the end of that pass.
/// </summary>
public sealed record DiagnosticApplicationFrame
{
    /// <summary>
    /// Identifies the application instance that published this frame. Frame ids count passes
    /// per instance, so (session, instance, frame id) identifies a frame; each inline flow step
    /// is its own instance.
    /// </summary>
    [JsonPropertyName("applicationInstanceId")]
    public string ApplicationInstanceId { get; init; } = "";

    /// <summary>The application instance's completed-pass count after this pass.</summary>
    [JsonPropertyName("frameId")]
    public long FrameId { get; init; }

    /// <summary>
    /// The highest input id the application had processed when this pass was published; absent
    /// when the session does not track input. A frame covers exactly the inputs up to this id.
    /// </summary>
    [JsonPropertyName("processedInput")]
    public long? ProcessedInput { get; init; }

    /// <summary>Frame width in columns.</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; init; }

    /// <summary>Frame height in rows.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>
    /// Whether this pass wrote cell or graphics changes to the terminal. Cursor-only updates and
    /// synchronized-update markers are not counted.
    /// </summary>
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
