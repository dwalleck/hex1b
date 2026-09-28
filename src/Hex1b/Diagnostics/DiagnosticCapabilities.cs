using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Discoverable description of what a diagnostic target supports.
/// </summary>
public sealed class DiagnosticCapabilities
{
    /// <summary>Version of the diagnostic contract.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = TerminalDiagnostics.ContractVersion;

    /// <summary>Outcome of the capability query itself.</summary>
    [JsonPropertyName("outcome")]
    public DiagnosticOutcome Outcome { get; init; }

    /// <summary>Why capabilities could not be described; absent on success.</summary>
    [JsonPropertyName("problem")]
    public DiagnosticProblem? Problem { get; init; }

    /// <summary>Supported operations.</summary>
    [JsonPropertyName("operations")]
    public IReadOnlyList<DiagnosticOperationCapability> Operations { get; init; } = [];

    /// <summary>Availability of each evidence layer on this target.</summary>
    [JsonPropertyName("layers")]
    public IReadOnlyList<DiagnosticLayerCapability> Layers { get; init; } = [];
}
