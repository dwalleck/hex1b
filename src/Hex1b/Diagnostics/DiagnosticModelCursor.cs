using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The projected cursor.</summary>
public sealed record DiagnosticModelCursor
{
    /// <summary>Column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Whether the next printed character wraps first.</summary>
    [JsonPropertyName("pendingWrap")]
    public bool PendingWrap { get; init; }

    /// <summary>Whether the cursor is shown.</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    /// <summary>DECSCUSR shape.</summary>
    [JsonPropertyName("shape")]
    public int Shape { get; init; }

    /// <summary>Whether written cells are protected.</summary>
    [JsonPropertyName("protected")]
    public bool Protected { get; init; }
}
