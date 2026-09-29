using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Request sent to the diagnostics socket.
/// </summary>
internal sealed class DiagnosticsRequest
{
    /// <summary>
    /// The method to invoke, for example "info", "capture", "capabilities", or "input".
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>
    /// For "input" method, the characters to send to the terminal.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>
    /// For "key" method, the key name (e.g., "Enter", "Tab", "N", "F1").
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>
    /// For "key" method, the modifiers (e.g., ["Alt"], ["Ctrl", "Shift"]).
    /// </summary>
    [JsonPropertyName("modifiers")]
    public string[]? Modifiers { get; set; }

    /// <summary>
    /// For "click" method, the X position (column, 0-based).
    /// </summary>
    [JsonPropertyName("x")]
    public int? X { get; set; }

    /// <summary>
    /// For "click" method, the Y position (row, 0-based).
    /// </summary>
    [JsonPropertyName("y")]
    public int? Y { get; set; }

    /// <summary>
    /// For "click" method, the mouse button ("left", "right", "middle").
    /// </summary>
    [JsonPropertyName("button")]
    public string? Button { get; set; }

    /// <summary>
    /// For "drag" method, the destination X position (column, 0-based).
    /// </summary>
    [JsonPropertyName("x2")]
    public int? X2 { get; set; }

    /// <summary>
    /// For "drag" method, the destination Y position (row, 0-based).
    /// </summary>
    [JsonPropertyName("y2")]
    public int? Y2 { get; set; }

    /// <summary>
    /// For "record-start" method, the output file path (.cast).
    /// </summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    /// <summary>
    /// For "record-start" method, the recording title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// For "record-start" method, max idle time in seconds between frames.
    /// </summary>
    [JsonPropertyName("idleLimit")]
    public double? IdleLimit { get; set; }

    /// <summary>
    /// For "capture" method, the shared diagnostic capture request.
    /// </summary>
    [JsonPropertyName("capture")]
    public DiagnosticCaptureRequest? Capture { get; set; }

    /// <summary>
    /// For "application-frame" method, the application-frame request; absent means no authorizations.
    /// </summary>
    [JsonPropertyName("applicationFrame")]
    public DiagnosticApplicationFrameRequest? ApplicationFrame { get; set; }

    /// <summary>
    /// For "delivery" method, the native delivery request; absent means every retained record, no bytes.
    /// </summary>
    [JsonPropertyName("delivery")]
    public DiagnosticDeliveryRequest? Delivery { get; set; }

    /// <summary>
    /// For "case-start" method, the case's bounds, authorizations and storage; absent means the defaults.
    /// </summary>
    [JsonPropertyName("caseStart")]
    public DiagnosticCaseStartRequest? CaseStart { get; set; }
}
