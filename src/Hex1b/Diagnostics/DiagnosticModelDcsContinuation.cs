using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The retained non-Sixel DCS continuation at an applied model boundary.</summary>
public sealed record DiagnosticModelDcsContinuation
{
    /// <summary>The parser substate: <c>introducer</c>, <c>payload</c>, <c>malformed-introducer</c>, or <c>escape</c>.</summary>
    [JsonPropertyName("state")]
    [JsonRequired]
    public string State { get; init; } = "";

    /// <summary>The substate before a held ESC; required when <see cref="State"/> is <c>escape</c>, otherwise null.</summary>
    [JsonPropertyName("stateBeforeEscape")]
    public string? StateBeforeEscape { get; init; }

    /// <summary>Exact retained DCS content bytes, base64, excluding outer framing and a still-held ESC.</summary>
    [JsonPropertyName("retainedBytes")]
    [JsonRequired]
    public string RetainedBytes { get; init; } = "";

    /// <summary>Content bytes already accounted for, excluding a still-held ESC.</summary>
    [JsonPropertyName("byteCount")]
    [JsonRequired]
    public long ByteCount { get; init; }

    /// <summary>Whether the parser has discarded required content after its retention limit; such state is not complete.</summary>
    [JsonPropertyName("retentionLimitExceeded")]
    [JsonRequired]
    public bool RetentionLimitExceeded { get; init; }
}
