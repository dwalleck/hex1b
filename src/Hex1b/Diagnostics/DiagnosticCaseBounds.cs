using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The effective size and time bounds of a case.</summary>
public sealed record DiagnosticCaseBounds
{
    /// <summary>Most artifact bytes the case writes.</summary>
    [JsonPropertyName("maxBytes")]
    public long MaxBytes { get; init; }

    /// <summary>Longest the case records, in seconds.</summary>
    [JsonPropertyName("maxSeconds")]
    public int MaxSeconds { get; init; }
}
