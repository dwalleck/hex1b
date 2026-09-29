using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Base response from the diagnostics socket.
/// </summary>
internal sealed class DiagnosticsResponse
{
    /// <summary>
    /// Whether the request was successful.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Error message if not successful.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// For "info" method: the application name.
    /// </summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>
    /// For "info" method: the process ID.
    /// </summary>
    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    /// <summary>
    /// For "info" method: when the process started.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// Terminal width in columns.
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>
    /// Terminal height in rows.
    /// </summary>
    [JsonPropertyName("height")]
    public int? Height { get; set; }

    /// <summary>
    /// Method-specific payload, for example the attach screen or an input acknowledgement.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }
    
    /// <summary>
    /// For "attach" method: whether this client is the resize leader.
    /// </summary>
    [JsonPropertyName("leader")]
    public bool? Leader { get; set; }

    /// <summary>
    /// For "info" and "record-status" methods: whether the terminal is currently recording.
    /// </summary>
    [JsonPropertyName("recording")]
    public bool? Recording { get; set; }

    /// <summary>
    /// For "record-status" and "record-stop" methods: the recording file path.
    /// </summary>
    [JsonPropertyName("recordingPath")]
    public string? RecordingPath { get; set; }

    /// <summary>
    /// For "capture" method: the shared diagnostic capture result, including failures.
    /// </summary>
    [JsonPropertyName("capture")]
    public DiagnosticCaptureResult? Capture { get; set; }

    /// <summary>
    /// For "capabilities" method: what the target supports.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public DiagnosticCapabilities? Capabilities { get; set; }

    /// <summary>
    /// For "application-frame" method: the latest published application frame, including failures.
    /// </summary>
    [JsonPropertyName("applicationFrame")]
    public DiagnosticApplicationFrameResult? ApplicationFrame { get; set; }

    /// <summary>
    /// For "delivery" method: the native delivery record, including failures.
    /// </summary>
    [JsonPropertyName("delivery")]
    public DiagnosticDeliveryResult? Delivery { get; set; }

    /// <summary>
    /// For "input", "key", "click" and "drag": the input ids the send was assigned, when the
    /// target tracks input.
    /// </summary>
    [JsonPropertyName("acceptedInput")]
    public DiagnosticAcceptedInput? AcceptedInput { get; set; }
}
