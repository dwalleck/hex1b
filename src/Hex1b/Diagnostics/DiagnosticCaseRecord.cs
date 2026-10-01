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
    /// Why. For an interval end: <c>graphics</c>, <c>application-without-ingress</c>, <c>reentrant-model-event</c>,
    /// <c>reentrant-application</c> or <c>stream-failed</c>. For a missing range: <c>overload</c>,
    /// <c>overload-unknown-extent</c>, <c>evicted</c>, <c>not-pulled</c>, <c>drain-timeout</c>, <c>size-limit</c>,
    /// <c>size-limit-unknown-extent</c> or <c>collector-failed</c> (and, from the reader, <c>unknown</c> or
    /// <c>unaccounted</c>). For a loss envelope (kind <c>loss-envelope</c>, the first and last ordinal a stream lost past
    /// the ledger's cap, the ones between not enumerated): <c>overload-envelope</c>. For a failed stream:
    /// <c>stream-failed: </c> and the error.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}
