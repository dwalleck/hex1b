using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The projected last printed cell.</summary>
public sealed record DiagnosticModelLastPrinted
{
    /// <summary>Column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Width in cells.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>The cell as printed.</summary>
    [JsonPropertyName("cell")]
    public DiagnosticModelCell Cell { get; init; }

    /// <summary>
    /// The first buffer cell, in reading order (history, saved main screen, screen), that is the same write as this copy;
    /// absent when none is. A later modifier (a combining mark, a ZWJ continuation, a variation selector) attaches to
    /// the last printed glyph only while the active screen's cell at (<see cref="X"/>, <see cref="Y"/>) is that write,
    /// which can stop and start again as rows move. The restore gives the copy that cell's restored write identity
    /// (shared with its whole write class), and a fresh one when absent.
    /// </summary>
    [JsonPropertyName("sameWrite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticModelCellLocation? SameWrite { get; init; }
}
