using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>One row of a projected screen or history.</summary>
public sealed record DiagnosticModelRow
{
    /// <summary>The row's cells, left to right.</summary>
    [JsonPropertyName("cells")]
    public IReadOnlyList<DiagnosticModelCell> Cells { get; init; } = [];

    /// <summary>A history row's identity in the history buffer; absent for screen rows.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>The width a history row was written at; absent for screen rows.</summary>
    [JsonPropertyName("originalWidth")]
    public int? OriginalWidth { get; init; }

    /// <summary>
    /// The row's never-written cells (write sequence 0: not written since the row was blanked, or erased), as runs of
    /// <c>start, count</c> pairs in column order; absent when every cell was written. Which cells share a write sequence
    /// decides what later output treats as one glyph, so the restore keeps these cells at 0 as the original does.
    /// </summary>
    [JsonPropertyName("unwritten")]
    public IReadOnlyList<int>? Unwritten { get; init; }
}
