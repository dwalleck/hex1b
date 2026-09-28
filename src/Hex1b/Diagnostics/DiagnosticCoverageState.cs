using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Whether a content class is present in an observation, and if not, why.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCoverageState>))]
public enum DiagnosticCoverageState
{
    /// <summary>The content is present in the returned observation.</summary>
    Included,

    /// <summary>The content exists or may exist but was withheld by request or permission.</summary>
    Excluded,

    /// <summary>The operation, format, or target cannot provide the content.</summary>
    Unavailable,
}
