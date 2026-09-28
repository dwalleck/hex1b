using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Result of a terminal-model capture. Every CLI, MCP, and socket client returns this shape.
/// </summary>
public sealed record DiagnosticCaptureResult
{
    /// <summary>Version of the diagnostic contract that produced this result.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome; only <see cref="DiagnosticOutcome.Captured"/> carries content.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why no observation was produced; absent when captured.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>Format of <see cref="Content"/>.</summary>
    [JsonPropertyName("format")]
    public DiagnosticCaptureFormat? Format { get; init; }

    /// <summary>
    /// Rendered content in <see cref="Format"/>. A client that writes the content to a file
    /// instead of returning it reports the file path separately and leaves this absent.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>Terminal-model dimensions and cursor at the observation.</summary>
    [JsonPropertyName("geometry")]
    public DiagnosticGeometry? Geometry { get; init; }

    /// <summary>Retained model-history coverage of the returned content.</summary>
    [JsonPropertyName("history")]
    public DiagnosticHistoryCoverage? History { get; init; }

    /// <summary>Where and when the observation was acquired.</summary>
    [JsonPropertyName("identity")]
    public DiagnosticObservationIdentity? Identity { get; init; }

    /// <summary>Included, excluded, and unavailable content classes.</summary>
    [JsonPropertyName("contentCoverage")]
    public IReadOnlyList<DiagnosticContentCoverage> ContentCoverage { get; init; } = [];

    /// <summary>Hidden metadata, present only when authorized.</summary>
    [JsonPropertyName("nonScreenMetadata")]
    public DiagnosticNonScreenMetadata? NonScreenMetadata { get; init; }

    /// <summary>Fields that are absent and why.</summary>
    [JsonPropertyName("unavailableFields")]
    public IReadOnlyList<DiagnosticUnavailableField> UnavailableFields { get; init; } = [];

    /// <summary>Limits on what this observation can support.</summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
