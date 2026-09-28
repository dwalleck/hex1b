using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The evidence layer an observation was acquired from. Layers are acquired independently
/// and are never implied to describe one atomic moment.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticLayer>))]
public enum DiagnosticLayer
{
    /// <summary>Hex1b's terminal model: cells, cursor, modes, and retained model history.</summary>
    TerminalModel,

    /// <summary>A frame published by a Hex1b application.</summary>
    ApplicationFrame,

    /// <summary>Delivery of output to a native terminal host.</summary>
    NativeDelivery,

    /// <summary>What a native terminal host physically displayed.</summary>
    NativePresentation,
}
