using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Identifies where and when an observation was acquired so independent observations can be
/// correlated without being presented as one atomic moment. Absent fields are listed in
/// <see cref="DiagnosticCaptureResult.UnavailableFields"/>.
/// </summary>
public sealed class DiagnosticObservationIdentity
{
    /// <summary>Operating-system process ID of the target.</summary>
    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    /// <summary>When the target process started, when the platform reports it.</summary>
    [JsonPropertyName("processStartedAt")]
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>Identity of the terminal-model session within the process.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    /// <summary>Evidence layer the observation came from.</summary>
    [JsonPropertyName("sourceLayer")]
    public DiagnosticLayer SourceLayer { get; init; }

    /// <summary>Application name reported by the target.</summary>
    [JsonPropertyName("applicationName")]
    public string ApplicationName { get; init; } = "";

    /// <summary>Informational version of the target's entry assembly, when it has one.</summary>
    [JsonPropertyName("applicationVersion")]
    public string? ApplicationVersion { get; init; }

    /// <summary>Informational version of the loaded Hex1b assembly, including its source revision.</summary>
    [JsonPropertyName("hex1bVersion")]
    public string Hex1bVersion { get; init; } = "";

    /// <summary>Terminal configuration relevant to the observation.</summary>
    [JsonPropertyName("configuration")]
    public DiagnosticModelConfiguration Configuration { get; init; } = new();

    /// <summary>Acquisition interval and clock domain.</summary>
    [JsonPropertyName("acquisition")]
    public DiagnosticAcquisition Acquisition { get; init; } = new();

    /// <summary>Terminal-model event sequence at the observation, when the model assigns one.</summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>Published application frame identity, when one is associated.</summary>
    [JsonPropertyName("applicationFrame")]
    public long? ApplicationFrame { get; init; }
}
