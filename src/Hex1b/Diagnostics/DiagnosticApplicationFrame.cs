using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// An immutable projection of one completed application render pass, published on the
/// application loop at the end of that pass.
/// </summary>
public sealed class DiagnosticApplicationFrame
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
}
