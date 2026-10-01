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

    /// <summary>
    /// The Hex1b build's id (its assembly's module version id), so two builds with one version string are told
    /// apart; absent in artifacts recorded before it was written (ticket 14).
    /// </summary>
    [JsonPropertyName("hex1bBuild")]
    public string? Hex1bBuild { get; init; }

    /// <summary>Terminal configuration relevant to the observation.</summary>
    [JsonPropertyName("configuration")]
    public DiagnosticModelConfiguration Configuration { get; init; } = new();

    /// <summary>Acquisition interval and clock domain.</summary>
    [JsonPropertyName("acquisition")]
    public DiagnosticAcquisition Acquisition { get; init; } = new();

    /// <summary>
    /// Count of model events (output application batches, geometry changes, and synchronized-update
    /// timeout releases) applied in this terminal session at the observation. Observations with equal values read the same
    /// model state; values are comparable only within one <see cref="SessionId"/>. An event can
    /// leave the visible state unchanged. Absent only from targets that do not assign one, in
    /// which case <see cref="DiagnosticCaptureResult.UnavailableFields"/> explains it.
    /// </summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>Published application frame identity, when one is associated.</summary>
    [JsonPropertyName("applicationFrame")]
    public long? ApplicationFrame { get; init; }

    /// <summary>
    /// The application instance that published <see cref="ApplicationFrame"/>. Frame numbers count
    /// passes per instance, so the two together identify a frame within a session.
    /// </summary>
    [JsonPropertyName("applicationInstanceId")]
    public string? ApplicationInstanceId { get; init; }
}
