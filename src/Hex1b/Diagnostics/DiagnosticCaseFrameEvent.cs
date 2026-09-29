using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>An application frame the application published.</summary>
public sealed record DiagnosticCaseFrameEvent
{
    /// <summary>The frame id, unique within its application instance.</summary>
    [JsonPropertyName("frameId")]
    public long FrameId { get; init; }

    /// <summary>The application instance that published it.</summary>
    [JsonPropertyName("applicationInstanceId")]
    public string ApplicationInstanceId { get; init; } = "";

    /// <summary>The processed-input watermark the frame covers.</summary>
    [JsonPropertyName("processedInput")]
    public long ProcessedInput { get; init; }

    /// <summary>Whether the pass wrote output.</summary>
    [JsonPropertyName("wroteOutput")]
    public bool WroteOutput { get; init; }

    /// <summary>The last output item the application had enqueued, when known.</summary>
    [JsonPropertyName("outputMark")]
    public long? OutputMark { get; init; }

    /// <summary>The frame projection, with focused-editor text only under editor-text; absent when projection failed.</summary>
    [JsonPropertyName("projection")]
    public DiagnosticApplicationFrame? Projection { get; init; }

    /// <summary>Why the projection failed.</summary>
    [JsonPropertyName("failure")]
    public string? Failure { get; init; }
}
