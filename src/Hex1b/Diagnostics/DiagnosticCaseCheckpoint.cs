using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A case's initial checkpoint: the model state its recorded events start from.</summary>
public sealed record DiagnosticCaseCheckpoint
{
    /// <summary>Checkpoint profile. <c>fresh-model/1</c> covers a model that has applied nothing since construction.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = DiagnosticCaseCheckpointProfiles.FreshModel;

    /// <summary>Whether the checkpoint is complete.</summary>
    [JsonPropertyName("status")]
    public DiagnosticCaseCheckpointStatus Status { get; init; }

    /// <summary>Why the checkpoint is not complete; absent when complete.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The configuration that determines the fresh state; absent when excluded.</summary>
    [JsonPropertyName("configuration")]
    public DiagnosticCaseModelConfiguration? Configuration { get; init; }

    /// <summary>The state surfaces the profile covers.</summary>
    [JsonPropertyName("coveredSurfaces")]
    public IReadOnlyList<string> CoveredSurfaces { get; init; } = [];

    /// <summary>
    /// The model sequence of a <c>text-state/1</c> start: the case was started on a terminal that had already applied
    /// output, and its recorded model events follow this sequence. Absent for <c>fresh-model/1</c>.
    /// </summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>
    /// The state surfaces a <c>text-state/1</c> start held that its restore cannot represent (for example
    /// <c>retained-history</c>, <c>titles</c>, <c>pending-input</c>); empty when complete. Absent for <c>fresh-model/1</c>.
    /// </summary>
    [JsonPropertyName("unsupportedSurfaces")]
    public IReadOnlyList<string>? UnsupportedSurfaces { get; init; }
}
