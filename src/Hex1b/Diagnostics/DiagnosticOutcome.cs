using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Outcome of a diagnostic operation. Only <see cref="Captured"/> carries observed content.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticOutcome>))]
public enum DiagnosticOutcome
{
    /// <summary>The target produced the requested observation.</summary>
    Captured,

    /// <summary>The target or a required layer cannot currently provide the observation.</summary>
    Unavailable,

    /// <summary>The request was malformed or asked for an unsupported value.</summary>
    InvalidRequest,

    /// <summary>The operation started but failed, or its transport failed.</summary>
    Failed,

    /// <summary>A requested milestone was not met within its timeout; nothing was captured.</summary>
    TimedOut,
}
