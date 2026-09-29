using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// What a presentation did with one write. None of these is a physical presentation acknowledgment.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticDeliveryOutcome>))]
public enum DiagnosticDeliveryOutcome
{
    /// <summary>The presentation's write returned normally: the host operating system or transport took the bytes.</summary>
    Accepted,

    /// <summary>The presentation declined to write (for example a batch composed for a superseded geometry).</summary>
    Refused,

    /// <summary>The write raised an error; the error propagates to the terminal as it does without diagnostics.</summary>
    Failed,
}
