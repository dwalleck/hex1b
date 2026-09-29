using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected tab stops.</summary>
public sealed record DiagnosticModelTabStops
{
    /// <summary>Columns the tab-stop table covers.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Columns with a tab stop, ascending.</summary>
    [JsonPropertyName("columns")]
    public IReadOnlyList<int> Columns { get; init; } = [];
}
