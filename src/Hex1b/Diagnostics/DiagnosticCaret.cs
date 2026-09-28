using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A position in editor text: a character offset and its 0-based line and column.
/// </summary>
public sealed class DiagnosticCaret
{
    /// <summary>Character offset from the start of the text.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>0-based line.</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>0-based column within the line, in characters.</summary>
    [JsonPropertyName("column")]
    public int Column { get; init; }
}
