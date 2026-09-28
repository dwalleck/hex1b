using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A node's most recent reconcile and render cost as of the frame's projection.
/// </summary>
public sealed class DiagnosticNodeTiming
{
    /// <summary>Last reconcile time in milliseconds.</summary>
    [JsonPropertyName("reconcileMs")]
    public double ReconcileMs { get; init; }

    /// <summary>Last render time in milliseconds.</summary>
    [JsonPropertyName("renderMs")]
    public double RenderMs { get; init; }

    /// <summary>Milliseconds between the node's last render and the frame's projection, or -1 when never rendered.</summary>
    [JsonPropertyName("lastRenderedMsAgo")]
    public double LastRenderedMsAgo { get; init; }
}
