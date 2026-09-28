using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Request for an immediate terminal-model capture. The same request is accepted by
/// in-process targets, the diagnostics socket, and every CLI and MCP client.
/// </summary>
public sealed class DiagnosticCaptureRequest
{
    /// <summary>
    /// Rendering format of the returned content.
    /// </summary>
    [JsonPropertyName("format")]
    public DiagnosticCaptureFormat Format { get; init; } = DiagnosticCaptureFormat.Text;

    /// <summary>
    /// Number of rows of retained terminal-model history to include above the active screen.
    /// Zero requests none. This is model history, not the native host's scrollback.
    /// </summary>
    [JsonPropertyName("historyRows")]
    public int HistoryRows { get; init; }

    /// <summary>
    /// Font family override for <see cref="DiagnosticCaptureFormat.Svg"/> and
    /// <see cref="DiagnosticCaptureFormat.Html"/> renderings.
    /// </summary>
    [JsonPropertyName("fontFamily")]
    public string? FontFamily { get; init; }

    /// <summary>
    /// Explicit content opt-ins beyond the default rendered-screen policy.
    /// </summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization>? Authorizations { get; init; }

    /// <summary>An optional milestone to wait for before capturing; absent means immediate.</summary>
    [JsonPropertyName("milestone")]
    public DiagnosticMilestoneRequest? Milestone { get; init; }
}
