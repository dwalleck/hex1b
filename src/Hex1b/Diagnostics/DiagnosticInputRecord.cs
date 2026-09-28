using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One tracked input. Metadata is reported by default; the payload only with raw-input
/// authorization.
/// </summary>
public sealed record DiagnosticInputRecord
{
    /// <summary>The input id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary><c>text</c>, <c>key</c>, <c>mouse</c>, <c>paste</c>, <c>resize</c>, or <c>other</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary><c>diagnostic-send</c> or <c>native</c>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary>When the input was accepted (UTC).</summary>
    [JsonPropertyName("acceptedAt")]
    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>
    /// When the input was accepted, as a <c>process-monotonic</c> timestamp in the clock domain of
    /// capture acquisitions (<see cref="DiagnosticAcquisition"/>), so it orders against them exactly.
    /// </summary>
    [JsonPropertyName("acceptedTimestamp")]
    public long AcceptedTimestamp { get; init; }

    /// <summary>When the application loop processed it (UTC), once processed.</summary>
    [JsonPropertyName("processedAt")]
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>When the application loop processed it, as a <c>process-monotonic</c> timestamp, once processed.</summary>
    [JsonPropertyName("processedTimestamp")]
    public long? ProcessedTimestamp { get; init; }

    /// <summary>The payload (key and modifiers, text, mouse button and position); only with raw-input authorization.</summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; init; }
}
