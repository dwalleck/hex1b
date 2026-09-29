using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Request to inspect a case artifact on disk.</summary>
public sealed record DiagnosticCaseInspectRequest
{
    /// <summary>The case's artifact directory.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>Return events after this case sequence.</summary>
    [JsonPropertyName("since")]
    public long? Since { get; init; }

    /// <summary>Most events to return, 1 to 4,096; absent returns no events, only the summary.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}
