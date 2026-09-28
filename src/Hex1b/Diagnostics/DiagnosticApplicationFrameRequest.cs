using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Request for the latest published application frame.
/// </summary>
public sealed class DiagnosticApplicationFrameRequest
{
    /// <summary>
    /// Explicit content opt-ins. <see cref="DiagnosticAuthorization.EditorText"/> adds the focused
    /// editor's full text; no other authorization changes the frame.
    /// </summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization>? Authorizations { get; init; }
}
