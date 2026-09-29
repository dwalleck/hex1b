using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A projected saved cursor.</summary>
public sealed record DiagnosticModelSavedCursor
{
    /// <summary>Column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Whether a wrap was pending.</summary>
    [JsonPropertyName("pendingWrap")]
    public bool PendingWrap { get; init; }

    /// <summary>Whether protection was on; absent where the model does not save it.</summary>
    [JsonPropertyName("protected")]
    public bool? Protected { get; init; }
}
