using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected retained history.</summary>
public sealed record DiagnosticModelHistory
{
    /// <summary>Most rows retained.</summary>
    [JsonPropertyName("capacity")]
    public int Capacity { get; init; }

    /// <summary>The identity the next retained row receives.</summary>
    [JsonPropertyName("nextRowId")]
    public long NextRowId { get; init; }

    /// <summary>Retained rows, oldest first.</summary>
    [JsonPropertyName("rows")]
    public IReadOnlyList<DiagnosticModelRow> Rows { get; init; } = [];
}
