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

    /// <summary>Compact text such as <c>reconcile=0.15ms render=0.30ms last=12ms ago</c>, omitting zero values.</summary>
    public override string ToString()
    {
        var parts = new List<string>(3);
        if (ReconcileMs > 0) parts.Add($"reconcile={ReconcileMs:F2}ms");
        if (RenderMs > 0) parts.Add($"render={RenderMs:F2}ms");
        if (LastRenderedMsAgo >= 0) parts.Add($"last={LastRenderedMsAgo:F0}ms ago");
        return string.Join(" ", parts);
    }
}
