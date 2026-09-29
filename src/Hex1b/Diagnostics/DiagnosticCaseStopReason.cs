using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Why a case stopped. Overload never stops a case; it is reported as missing ranges.</summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaseStopReason>))]
public enum DiagnosticCaseStopReason
{
    /// <summary>A client asked the case to stop.</summary>
    Requested,

    /// <summary>The artifact reached its size bound.</summary>
    SizeLimit,

    /// <summary>The case reached its time bound.</summary>
    TimeLimit,

    /// <summary>The artifact writer or its storage failed.</summary>
    CollectorFailed,

    /// <summary>The terminal was disposed.</summary>
    TargetDisposed,
}
