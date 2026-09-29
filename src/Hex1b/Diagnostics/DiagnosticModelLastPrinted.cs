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
}
