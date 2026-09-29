using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Where a case is in its lifecycle.</summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaseState>))]
public enum DiagnosticCaseState
{
    /// <summary>Recording events.</summary>
    Recording,

    /// <summary>No longer accepting events; writing what was queued.</summary>
    Stopping,

    /// <summary>Finished; its completion record is written.</summary>
    Stopped,
}
