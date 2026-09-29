using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>One observation stream of a case, as recorded so far.</summary>
public sealed record DiagnosticCaseStreamStatus
{
    /// <summary>Stream name: <c>model</c>, <c>input</c>, <c>frames</c> or <c>delivery</c>.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    /// <summary>Events the stream offered to the case.</summary>
    [JsonPropertyName("offered")]
    public long Offered { get; init; }

    /// <summary>Events written to the artifact.</summary>
    [JsonPropertyName("written")]
    public long Written { get; init; }

    /// <summary>Events dropped because the case could not keep up.</summary>
    [JsonPropertyName("dropped")]
    public long Dropped { get; init; }
}
