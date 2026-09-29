using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Which terminal path made a presentation write.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticDeliverySource>))]
public enum DiagnosticDeliverySource
{
    /// <summary>Workload output forwarded by the terminal's output pump (raw or filtered).</summary>
    WorkloadOutput,

    /// <summary>A geometry-gated batch, written only if the host still reports the geometry it was composed for.</summary>
    GatedDelivery,

    /// <summary>The terminal's own control sequences, such as mode restores and exit sequences.</summary>
    TerminalControl,
}
