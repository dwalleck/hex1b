using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Whether a case's initial checkpoint represents the model's complete state.</summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaseCheckpointStatus>))]
public enum DiagnosticCaseCheckpointStatus
{
    /// <summary>Complete for its declared profile and state surfaces.</summary>
    Complete,

    /// <summary>The model's state is not one the profile can represent; the reason says why.</summary>
    Unsupported,

    /// <summary>Not recorded, because re-application data was not authorized.</summary>
    Excluded,
}
