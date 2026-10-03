using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One projected cell. Names are short because a projection can hold millions of cells.
/// </summary>
public readonly record struct DiagnosticModelCell
{
    /// <summary>The cell's text: a grapheme, a space, or empty for a wide glyph's continuation.</summary>
    [JsonPropertyName("t")]
    public string Text { get; init; }

    /// <summary>The index of the cell's style in <see cref="DiagnosticModelState.Styles"/>.</summary>
    [JsonPropertyName("s")]
    public int Style { get; init; }

    /// <summary>Whether the cell is the blank left where a wide glyph wrapped; omitted when false.</summary>
    [JsonPropertyName("w")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WideWrapPadding { get; init; }

    /// <summary>
    /// Whether this empty cell continues the glyph in the cell before it in reading order: the cell to its left, or, at a
    /// row's first column, the previous row's last cell across a soft wrap (they share a write sequence, as when written
    /// together); omitted when false.
    /// </summary>
    [JsonPropertyName("c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Continues { get; init; }

    /// <summary>
    /// Projection-local equality class for a repeated nonzero buffer-cell write sequence. Every member shares a
    /// positive label; singleton and never-written cells omit it. Labels are not write-order values. The domain spans
    /// retained history, main (or saved main), and alternate screen; the last-printed cell is not a member.
    /// </summary>
    [JsonPropertyName("q")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WriteClass { get; init; }
}
