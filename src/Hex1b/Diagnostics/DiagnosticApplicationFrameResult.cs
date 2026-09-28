using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Result of an application-frame capture. Every CLI, MCP, and socket client returns this shape.
/// </summary>
public sealed record DiagnosticApplicationFrameResult
{
    /// <summary>Version of the diagnostic contract that produced this result.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome; only <see cref="DiagnosticOutcome.Captured"/> carries a frame.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why no frame was returned; absent when captured.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>The published frame.</summary>
    [JsonPropertyName("frame")]
    public DiagnosticApplicationFrame? Frame { get; init; }

    /// <summary>
    /// Where and when the frame was projected: source layer <c>application-frame</c>, the frame
    /// identity, and the projection interval on the application loop.
    /// </summary>
    [JsonPropertyName("identity")]
    public DiagnosticObservationIdentity? Identity { get; init; }

    /// <summary>Included, excluded, and unavailable content classes.</summary>
    [JsonPropertyName("contentCoverage")]
    public IReadOnlyList<DiagnosticContentCoverage> ContentCoverage { get; init; } = [];

    /// <summary>Fields that are absent and why.</summary>
    [JsonPropertyName("unavailableFields")]
    public IReadOnlyList<DiagnosticUnavailableField> UnavailableFields { get; init; } = [];

    /// <summary>Limits on what this observation can support.</summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
