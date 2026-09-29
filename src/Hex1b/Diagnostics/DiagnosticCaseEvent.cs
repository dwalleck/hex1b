using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One recorded case event, exactly as written in the artifact. Payloads appear only under their own
/// authorization: original model input (<c>data</c>) with reapplication-data, key payloads with
/// raw-input, focused-editor text with editor-text, written bytes with native-output.
/// </summary>
public sealed record DiagnosticCaseEvent
{
    /// <summary>Orders every event across streams, from 1.</summary>
    [JsonPropertyName("caseSequence")]
    public long CaseSequence { get; init; }

    /// <summary>Stream: <c>model</c>, <c>input</c>, <c>frames</c>, <c>delivery</c>, or <c>case</c> for the case's own records.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    /// <summary>Dense position within the stream, from 1; a missing ordinal is a gap.</summary>
    [JsonPropertyName("ordinal")]
    public long Ordinal { get; init; }

    /// <summary>When the event was observed, in the manifest's <c>process-monotonic</c> domain.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    /// <summary>
    /// What happened. Model: <c>application</c>, <c>application-without-ingress</c>, <c>resize</c>,
    /// <c>synchronized-update-timeout</c>. Input: <c>accepted</c>, <c>processed</c>. Frames: <c>published</c>.
    /// Delivery: <c>delivery</c>. Case: <c>interval-end</c>, <c>missing</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>The model sequence of a model event, or the first affected model sequence of an interval end.</summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>Columns: the geometry an application saw, or a resize's new width.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; init; }

    /// <summary>Rows: the geometry an application saw, or a resize's new height.</summary>
    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>Original input bytes the application consumed.</summary>
    [JsonPropertyName("length")]
    public int? Length { get; init; }

    /// <summary>The original input bytes (base64), under reapplication-data.</summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>An input stream event.</summary>
    [JsonPropertyName("input")]
    public DiagnosticCaseInputEvent? Input { get; init; }

    /// <summary>A frames stream event.</summary>
    [JsonPropertyName("frame")]
    public DiagnosticCaseFrameEvent? Frame { get; init; }

    /// <summary>A delivery stream event: the native delivery record.</summary>
    [JsonPropertyName("delivery")]
    public DiagnosticDeliveryRecord? Delivery { get; init; }

    /// <summary>A case record: an interval end or a missing range.</summary>
    [JsonPropertyName("record")]
    public DiagnosticCaseRecord? Record { get; init; }
}
