using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// When a write happened relative to the terminal model applying the same bytes.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticDeliveryPhase>))]
public enum DiagnosticDeliveryPhase
{
    /// <summary>The bytes were written before the model applied them (raw passthrough, a batch the presentation gates on its own geometry).</summary>
    BeforeModel,

    /// <summary>The bytes were written after the model applied them (filtered output, a batch the terminal gates against its model).</summary>
    AfterModel,
}
