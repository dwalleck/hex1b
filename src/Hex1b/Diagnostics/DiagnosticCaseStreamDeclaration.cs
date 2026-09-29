using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>What a case records for one observation stream.</summary>
public sealed record DiagnosticCaseStreamDeclaration
{
    /// <summary>Stream name: <c>model</c>, <c>input</c>, <c>frames</c> or <c>delivery</c>.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    /// <summary>Whether the stream's events are recorded.</summary>
    [JsonPropertyName("events")]
    public DiagnosticCoverageState Events { get; init; }

    /// <summary>Whether the events carry their content payloads (bytes, key payloads, editor text).</summary>
    [JsonPropertyName("payloads")]
    public DiagnosticCoverageState Payloads { get; init; }

    /// <summary>Why events or payloads are excluded or unavailable.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
