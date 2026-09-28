using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Interaction state of the focused editor in a published frame. Full text is present only
/// when the capture carried the editor-text authorization.
/// </summary>
public sealed record DiagnosticEditorState
{
    /// <summary>Editor kind: <c>text-box</c> or <c>editor</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>The editor node's bounds.</summary>
    [JsonPropertyName("bounds")]
    public DiagnosticRect Bounds { get; init; } = new();

    /// <summary>Total text length in characters.</summary>
    [JsonPropertyName("length")]
    public int Length { get; init; }

    /// <summary>Number of lines.</summary>
    [JsonPropertyName("lineCount")]
    public int LineCount { get; init; }

    /// <summary>Every caret, in the editor's cursor order; a multi-cursor editor reports all of them.</summary>
    [JsonPropertyName("carets")]
    public IReadOnlyList<DiagnosticCaret> Carets { get; init; } = [];

    /// <summary>Every non-empty selection, in caret order.</summary>
    [JsonPropertyName("selections")]
    public IReadOnlyList<DiagnosticSelection> Selections { get; init; } = [];

    /// <summary>Full editor text; present only with editor-text authorization.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
