using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A named processing stage of one input that a capture can wait for.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticMilestone>))]
public enum DiagnosticMilestone
{
    /// <summary>The input was queued for the application (or written to a PTY).</summary>
    InputAccepted,

    /// <summary>The application loop consumed the input.</summary>
    InputProcessed,

    /// <summary>A frame whose processed-input watermark covers the input was published.</summary>
    FramePublished,

    /// <summary>Everything the application enqueued up to that frame was applied to the terminal model.</summary>
    ModelApplied,
}
