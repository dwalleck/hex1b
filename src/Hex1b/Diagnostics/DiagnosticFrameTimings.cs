using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// How long the phases of a published frame's pass took.
/// </summary>
public sealed class DiagnosticFrameTimings
{
    /// <summary>Widget build time in milliseconds.</summary>
    [JsonPropertyName("buildMs")]
    public double BuildMs { get; init; }

    /// <summary>Reconcile time in milliseconds.</summary>
    [JsonPropertyName("reconcileMs")]
    public double ReconcileMs { get; init; }

    /// <summary>Render time in milliseconds.</summary>
    [JsonPropertyName("renderMs")]
    public double RenderMs { get; init; }
}
