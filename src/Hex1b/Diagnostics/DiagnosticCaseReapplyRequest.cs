using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Re-applies a recorded case offline: a detached model is built from the case's recorded configuration and
/// its recorded events are applied up to a target boundary, then compared with the checkpoint recorded there.
/// Name exactly one target.
/// </summary>
public sealed record DiagnosticCaseReapplyRequest
{
    /// <summary>The case directory.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>Target: a model sequence (0 is the fresh model).</summary>
    [JsonPropertyName("toModelSequence")]
    public long? ToModelSequence { get; init; }

    /// <summary>Target: a checkpoint's label (a mark's, or <c>stop</c>).</summary>
    [JsonPropertyName("toLabel")]
    public string? ToLabel { get; init; }

    /// <summary>Target: the case sequence of a checkpoint or a model event.</summary>
    [JsonPropertyName("toCaseSequence")]
    public long? ToCaseSequence { get; init; }

    /// <summary>
    /// The origin to restore from: <c>start</c> (the case's initial checkpoint), a recovery checkpoint's label,
    /// <c>case:N</c> (its case sequence) or <c>checkpoint:N</c> (its ordinal). Absent, the earliest valid interval that
    /// covers the target chooses it. A named origin whose interval does not cover the target is refused.
    /// </summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>Faults to inject into the reconstructed state before comparing; the result is labelled.</summary>
    [JsonPropertyName("faults")]
    public IReadOnlyList<string>? Faults { get; init; }

    /// <summary>Most differences listed (1 to 100,000; default 1,000). Every difference is counted.</summary>
    [JsonPropertyName("maxDifferences")]
    public int? MaxDifferences { get; init; }

    /// <summary>Previews to write: <c>text</c>, <c>ansi</c>, <c>svg</c>, <c>html</c>.</summary>
    [JsonPropertyName("previews")]
    public IReadOnlyList<string>? Previews { get; init; }
}
