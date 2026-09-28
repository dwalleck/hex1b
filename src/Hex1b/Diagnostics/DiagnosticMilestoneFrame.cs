using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A published frame identified for a milestone: the first frame whose processed-input
/// watermark covered the input.
/// </summary>
public sealed class DiagnosticMilestoneFrame
{
    /// <summary>The application instance that published the frame.</summary>
    [JsonPropertyName("applicationInstanceId")]
    public string ApplicationInstanceId { get; init; } = "";

    /// <summary>The frame's id within that instance.</summary>
    [JsonPropertyName("frameId")]
    public long FrameId { get; init; }

    /// <summary>The frame's processed-input watermark.</summary>
    [JsonPropertyName("processedInput")]
    public long ProcessedInput { get; init; }

    /// <summary>Whether the frame's pass wrote cell or graphics output.</summary>
    [JsonPropertyName("wroteOutput")]
    public bool WroteOutput { get; init; }
}
