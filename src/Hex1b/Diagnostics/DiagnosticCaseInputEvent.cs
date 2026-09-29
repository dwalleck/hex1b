using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>An input accepted by, or processed from, the terminal's input channel.</summary>
public sealed record DiagnosticCaseInputEvent
{
    /// <summary>The input id (the session's input milestone numbering).</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>The input's kind, for example <c>key</c>, <c>text</c> or <c>mouse</c>.</summary>
    [JsonPropertyName("inputKind")]
    public string? InputKind { get; init; }

    /// <summary>Where the input came from: <c>native</c> or <c>diagnostic-send</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The application instance that processed it.</summary>
    [JsonPropertyName("processedBy")]
    public string? ProcessedBy { get; init; }

    /// <summary>The processed-input watermark after this event.</summary>
    [JsonPropertyName("watermark")]
    public long? Watermark { get; init; }

    /// <summary>The input's payload (key and modifiers, or text), under raw-input.</summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; init; }
}
