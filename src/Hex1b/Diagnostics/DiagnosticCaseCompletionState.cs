using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Whether a case artifact was finished.</summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaseCompletionState>))]
public enum DiagnosticCaseCompletionState
{
    /// <summary>The completion record is present and every line verified.</summary>
    Complete,

    /// <summary>No completion record: the recording ended abruptly. Only verified events are claimed.</summary>
    Interrupted,

    /// <summary>A torn or corrupt line: events from it on are not used.</summary>
    Truncated,
}
