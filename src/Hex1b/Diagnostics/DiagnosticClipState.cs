using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// How much of a node its enclosing clip regions leave visible.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticClipState>))]
public enum DiagnosticClipState
{
    /// <summary>The whole node is inside every enclosing clip region.</summary>
    Visible,

    /// <summary>Part of the node is outside an enclosing clip region.</summary>
    PartiallyClipped,

    /// <summary>No part of the node is inside the enclosing clip regions.</summary>
    FullyClipped,
}
