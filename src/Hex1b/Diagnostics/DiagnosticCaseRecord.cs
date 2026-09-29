using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A case's own record: where re-applicable coverage ends, or a range the case did not keep.</summary>
public sealed record DiagnosticCaseRecord
{
    /// <summary>The affected stream.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    /// <summary>First missing ordinal of the stream (a missing range).</summary>
    [JsonPropertyName("fromOrdinal")]
    public long? FromOrdinal { get; init; }

    /// <summary>Last missing ordinal of the stream, or absent when the extent is unknown.</summary>
    [JsonPropertyName("toOrdinal")]
    public long? ToOrdinal { get; init; }

    /// <summary>
    /// Why: for an interval end, <c>graphics</c>, <c>application-without-ingress</c> or
    /// <c>stream-failed</c>; for a missing range, <c>overload</c>, <c>evicted</c> or <c>drain-timeout</c>.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}
