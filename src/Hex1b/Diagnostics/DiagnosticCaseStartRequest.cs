using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Request to start recording a bounded diagnostic case on a terminal.
/// </summary>
public sealed record DiagnosticCaseStartRequest
{
    /// <summary>Most artifact bytes to write, 1 MiB to 1 GiB; absent means 64 MiB.</summary>
    [JsonPropertyName("maxBytes")]
    public long? MaxBytes { get; init; }

    /// <summary>Longest recording, 1 second to 24 hours; absent means 10 minutes.</summary>
    [JsonPropertyName("maxSeconds")]
    public int? MaxSeconds { get; init; }

    /// <summary>
    /// Opt-ins. <see cref="DiagnosticAuthorization.ReapplicationData"/> adds original model input and the
    /// checkpoint; <see cref="DiagnosticAuthorization.RawInput"/>, <see cref="DiagnosticAuthorization.EditorText"/>
    /// and <see cref="DiagnosticAuthorization.NativeOutput"/> add their own payloads. None implies another.
    /// </summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization>? Authorizations { get; init; }

    /// <summary>
    /// Directory that holds the case's own directory; absent means <c>~/.hex1b/cases</c>. It must be
    /// accessible only to its owner.
    /// </summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }
}
