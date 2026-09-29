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
}
