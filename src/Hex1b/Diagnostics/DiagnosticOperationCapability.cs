using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A supported diagnostic operation and its exact limits.
/// </summary>
public sealed class DiagnosticOperationCapability
{
    /// <summary>Operation name, for example <c>capture</c>.</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    /// <summary>Evidence layer the operation observes.</summary>
    [JsonPropertyName("layer")]
    public DiagnosticLayer Layer { get; init; }

    /// <summary>Supported rendering formats.</summary>
    [JsonPropertyName("formats")]
    public IReadOnlyList<DiagnosticCaptureFormat> Formats { get; init; } = [];

    /// <summary>
    /// Supported observation timing. <c>immediate</c> observes current state without waiting;
    /// no named processing milestones are supported yet.
    /// </summary>
    [JsonPropertyName("timing")]
    public IReadOnlyList<string> Timing { get; init; } = [];

    /// <summary>Whether retained model history can be requested.</summary>
    [JsonPropertyName("modelHistory")]
    public bool ModelHistory { get; init; }

    /// <summary>Authorizations that change what this operation returns.</summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization> Authorizations { get; init; } = [];

    /// <summary>Exact limitations of the operation.</summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = [];
}
