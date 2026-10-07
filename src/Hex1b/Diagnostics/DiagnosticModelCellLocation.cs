using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A cell of a projected buffer: which buffer, its row in that buffer, and its column.</summary>
public sealed record DiagnosticModelCellLocation
{
    /// <summary>The state field holding the row: <c>history</c>, <c>savedMainScreen</c> or <c>screen</c>.</summary>
    [JsonPropertyName("buffer")]
    public string Buffer { get; init; } = "";

    /// <summary>Row index within that buffer (history rows oldest first).</summary>
    [JsonPropertyName("row")]
    public int Row { get; init; }

    /// <summary>Column.</summary>
    [JsonPropertyName("column")]
    public int Column { get; init; }
}
